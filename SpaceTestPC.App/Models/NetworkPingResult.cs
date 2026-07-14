namespace SpaceTestPC.App.Models;

public sealed class NetworkPingResult
{
    public bool Connected { get; init; }
    public bool Linked { get; init; }
    public string Ip { get; init; } = string.Empty;
    public bool PingOk { get; init; }
    public int AvgDelayMs { get; init; }
}
