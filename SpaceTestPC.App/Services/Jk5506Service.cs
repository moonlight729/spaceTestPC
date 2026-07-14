using System.IO.Ports;
using System.IO;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class Jk5506Service
{
    private readonly Jk5506Configuration _configuration;

    public Jk5506Service(Jk5506Configuration configuration) => _configuration = configuration;

    public async Task PrepareChargeTestAsync(int batteryVoltageMv, CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled) return;
        await ExecuteAsync(0x3000, 2, cancellationToken); // Clear previous instrument errors.
        await ExecuteAsync(0x2000, checked((ushort)batteryVoltageMv), cancellationToken);
        await ExecuteAsync(0x3000, 1, cancellationToken);
    }

    public Task StopOutputAsync(CancellationToken cancellationToken = default) =>
        !_configuration.Enabled ? Task.CompletedTask : ExecuteAsync(0x3000, 0, cancellationToken);

    private async Task ExecuteAsync(ushort register, ushort value, CancellationToken cancellationToken)
    {
        using var port = new SerialPort(_configuration.PortName, _configuration.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = _configuration.TimeoutMs,
            WriteTimeout = _configuration.TimeoutMs
        };
        port.Open();
        var frame = BuildWriteFrame(_configuration.SlaveAddress, register, value);
        await port.BaseStream.WriteAsync(frame, cancellationToken);
        await port.BaseStream.FlushAsync(cancellationToken);
        var response = new byte[8];
        var total = 0;
        while (total < response.Length)
        {
            var read = await port.BaseStream.ReadAsync(response.AsMemory(total), cancellationToken);
            if (read == 0) throw new IOException("JK5506 closed the RS485 response stream.");
            total += read;
        }
        if (!response.SequenceEqual(frame)) throw new InvalidOperationException("JK5506 write response did not match the Modbus RTU request.");
    }

    private static byte[] BuildWriteFrame(byte slaveAddress, ushort register, ushort value)
    {
        var frame = new byte[] { slaveAddress, 0x06, (byte)(register >> 8), (byte)register, (byte)(value >> 8), (byte)value, 0, 0 };
        var crc = CalculateCrc(frame.AsSpan(0, 6));
        frame[6] = (byte)crc;
        frame[7] = (byte)(crc >> 8);
        return frame;
    }

    private static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) == 1 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }
}
