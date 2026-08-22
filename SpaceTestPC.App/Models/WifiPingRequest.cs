namespace SpaceTestPC.App.Models;

public sealed class WifiPingRequest
{
    public string Ssid { get; set; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string TargetIp { get; init; } = string.Empty;
    public int PingCount { get; init; } = 4;
    public int TimeoutMs { get; init; } = 10000;
}
