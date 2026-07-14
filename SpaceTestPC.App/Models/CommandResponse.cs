namespace SpaceTestPC.App.Models;

public sealed class CommandResponse
{
    public string ProtocolVersion { get; init; } = "1.0";
    public string RequestId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public string BoardId { get; init; } = string.Empty;
    public int ResultCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
}
