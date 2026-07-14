namespace SpaceTestPC.App.Models;

public sealed class EthernetPingRequest
{
    public string RouterIp { get; init; } = string.Empty;
    public string TargetIp { get; init; } = string.Empty;
    public int PingCount { get; init; } = 4;
    public int TimeoutMs { get; init; } = 10000;
}
