namespace SpaceTestPC.App.ViewModels;

public sealed class DirectionalKeyViewModel : ObservableObject
{
    private bool _isDetected;
    private bool _isChecking;
    private bool _isFailed;

    public DirectionalKeyViewModel(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }

    public bool IsDetected
    {
        get => _isDetected;
        set
        {
            if (SetProperty(ref _isDetected, value)) RefreshVisualState();
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetProperty(ref _isChecking, value)) RefreshVisualState();
        }
    }

    public bool IsFailed
    {
        get => _isFailed;
        set
        {
            if (SetProperty(ref _isFailed, value)) RefreshVisualState();
        }
    }

    public string Background => IsDetected ? "#16A34A" : IsFailed ? "#B42318" : IsChecking ? "#D97706" : "#94A3B8";
    public string StatusText => IsDetected ? "已识别" : IsFailed ? "失败" : IsChecking ? "检测中" : "等待按下";

    private void RefreshVisualState()
    {
        RaisePropertyChanged(nameof(Background));
        RaisePropertyChanged(nameof(StatusText));
    }
}
