namespace SpaceTestPC.App.Models;

public sealed class BoardState
{
    public string BoardId { get; init; } = string.Empty;
    public string BoardSn { get; init; } = string.Empty;
    public string TestMode { get; init; } = "ready";
    public string CurrentState { get; init; } = "idle";
    public string LastSessionId { get; init; } = string.Empty;
    public string LastStartTime { get; init; } = string.Empty;
    public string LastEndTime { get; init; } = string.Empty;
    public string LastVerdict { get; init; } = string.Empty;
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public int TotalCount { get; init; }
    public int Version { get; init; } = 1;
    public IReadOnlyList<BoardTestItemSummary> TestItems { get; init; } = Array.Empty<BoardTestItemSummary>();
}

public sealed class BoardTestItemSummary
{
    public string TestId { get; init; } = string.Empty;
    public string LastStatus { get; init; } = string.Empty;
    public int TestCount { get; init; }
}
