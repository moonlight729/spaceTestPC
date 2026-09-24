using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;
using System.Text.Json;
using System.Windows.Threading;

// The view model owns WPF collections, so every await must resume on a real
// dispatcher thread with a running message pump (same as the desktop app).
await RunOnDispatcherAsync(VerifyAllAsync);

static async Task VerifyAllAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "SpaceTestPC-MockFlowVerifier", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    try
    {
        Console.WriteLine("[1/6] full pass flow");
        await VerifyAsync(root, "PASSSN00000000000001", null, "Pass");
        Console.WriteLine("[2/6] full failure flow");
        await VerifyAsync(root, "FAILSN00000000000001", "wifi", "Fail");
        Console.WriteLine("[3/6] filtered plan");
        await VerifyFilteredPlanAsync(root);
        Console.WriteLine("[4/6] sqlite storage");
        await VerifySqliteStorageAsync(root);
        Console.WriteLine("[5/6] sn write safety");
        await VerifySnWriteSafetyAsync();
        Console.WriteLine("[6/6] retest final verdict");
        await VerifyRetestFinalVerdictAsync(root);
        Console.WriteLine("Mock flow verification passed: success, failure and retest paths all behaved as expected.");
    }
    catch (Exception ex)
    {
        // Keep the full message: the console truncates long lines.
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "SpaceTestPC-MockFlowVerifier-failure.txt"), ex.ToString());
        throw;
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RunOnDispatcherAsync(Func<Task> work)
{
    var completion = new TaskCompletionSource();
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await work();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        });
        Dispatcher.Run();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    await completion.Task;
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
            new AdbPcbaCommandClient(),
            new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService(),
        configuration,
        manualTestInteractionService);

    await viewModel.InitializeAsync();
    viewModel.ScannerInput = sn;
    viewModel.ScanCommand.Execute(null);
    _ = AutoConfirmManualTestsAsync(viewModel);

    var record = await WaitForRecordAsync(repository, viewModel);
    await WaitForSessionCompletionAsync(viewModel);
    record = (await repository.GetRecentSessionsAsync(1)).Single();
    if (record.Session.FinalVerdict != expectedVerdict)
    {
        var itemStates = string.Join(", ", viewModel.TestItems.Select(item => $"{item.TestId}={item.State}"));
        // Logs are stored newest-first.
        var recentLogs = string.Join(" | ", viewModel.Logs.Take(20));
        throw new InvalidOperationException(
            $"Expected {expectedVerdict}, got {record.Session.FinalVerdict}. " +
            $"lastResult={viewModel.LastResult}, sn={viewModel.CurrentSn}, hasSelectedTests={viewModel.HasSelectedTests}, " +
            $"canStart={viewModel.StartPhaseOneCommand.CanExecute(null)}, upgradeReady={viewModel.IsUpgradePackageReady}, " +
            $"instruction={viewModel.OperatorInstruction}. Items: {itemStates} Logs: {recentLogs}");
    }

    if (!File.Exists(Path.ChangeExtension(databasePath, ".csv")))
    {
        throw new InvalidOperationException("CSV summary was not created.");
    }

    // application_upgrade is a pre-test gate, not a test item; it stays Pending when the upgrade check is disabled.
    if (failingTestId is null && viewModel.TestItems.Any(item => item.TestId != "application_upgrade" && item.State != TestItemState.Passed))
    {
        var itemStates = string.Join(", ", viewModel.TestItems.Select(item => $"{item.TestId}={item.State}"));
        throw new InvalidOperationException($"The successful Mock session did not pass every test item: {itemStates}");
    }

    // board_state, wifi, bluetooth plus the application_upgrade pre-test gate.
    if (viewModel.TestResults.Count != 4)
    {
        throw new InvalidOperationException($"Expected 4 test detail items, got {viewModel.TestResults.Count}.");
    }

    var boardStateResult = viewModel.TestResults.First(item => item.TestId == "board_state");
    if (!boardStateResult.DataText.Contains("ubootVersion:", StringComparison.Ordinal) ||
        !boardStateResult.DataText.Contains("kernelVersion:", StringComparison.Ordinal) ||
        !boardStateResult.DataText.Contains("rootfsVersion:", StringComparison.Ordinal) ||
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

    // TestItems starts with application_upgrade, so WiFi is index 2 of the enabled plan.
    if (failingTestId is not null && viewModel.TestItems.Skip(3).Any(item => item.State != TestItemState.Pending))
    {
        throw new InvalidOperationException("Tests after the failing WiFi test were executed.");
    }

    if (failingTestId is not null && viewModel.TestItems[2].State != TestItemState.Failed)
    {
        throw new InvalidOperationException("The injected WiFi failure was not reflected in the UI state.");
    }
}

