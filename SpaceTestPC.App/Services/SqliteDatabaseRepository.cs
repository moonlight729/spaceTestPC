using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.Data.Sqlite;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class SqliteDatabaseRepository : IDatabaseRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDatabaseRepository(string databasePath)
    {
        _databasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }

    public async Task SaveSessionAsync(TestSessionRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO test_sessions(session_id, sn, start_time, end_time, final_verdict, board_id)
                VALUES($id, $sn, $start, $end, $verdict, $boardId)
                """, cancellationToken,
                ("$id", record.Session.SessionId), ("$sn", record.Session.Sn), ("$start", record.Session.StartTime.ToString("O")),
                ("$end", record.Session.EndTime?.ToString("O") ?? string.Empty), ("$verdict", record.Session.FinalVerdict), ("$boardId", record.BoardState?.BoardId ?? string.Empty));

            foreach (var result in record.TestResults)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO test_results(session_id, test_id, status, result_code, message, data_json)
                    VALUES($sessionId, $testId, $status, $code, $message, $data)
                    """, cancellationToken,
                    ("$sessionId", record.Session.SessionId), ("$testId", result.TestId), ("$status", result.Status),
                    ("$code", result.ResultCode), ("$message", result.Message), ("$data", JsonSerializer.Serialize(result.Data)));
            }

            await transaction.CommitAsync(cancellationToken);
            await ExportPendingCsvAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TestSessionRecord>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id, sn, start_time, end_time, final_verdict, board_id FROM test_sessions ORDER BY start_time DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", count);
            var rows = new List<SessionRow>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadSessionRow(reader));
            }
            var records = new List<TestSessionRecord>();
            foreach (var row in rows) records.Add(await CreateRecordAsync(connection, row, cancellationToken));
            return records;
        }
        finally { _gate.Release(); }
    }

    public async Task<TestSessionRecord?> GetLatestSessionBySnAsync(string sn, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id, sn, start_time, end_time, final_verdict, board_id FROM test_sessions WHERE sn = $sn ORDER BY start_time DESC LIMIT 1";
            command.Parameters.AddWithValue("$sn", sn);
            SessionRow? row = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken)) row = ReadSessionRow(reader);
            }
            return row is null ? null : await CreateRecordAsync(connection, row, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static SessionRow ReadSessionRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));

    private static async Task<TestSessionRecord> CreateRecordAsync(SqliteConnection connection, SessionRow row, CancellationToken cancellationToken)
    {
        var results = new List<TestResultRecord>();
        await using var resultCommand = connection.CreateCommand();
        resultCommand.CommandText = "SELECT test_id, status, result_code, message, data_json FROM test_results WHERE session_id = $sessionId ORDER BY id";
        resultCommand.Parameters.AddWithValue("$sessionId", row.SessionId);
        await using var resultReader = await resultCommand.ExecuteReaderAsync(cancellationToken);
        while (await resultReader.ReadAsync(cancellationToken))
        {
            results.Add(new TestResultRecord
            {
                TestId = resultReader.GetString(0),
                Status = resultReader.GetString(1),
                ResultCode = resultReader.GetInt32(2),
                Message = resultReader.IsDBNull(3) ? string.Empty : resultReader.GetString(3),
                Data = JsonSerializer.Deserialize<Dictionary<string, object?>>(resultReader.GetString(4)) ?? new Dictionary<string, object?>()
            });
        }

        return new TestSessionRecord
        {
            Session = new TestSession
            {
                SessionId = row.SessionId,
                Sn = row.Sn,
                StartTime = DateTimeOffset.Parse(row.StartTime),
                EndTime = string.IsNullOrEmpty(row.EndTime) ? null : DateTimeOffset.Parse(row.EndTime),
                FinalVerdict = row.FinalVerdict
            },
            BoardState = new BoardState { BoardId = row.BoardId },
            TestResults = results
        };
    }

    private sealed record SessionRow(string SessionId, string Sn, string StartTime, string EndTime, string FinalVerdict, string BoardId);

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS test_sessions (session_id TEXT PRIMARY KEY, sn TEXT NOT NULL, start_time TEXT NOT NULL, end_time TEXT, final_verdict TEXT NOT NULL, board_id TEXT);
            CREATE TABLE IF NOT EXISTS test_results (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, test_id TEXT NOT NULL, status TEXT NOT NULL, result_code INTEGER NOT NULL, message TEXT, data_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS csv_exports (session_id TEXT PRIMARY KEY, exported_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_test_sessions_sn ON test_sessions(sn);
            CREATE INDEX IF NOT EXISTS ix_test_results_session ON test_results(session_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExportPendingCsvAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.session_id, s.sn, s.start_time, s.end_time, s.final_verdict,
                       r.test_id, r.status, r.result_code, r.message, r.data_json
                FROM test_sessions s
                JOIN test_results r ON r.session_id = s.session_id
                LEFT JOIN csv_exports e ON e.session_id = s.session_id
                WHERE e.session_id IS NULL
                ORDER BY s.start_time, r.id
                """;
            var pendingRows = new List<CsvRow>();
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pendingRows.Add(new CsvRow(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.IsDBNull(8) ? string.Empty : reader.GetString(8), reader.GetString(9)));
                }
            }

            foreach (var sessionRows in pendingRows.GroupBy(row => row.SessionId))
            {
                await AppendSessionCsvAsync(sessionRows.ToArray(), cancellationToken);
                await using var markCommand = connection.CreateCommand();
                markCommand.CommandText = "INSERT INTO csv_exports(session_id, exported_at) VALUES($id, $exportedAt)";
                markCommand.Parameters.AddWithValue("$id", sessionRows.Key);
                markCommand.Parameters.AddWithValue("$exportedAt", DateTimeOffset.UtcNow.ToString("O"));
                await markCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task AppendSessionCsvAsync(IReadOnlyList<CsvRow> rows, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "records");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Regex.Replace(rows[0].Sn, "[^A-Za-z0-9_.-]", "_")}.csv");
        var writeHeader = !File.Exists(path);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(writeHeader));
        if (writeHeader) await writer.WriteLineAsync("session_id,sn,start_time,end_time,final_verdict,test_id,status,result_code,message,data_json");
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', Escape(row.SessionId), Escape(row.Sn), Escape(row.StartTime), Escape(row.EndTime), Escape(row.FinalVerdict), Escape(row.TestId), Escape(row.Status), row.ResultCode, Escape(row.Message), Escape(row.DataJson)));
        }
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record CsvRow(string SessionId, string Sn, string StartTime, string EndTime, string FinalVerdict, string TestId, string Status, int ResultCode, string Message, string DataJson);
}
