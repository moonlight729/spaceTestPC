using System.Text.Json;

namespace SpaceTestPC.Core.Models;

/// <summary>
/// aging.status 的返回快照。
/// MediaPresent / MediaBytes 是上位机判断"现场还在不在"的依据（§12.1）。
/// </summary>
public sealed class AgingStatusSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public string State { get; init; } = AgingRunStates.Idle;
    public string? FailReason { get; init; }
    public string? LastError { get; init; }

    public int EffectiveSec { get; init; }
    public int RemainingSec { get; init; }
    public int PrepareRemainingSec { get; init; }
    public int ResumeCount { get; init; }
    public int LostTimeSec { get; init; }

    public string? StartedAtUtc { get; init; }
    public string? UpdatedAtUtc { get; init; }

    public bool MediaPresent { get; init; }
    public long MediaBytes { get; init; }

    /// <summary>各测试项实时摘要，透传板端结构。</summary>
    public IReadOnlyDictionary<string, JsonElement> Items { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// aging.result 的返回（设备自评，唯一真相源 §5.2）。
/// </summary>
public sealed class AgingRunResult
{
    public string RunId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public string Verdict { get; init; } = string.Empty;
    public string? FailReason { get; init; }
    public string? FailedAtUtc { get; init; }
    public string? StartedAtUtc { get; init; }
    public string? FinishedAtUtc { get; init; }

    public int EffectiveSec { get; init; }
    public int ResumeCount { get; init; }
    public int LostTimeSec { get; init; }
    public bool DebugMode { get; init; }

    public AgingCleanupInfo? Cleanup { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Items { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// 清理结果（§8.9）。Ok=false 即为关键失败，不允许出货。
/// </summary>
public sealed class AgingCleanupInfo
{
    public bool Ok { get; init; }
    public double BeforeFreeGiB { get; init; }
    public double AfterFreeGiB { get; init; }
    public int DurationSec { get; init; }
    public bool MediaPresent { get; init; }
}
