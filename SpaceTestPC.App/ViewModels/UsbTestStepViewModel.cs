namespace SpaceTestPC.App.ViewModels;

public sealed class UsbTestStepViewModel : ObservableObject
{
    private string _phase = "pending";
    private int _actualSpeedMbps;

    public UsbTestStepViewModel(int stepIndex, string port, string direction)
    {
        StepIndex = stepIndex;
        Port = port;
        Direction = direction;
    }

    public int StepIndex { get; }
    public string Port { get; }
    public string Direction { get; }
    public string StepLabel => $"第 {StepIndex} 步";
    public string PortLabel => Port == "port1" ? "接口 1" : "接口 2";
    public string DirectionLabel => Direction == "normal" ? "正插" : "反插";

    public string Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value)) RefreshVisualState();
        }
    }

    public int ActualSpeedMbps
    {
        get => _actualSpeedMbps;
        private set
        {
            if (SetProperty(ref _actualSpeedMbps, value))
            {
                RaisePropertyChanged(nameof(SpeedText));
                RaisePropertyChanged(nameof(DetailText));
            }
        }
    }

    public string Background => Phase switch
    {
        "completed" => "#ECFDF3",
        "detected" or "wait_remove" => "#EFF8FF",
        "failed" => "#FEF3F2",
        "wait_insert" => "#FFFAEB",
        _ => "#F2F4F7"
    };

    public string BorderBrush => Phase switch
    {
        "completed" => "#22C55E",
        "detected" or "wait_remove" => "#2E90FA",
        "failed" => "#F04438",
        "wait_insert" => "#F79009",
        _ => "#D0D5DD"
    };

    public string Foreground => Phase switch
    {
        "completed" => "#15803D",
        "detected" or "wait_remove" => "#175CD3",
        "failed" => "#B42318",
        "wait_insert" => "#B54708",
        _ => "#667085"
    };

    public string ActionSymbol => Phase switch
    {
        "completed" => "✓",
        "detected" or "wait_remove" => "●",
        "failed" => "×",
        "wait_insert" => "→",
        _ => "·"
    };

    public string StatusText => Phase switch
    {
        "completed" => "已完成",
        "detected" => "已插入",
        "wait_remove" => "请拔出",
        "failed" => "检测失败",
        "wait_insert" => "等待插入",
        _ => "等待测试"
    };

    public string SpeedText => ActualSpeedMbps > 0 ? $"{ActualSpeedMbps} Mbps" : "等待速率";
    public string DetailText => Phase is "detected" or "wait_remove" or "completed" && ActualSpeedMbps > 0
        ? SpeedText
        : DirectionLabel;

    public void SetState(string phase, int actualSpeedMbps = 0)
    {
        ActualSpeedMbps = actualSpeedMbps;
        Phase = phase;
    }

    public void Reset() => SetState("pending");

    private void RefreshVisualState()
    {
        RaisePropertyChanged(nameof(Background));
        RaisePropertyChanged(nameof(BorderBrush));
        RaisePropertyChanged(nameof(Foreground));
        RaisePropertyChanged(nameof(ActionSymbol));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(DetailText));
    }
}
