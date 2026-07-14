namespace SpaceTestPC.App.Models;

public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Level { get; init; } = "Info";
    public string Message { get; init; } = string.Empty;
}
