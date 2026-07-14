namespace SpaceTestPC.App.Models;

public sealed class TestSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public string Sn { get; init; } = string.Empty;
    public string ProductModel { get; init; } = "PCBA_X1";
    public string StationCode { get; init; } = "ST01";
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndTime { get; init; }
    public string FinalVerdict { get; init; } = "Pending";
}
