using System.IO.Ports;
using System.IO;
using SpaceTestPC.Core.Models;

namespace SpaceTestPC.Core.Services;

/// <summary>
/// One channel reading of the 32-channel sweep.  A sweep must survive single-channel
/// failures: an unreadable channel keeps its own diagnostics while the remaining
/// channels are still measured, so <see cref="IsValid"/> distinguishes "the meter
/// answered 0 mV" from "the meter did not answer at all".
/// </summary>
public sealed record JxTvmChannelReading(int Channel, int ValueMv, bool IsValid, string? Error);

public sealed class JxTvmService
{
    private const int BaudRate = 9600;
    private const byte SlaveAddress = 1;
    private const int TimeoutMs = 1000;
    private const int WriteSettleDelayMs = 30;
    private const int ResponseLength = 7;
    private const int ChannelCount = 32;
    private readonly JxTvmConfiguration _configuration;
    public JxTvmService(JxTvmConfiguration configuration) => _configuration = configuration;
    public Action<string>? Log { get; set; }
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

    /// <summary>
    /// Reads every live channel.  The whole sweep runs on a worker thread so the UI
    /// thread is never blocked by RS485 round-trips, and a failing channel is reported
    /// per channel instead of aborting the sweep.
    /// </summary>
    public Task<JxTvmChannelReading[]> ReadAllChannelVoltagesMvAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadAllChannelsCoreAsync(cancellationToken), cancellationToken);

    private async Task<JxTvmChannelReading[]> ReadAllChannelsCoreAsync(CancellationToken cancellationToken)
    {
        var readings = new JxTvmChannelReading[ChannelCount];
        for (var i = 0; i < readings.Length; i++)
        {
            var channel = i + 1;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await ReadChannelVoltageMvAsync(channel, cancellationToken).ConfigureAwait(false);
                readings[i] = new JxTvmChannelReading(channel, value, true, null);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentOutOfRangeException or OverflowException)
            {
                readings[i] = new JxTvmChannelReading(channel, 0, false, ex.Message);
                Log?.Invoke($"JX-TVM channel[{channel}] read failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return readings;
    }

    private async Task<int> ReadRegisterAsync(ushort register, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var port = new SerialPort(_configuration.PortName, BaudRate, Parity.None, 8, StopBits.One) { WriteTimeout = TimeoutMs };
        port.Open();
        try
        {
            var request = BuildReadFrame(SlaveAddress, register, 1);
            Log?.Invoke($"JX-TVM TX: {Convert.ToHexString(request)} register={register}");
            port.DiscardInBuffer();
            // SerialPort.ReadTimeout is not honoured reliably by async base-stream reads,
            // so the whole transaction shares one explicit timeout budget.
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeoutMs);
            try
            {
                await port.BaseStream.WriteAsync(request, 0, request.Length, timeoutSource.Token).ConfigureAwait(false);
                await Task.Delay(WriteSettleDelayMs, timeoutSource.Token).ConfigureAwait(false);
                var response = await ReadExactAsync(port.BaseStream, ResponseLength, timeoutSource.Token).ConfigureAwait(false);
                return ParseRegisterResponse(register, response);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"JX-TVM did not return an RS485 Modbus response within {TimeoutMs} ms (register={register}).");
            }
        }
        finally
        {
            if (port.IsOpen)
            {
                try { port.Close(); } catch (IOException) { }
            }
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer, total, count - total, cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new TimeoutException("JX-TVM did not return an RS485 Modbus response before the read timeout.");
            total += read;
        }
        return buffer;
    }

    private int ParseRegisterResponse(ushort register, byte[] response)
    {
        Log?.Invoke($"JX-TVM RX: {Convert.ToHexString(response)} register={register}");
        var expectedCrc = CalculateCrc(response.AsSpan(0, 5));
        var actualCrc = (ushort)(response[5] | (response[6] << 8));
        if (response[0] != SlaveAddress || response[1] != 0x03 || response[2] != 2) throw new InvalidOperationException("Invalid JX-TVM voltage response.");
        if (expectedCrc != actualCrc) throw new InvalidOperationException($"JX-TVM CRC mismatch: expected=0x{expectedCrc:X4}, actual=0x{actualCrc:X4}");
        return (response[3] << 8) | response[4];
    }

    private static byte[] BuildReadFrame(byte slave, ushort register, ushort count)
    {
        var frame = new byte[] { slave, 0x03, (byte)(register >> 8), (byte)register, (byte)(count >> 8), (byte)count, 0, 0 };
        ushort crc = 0xFFFF; foreach (var value in frame.AsSpan(0, 6)) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1); }
        frame[6] = (byte)crc; frame[7] = (byte)(crc >> 8); return frame;
    }

    private static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1); }
        return crc;
    }
}
