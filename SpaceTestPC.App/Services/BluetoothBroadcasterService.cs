using System.IO;
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
        using var port = CreatePort();
        port.Open();
        /*
         * The CDEBYTE module is used as an upper-PC BLE broadcaster.  The 3576
         * side scans for the same name that is configured here.  Keep the name
         * sourced from appsettings.bluetoothBroadcaster.broadcastName so there
         * is only one production configuration point.
         *
         * Verified module settings:
         * - ROLE=0: phone/3576 visible advertising mode.
         * - PWR=4: highest accepted transmit-power level on the current module.
         * - SCANRSP=1: lets scanners receive the configured local name.
         * - RESET: required for name/scan-response changes to take effect.
         */
        SendAndRequireOk(port, "AT+ROLE=0");
        SendAndRequireOk(port, "AT+PWR=4");
        SendAndRequireOk(port, $"AT+NAME={_configuration.BroadcastName}");
        SendAndRequireOk(port, "AT+SCANRSP=1");
        SendAndRequireOk(port, "AT+RESET");
    }, cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyDictionary<string, string>>(() =>
    {
        using var port = OpenPortWithRetry();
        return new Dictionary<string, string>
        {
            ["role"] = Query(port, "AT+ROLE?"),
            ["power"] = Query(port, "AT+PWR?"),
            ["name"] = Query(port, "AT+NAME?"),
            ["scanResponse"] = Query(port, "AT+SCANRSP?"),
            ["advertising"] = Query(port, "AT+ADV?"),
            ["mac"] = Query(port, "AT+MAC?")
        };
    }, cancellationToken);

    private SerialPort CreatePort() =>
        new(_configuration.PortName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1500,
            WriteTimeout = 1500,
            NewLine = "\r\n"
        };

    private SerialPort OpenPortWithRetry()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = CreatePort();
            try
            {
                port.Open();
                return port;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                port.Dispose();
                Thread.Sleep(500);
            }
        }

        throw new IOException($"Bluetooth module port {_configuration.PortName} is not available after reset.", lastError);
    }

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
