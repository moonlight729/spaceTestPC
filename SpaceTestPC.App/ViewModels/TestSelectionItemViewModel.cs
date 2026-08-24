namespace SpaceTestPC.App.ViewModels;

public sealed class TestSelectionItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public TestSelectionItemViewModel(string testId, string displayName, bool isSelected, Action selectionChanged)
    {
        TestId = testId;
        DisplayName = displayName;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public string TestId { get; }
    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }
}
