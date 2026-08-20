using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

/// <summary>Independent, sequential pressure loop for one configured station.</summary>
public sealed class PressureStationRunner
{
    private readonly PressureRunController _controller = new();

    public void Stop() => _controller.Stop();
    public void Pause() => _controller.Pause();
    public void Resume() => _controller.Resume();

    public async Task RunAsync(
        IPcbaCommandClient client,
        string sn,
        PressureTestConfiguration configuration,
        Action<int, string>? itemStarted,
        Action<int, string, bool, string> itemCompleted,
        CancellationToken cancellationToken = default)
    {
        var items = new[] { "cpu", "memory", "wifi", "emmc", "tf", "usb_camera", "bluetooth", "fan" }
            .Where(id => configuration.EnabledItems.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (items.Length == 0) throw new InvalidOperationException("No pressure-test items are enabled.");

        await _controller.RunRoundsAsync(items,
            TimeSpan.FromHours(Math.Max(1, configuration.TotalDurationHours)),
            TimeSpan.FromMinutes(Math.Max(0, configuration.PeripheralRoundIntervalMinutes)),
            async (round, itemId, token) =>
            {
                var sessionId = $"pressure-s{Guid.NewGuid():N}";
                itemStarted?.Invoke(round, itemId);
                try
                {
                    var (passed, detail) = await ExecuteAsync(client, sessionId, itemId, token);
                    itemCompleted(round, itemId, passed, detail);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    itemCompleted(round, itemId, false, ex.Message);
                }
            }, cancellationToken);
    }

    private static async Task<(bool Passed, string Detail)> ExecuteAsync(IPcbaCommandClient client, string sessionId, string itemId, CancellationToken token)
    {
        if (itemId == "cpu" || itemId == "memory")
        {
            var result = await (itemId == "cpu" ? client.RunCpuPressureAsync(sessionId, token) : client.RunMemoryPressureAsync(sessionId, token))
                .WaitAsync(TimeSpan.FromMinutes(3), token);
            return (result.Passed, $"{result.DurationSec}s, errors={result.ErrorCount}");
        }
        if (itemId is "wifi" or "bluetooth")
        {
            var result = itemId == "bluetooth"
                ? await client.RunPressureBluetoothAsync(sessionId, token).WaitAsync(TimeSpan.FromSeconds(30), token)
                : await client.RunPressureNetworkAsync(sessionId, itemId, token).WaitAsync(TimeSpan.FromSeconds(30), token);
            return (result.Found, result.Found ? $"RSSI {result.Rssi} dBm" : result.FailureReason);
        }

        var peripheral = await client.RunPressurePeripheralAsync(sessionId, itemId, token)
            .WaitAsync(itemId == "usb_camera" ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30), token);
        return (peripheral.Active, peripheral.Detail);
    }
}
