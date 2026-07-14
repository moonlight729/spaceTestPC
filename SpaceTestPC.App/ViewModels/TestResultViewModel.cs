using System.Collections.ObjectModel;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.ViewModels;

public sealed class TestResultViewModel : ObservableObject
{
    private TestItemState _state = TestItemState.Pending;
    private int _resultCode;
    private string _message = "Waiting";
    private string _dataText = "No result data";
    private DateTimeOffset? _startedAt;
    private TimeSpan? _duration;

    public TestResultViewModel(string testId, string displayName)
    {
        TestId = testId;
        DisplayName = displayName;
    }

    public string TestId { get; }
    public string DisplayName { get; }
    public TestItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StateLabel));
                RaisePropertyChanged(nameof(StateBrush));
            }
        }
    }

    public int ResultCode { get => _resultCode; private set => SetProperty(ref _resultCode, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string DataText { get => _dataText; private set => SetProperty(ref _dataText, value); }
    public DateTimeOffset? StartedAt { get => _startedAt; private set => SetProperty(ref _startedAt, value); }
    public TimeSpan? Duration { get => _duration; private set => SetProperty(ref _duration, value); }

    public string StateLabel => State switch
    {
        TestItemState.Running => "TESTING",
        TestItemState.Passed => "PASS",
        TestItemState.Failed => "FAIL",
        _ => "PENDING"
    };

    public string StateBrush => State switch
    {
        TestItemState.Running => "#2563EB",
        TestItemState.Passed => "#16A34A",
        TestItemState.Failed => "#DC2626",
        _ => "#94A3B8"
    };

    public void Apply(TestSessionEvent testEvent)
    {
        if (testEvent.Status == "running")
        {
            StartedAt = testEvent.Timestamp;
        }

        State = testEvent.Status switch
        {
            "running" => TestItemState.Running,
            "passed" => TestItemState.Passed,
            _ => TestItemState.Failed
        };
        ResultCode = testEvent.ResultCode;
        Message = testEvent.Message;
        DataText = testEvent.Data.Count == 0
            ? "No result data"
            : string.Join(Environment.NewLine, testEvent.Data.Select(pair => $"{pair.Key}: {pair.Value}"));
        if (StartedAt is { } startedAt && State is TestItemState.Passed or TestItemState.Failed)
        {
            Duration = testEvent.Timestamp - startedAt;
        }
    }

    public void Reset()
    {
        State = TestItemState.Pending;
        ResultCode = 0;
        Message = "Waiting";
        DataText = "No result data";
        StartedAt = null;
        Duration = null;
    }
}
