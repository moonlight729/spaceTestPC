using System.IO;
using System.Text;
using System.Text.Json;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class FileDatabaseRepository : IDatabaseRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDatabaseRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task SaveSessionAsync(TestSessionRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            current.Insert(0, record);
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var stream = File.Create(_databasePath);
            await JsonSerializer.SerializeAsync(stream, current, JsonOptions, cancellationToken);
            await AppendCsvAsync(record, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TestSessionRecord>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            return current.Take(count).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<TestSessionRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_databasePath);
        return await JsonSerializer.DeserializeAsync<List<TestSessionRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task AppendCsvAsync(TestSessionRecord record, CancellationToken cancellationToken)
    {
        var csvPath = Path.ChangeExtension(_databasePath, ".csv");
        var writeHeader = !File.Exists(csvPath);
        await using var stream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: writeHeader));

        if (writeHeader)
        {
            await writer.WriteLineAsync("session_id,sn,start_time,end_time,verdict,board_id");
        }

        var session = record.Session;
        var row = string.Join(',',
            Escape(session.SessionId),
            Escape(session.Sn),
            Escape(session.StartTime.ToString("O")),
            Escape(session.EndTime?.ToString("O") ?? string.Empty),
            Escape(session.FinalVerdict),
            Escape(record.BoardState?.BoardId ?? string.Empty));
        await writer.WriteLineAsync(row);
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
