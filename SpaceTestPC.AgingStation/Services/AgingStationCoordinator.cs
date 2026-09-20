using System.Text.Json;
using SpaceTestPC.AgingStation.Models;
using SpaceTestPC.AgingStation.ViewModels;
using SpaceTestPC.Core.Models;
using SpaceTestPC.Core.Services;

namespace SpaceTestPC.AgingStation.Services;

public sealed class AgingBatchStartReport
{
    public int Requested { get; set; }
    public int Dispatched { get; set; }
    public int Failed { get; set; }
    public int SkippedLate { get; set; }
    public int SkippedOffline { get; set; }

    public string Summary =>
        $"已下发 {Dispatched}/{Requested}" +
        (Failed > 0 ? $"，失败 {Failed}" : string.Empty) +
        (SkippedLate > 0 ? $"，未赶上批次 {SkippedLate}" : string.Empty) +
        (SkippedOffline > 0 ? $"，离线 {SkippedOffline}" : string.Empty);
}

/// <summary>
/// 老化工站的编排器：启动编排（§5.5）、状态轮询、停止与清理。
///
/// 这里只做"下发 + 收集"，不做判定——判定在设备端，result.json 是唯一真相源（§16-7）。
/// 因此本类在整个长跑期间可以随时重启：状态全在设备侧。
/// </summary>
public sealed class AgingStationCoordinator
{
    /// <summary>下发并发度。启动下发按 8 路限流（§4.4）。</summary>
    private const int DispatchParallelism = 8;

    private readonly AgingStationConfiguration _configuration;
    private readonly Dictionary<int, IAgingCommandClient> _clients = new();

    public event Action<string>? Log;