static async Task VerifyFilteredPlanAsync(string root)
{
    var databasePath = Path.Combine(root, "filtered", "stage1-db.json");
    var configuration = CreateFastConfiguration();
    configuration.BluetoothBroadcaster.BroadcastName = "VerifierBeacon";
    configuration.TestPlan.EnabledTests = ["board_state", "wifi", "bluetooth", "ddr"];
    configuration.TestPlan.TestParameters["wifi"] = new Dictionary<string, JsonElement>
    {
        ["ssid"] = JsonSerializer.SerializeToElement("VerifierRouter"),
        ["routerIp"] = JsonSerializer.SerializeToElement("10.20.30.1"),
        ["pingCount"] = JsonSerializer.SerializeToElement(6),
        // Mock reports -62 dBm, so keep the relaxed threshold from the fast configuration.
        ["minRssi"] = JsonSerializer.SerializeToElement(-75)
    };
    configuration.TestPlan.TestParameters["bluetooth"] = new Dictionary<string, JsonElement>
    {
        ["targetName"] = JsonSerializer.SerializeToElement("VerifierBeacon"),
        ["minRssi"] = JsonSerializer.SerializeToElement(-75),
        ["scanWindowMs"] = JsonSerializer.SerializeToElement(8000)
    };
    configuration.TestPlan.TestParameters["ddr"] = new Dictionary<string, JsonElement>
    {
        ["minSpeedMbps"] = JsonSerializer.SerializeToElement(100),
        ["timeoutMs"] = JsonSerializer.SerializeToElement(2500)
    };
    var repository = new FileDatabaseRepository(databasePath);
    var viewModel = new MainViewModel(
        new ScannerService(),
        new PcbaCommandClientFactory(new MockPcbaCommandClient(mockConfiguration: configuration.TestPlan.Mock), new AdbPcbaCommandClient(), new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService(),
        configuration);

    await viewModel.InitializeAsync();
    viewModel.ScannerInput = "FILTEREDSN0000000001";
    viewModel.ScanCommand.Execute(null);
    _ = await WaitForRecordAsync(repository);

    // application_upgrade is the pre-test gate and is not part of the enabled plan.
    var enabledIds = viewModel.TestResults.Select(item => item.TestId).Where(id => id != "application_upgrade").ToArray();
    if (enabledIds is not ["board_state", "wifi", "bluetooth", "ddr"])
    {
        throw new InvalidOperationException($"Filtered test plan did not keep the configured tests: {string.Join(",", enabledIds)}");
    }

    // 4 enabled tests plus the application_upgrade pre-test gate.
    if (viewModel.TestItems.Count != 5 || viewModel.TestOverviewColumns != 5)
    {
        throw new InvalidOperationException($"Filtered UI overview was not reduced to the enabled tests: items={viewModel.TestItems.Count}, columns={viewModel.TestOverviewColumns}");
    }

    var wifiResult = viewModel.TestResults.Single(item => item.TestId == "wifi");
    if (!wifiResult.DataText.Contains("ssid: VerifierRouter", StringComparison.Ordinal) ||
        !wifiResult.DataText.Contains("minRssi: -75", StringComparison.Ordinal) ||
        !wifiResult.DataText.Contains("found: True", StringComparison.Ordinal))
    {
        var wifiLogs = string.Join(" | ", viewModel.Logs.Where(log => log.Contains("wifi", StringComparison.OrdinalIgnoreCase)).Take(5));
        throw new InvalidOperationException($"WiFi parameters were not passed into the test plan and Mock device result: data={wifiResult.DataText}, message={wifiResult.Message}, logs={wifiLogs}");
    }

    var bluetoothResult = viewModel.TestResults.Single(item => item.TestId == "bluetooth");
    if (!bluetoothResult.DataText.Contains("targetName: VerifierBeacon", StringComparison.Ordinal) ||
        !bluetoothResult.DataText.Contains("minRssi: -75", StringComparison.Ordinal) ||
        !bluetoothResult.DataText.Contains("scanWindowMs: 8000", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Bluetooth parameters were not passed into the test plan and Mock device result.");
    }

    var ddrResult = viewModel.TestResults.Single(item => item.TestId == "ddr");
    if (string.IsNullOrWhiteSpace(ddrResult.DataText))
    {
        throw new InvalidOperationException("DDR result data was not produced by the Mock device.");
    }

    var record = await repository.GetRecentSessionsAsync(1);
    if (record.Single().TestResults.Single(result => result.TestId == "bluetooth").Data.Count == 0)
    {
        throw new InvalidOperationException("Bluetooth result data was not persisted by the PC application.");
    }
}

static async Task VerifySqliteStorageAsync(string root)
{
    var databasePath = Path.Combine(root, "sqlite", "stage1.db");
    var repository = new SqliteDatabaseRepository(databasePath);
    await repository.SaveSessionAsync(new TestSessionRecord
    {
        Session = new TestSession { SessionId = "sqlite-session", Sn = "SQLITE-SN", StartTime = DateTimeOffset.Now, EndTime = DateTimeOffset.Now, FinalVerdict = "Pass" },
        TestResults = [new TestResultRecord { TestId = "wifi", Status = "PASS", ResultCode = 0, Message = "ok", Data = new Dictionary<string, object?> { ["pingOk"] = true } }]
    });
    var records = await repository.GetRecentSessionsAsync(1);
    // The per-SN CSV is named "<sn>_<testMode>.csv".
    var csvDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "records");
    var csvFiles = Directory.Exists(csvDirectory) ? Directory.GetFiles(csvDirectory, "SQLITE-SN*.csv") : [];
    if (records.Single().Session.Sn != "SQLITE-SN" || csvFiles.Length != 1)
    {
        throw new InvalidOperationException("SQLite database or per-SN CSV output was not created.");
    }
}

static async Task VerifySnWriteSafetyAsync()
{
    var client = new MockPcbaCommandClient();
    var initialState = await client.GetBoardStateAsync("sn-session", "SN-001");
    if (!string.IsNullOrEmpty(initialState.BoardSn)) throw new InvalidOperationException("Mock board must start without an SN.");
    var write = await client.WriteSnAsync("sn-session", "SN-001", initialState.BoardId);
    var verifiedState = await client.GetBoardStateAsync("sn-session", "SN-001");
    var overwrite = await client.WriteSnAsync("sn-session", "SN-002", initialState.BoardId);
    if (write.ResultCode != 0 || verifiedState.BoardSn != "SN-001" || overwrite.ResultCode == 0)
    {
        throw new InvalidOperationException("SN write/verification or overwrite protection failed.");
    }
}

static async Task VerifyRetestFinalVerdictAsync(string root)
{
    var databasePath = Path.Combine(root, "retest", "stage1-db.json");
    var configuration = CreateFastConfiguration();
    var repository = new FileDatabaseRepository(databasePath);
    var manualTestInteractionService = new ManualTestInteractionService();
    var mockClient = new MockPcbaCommandClient("wifi", configuration.TestPlan.Mock, manualTestInteractionService);
    var viewModel = new MainViewModel(
        new ScannerService(),
        new PcbaCommandClientFactory(mockClient, new AdbPcbaCommandClient(), new AdbPcbaCommandClient()),
        new StatusMonitorService("Voltage"),
        new StatusMonitorService("Battery"),
        repository,
        new LogService(),
        configuration,
        manualTestInteractionService);

    await viewModel.InitializeAsync();
    viewModel.ScannerInput = "RETESTSN000000000001";
    viewModel.ScanCommand.Execute(null);
    _ = AutoConfirmManualTestsAsync(viewModel);
    await WaitForRecordAsync(repository);

    if (viewModel.FinalVerdictDisplay != "FAIL")
    {
        throw new InvalidOperationException($"Expected FAIL after the first failing session, got {viewModel.FinalVerdictDisplay}.");
    }

    if (!viewModel.RetestCommand.CanExecute("wifi"))
    {
        throw new InvalidOperationException("Retest was not offered for the failed WiFi item.");
    }

    // 1) The fault is still present: retest fails again, the card must keep showing FAIL.
    viewModel.RetestCommand.Execute("wifi");
    await WaitForLastResultAsync(viewModel, "Retest completed with failures");
    if (viewModel.FinalVerdictDisplay != "FAIL")
    {
        throw new InvalidOperationException($"Expected FAIL after a failing retest, got {viewModel.FinalVerdictDisplay}.");
    }

    // 2) The fault is fixed: retest passes, the card must switch to PASS instead of staying WAIT.
    if (!viewModel.RetestCommand.CanExecute("wifi"))
    {
        throw new InvalidOperationException("Retest was not offered again after the failed retest.");
    }

    mockClient.ClearInjectedFailure();
    viewModel.RetestCommand.Execute("wifi");
    await WaitForLastResultAsync(viewModel, "Retest passed");
    if (viewModel.FinalVerdictDisplay != "PASS")
    {
        throw new InvalidOperationException($"Expected PASS after a successful retest, got {viewModel.FinalVerdictDisplay}.");
    }

    if (viewModel.FinalVerdictForeground != "#15803D")
    {
        throw new InvalidOperationException($"Expected the PASS colour after a successful retest, got {viewModel.FinalVerdictForeground}.");
    }
}

static async Task WaitForSessionCompletionAsync(MainViewModel viewModel)
{
    var deadline = DateTimeOffset.Now.AddSeconds(90);
    while (DateTimeOffset.Now < deadline)
    {
        if (viewModel.LastResult.StartsWith("Stage 1 passed", StringComparison.Ordinal) ||
            viewModel.LastResult.StartsWith("Stage 1 failed", StringComparison.Ordinal) ||
            viewModel.LastResult.StartsWith("Stage 1 aborted", StringComparison.Ordinal))
        {
            return;
        }

        await Task.Delay(50);
    }

    throw new TimeoutException($"Timed out waiting for session completion, last value '{viewModel.LastResult}'. Logs: {string.Join(" | ", viewModel.Logs.Take(20))}");
}

static async Task WaitForLastResultAsync(MainViewModel viewModel, string expected)
{
    var deadline = DateTimeOffset.Now.AddSeconds(60);
    while (DateTimeOffset.Now < deadline)
    {
        if (string.Equals(viewModel.LastResult, expected, StringComparison.Ordinal))
        {
            return;
        }

        await Task.Delay(50);
    }

    throw new TimeoutException($"Timed out waiting for LastResult '{expected}', last value was '{viewModel.LastResult}'. Logs: {string.Join(" | ", viewModel.Logs.Take(10))}");
}

static AppConfiguration CreateFastConfiguration() => new()
{
    // No real upgrade package exists in the verifier, so the pre-test upgrade gate is disabled.
    Upgrade = new UpgradeConfiguration { Enabled = false },
    // Drive the whole flow through the in-memory Mock device client.
    PcbaConnection = new PcbaConnectionConfiguration { Mode = "mock" },
    // testPlan.EnabledTests is honoured in developer mode only.
    OperationMode = "developer",
    TestPlan = new TestPlanConfiguration
    {
        // Only the tests that need no extra operator interaction (battery discharge
        // preparation, TF removal...) are enabled, so the flow runs unattended.
        EnabledTests = ["board_state", "wifi", "bluetooth"],
        Mock = new MockConfiguration
        {
            RunningDelayMs = 10,
            ResultHoldMs = 1,
            BoardStateRunningDelayMs = 10,
            BoardStateResultHoldMs = 1
        },
        // The board-state test aborts the whole session when the versions do not
        // match, so the verifier expects exactly what the Mock device reports.
        TestParameters = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["board_state"] = new()
            {
                ["expectedUbootVersion"] = JsonSerializer.SerializeToElement("v0.1.1"),
                ["expectedKernelVersion"] = JsonSerializer.SerializeToElement("v0.1.6"),
                ["expectedRootfsVersion"] = JsonSerializer.SerializeToElement("0.1.6"),
                ["expectedGen1AppVersion"] = JsonSerializer.SerializeToElement("0.5.0")
            },
            // The Mock device reports -62 dBm, so the default -40 dBm threshold would fail it.
            ["wifi"] = new()
            {
                ["minRssi"] = JsonSerializer.SerializeToElement(-75)
            }
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

static async Task<TestSessionRecord> WaitForRecordAsync(IDatabaseRepository repository, MainViewModel? viewModel = null)
{
    var deadline = DateTimeOffset.Now.AddSeconds(90);
    while (DateTimeOffset.Now < deadline)
    {
        var sessions = await repository.GetRecentSessionsAsync(1);
        if (sessions.Count >= 1)
        {
            return sessions[0];
        }

        await Task.Delay(100);
    }

    var diagnosis = viewModel is null
        ? string.Empty
        : $" lastResult={viewModel.LastResult}, instruction={viewModel.OperatorInstruction}, items={string.Join(", ", viewModel.TestItems.Select(item => $"{item.TestId}={item.State}"))}, logs={string.Join(" | ", viewModel.Logs.Take(15))}";
    throw new TimeoutException($"Timed out waiting for the session record.{diagnosis}");
}
