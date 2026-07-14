using System.Collections.ObjectModel;
using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

namespace SpaceTestPC.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static bool UseUnifiedSessionProtocol => true;
    private static readonly IReadOnlyList<TestPlanItem> TestPlan =
    [
        new() { Id = "board_state" }, new() { Id = "test_mode" }, new() { Id = "bluetooth" },
        new() { Id = "wifi" }, new() { Id = "ethernet" }, new() { Id = "tf" }, new() { Id = "lcd" },
        new() { Id = "fingerprint" }, new() { Id = "keys" }, new() { Id = "hdmi" }, new() { Id = "typec" },
        new() { Id = "battery" }, new() { Id = "fan" }, new() { Id = "otg" }, new() { Id = "camera" }
    ];

    private static readonly IReadOnlyDictionary<string, int> TestItemIndexes = new Dictionary<string, int>
    {
        ["board_state"] = 0, ["test_mode"] = 0, ["bluetooth"] = 1, ["wifi"] = 2, ["ethernet"] = 3,
        ["tf"] = 4, ["lcd"] = 5, ["fingerprint"] = 6, ["keys"] = 7, ["hdmi"] = 8,
        ["typec"] = 9, ["battery"] = 10, ["fan"] = 11, ["otg"] = 12, ["camera"] = 13
    };
    // Change this value during deployment; operators do not choose the transport mode.
    private const PcbaConnectionMode ConnectionMode = PcbaConnectionMode.Mock;
    private const string BoardStateItemName = "板状态";
    private const string TestModeItemName = "测试模式";
    private const string BluetoothItemName = "蓝牙";
    private const string WifiItemName = "WiFi";
    private const string EthernetItemName = "网线";
    private const string BatteryItemName = "电池";

    private readonly IScannerService _scannerService;
    private readonly IPcbaCommandClientFactory _pcbaCommandClientFactory;
    private readonly IStatusMonitorService _voltageMonitorService;
    private readonly IStatusMonitorService _batterySimulatorService;
    private readonly IDatabaseRepository _databaseRepository;
    private readonly ILogService _logService;
    private readonly BluetoothScanRequest _bluetoothRequest = new()
    {
        TargetName = "NODE_A_01",
        TimeoutMs = 5000,
        MinRssi = -80
    };
    private readonly WifiPingRequest _wifiRequest = new()
    {
        Ssid = "FactoryAP",
        Password = "12345678",
        TargetIp = "192.168.1.1",
        PingCount = 4,
        TimeoutMs = 10000
    };
    private readonly EthernetPingRequest _ethernetRequest = new()
    {
        RouterIp = "192.168.1.1",
        TargetIp = "192.168.1.1",
        PingCount = 4,
        TimeoutMs = 10000
    };

    private string _scannerInput = string.Empty;
    private string _currentSn = string.Empty;
    private string _sessionId = string.Empty;
    private string _boardId = "-";
    private string _boardState = "Unknown";
    private string _testMode = "Unknown";
    private string _voltageStatus = "Idle";
    private string _batteryStatus = "Idle";
    private string _lastResult = "Waiting";
    private string _operatorInstruction = "请扫描产品 SN，系统将自动按顺序执行检测。";
    private string _debugOutput = "Waiting for scan...";
    private TestResultViewModel? _selectedTestResult;

    public MainViewModel(
        IScannerService scannerService,
        IPcbaCommandClientFactory pcbaCommandClientFactory,
        IStatusMonitorService voltageMonitorService,
        IStatusMonitorService batterySimulatorService,
        IDatabaseRepository databaseRepository,
        ILogService logService)
    {
        _scannerService = scannerService;
        _pcbaCommandClientFactory = pcbaCommandClientFactory;
        _voltageMonitorService = voltageMonitorService;
        _batterySimulatorService = batterySimulatorService;
        _databaseRepository = databaseRepository;
        _logService = logService;

        ScanCommand = new RelayCommand(HandleScan, () => !string.IsNullOrWhiteSpace(ScannerInput));
        StartMockSessionCommand = new RelayCommand(StartMockSession);
        ReadBoardStateCommand = new AsyncRelayCommand(ReadBoardStateAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        StartPhaseOneCommand = new AsyncRelayCommand(StartPhaseOneAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));

        Logs = new ObservableCollection<string>();
        RecentSessions = new ObservableCollection<string>();
        TestItems = new ObservableCollection<TestItemViewModel>(BuildTestItems());
        TestResults = new ObservableCollection<TestResultViewModel>(
            TestPlan
                .Select(item => new TestResultViewModel(item.Id, GetTestDisplayName(item.Id))));
        SelectedTestResult = TestResults.FirstOrDefault();

        AppendLog("Stage 1 UI ready.");
        UpdateDebugOutput();
    }

    public string ScannerInput
    {
        get => _scannerInput;
        set
        {
            if (SetProperty(ref _scannerInput, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentSn
    {
        get => _currentSn;
        private set
        {
            if (SetProperty(ref _currentSn, value))
            {
                ReadBoardStateCommand.NotifyCanExecuteChanged();
                StartPhaseOneCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SessionId
    {
        get => _sessionId;
        private set => SetProperty(ref _sessionId, value);
    }

    public string BoardId
    {
        get => _boardId;
        private set => SetProperty(ref _boardId, value);
    }

    public string BoardState
    {
        get => _boardState;
        private set => SetProperty(ref _boardState, value);
    }

    public string TestMode
    {
        get => _testMode;
        private set => SetProperty(ref _testMode, value);
    }

    public string VoltageStatus
    {
        get => _voltageStatus;
        private set => SetProperty(ref _voltageStatus, value);
    }

    public string BatteryStatus
    {
        get => _batteryStatus;
        private set => SetProperty(ref _batteryStatus, value);
    }

    public string LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public string OperatorInstruction
    {
        get => _operatorInstruction;
        private set => SetProperty(ref _operatorInstruction, value);
    }

    public string DebugOutput
    {
        get => _debugOutput;
        private set => SetProperty(ref _debugOutput, value);
    }

    public ObservableCollection<string> Logs { get; }
    public ObservableCollection<string> RecentSessions { get; }
    public ObservableCollection<TestItemViewModel> TestItems { get; }
    public ObservableCollection<TestResultViewModel> TestResults { get; }
    public TestResultViewModel? SelectedTestResult
    {
        get => _selectedTestResult;
        set => SetProperty(ref _selectedTestResult, value);
    }
    public RelayCommand ScanCommand { get; }
    public RelayCommand StartMockSessionCommand { get; }
    public AsyncRelayCommand ReadBoardStateCommand { get; }
    public AsyncRelayCommand StartPhaseOneCommand { get; }

    private void StartMockSession()
    {
        ScannerInput = $"MOCK-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        HandleScan();
    }

    private void HandleScan()
    {
        var sn = _scannerService.Normalize(ScannerInput);
        if (string.IsNullOrWhiteSpace(sn))
        {
            return;
        }

        CurrentSn = sn;
        SessionId = Guid.NewGuid().ToString("N");
        LastResult = "SN scanned";
        OperatorInstruction = "SN 已确认，正在自动执行检测。请保持产品连接稳定。";
        ScannerInput = string.Empty;
        ResetTestItems();
        AppendLog($"Scan received: {CurrentSn}");
        AppendLog($"Session created: {SessionId}");
        UpdateDebugOutput();

        AppendLog("Auto-starting Stage 1.");
        StartPhaseOneCommand.Execute(null);
    }

    private async Task ReadBoardStateAsync()
    {
        try
        {
            var client = _pcbaCommandClientFactory.Create(ConnectionMode);
            AppendLog("Reading board state...");
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            var state = await client.GetBoardStateAsync(SessionId, CurrentSn);
            ApplyBoardState(state);
            LastResult = "Board state loaded";
            SetTestItemState(BoardStateItemName, TestItemState.Passed);
            AppendLog($"Board state loaded: {BoardId} / {BoardState}");
        }
        catch (Exception ex)
        {
            SetTestItemState(BoardStateItemName, TestItemState.Failed);
            LastResult = "Board state failed";
            AppendLog($"Read board state failed: {ex.Message}");
        }

        UpdateDebugOutput();
    }

    private async Task StartPhaseOneAsync()
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            SessionId = Guid.NewGuid().ToString("N");
        }

        if (UseUnifiedSessionProtocol)
        {
            await RunUnifiedSessionAsync();
            return;
        }

        AppendLog("Stage 1 started.");
        LastResult = "Stage 1 running";
        UpdateDebugOutput();

        var client = _pcbaCommandClientFactory.Create(ConnectionMode);
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            state = await client.GetBoardStateAsync(SessionId, CurrentSn);
            ApplyBoardState(state);
            AppendLog($"Board state ok: {BoardId} / {BoardState} / {TestMode}");

            SetTestItemState(BatteryItemName, TestItemState.Running);
            await _voltageMonitorService.StartAsync(SessionId);
            await _batterySimulatorService.StartAsync(SessionId);
            VoltageStatus = _voltageMonitorService.Status;
            BatteryStatus = _batterySimulatorService.Status;
            SetTestItemState(BatteryItemName, TestItemState.Passed);
            AppendLog($"Monitor started: voltage={VoltageStatus}, battery={BatteryStatus}");

            OperatorInstruction = "正在进入测试模式，请勿操作产品。";
            var testModeResponse = await client.EnterTestModeAsync(SessionId, CurrentSn, BoardId);
            var enteredTestMode = testModeResponse.ResultCode == 0;
            SetTestItemState(BoardStateItemName, enteredTestMode ? TestItemState.Passed : TestItemState.Failed);
            AppendLog($"Enter test mode result: {testModeResponse.Message}");

            if (!enteredTestMode)
            {
                LastResult = "Stage 1 failed";
                return;
            }

            OperatorInstruction = "正在按顺序执行蓝牙、WiFi 和网口检测。";
            var bluetoothPassed = await RunBluetoothAsync(client);
            var wifiPassed = await RunWifiAsync(client);
            var ethernetPassed = await RunEthernetAsync(client);

            if (ConnectionMode == PcbaConnectionMode.Mock)
            {
                await RunMockOnlyTestsAsync();
            }

            var stagePassed = bluetoothPassed && wifiPassed && ethernetPassed;
            LastResult = stagePassed ? "Stage 1 passed" : "Stage 1 failed";
            finalVerdict = stagePassed ? "Pass" : "Fail";
            OperatorInstruction = stagePassed
                ? "检测完成，请取下产品并扫描下一台。"
                : "检测失败，请处理异常后重新扫描产品。";
        }
        catch (Exception ex)
        {
            LastResult = "Stage 1 failed";
            OperatorInstruction = "检测异常，请检查连接后重新扫描产品。";
            AppendLog($"Stage 1 execution failed: {ex.Message}");
        }
        finally
        {
            UpdateDebugOutput();
        }

        var record = new TestSessionRecord
        {
            Session = new TestSession
            {
                SessionId = SessionId,
                Sn = CurrentSn,
                ProductModel = "PCBA_X1",
                StationCode = "ST01",
                StartTime = DateTimeOffset.Now,
                EndTime = DateTimeOffset.Now,
                FinalVerdict = finalVerdict
            },
            BoardState = state ?? new BoardState
            {
                BoardId = BoardId,
                BoardSn = CurrentSn,
                TestMode = TestMode,
                CurrentState = BoardState
            },
            Logs = _logService.Snapshot().Select(message => new LogEntry { Message = message }).ToArray()
        };

        await _databaseRepository.SaveSessionAsync(record);
        await LoadRecentSessionsAsync();
        AppendLog("Session persisted.");
        UpdateDebugOutput();
    }

    private async Task RunUnifiedSessionAsync()
    {
        AppendLog("Test session started.");
        LastResult = "Stage 1 running";
        OperatorInstruction = "正在接收底层测试结果，请勿断开产品连接。";

        var client = _pcbaCommandClientFactory.Create(ConnectionMode);
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            await foreach (var testEvent in client.RunSessionAsync(SessionId, CurrentSn, TestPlan))
            {
                if (testEvent.Event == "test.report")
                {
                    ApplyTestReport(testEvent);
                    if (testEvent.TestId == "board_state" && testEvent.Status == "passed")
                    {
                        state = new BoardState
                        {
                            BoardId = GetDataString(testEvent.Data, "boardId", BoardId),
                            BoardSn = CurrentSn,
                            CurrentState = GetDataString(testEvent.Data, "currentState", "idle"),
                            TestMode = "ready"
                        };
                        ApplyBoardState(state);
                    }
                }
                else if (testEvent.Event == "session.completed")
                {
                    finalVerdict = testEvent.Status == "passed" ? "Pass" : "Fail";
                    LastResult = finalVerdict == "Pass" ? "Stage 1 passed" : "Stage 1 failed";
                    OperatorInstruction = finalVerdict == "Pass"
                        ? "检测完成，请取下产品并扫描下一台。"
                        : "检测失败，底层已终止后续项目。请处理异常后重新扫描。";
                    AppendLog($"Session completed: {testEvent.Status} ({testEvent.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            LastResult = "Stage 1 failed";
            OperatorInstruction = "通信异常，检测已停止。请检查连接后重新扫描。";
            AppendLog($"Session failed: {ex.Message}");
        }

        var record = new TestSessionRecord
        {
            Session = new TestSession
            {
                SessionId = SessionId,
                Sn = CurrentSn,
                ProductModel = "PCBA_X1",
                StationCode = "ST01",
                StartTime = DateTimeOffset.Now,
                EndTime = DateTimeOffset.Now,
                FinalVerdict = finalVerdict
            },
            BoardState = state,
            Logs = _logService.Snapshot().Select(message => new LogEntry { Message = message }).ToArray()
        };

        await _databaseRepository.SaveSessionAsync(record);
        await LoadRecentSessionsAsync();
        AppendLog("Session persisted.");
        UpdateDebugOutput();
    }

    private void ApplyTestReport(TestSessionEvent testEvent)
    {
        var result = TestResults.FirstOrDefault(item => item.TestId == testEvent.TestId);
        result?.Apply(testEvent);
        if (testEvent.Status == "running" && result is not null)
        {
            SelectedTestResult = result;
        }

        if (TestItemIndexes.TryGetValue(testEvent.TestId, out var index) && index < TestItems.Count)
        {
            TestItems[index].State = testEvent.Status switch
            {
                "running" => TestItemState.Running,
                "passed" => TestItemState.Passed,
                _ => TestItemState.Failed
            };
        }

        if (testEvent.TestId == "battery")
        {
            BatteryStatus = testEvent.Status;
        }

        OperatorInstruction = testEvent.Status == "running"
            ? $"正在检测：{testEvent.TestId}。"
            : $"{testEvent.TestId}：{testEvent.Status}。";
        AppendLog($"{testEvent.TestId}: {testEvent.Status} ({testEvent.Message})");
    }

    private static string GetTestDisplayName(string testId) => testId switch
    {
        "board_state" => "板状态",
        "test_mode" => "测试模式",
        "bluetooth" => "蓝牙",
        "wifi" => "WiFi",
        "ethernet" => "网口",
        "tf" => "TF 卡",
        "lcd" => "LCD",
        "fingerprint" => "指纹",
        "keys" => "按键",
        "hdmi" => "HDMI",
        "typec" => "Type-C",
        "battery" => "电池",
        "fan" => "风扇",
        "otg" => "OTG",
        "camera" => "相机",
        _ => testId
    };

    private static string GetDataString(IReadOnlyDictionary<string, object?> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString() ?? fallback
            : value.ToString() ?? fallback;
    }

    public async Task InitializeAsync()
    {
        await LoadRecentSessionsAsync();
    }

    private async Task LoadRecentSessionsAsync()
    {
        var sessions = await _databaseRepository.GetRecentSessionsAsync(10);
        RecentSessions.Clear();
        foreach (var session in sessions)
        {
            RecentSessions.Add($"{session.Session.Sn} | {session.Session.FinalVerdict} | {session.Session.StartTime:MM-dd HH:mm:ss}");
        }
    }

    private void AppendLog(string message)
    {
        _logService.Info(message);
        Logs.Clear();
        foreach (var entry in _logService.Snapshot().TakeLast(3))
        {
            Logs.Add(entry);
        }
    }

    private static IReadOnlyList<TestItemViewModel> BuildTestItems()
    {
        var names = new[]
        {
            BoardStateItemName, TestModeItemName, BluetoothItemName, WifiItemName, EthernetItemName, "TF卡", "LCD", "指纹", "按键",
            "HDMI", "TypeC", BatteryItemName, "风扇", "OTG", "相机"
        };

        var visibleNames = names.Where(name => name != TestModeItemName).ToArray();

        return visibleNames
            .Select((name, index) => new TestItemViewModel(name, index < visibleNames.Length - 1))
            .ToArray();
    }

    private void ResetTestItems()
    {
        foreach (var item in TestItems)
        {
            item.State = TestItemState.Pending;
        }

        foreach (var result in TestResults)
        {
            result.Reset();
        }

        SelectedTestResult = TestResults.FirstOrDefault();
    }

    private void SetTestItemState(string name, TestItemState state)
    {
        var item = TestItems.FirstOrDefault(x => x.Name == name);
        if (item is not null)
        {
            item.State = state;
        }
    }

    private void ApplyBoardState(BoardState state)
    {
        BoardId = state.BoardId;
        BoardState = state.CurrentState;
        TestMode = state.TestMode;
    }

    private async Task<bool> RunBluetoothAsync(IPcbaCommandClient client)
    {
        try
        {
            SetTestItemState(BluetoothItemName, TestItemState.Running);
            AppendLog($"Running bluetooth test: target={_bluetoothRequest.TargetName}");
            var result = await client.ScanBluetoothTargetAsync(SessionId, CurrentSn, BoardId, _bluetoothRequest);
            var passed = result.Found;
            SetTestItemState(BluetoothItemName, passed ? TestItemState.Passed : TestItemState.Failed);
            AppendLog(
                passed
                    ? $"Bluetooth test passed: target={result.TargetName}, rssi={result.Rssi}"
                    : $"Bluetooth test failed: target={_bluetoothRequest.TargetName} not found");
            return passed;
        }
        catch (Exception ex)
        {
            SetTestItemState(BluetoothItemName, TestItemState.Failed);
            AppendLog($"Bluetooth test failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RunWifiAsync(IPcbaCommandClient client)
    {
        try
        {
            SetTestItemState(WifiItemName, TestItemState.Running);
            AppendLog($"Running WiFi test: ssid={_wifiRequest.Ssid}, targetIp={_wifiRequest.TargetIp}");
            var result = await client.ConnectWifiAndPingAsync(SessionId, CurrentSn, BoardId, _wifiRequest);
            var passed = result.Connected && result.Linked && result.PingOk;
            SetTestItemState(WifiItemName, passed ? TestItemState.Passed : TestItemState.Failed);
            AppendLog(
                passed
                    ? $"WiFi test passed: ip={result.Ip}, avgDelay={result.AvgDelayMs}ms"
                    : $"WiFi test failed: connected={result.Connected}, linked={result.Linked}, pingOk={result.PingOk}");
            return passed;
        }
        catch (Exception ex)
        {
            SetTestItemState(WifiItemName, TestItemState.Failed);
            AppendLog($"WiFi test failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RunEthernetAsync(IPcbaCommandClient client)
    {
        try
        {
            SetTestItemState(EthernetItemName, TestItemState.Running);
            AppendLog($"Running ethernet test: routerIp={_ethernetRequest.RouterIp}, targetIp={_ethernetRequest.TargetIp}");
            var result = await client.ConnectEthernetAndPingAsync(SessionId, CurrentSn, BoardId, _ethernetRequest);
            var passed = result.Connected && result.Linked && result.PingOk;
            SetTestItemState(EthernetItemName, passed ? TestItemState.Passed : TestItemState.Failed);
            AppendLog(
                passed
                    ? $"Ethernet test passed: ip={result.Ip}, avgDelay={result.AvgDelayMs}ms"
                    : $"Ethernet test failed: connected={result.Connected}, linked={result.Linked}, pingOk={result.PingOk}");
            return passed;
        }
        catch (Exception ex)
        {
            SetTestItemState(EthernetItemName, TestItemState.Failed);
            AppendLog($"Ethernet test failed: {ex.Message}");
            return false;
        }
    }

    private async Task RunMockOnlyTestsAsync()
    {
        foreach (var item in TestItems.Where(item => item.State == TestItemState.Pending))
        {
            item.State = TestItemState.Running;
            AppendLog($"Mock running: {item.Name}");

            // Keep the active state visible while hardware-specific protocols are still pending.
            await Task.Delay(150);

            item.State = TestItemState.Passed;
            AppendLog($"Mock passed: {item.Name}");
        }
    }

    private void UpdateDebugOutput()
    {
        DebugOutput =
            $"Mode: {ConnectionMode}\n" +
            $"SessionId: {SessionId}\n" +
            $"SN: {CurrentSn}\n" +
            $"BoardId: {BoardId}\n" +
            $"BoardState: {BoardState}\n" +
            $"TestMode: {TestMode}\n" +
            $"VoltageStatus: {VoltageStatus}\n" +
            $"BatteryStatus: {BatteryStatus}\n" +
            $"LastResult: {LastResult}";
    }
}
