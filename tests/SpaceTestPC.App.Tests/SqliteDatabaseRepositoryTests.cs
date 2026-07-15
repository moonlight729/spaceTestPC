using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;
using Xunit;

namespace SpaceTestPC.App.Tests;

public sealed class SqliteDatabaseRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpaceTestPC.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveSessionAsync_PersistsSessionAndExportsCsv()
    {
        var databasePath = Path.Combine(_root, "stage1.db");
        var repository = new SqliteDatabaseRepository(databasePath);

        await repository.SaveSessionAsync(CreateRecord("session-1", "SN-001"));

        var session = Assert.Single(await repository.GetRecentSessionsAsync(1));
        Assert.Equal("SN-001", session.Session.Sn);
        var result = Assert.Single(session.TestResults);
        Assert.Equal("wifi", result.TestId);
        Assert.Equal("PASS", result.Status);
        Assert.True(Assert.IsType<System.Text.Json.JsonElement>(result.Data["pingOk"]).GetBoolean());
        var history = await repository.GetLatestSessionBySnAsync("SN-001");
        Assert.NotNull(history);
        Assert.Equal("wifi", Assert.Single(history.TestResults).TestId);
        var csv = await File.ReadAllTextAsync(Path.Combine(_root, "records", "SN-001.csv"));
        Assert.Contains("session-1", csv);
        Assert.Contains("wifi", csv);
    }

    [Fact]
    public async Task SaveSessionAsync_RetriesCsvExportAfterTemporaryFileLock()
    {
        var databasePath = Path.Combine(_root, "stage1.db");
        var repository = new SqliteDatabaseRepository(databasePath);
        Directory.CreateDirectory(Path.Combine(_root, "records"));
        var lockedCsv = Path.Combine(_root, "records", "LOCKED-SN.csv");

        await using (new FileStream(lockedCsv, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await repository.SaveSessionAsync(CreateRecord("session-locked", "LOCKED-SN"));
        }

        await repository.SaveSessionAsync(CreateRecord("session-next", "NEXT-SN"));

        var lockedCsvText = await File.ReadAllTextAsync(lockedCsv);
        Assert.Contains("session-locked", lockedCsvText);
        Assert.Contains("wifi", lockedCsvText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static TestSessionRecord CreateRecord(string sessionId, string sn) => new()
    {
        Session = new TestSession
        {
            SessionId = sessionId,
            Sn = sn,
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow,
            FinalVerdict = "Pass"
        },
        TestResults =
        [
            new TestResultRecord
            {
                TestId = "wifi", Status = "PASS", ResultCode = 0, Message = "ok",
                Data = new Dictionary<string, object?> { ["pingOk"] = true }
            }
        ]
    };
}
