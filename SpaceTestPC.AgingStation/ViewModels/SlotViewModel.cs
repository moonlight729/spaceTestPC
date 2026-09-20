using SpaceTestPC.AgingStation.Models;
using SpaceTestPC.Core.Models;

namespace SpaceTestPC.AgingStation.ViewModels;

/// <summary>
/// 单个槽位的实时视图。老化是长跑，界面上最有价值的三个数是：
/// 状态、已跑有效时长、剩余时长。
/// </summary>
public sealed class SlotViewModel : ObservableObject
{
    private string _state = AgingRunStates.Idle;
    private string? _sn;
    private string? _failReason;
    private string? _lastError;
    private int _effectiveSec;
    private int _remainingSec;
    private int _prepareRemainingSec;
    private int _resumeCount;
    private int _lostTimeSec;
    private bool _isSelected;
    private bool _isBusy;
    private bool _mediaPresent;
    private long _mediaBytes;
    private string _verdict = string.Empty;
    private string? _runId;
    private string? _startedAtUtc;
    private DateTimeOffset? _plannedStartAtUtc;
    private string _note = string.Empty;
    private string _disposalAction = string.Empty;
    private string? _disposedBy;
    private string? _disposalAtUtc;

    public SlotViewModel(AgingSlotConfiguration configuration, int devicePort, int commandTimeoutMs)
    {
        Slot = configuration.Slot;
        Name = string.IsNullOrWhiteSpace(configuration.Name) ? $"工位{configuration.Slot:00}" : configuration.Name;
        Ip = configuration.Ip;
        ExpectedSn = configuration.ExpectedSn;
        Enabled = configuration.Enabled;

        DevicePort = devicePort;
        CommandTimeoutMs = commandTimeoutMs;
    }

    public int Slot { get; }
    public string Name { get; }
    public string Ip { get; }
    public string ExpectedSn { get; }
    public bool Enabled { get; }
    public int DevicePort { get; }
    public int CommandTimeoutMs { get; }

    /// <summary>勾选状态，用于"开始选中"。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrushKey));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public string? Sn
    {
        get => _sn;
        set => SetProperty(ref _sn, value);
    }

    public string? FailReason
    {
        get => _failReason;
        set => SetProperty(ref _failReason, value);
    }

    public string? LastError
    {
        get => _lastError;
        set => SetProperty(ref _lastError, value);
    }

    public int EffectiveSec
    {
        get => _effectiveSec;
        set
        {
            if (SetProperty(ref _effectiveSec, value))
            {
                OnPropertyChanged(nameof(EffectiveText));
            }
        }
    }

    public int RemainingSec
    {
        get => _remainingSec;
        set
        {
            if (SetProperty(ref _remainingSec, value))
            {
                OnPropertyChanged(nameof(RemainingText));
            }
        }
    }

    public int PrepareRemainingSec
    {
        get => _prepareRemainingSec;
        set => SetProperty(ref _prepareRemainingSec, value);
    }

    public int ResumeCount
    {
        get => _resumeCount;
        set => SetProperty(ref _resumeCount, value);
    }

    public int LostTimeSec
    {
        get => _lostTimeSec;
        set => SetProperty(ref _lostTimeSec, value);
    }

    /// <summary>现场是否还在（§12.1：上位机靠它判断能否取证）。</summary>
    public bool MediaPresent
    {
        get => _mediaPresent;
        set => SetProperty(ref _mediaPresent, value);
    }

    public long MediaBytes
    {
        get => _mediaBytes;
        set
        {
            if (SetProperty(ref _mediaBytes, value))
            {
                OnPropertyChanged(nameof(MediaText));
            }
        }
    }

    public string Verdict
    {
        get => _verdict;
        set => SetProperty(ref _verdict, value);
    }

    public string? RunId
    {
        get => _runId;
        set => SetProperty(ref _runId, value);
    }

    /// <summary>设备实际开始时刻。批量错开后同批次必然有差异，不记录就无法解释（§11）。</summary>
    public string? StartedAtUtc
    {
        get => _startedAtUtc;
        set => SetProperty(ref _startedAtUtc, value);
    }

