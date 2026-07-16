using System.IO;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class LogService : ILogService
{
    private readonly List<string> _entries = [];
    private readonly object _sync = new();
    private readonly bool _fileEnabled;
    private readonly string _filePath;

    public LogService(LoggingConfiguration? configuration = null)
    {
        configuration ??= new LoggingConfiguration();
        _fileEnabled = configuration.FileEnabled;
        _filePath = ResolveLogPath(configuration.FilePath);
    }

    public string FilePath => _filePath;

    public void Info(string message)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        lock (_sync)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > 200)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            if (_fileEnabled)
            {
                WriteFileEntry(entry);
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

    private void WriteFileEntry(string entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_filePath, entry + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the production test flow.
        }
    }

    private static string ResolveLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "logs/space-test-pc.log";
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }
}
