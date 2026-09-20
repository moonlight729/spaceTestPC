namespace SpaceTestPC.Core.Models;

/// <summary>
/// aging.start 的下发参数（§5.1 / §5.5）。
/// startDelaySec 是"预约倒计时"：设备本地倒计时归零后才开始计有效时长。
/// </summary>
public sealed class AgingStartRequest
{
    public string RunId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public int DurationSec { get; init; }
    public int StartDelaySec { get; init; }
    public bool DebugMode { get; init; }
    public bool EnableLvgl { get; init; }
    public int ReportIntervalMs { get; init; } = 60000;
    public int MaxResumeCount { get; init; } = 2;

    /// <summary>
    /// 各测试项参数（§13 items）。用字典透传：板端字段仍在演进，
    /// 强类型模型会随板端每次改动而失配。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Items { get; init; } = new Dictionary<string, object?>();
}

/// <summary>
/// aging.start 的返回。板端应立即返回（后台起任务），不应等到跑完。
/// </summary>
public sealed class AgingStartAck
{
    public bool Accepted { get; init; }
    public string RunId { get; init; } = string.Empty;
    public string State { get; init; } = AgingRunStates.Idle;
    public string Reason { get; init; } = string.Empty;
}