    public AgingStationCoordinator(AgingStationConfiguration configuration)
    {
        _configuration = configuration;

        foreach (var slot in configuration.Station.Slots.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Ip)))
        {
            var client = new AgingCommandClient(slot.Ip, configuration.Station.DevicePort, configuration.Station.CommandTimeoutMs);
            client.Log += message => Log?.Invoke(message);
            _clients[slot.Slot] = client;
        }
    }

    /// <summary>
    /// 启动一批槽位。isBatch=false 时按单槽位处理（准备窗口更短、不错开）。
    /// </summary>
    public async Task<AgingBatchStartReport> StartAsync(
        IReadOnlyList<SlotViewModel> slots, bool isBatch, CancellationToken cancellationToken = default)
    {
        var report = new AgingBatchStartReport();
        var ordered = slots.Where(s => s.Enabled).OrderBy(s => s.Slot).ToList();
        report.Requested = ordered.Count;
        if (ordered.Count == 0) return report;

        var prepareSec = isBatch ? _configuration.Run.BatchPrepareSec : _configuration.Run.SinglePrepareSec;
        var staggerSec = isBatch ? _configuration.Run.BatchStaggerSec : 0;
        var batchStamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var baseStart = DateTimeOffset.UtcNow.AddSeconds(prepareSec);

        Log?.Invoke(isBatch
            ? $"批量启动：{ordered.Count} 台，准备窗口 {prepareSec}s，错开 {staggerSec}s/台。"
            : $"单槽位启动：{ordered.Count} 台，准备窗口 {prepareSec}s。");

        var indexed = ordered.Select((slot, index) => (slot, index)).ToList();

        await Parallel.ForEachAsync(
            indexed,
            new ParallelOptions { MaxDegreeOfParallelism = DispatchParallelism, CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                var (slot, index) = entry;

                if (!_clients.TryGetValue(slot.Slot, out var client))
                {
                    lock (report) { report.SkippedOffline++; }
                    slot.Note = "离线未启动";
                    return;
                }

                // 预约启动：每台按自己的下发时刻换算倒计时，最终对齐到同一计划时刻（§5.5）。
                var plannedStart = baseStart.AddSeconds(index * staggerSec);
                slot.PlannedStartAtUtc = plannedStart;

                var delaySec = (int)(plannedStart - DateTimeOffset.UtcNow).TotalSeconds;
                if (delaySec <= 0)
                {
                    lock (report) { report.SkippedLate++; }
                    slot.Note = "未赶上批次";
                    Log?.Invoke($"槽位 {slot.Slot} 未赶上批次：下发过慢，需用新的开始时刻单独补发。");
                    return;
                }

                var request = BuildStartRequest(slot, batchStamp, delaySec);
                slot.IsBusy = true;
                try
                {
                    var ack = await client.StartAsync(request, token);
                    if (ack.Accepted)
                    {
                        lock (report) { report.Dispatched++; }
                        slot.RunId = request.RunId;
                        if (!string.IsNullOrWhiteSpace(ack.State)) slot.State = ack.State;
                        slot.Note = $"已下发，{delaySec}s 后开始";
                        Log?.Invoke($"槽位 {slot.Slot} 下发成功：runId={request.RunId}, delay={delaySec}s。");
                    }
                    else
                    {
                        lock (report) { report.Failed++; }
                        slot.Note = $"下发失败：{ack.Reason}";
                        Log?.Invoke($"槽位 {slot.Slot} 下发失败：{ack.Reason}");
                    }
                }
                catch (Exception ex)
                {
                    lock (report) { report.Failed++; }
                    slot.Note = $"下发异常：{ex.Message}";
                    Log?.Invoke($"槽位 {slot.Slot} 下发异常：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    slot.IsBusy = false;
                }
            });

        Log?.Invoke($"启动编排完成：{report.Summary}。");
        return report;
    }

    public async Task StopAsync(IReadOnlyList<SlotViewModel> slots, CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(
            slots.Where(s => _clients.ContainsKey(s.Slot)),
            new ParallelOptions { MaxDegreeOfParallelism = DispatchParallelism, CancellationToken = cancellationToken },
            async (slot, token) =>
            {
                try
                {
                    var stopped = await _clients[slot.Slot].StopAsync(token);
                    slot.Note = stopped ? "已停止" : "停止被拒";
                    Log?.Invoke($"槽位 {slot.Slot} 停止：{(stopped ? "ok" : "rejected")}。");
                }
                catch (Exception ex)
                {
                    slot.Note = $"停止异常：{ex.Message}";
                    Log?.Invoke($"槽位 {slot.Slot} 停止异常：{ex.Message}");
                }
            });
    }

    /// <summary>
    /// 清理一台设备的 media/（§8.9）。scope=media 只删大数据，scope=all 连元数据一起删。
    /// </summary>
    public async Task<AgingCleanupInfo?> CleanupAsync(SlotViewModel slot, string scope = "media", CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(slot.Slot, out var client))
        {
            slot.Note = "离线，无法清理";
            return null;
        }

        slot.IsBusy = true;
        try
        {
            var timeoutMs = Math.Max(_configuration.Cleanup.TimeoutSec, 60) * 1000;
            var info = await client.CleanupAsync(scope, timeoutMs, cancellationToken);
            slot.MediaPresent = info.MediaPresent;
            slot.Note = info.Ok
                ? $"清理完成（{info.BeforeFreeGiB:F1} → {info.AfterFreeGiB:F1} GB）"
                : "清理失败：现场仍存在";
            Log?.Invoke($"槽位 {slot.Slot} 清理：ok={info.Ok}, scope={scope}, {info.BeforeFreeGiB:F1} → {info.AfterFreeGiB:F1} GB。");
            return info;
        }
        catch (Exception ex)
        {
            slot.Note = $"清理异常：{ex.Message}";
            Log?.Invoke($"槽位 {slot.Slot} 清理异常：{ex.Message}");
            return null;
        }
        finally
        {
            slot.IsBusy = false;
        }
    }

    /// <summary>拉取单台结果。设备尚未产出结论时返回 null。</summary>
    public async Task<AgingRunResult?> FetchResultAsync(SlotViewModel slot, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(slot.Slot, out var client)) return null;

        try
        {
            return await client.GetResultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"槽位 {slot.Slot} 拉取结果失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 一轮状态轮询。监控是轻量 status，可以全量并行（§4.4）。
    /// 单台失败只影响该槽位，不打断其它槽位。
    /// </summary>
    public async Task PollOnceAsync(IReadOnlyList<SlotViewModel> slots, CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(
            slots.Where(s => s.Enabled && _clients.ContainsKey(s.Slot)),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _configuration.Station.MonitorParallelism),
                CancellationToken = cancellationToken
            },
            async (slot, token) =>
            {
                try
                {
                    var status = await _clients[slot.Slot].GetStatusAsync(token);
                    slot.ApplyStatus(status);
                }
                catch (Exception ex)
                {
                    slot.MarkOffline(ex.Message);
                }
            });
    }

    private AgingStartRequest BuildStartRequest(SlotViewModel slot, string batchStamp, int delaySec)
    {
        var items = new Dictionary<string, object?>();
        foreach (var item in _configuration.Items)
        {
            items[item.Key] = item.Value;
        }

        return new AgingStartRequest
        {
            RunId = $"run-{batchStamp}-s{slot.Slot:00}",
            Sn = slot.ExpectedSn ?? string.Empty,
            DurationSec = _configuration.Run.EffectiveDurationSec,
            StartDelaySec = delaySec,
            DebugMode = _configuration.Run.DebugMode,
            EnableLvgl = _configuration.Run.EnableLvgl,
            ReportIntervalMs = _configuration.Run.ReportIntervalMs,
            MaxResumeCount = _configuration.Run.MaxResumeCount,
            Items = items
        };
    }
}
