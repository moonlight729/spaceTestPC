using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.ViewModels;

public sealed class TestItemViewModel : ObservableObject
{
    private TestItemState _state;

    public TestItemViewModel(string testId, string name, bool showsConnector = true)
    {
        TestId = testId;
        Name = name;
        ShowsConnector = showsConnector;
    }

    public string TestId { get; }
    public string Name { get; }
    public bool ShowsConnector { get; }

    public TestItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StateLabel));
                RaisePropertyChanged(nameof(StateBrush));
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

    public string ConnectorBrush => State switch
    {
        TestItemState.Running => "#60A5FA",
        TestItemState.Passed => "#22C55E",
        TestItemState.Failed => "#EF4444",
        TestItemState.Skipped => "#94A3B8",
        _ => "#334155"
    };
}
