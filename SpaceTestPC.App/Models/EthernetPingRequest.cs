namespace SpaceTestPC.App.Models;

public sealed class EthernetPingRequest
{
    public string RouterIp { get; set; } = string.Empty;
    public string TargetIp { get; set; } = string.Empty;
    public int PingCount { get; init; } = 4;
    public int TimeoutMs { get; init; } = 10000;
}
