using System.IO.Ports;
using System.IO;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class JxTvmService
{
    private const int BaudRate = 9600;
    private const byte SlaveAddress = 1;
    private const int TimeoutMs = 1000;
    private readonly JxTvmConfiguration _configuration;
    public JxTvmService(JxTvmConfiguration configuration) => _configuration = configuration;
    public bool IsEnabled => _configuration.Enabled;

    public async Task<int> ReadChannelVoltageMvAsync(int channel, CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled) return 0;
        if (channel is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channel));
        // The JX-TVM protocol exposes the live channel voltage at 1233-1264, in 0.01V units.
        var register = checked((ushort)(1233 + channel - 1));
        var value = await ReadRegisterAsync(register, cancellationToken);
        return value * 10;
    }

    public async Task<int> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled) return 0;
        return await ReadRegisterAsync(1000, cancellationToken);
    }

    public async Task<(int WorkMode, int TestMode, int SamplingMode)> ReadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled) return (0, 0, 0);
        var work = await ReadRegisterAsync(1000, cancellationToken);
        var test = await ReadRegisterAsync(1001, cancellationToken);
        var sampling = await ReadRegisterAsync(1050, cancellationToken);
        return (work, test, sampling);
    }

    public async Task<int[]> ReadAllChannelVoltagesMvAsync(CancellationToken cancellationToken = default)
    {
        var values = new int[32];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = await ReadChannelVoltageMvAsync(i + 1, cancellationToken);
        }
        return values;
    }

    private Task<int> ReadRegisterAsync(ushort register, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var port = new SerialPort(_configuration.PortName, BaudRate, Parity.None, 8, StopBits.One) { ReadTimeout = TimeoutMs, WriteTimeout = TimeoutMs };
        port.Open();
        var request = BuildReadFrame(SlaveAddress, register, 1);
        port.DiscardInBuffer();
        port.Write(request, 0, request.Length);
        Thread.Sleep(30);
        var response = new byte[7];
        var total = 0;
        while (total < response.Length) { var read = port.Read(response, total, response.Length - total); if (read == 0) throw new TimeoutException("JX-TVM did not return an RS485 Modbus response before the read timeout."); total += read; }
        if (response[0] != SlaveAddress || response[1] != 0x03 || response[2] != 2) throw new InvalidOperationException("Invalid JX-TVM voltage response.");
        return Task.FromResult((response[3] << 8) | response[4]);
    }

    private static byte[] BuildReadFrame(byte slave, ushort register, ushort count)
    {
        var frame = new byte[] { slave, 0x03, (byte)(register >> 8), (byte)register, (byte)(count >> 8), (byte)count, 0, 0 };
        ushort crc = 0xFFFF; foreach (var value in frame.AsSpan(0, 6)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1); }
        frame[6] = (byte)crc; frame[7] = (byte)(crc >> 8); return frame;
    }
}
