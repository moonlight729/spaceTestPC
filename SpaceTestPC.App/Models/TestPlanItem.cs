namespace SpaceTestPC.App.Models;

public sealed class TestPlanItem
{
    public required string Id { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}
