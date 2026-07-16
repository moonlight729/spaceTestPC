namespace SpaceTestPC.App.Models;

public sealed class TestPlanItem
{
    public required string Id { get; init; }
    public bool Skip { get; init; }
    public string? SkipReason { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}
