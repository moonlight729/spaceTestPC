using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "SpaceTestPC-MockFlowVerifier", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    await VerifyAsync(root, "PASS-SN", null, "Pass");
    await VerifyAsync(root, "FAIL-SN", "wifi", "Fail");
    await VerifyFilteredPlanAsync(root);
    Console.WriteLine("Mock flow verification passed: success and failure paths both persisted.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task VerifyAsync(string root, string sn, string? failingTestId, string expectedVerdict)
{
    var databasePath = Path.Combine(root, failingTestId ?? "pass", "stage1-db.json");
    var configuration = CreateFastConfiguration();
    var repository = new FileDatabaseRepository(databasePath);
    var manualTestInteractionService = new ManualTestInteractionService();
    var viewModel = new MainViewModel(
        new ScannerService(),
        new PcbaCommandClientFactory(
            new MockPcbaCommandClient(failingTestId, configuration.TestPlan.Mock, manualTestInteractionService),
            new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService(),
        configuration,
        manualTestInteractionService);

    viewModel.ScannerInput = sn;
    viewModel.ScanCommand.Execute(null);
    _ = AutoConfirmManualTestsAsync(viewModel);

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

    if (viewModel.TestResults.Count != 14)
    {
        throw new InvalidOperationException($"Expected 14 test detail items, got {viewModel.TestResults.Count}.");
    }

    if (failingTestId is null && viewModel.DirectionalKeys.Any(key => !key.IsDetected))
    {
        throw new InvalidOperationException("Mock key events did not light every directional key in the UI.");
    }

    var boardStateResult = viewModel.TestResults.First(item => item.TestId == "board_state");
    if (!boardStateResult.DataText.Contains("ubootVersion: 2024.01", StringComparison.Ordinal) ||
        !boardStateResult.DataText.Contains("kernelVersion: 6.1.0", StringComparison.Ordinal) ||
        !boardStateResult.DataText.Contains("rootfsVersion: 1.0.3", StringComparison.Ordinal) ||
        !boardStateResult.DataText.Contains("lastTestVerdict: Pass", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Board state detail data was not displayed.");
    }

    var bluetoothResult = viewModel.TestResults.First(item => item.TestId == "bluetooth");
    if (failingTestId is null &&
        (!bluetoothResult.DataText.Contains("targetName: NODE_A_01", StringComparison.Ordinal) ||
         !bluetoothResult.DataText.Contains("rssi: -62", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("Bluetooth detail data was not displayed.");
    }

    if (failingTestId is not null && viewModel.TestItems.Skip(5).Any(item => item.State != TestItemState.Pending))
    {
        throw new InvalidOperationException("Tests after the failing WiFi test were executed.");
    }

    if (failingTestId is not null && viewModel.TestItems[4].State != TestItemState.Failed)
    {
        throw new InvalidOperationException("The injected WiFi failure was not reflected in the UI state.");
    }
}

static async Task VerifyFilteredPlanAsync(string root)
{
    var databasePath = Path.Combine(root, "filtered", "stage1-db.json");
    var configuration = CreateFastConfiguration();
    configuration.BluetoothBroadcaster.BroadcastName = "VerifierBeacon";
    configuration.TestPlan.EnabledTests = ["board_state", "wifi", "bluetooth", "fingerprint"];
    configuration.TestPlan.TestParameters["wifi"] = new Dictionary<string, JsonElement>
    {
        ["ssid"] = JsonSerializer.SerializeToElement("VerifierRouter"),
        ["routerIp"] = JsonSerializer.SerializeToElement("10.20.30.1"),
        ["pingCount"] = JsonSerializer.SerializeToElement(6)
    };
    configuration.TestPlan.TestParameters["bluetooth"] = new Dictionary<string, JsonElement>
    {
        ["targetName"] = JsonSerializer.SerializeToElement("VerifierBeacon"),
        ["minRssi"] = JsonSerializer.SerializeToElement(-75),
        ["scanWindowMs"] = JsonSerializer.SerializeToElement(8000)
    };
    configuration.TestPlan.TestParameters["fingerprint"] = new Dictionary<string, JsonElement>
    {
        ["bus"] = JsonSerializer.SerializeToElement("spi"),
        ["command"] = JsonSerializer.SerializeToElement("get_status"),
        ["timeoutMs"] = JsonSerializer.SerializeToElement(2500)
    };
    var repository = new FileDatabaseRepository(databasePath);
    var viewModel = new MainViewModel(
        new ScannerService(),
        new PcbaCommandClientFactory(new MockPcbaCommandClient(mockConfiguration: configuration.TestPlan.Mock), new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService(),
        configuration);

    viewModel.ScannerInput = "FILTERED-SN";
    viewModel.ScanCommand.Execute(null);
    _ = await WaitForRecordAsync(repository);

    if (viewModel.TestResults.Select(item => item.TestId).ToArray() is not ["board_state", "wifi", "bluetooth", "fingerprint"])
    {
        throw new InvalidOperationException("Filtered test plan did not keep board_state and WiFi only.");
    }

    if (viewModel.TestItems.Count != 4 || viewModel.TestOverviewColumns != 4)
    {
        throw new InvalidOperationException("Filtered UI overview was not reduced to the enabled tests.");
    }

    var wifiResult = viewModel.TestResults.Single(item => item.TestId == "wifi");
    if (!wifiResult.DataText.Contains("ssid: VerifierRouter", StringComparison.Ordinal) ||
        !wifiResult.DataText.Contains("routerIp: 10.20.30.1", StringComparison.Ordinal) ||
        !wifiResult.DataText.Contains("pingCount: 6", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("WiFi parameters were not passed into the test plan and Mock device result.");
    }

    var bluetoothResult = viewModel.TestResults.Single(item => item.TestId == "bluetooth");
    if (!bluetoothResult.DataText.Contains("targetName: VerifierBeacon", StringComparison.Ordinal) ||
        !bluetoothResult.DataText.Contains("minRssi: -75", StringComparison.Ordinal) ||
        !bluetoothResult.DataText.Contains("scanWindowMs: 8000", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Bluetooth parameters were not passed into the test plan and Mock device result.");
    }

    var fingerprintResult = viewModel.TestResults.Single(item => item.TestId == "fingerprint");
    if (!fingerprintResult.DataText.Contains("command: get_status", StringComparison.Ordinal) ||
        !fingerprintResult.DataText.Contains("timeoutMs: 2500", StringComparison.Ordinal) ||
        !fingerprintResult.DataText.Contains("communicationOk: True", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Fingerprint parameters were not passed into the test plan and Mock device result.");
    }

    var record = await repository.GetRecentSessionsAsync(1);
    if (record.Single().TestResults.Single(result => result.TestId == "bluetooth").Data.Count == 0)
    {
        throw new InvalidOperationException("Bluetooth result data was not persisted by the PC application.");
    }
}

static AppConfiguration CreateFastConfiguration() => new()
{
    TestPlan = new TestPlanConfiguration
    {
        Mock = new MockConfiguration
        {
            RunningDelayMs = 10,
            ResultHoldMs = 1,
            BoardStateRunningDelayMs = 10,
            BoardStateResultHoldMs = 1
        }
    }
};

static async Task AutoConfirmManualTestsAsync(MainViewModel viewModel)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var confirmedTestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (!timeout.IsCancellationRequested)
    {
        if (viewModel.IsManualDecisionVisible && viewModel.ConfirmManualPassCommand.CanExecute(null))
        {
            viewModel.ConfirmManualPassCommand.Execute(null);
            if (viewModel.SelectedTestResult is not null)
            {
                confirmedTestIds.Add(viewModel.SelectedTestResult.TestId);
            }

            if (confirmedTestIds.SetEquals(["hdmi", "lcd"]))
            {
                return;
            }
        }

        await Task.Delay(20, timeout.Token);
    }
}

static async Task<TestSessionRecord> WaitForRecordAsync(IDatabaseRepository repository)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
