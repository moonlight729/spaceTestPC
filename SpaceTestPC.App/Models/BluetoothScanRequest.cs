namespace SpaceTestPC.App.Models;

public sealed class BluetoothScanRequest
{
    public string TargetName { get; init; } = string.Empty;
    public int TimeoutMs { get; init; } = 5000;
    public int MinRssi { get; init; } = -80;
}
