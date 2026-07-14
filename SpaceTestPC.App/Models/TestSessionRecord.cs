namespace SpaceTestPC.App.Models;

public sealed class TestSessionRecord
{
    public TestSession Session { get; init; } = new();
    public BoardState? BoardState { get; init; }
    public IReadOnlyList<LogEntry> Logs { get; init; } = Array.Empty<LogEntry>();
}
