namespace SpaceTestPC.App.Services;

public sealed class LogService : ILogService
{
    private readonly List<string> _entries = [];
    private readonly object _sync = new();

    public void Info(string message)
    {
        lock (_sync)
        {
            _entries.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss} {message}");
            if (_entries.Count > 200)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }
}
