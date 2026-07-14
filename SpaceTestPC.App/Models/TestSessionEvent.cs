namespace SpaceTestPC.App.Models;

public sealed class TestSessionEvent
{
    public required string Event { get; init; }
    public string TestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ResultCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
