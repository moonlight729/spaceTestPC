namespace SpaceTestPC.App.Models;

public sealed class HostCommand
{
    public string ProtocolVersion { get; init; } = "1.0";
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public string BoardId { get; init; } = string.Empty;
    public string CommandGroup { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public string Timestamp { get; init; } = DateTimeOffset.Now.ToString("O");
}
