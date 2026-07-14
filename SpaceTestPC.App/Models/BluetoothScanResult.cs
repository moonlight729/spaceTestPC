namespace SpaceTestPC.App.Models;

public sealed class BluetoothScanResult
{
    public bool Found { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public int Rssi { get; init; }
}
