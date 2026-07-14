using System.IO.Ports;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class BluetoothBroadcasterService
{
    private readonly BluetoothBroadcasterConfiguration _configuration;
    public BluetoothBroadcasterService(BluetoothBroadcasterConfiguration configuration) => _configuration = configuration;

    public Task ConfigureAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!_configuration.Enabled) return;
        using var port = new SerialPort(_configuration.PortName, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 1500, WriteTimeout = 1500, NewLine = "\r\n" };
        port.Open();
        SendAndRequireOk(port, "AT+ROLE=1");
        SendAndRequireOk(port, $"AT+NAME={_configuration.BroadcastName}");
        SendAndRequireOk(port, "AT+ADV=1,0,500");
    }, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyDictionary<string, string>>(() =>
    {
        using var port = new SerialPort(_configuration.PortName, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 1500, WriteTimeout = 1500, NewLine = "\r\n" };
        port.Open();
        return new Dictionary<string, string>
        {
            ["role"] = Query(port, "AT+ROLE?"),
            ["name"] = Query(port, "AT+NAME?"),
            ["advertising"] = Query(port, "AT+ADV?"),
            ["mac"] = Query(port, "AT+MAC?")
        };
    }, cancellationToken);

    private static void SendAndRequireOk(SerialPort port, string command)
    {
        port.DiscardInBuffer();
        port.Write(command + "\r\n");
        var response = port.ReadLine();
        if (!response.Contains("+OK", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Bluetooth module rejected {command}: {response}");
    }

    private static string Query(SerialPort port, string command)
    {
        port.DiscardInBuffer();
        port.Write(command + "\r\n");
        return port.ReadLine().Trim();
    }
}