    /// <summary>本批次下发给该槽位的计划开始时刻。</summary>
    public DateTimeOffset? PlannedStartAtUtc
    {
        get => _plannedStartAtUtc;
        set => SetProperty(ref _plannedStartAtUtc, value);
    }

    /// <summary>操作提示，如"未赶上批次""离线未启动"。</summary>
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    /// <summary>处置方式：判FAIL转返修 / 现场分析 / 复测（§12.1）。</summary>
    public string DisposalAction
    {
        get => _disposalAction;
        set => SetProperty(ref _disposalAction, value);
    }

    public string? DisposedBy
    {
        get => _disposedBy;
        set => SetProperty(ref _disposedBy, value);
    }

    public string? DisposalAtUtc
    {
        get => _disposalAtUtc;
        set => SetProperty(ref _disposalAtUtc, value);
    }

    /// <summary>记录一次人工处置。不留痕就没有处置（§12.1 第 4 步）。</summary>
    public void RecordDisposal(string action, string disposedBy)
    {
        DisposalAction = action;
        DisposedBy = disposedBy;
        DisposalAtUtc = DateTimeOffset.UtcNow.ToString("O");
    }

    public bool IsFailed => string.Equals(State, AgingRunStates.Failed, StringComparison.OrdinalIgnoreCase);

    public string StateText => State switch
    {
        AgingRunStates.Idle => "空闲",
        AgingRunStates.Preparing => PrepareRemainingSec > 0 ? $"准备中 {PrepareRemainingSec}s" : "准备中",
        AgingRunStates.Running => "运行中",
        AgingRunStates.Cleaning => "清理中",
        AgingRunStates.WaitingManualCheck => "待终检",
        AgingRunStates.Passed => "通过",
        AgingRunStates.Failed => "失败",
        _ => string.IsNullOrWhiteSpace(State) ? "未知" : State
    };

    /// <summary>状态色键，XAML 里用 StaticResource 绑定。</summary>
    public string StateBrushKey => State switch
    {
        AgingRunStates.Running => "RunningBrush",
        AgingRunStates.Preparing => "PreparingBrush",
        AgingRunStates.Cleaning => "CleaningBrush",
        AgingRunStates.WaitingManualCheck => "WaitingBrush",
        AgingRunStates.Passed => "PassedBrush",
        AgingRunStates.Failed => "FailedBrush",
        _ => "IdleBrush"
    };

    public string EffectiveText => AgingDuration.Format(EffectiveSec);

    public string RemainingText => RemainingSec > 0 ? AgingDuration.Format(RemainingSec) : "--";

    public string MediaText => !MediaPresent
        ? "无"
        : MediaBytes >= 1024L * 1024 * 1024
            ? $"{MediaBytes / (1024.0 * 1024 * 1024):F1} GB"
            : $"{MediaBytes / (1024.0 * 1024):F0} MB";

    public void ApplyStatus(AgingStatusSnapshot snapshot)
    {
        State = snapshot.State;
        if (!string.IsNullOrWhiteSpace(snapshot.Sn)) Sn = snapshot.Sn;
        if (!string.IsNullOrWhiteSpace(snapshot.RunId)) RunId = snapshot.RunId;
        FailReason = snapshot.FailReason;
        LastError = snapshot.LastError;
        EffectiveSec = snapshot.EffectiveSec;
        RemainingSec = snapshot.RemainingSec;
        PrepareRemainingSec = snapshot.PrepareRemainingSec;
        ResumeCount = snapshot.ResumeCount;
        LostTimeSec = snapshot.LostTimeSec;
        MediaPresent = snapshot.MediaPresent;
        MediaBytes = snapshot.MediaBytes;
        if (!string.IsNullOrWhiteSpace(snapshot.StartedAtUtc)) StartedAtUtc = snapshot.StartedAtUtc;
    }

    public void MarkOffline(string reason)
    {
        LastError = reason;
        Note = "离线";
    }
}
