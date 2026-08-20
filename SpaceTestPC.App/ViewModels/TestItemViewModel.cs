using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.ViewModels;

public sealed class TestItemViewModel : ObservableObject
{
    private TestItemState _state;
    private bool _canRetest;
    private bool _isRetesting;

    public TestItemViewModel(string testId, string name, bool showsConnector = true)
    {
        TestId = testId;
        Name = name;
        ShowsConnector = showsConnector;
    }

    public string TestId { get; }
    public string Name { get; }
    public bool ShowsConnector { get; }

    public bool CanRetest
    {
        get => _canRetest;
        set => SetProperty(ref _canRetest, value);
    }

    public bool IsRetesting
    {
        get => _isRetesting;
        set
        {
            if (SetProperty(ref _isRetesting, value))
            {
                RaisePropertyChanged(nameof(RetestButtonText));
            }
        }
    }

    public string RetestButtonText => IsRetesting ? "复测中" : "重新测试";

    public TestItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StateLabel));
                RaisePropertyChanged(nameof(StateBrush));
                RaisePropertyChanged(nameof(BackgroundBrush));
                RaisePropertyChanged(nameof(BorderBrush));
                RaisePropertyChanged(nameof(AccentBrush));
                RaisePropertyChanged(nameof(ConnectorBrush));
            }
        }
    }

    public string StateLabel => State switch
    {
        TestItemState.Running => "TESTING",
        TestItemState.Passed => "PASS",
        TestItemState.Failed => "FAIL",
        TestItemState.Skipped => "SKIPPED",
        _ => "PENDING"
    };

    public string StateBrush => State switch
    {
        TestItemState.Running => "#3B82F6",
        TestItemState.Passed => "#22C55E",
        TestItemState.Failed => "#EF4444",
        TestItemState.Skipped => "#64748B",
        _ => "#6B7280"
    };

    public string BackgroundBrush => State switch
    {
        TestItemState.Running => "#EFF8FF",
        TestItemState.Passed => "#ECFDF3",
        TestItemState.Failed => "#FEF3F2",
        TestItemState.Skipped => "#FFF7ED",
        _ => "#F8FAFC"
    };

    public string BorderBrush => State switch
    {
        TestItemState.Running => "#84CAFF",
        TestItemState.Passed => "#86EFAC",
        TestItemState.Failed => "#FDA29B",
        TestItemState.Skipped => "#FED7AA",
        _ => "#E4E7EC"
    };

    public string AccentBrush => State switch
    {
        TestItemState.Running => "#2E90FA",
        TestItemState.Passed => "#22C55E",
        TestItemState.Failed => "#EF4444",
        TestItemState.Skipped => "#F59E0B",
        _ => "#CBD5E1"
    };

    public string ConnectorBrush => State switch
    {
        TestItemState.Running => "#60A5FA",
        TestItemState.Passed => "#22C55E",
        TestItemState.Failed => "#EF4444",
        TestItemState.Skipped => "#94A3B8",
        _ => "#334155"
    };
}
