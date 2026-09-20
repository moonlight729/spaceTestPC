using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using SpaceTestPC.AgingStation.Models;
using SpaceTestPC.AgingStation.Services;
using SpaceTestPC.Core.Models;
using SpaceTestPC.Core.Services;

namespace SpaceTestPC.AgingStation.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly AgingStationConfiguration _configuration;
    private readonly AgingStationCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _pollCts;

    private bool _isMonitoring;
    private string _summaryText = "未开始";
    private string _statusMessage = string.Empty;

    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    public ObservableCollection<string> Logs { get; } = new();

    /// <summary>由窗口注入的确认回调。VM 不直接弹 MessageBox，保持可测试。</summary>
    public Func<string, bool>? Confirm { get; set; }

    public AgingStationConfiguration Configuration => _configuration;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                OnPropertyChanged(nameof(MonitoringButtonText));
            }
        }
    }

    public string MonitoringButtonText => IsMonitoring ? "停止监控" : "开始监控";

    public string DurationText =>
        $"{(_configuration.Run.DebugMode ? "调试时长" : "总时长")} {AgingDuration.Format(_configuration.Run.EffectiveDurationSec)}";

    public AsyncRelayCommand StartAllCommand { get; }
    public AsyncRelayCommand StartSelectedCommand { get; }
    public AsyncRelayCommand<SlotViewModel> StartSlotCommand { get; }
    public AsyncRelayCommand StopSelectedCommand { get; }
    public AsyncRelayCommand StopAllCommand { get; }
    public AsyncRelayCommand<SlotViewModel> CleanupMediaCommand { get; }
    public AsyncRelayCommand<SlotViewModel> CleanupAllCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportReportCommand { get; }
    public RelayCommand ToggleMonitoringCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _configuration = new ConfigurationService().LoadSection<AgingStationConfiguration>(path);

        foreach (var slot in _configuration.Station.Slots.OrderBy(s => s.Slot))
        {
            Slots.Add(new SlotViewModel(slot, _configuration.Station.DevicePort, _configuration.Station.CommandTimeoutMs));
        }

        _coordinator = new AgingStationCoordinator(_configuration);
        _coordinator.Log += AppendLog;

        StartAllCommand = new AsyncRelayCommand(() => StartAsync(Slots.ToList(), true));
        StartSelectedCommand = new AsyncRelayCommand(() => StartAsync(SelectedSlots().ToList(), true));
        StartSlotCommand = new AsyncRelayCommand<SlotViewModel>(slot => slot is null ? Task.CompletedTask : StartAsync(new[] { slot }, false));
        StopSelectedCommand = new AsyncRelayCommand(() => StopAsync(SelectedSlots().ToList()));
        StopAllCommand = new AsyncRelayCommand(() => StopAsync(Slots.ToList(), requireConfirmation: true));
        CleanupMediaCommand = new AsyncRelayCommand<SlotViewModel>(slot => CleanupAsync(slot, "media"));
        CleanupAllCommand = new AsyncRelayCommand<SlotViewModel>(slot => CleanupAsync(slot, "all"));
        RefreshCommand = new AsyncRelayCommand(PollOnceAsync);
        ExportReportCommand = new AsyncRelayCommand(ExportReportAsync);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        SelectAllCommand = new RelayCommand(() =>
        {
            foreach (var slot in Slots) slot.IsSelected = true;
        });
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var slot in Slots) slot.IsSelected = false;
        });

        AppendLog($"配置已加载：{Slots.Count} 个槽位，设备端口 {_configuration.Station.DevicePort}。");
        UpdateSummary();
    }

    private IEnumerable<SlotViewModel> SelectedSlots() => Slots.Where(s => s.IsSelected);

    private async Task StartAsync(IReadOnlyList<SlotViewModel> slots, bool isBatch)
    {
        if (slots.Count == 0)
        {
            StatusMessage = "未选择槽位";
            return;
        }

        StatusMessage = isBatch ? $"正在下发启动指令（{slots.Count} 台）…" : "正在下发启动指令…";
        var report = await _coordinator.StartAsync(slots, isBatch);

        // 启动后立刻拉一次状态，避免等到下一个轮询周期才看到 PREPARING。
        await PollOnceAsync();
        StatusMessage = report.Summary;
        UpdateSummary();
    }

    private async Task StopAsync(IReadOnlyList<SlotViewModel> slots, bool requireConfirmation = false)
    {
        if (slots.Count == 0)
        {
            StatusMessage = "未选择槽位";
            return;
        }

        // "全部停止"是维护操作，必须二次确认（§5.5）。
        if (requireConfirmation && Confirm is not null &&
            !Confirm($"确认停止全部 {slots.Count} 台设备的老化？\n停止后已跑时长不会清零，但设备不再继续计时。"))
        {
            StatusMessage = "已取消";
            return;
        }

        StatusMessage = $"正在停止 {slots.Count} 台…";
        await _coordinator.StopAsync(slots);
        await PollOnceAsync();
        StatusMessage = "停止指令已下发";
        UpdateSummary();
    }

    private async Task CleanupAsync(SlotViewModel? slot, string scope)
    {
        if (slot is null) return;

        var action = scope == "all" ? "复位（清理 media + 元数据）" : "清理 media";
        if (Confirm is not null && !Confirm($"确认对槽位 {slot.Slot} 执行「{action}」？"))
        {
            return;
        }

        var info = await _coordinator.CleanupAsync(slot, scope);
        if (info is not null)
        {
            slot.RecordDisposal(action, Environment.UserName);
        }

        StatusMessage = slot.Note;
        UpdateSummary();
    }

    private async Task PollOnceAsync()
    {
        await _coordinator.PollOnceAsync(Slots.ToList());
        UpdateSummary();
    }

    private void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            _pollCts?.Cancel();
            _pollCts = null;
            IsMonitoring = false;
            AppendLog("监控已停止。");
            return;
        }

        _pollCts = new CancellationTokenSource();
        IsMonitoring = true;
        AppendLog($"监控已启动：每 {_configuration.Station.PollIntervalMs / 1000.0:F0}s 轮询一次。");
        _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _coordinator.PollOnceAsync(Slots.ToList(), token);
                _dispatcher.BeginInvoke(UpdateSummary);
            }
            catch (Exception ex)
            {
                AppendLog($"轮询异常：{ex.Message}");
            }

            try
            {
                await Task.Delay(_configuration.Station.PollIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ExportReportAsync()
    {
        StatusMessage = "正在汇总结果…";

        var results = new Dictionary<int, SpaceTestPC.Core.Models.AgingRunResult?>();
        await Parallel.ForEachAsync(
            Slots.ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (slot, token) =>
            {
                var result = await _coordinator.FetchResultAsync(slot, token);
                lock (results)
                {
                    results[slot.Slot] = result;
                }

                if (result is not null && !string.IsNullOrWhiteSpace(result.Verdict))
                {
                    slot.Verdict = result.Verdict;
                }
            });

        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        var (markdownPath, csvPath) = AgingReportWriter.Write(outputDirectory, _configuration, Slots.ToList(), results);

        AppendLog($"报告已导出：{markdownPath}");
        StatusMessage = $"报告已导出到 {outputDirectory}";
    }

    private void UpdateSummary()
    {
        var running = Slots.Count(s => s.State == AgingRunStates.Running);
        var preparing = Slots.Count(s => s.State == AgingRunStates.Preparing);
        var waiting = Slots.Count(s => s.State == AgingRunStates.WaitingManualCheck);
        var passed = Slots.Count(s => s.State == AgingRunStates.Passed);
        var failed = Slots.Count(s => s.State == AgingRunStates.Failed);

        SummaryText = $"准备 {preparing} · 运行 {running} · 待终检 {waiting} · 通过 {passed} · 失败 {failed}";
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _dispatcher.BeginInvoke(() =>
        {
            Logs.Add(line);
            while (Logs.Count > MaxLogLines)
            {
                Logs.RemoveAt(0);
            }
        });
    }
}
