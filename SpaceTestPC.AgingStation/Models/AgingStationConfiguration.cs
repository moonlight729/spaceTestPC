using System.Globalization;
using System.Text.Json;

namespace SpaceTestPC.AgingStation.Models;

/// <summary>
/// 老化工作站配置（§13）。与 PCBA 上位机的 appsettings 完全隔离：独立文件、独立库。
/// </summary>
public sealed class AgingStationConfiguration
{
    public AgingStationSection Station { get; init; } = new();
    public AgingRunSection Run { get; init; } = new();
    public AgingCleanupSection Cleanup { get; init; } = new();

    /// <summary>
    /// 各测试项参数，透传给板端。用字典而非强类型：板端字段仍在演进，
    /// 强类型会让上位机随板端每次改动而失配。
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Items { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed class AgingStationSection
{
    public int DevicePort { get; init; } = 19001;
    public string NetSinkHost { get; init; } = "192.168.110.2";
    public int NetSinkPort { get; init; } = 19002;

    /// <summary>单条命令超时。status 轮询用，start/cleanup 会单独放宽。</summary>
    public int CommandTimeoutMs { get; init; } = 15000;

    /// <summary>状态轮询间隔。老化是长跑，30 s 足够，不必追着设备问。</summary>
    public int PollIntervalMs { get; init; } = 30000;

    /// <summary>监控并发路数。监控是轻量 status，可以全量并行（§4.4）。</summary>
    public int MonitorParallelism { get; init; } = 32;

    public IReadOnlyList<AgingSlotConfiguration> Slots { get; init; } = Array.Empty<AgingSlotConfiguration>();
}

public sealed class AgingSlotConfiguration
{
    public int Slot { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Ip { get; init; } = string.Empty;
    public string ExpectedSn { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed class AgingRunSection
{
    /// <summary>如 "8h" / "24h" / "72h"。</summary>
    public string TotalDuration { get; init; } = "24h";
    public bool DebugMode { get; init; }
    public string DebugDuration { get; init; } = "5m";

    /// <summary>单槽位启动的准备窗口，秒。</summary>
    public int SinglePrepareSec { get; init; } = 10;

    /// <summary>批量启动的准备窗口，秒。须大于"全部下发完"的实测耗时（§17-6）。</summary>
    public int BatchPrepareSec { get; init; } = 60;

    /// <summary>批量启动时相邻槽位的错开间隔，秒（§16-25）。</summary>
    public int BatchStaggerSec { get; init; } = 30;

    public int MaxResumeCount { get; init; } = 2;
    public int ReportIntervalMs { get; init; } = 60000;
    public bool EnableLvgl { get; init; } = true;

    /// <summary>实际下发的时长（秒）。调试模式用 debugDuration。</summary>
    public int EffectiveDurationSec => AgingDuration.ParseToSeconds(
        DebugMode ? DebugDuration : TotalDuration, 24 * 3600);
}

public sealed class AgingCleanupSection
{
    /// <summary>达标后自动清理 media/。</summary>
    public bool OnComplete { get; init; } = true;

    /// <summary>关键失败时保留现场（§16-26）。</summary>
    public bool RetainOnFailure { get; init; } = true;

    /// <summary>现场保留提醒时限（小时）。超时只提醒，不自动删（§12.1）。</summary>
    public int RetainTimeoutHours { get; init; } = 24;

    /// <summary>清理命令超时，秒。删几十 GB 可能要几分钟。</summary>
    public int TimeoutSec { get; init; } = 300;
}

public static class AgingDuration
{
    /// <summary>
    /// 解析 "30s" / "30m" / "8h" / "24h" / "72h"，也接受纯数字（按秒）。
    /// 解析失败时返回 fallback 而不是抛异常：配置里一个笔误不该让整个工位起不来。
    /// </summary>
    public static int ParseToSeconds(string? value, int fallbackSeconds)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallbackSeconds;
        }

        var text = value.Trim();
        var unit = text[^1];
        var numberText = char.IsDigit(unit) ? text : text[..^1];

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return fallbackSeconds;
        }

        return unit switch
        {
            's' or 'S' => (int)number,
            'm' or 'M' => (int)(number * 60),
            'h' or 'H' => (int)(number * 3600),
            _ => (int)number
        };
    }

    public static string Format(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m{seconds % 60}s";
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return minutes == 0 ? $"{hours}h" : $"{hours}h{minutes}m";
    }
}
