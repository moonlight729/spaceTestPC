using System.Globalization;
using System.IO;
using System.Text;
using SpaceTestPC.AgingStation.Models;
using SpaceTestPC.AgingStation.ViewModels;
using SpaceTestPC.Core.Models;

namespace SpaceTestPC.AgingStation.Services;

/// <summary>
/// 老化报告导出（§11）。清理结果与人工处置留痕必须进报告——
/// 前者是"设备已恢复原样"的唯一凭据，后者解释失败机为什么 FAIL、现场去哪了。
/// </summary>
public static class AgingReportWriter
{
    public static (string MarkdownPath, string CsvPath) Write(
        string outputDirectory,
        AgingStationConfiguration configuration,
        IReadOnlyList<SlotViewModel> slots,
        IReadOnlyDictionary<int, AgingRunResult?> results)
    {
        Directory.CreateDirectory(outputDirectory);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var markdownPath = Path.Combine(outputDirectory, $"aging-report-{stamp}.md");
        var csvPath = Path.Combine(outputDirectory, $"aging-summary-{stamp}.csv");

        File.WriteAllText(markdownPath, BuildMarkdown(configuration, slots, results), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(slots, results), Encoding.UTF8);

        return (markdownPath, csvPath);
    }

    public static string BuildMarkdown(
        AgingStationConfiguration configuration,
        IReadOnlyList<SlotViewModel> slots,
        IReadOnlyDictionary<int, AgingRunResult?> results)
    {
        var builder = new StringBuilder();
        var now = DateTimeOffset.Now;

        builder.AppendLine("# 老化测试报告");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 计划总时长：{configuration.Run.TotalDuration}（{AgingDuration.Format(configuration.Run.EffectiveDurationSec)}）");
        builder.AppendLine($"- **调试模式：{(configuration.Run.DebugMode ? "是（debug: true，不得作为出货依据）" : "否")}**");
        builder.AppendLine("- **覆盖范围：老化不覆盖 HDMI**（§16-17）");
        builder.AppendLine($"- 槽位数：{slots.Count}，通过 {slots.Count(s => s.State == AgingRunStates.Passed)}，" +
                           $"失败 {slots.Count(s => s.State == AgingRunStates.Failed)}");
        builder.AppendLine();

        builder.AppendLine("## 各槽位结果");
        builder.AppendLine();
        builder.AppendLine("| 槽位 | 名称 | SN | IP | 计划开始(UTC) | 实际开始(UTC) | 有效时长 | 续跑 | 丢失 | 状态 | 结论 |");
        builder.AppendLine("|---:|---|---|---|---|---|---|---:|---:|---|:--|");
        foreach (var slot in slots.OrderBy(s => s.Slot))
        {
            var result = results.TryGetValue(slot.Slot, out var value) ? value : null;
            builder.AppendLine(string.Join(" | ",
                $"{slot.Slot}",
                Escape(slot.Name),
                Escape(slot.Sn ?? slot.ExpectedSn),
                Escape(slot.Ip),
                slot.PlannedStartAtUtc?.ToString("HH:mm:ss") ?? "-",
                TrimUtc(slot.StartedAtUtc),
                AgingDuration.Format(result?.EffectiveSec ?? slot.EffectiveSec),
                $"{result?.ResumeCount ?? slot.ResumeCount}",
                $"{result?.LostTimeSec ?? slot.LostTimeSec}s",
                slot.StateText,
                Escape(result?.Verdict ?? slot.Verdict)));
        }

        builder.AppendLine();
        builder.AppendLine("## 清理与现场（§8.9）");
        builder.AppendLine();
        builder.AppendLine("| 槽位 | 清理成功 | 清理前可用 | 清理后可用 | media 残留 |");
        builder.AppendLine("|---:|:--|---:|---:|:--|");
        foreach (var slot in slots.OrderBy(s => s.Slot))
        {
            var cleanup = results.TryGetValue(slot.Slot, out var value) ? value?.Cleanup : null;
            builder.AppendLine(string.Join(" | ",
                $"{slot.Slot}",
                cleanup is null ? "-" : cleanup.Ok ? "是" : "**否**",
                cleanup is null ? "-" : cleanup.BeforeFreeGiB.ToString("F1", CultureInfo.InvariantCulture),
                cleanup is null ? "-" : cleanup.AfterFreeGiB.ToString("F1", CultureInfo.InvariantCulture),
                slot.MediaPresent ? $"**是（{slot.MediaText}）**" : "否"));
        }

        builder.AppendLine();
        builder.AppendLine("## 人工处置（§12.1）");
        builder.AppendLine();
        var disposed = slots.Where(s => !string.IsNullOrWhiteSpace(s.DisposalAction)).OrderBy(s => s.Slot).ToList();
        if (disposed.Count == 0)
        {
            builder.AppendLine("本次无人工处置记录。");
        }
        else
        {
            builder.AppendLine("| 槽位 | 处置方式 | 操作人 | 时间(UTC) |");
            builder.AppendLine("|---:|---|---|---|");
            foreach (var slot in disposed)
            {
                builder.AppendLine(string.Join(" | ",
                    $"{slot.Slot}", Escape(slot.DisposalAction), Escape(slot.DisposedBy), TrimUtc(slot.DisposalAtUtc)));
            }
        }

        return builder.ToString();
    }

    public static string BuildCsv(
        IReadOnlyList<SlotViewModel> slots,
        IReadOnlyDictionary<int, AgingRunResult?> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("slot,name,sn,ip,planned_start_utc,actual_start_utc,effective_sec,resume_count,lost_sec,state,verdict,cleanup_ok,media_present,disposal,disposed_by");

        foreach (var slot in slots.OrderBy(s => s.Slot))
        {
            var result = results.TryGetValue(slot.Slot, out var value) ? value : null;
            var cleanup = result?.Cleanup;
            builder.AppendLine(string.Join(",",
                slot.Slot,
                Csv(slot.Name),
                Csv(slot.Sn ?? slot.ExpectedSn),
                Csv(slot.Ip),
                slot.PlannedStartAtUtc?.ToString("O") ?? string.Empty,
                slot.StartedAtUtc ?? string.Empty,
                result?.EffectiveSec ?? slot.EffectiveSec,
                result?.ResumeCount ?? slot.ResumeCount,
                result?.LostTimeSec ?? slot.LostTimeSec,
                slot.State,
                Csv(result?.Verdict ?? slot.Verdict),
                cleanup?.Ok.ToString() ?? string.Empty,
                slot.MediaPresent,
                Csv(slot.DisposalAction),
                Csv(slot.DisposedBy)));
        }

        return builder.ToString();
    }

    private static string Escape(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "\\|");

    private static string Csv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static string TrimUtc(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Length > 19 ? value[..19] : value;
}
