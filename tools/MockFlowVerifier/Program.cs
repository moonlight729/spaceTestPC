using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;

var root = Path.Combine(Path.GetTempPath(), "SpaceTestPC-MockFlowVerifier", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await VerifyAsync(root, "PASS-SN", null, "Pass");
    await VerifyAsync(root, "FAIL-SN", "wifi", "Fail");
    Console.WriteLine("Mock flow verification passed: success and failure paths both persisted.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task VerifyAsync(string root, string sn, string? failingTestId, string expectedVerdict)
{
    var databasePath = Path.Combine(root, failingTestId ?? "pass", "stage1-db.json");
    var repository = new FileDatabaseRepository(databasePath);
    var viewModel = new MainViewModel(
        new ScannerService(),
        new PcbaCommandClientFactory(new MockPcbaCommandClient(failingTestId), new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService());

    viewModel.ScannerInput = sn;
    viewModel.ScanCommand.Execute(null);

    var record = await WaitForRecordAsync(repository);
    if (record.Session.FinalVerdict != expectedVerdict)
    {
        throw new InvalidOperationException($"Expected {expectedVerdict}, got {record.Session.FinalVerdict}.");
    }

    if (!File.Exists(Path.ChangeExtension(databasePath, ".csv")))
    {
        throw new InvalidOperationException("CSV summary was not created.");
    }

    if (failingTestId is null && viewModel.TestItems.Any(item => item.State != TestItemState.Passed))
    {
        throw new InvalidOperationException("The successful Mock session did not pass every test item.");
    }

    if (viewModel.TestResults.Count != 15)
    {
        throw new InvalidOperationException($"Expected 15 test detail items, got {viewModel.TestResults.Count}.");
    }

    var bluetoothResult = viewModel.TestResults.First(item => item.TestId == "bluetooth");
    if (!bluetoothResult.DataText.Contains("targetName: NODE_A_01", StringComparison.Ordinal) ||
        !bluetoothResult.DataText.Contains("rssi: -62", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Bluetooth detail data was not displayed.");
    }

    if (failingTestId is not null && viewModel.TestItems.Skip(3).Any(item => item.State != TestItemState.Pending))
    {
        throw new InvalidOperationException("Tests after the failing WiFi test were executed.");
    }

    if (failingTestId is not null && viewModel.TestItems[2].State != TestItemState.Failed)
    {
        throw new InvalidOperationException("The injected WiFi failure was not reflected in the UI state.");
    }
}

static async Task<TestSessionRecord> WaitForRecordAsync(IDatabaseRepository repository)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    while (!timeout.IsCancellationRequested)
    {
        var sessions = await repository.GetRecentSessionsAsync(1, timeout.Token);
        if (sessions.Count == 1)
        {
            return sessions[0];
        }

        await Task.Delay(100, timeout.Token);
    }

    throw new TimeoutException("Timed out waiting for the session record.");
}
