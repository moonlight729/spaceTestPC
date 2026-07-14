namespace SpaceTestPC.App.ViewModels;

public sealed class DirectionalKeyViewModel : ObservableObject
{
    private bool _isDetected;

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
            if (SetProperty(ref _isDetected, value))
            {
                RaisePropertyChanged(nameof(Background));
                RaisePropertyChanged(nameof(StatusText));
            }
        }
    }

    public string Background => IsDetected ? "#16A34A" : "#94A3B8";
    public string StatusText => IsDetected ? "已识别" : "等待按下";
}
