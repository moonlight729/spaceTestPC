using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

namespace SpaceTestPC.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static bool UseUnifiedSessionProtocol => true;
    private static readonly IReadOnlyList<TestPlanItem> AllTestPlan =
    [
        new() { Id = "board_state" }, new() { Id = "hdmi" }, new() { Id = "keys" }, new() { Id = "lcd" },
        new() { Id = "wifi" }, new() { Id = "bluetooth" }, new() { Id = "fingerprint" },
        new() { Id = "typec_fast_charge" }, new() { Id = "typec_camera" }, new() { Id = "tf" },
        new() { Id = "indicator_led" }, new() { Id = "fan" }, new() { Id = "otg" },
        new() { Id = "battery_management" }
    ];

    // Change this value during deployment; operators do not choose the transport mode.
    private const PcbaConnectionMode ConnectionMode = PcbaConnectionMode.Mock;
    private const string BoardStateItemName = "板状态";
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
    private readonly ManualTestInteractionService? _manualTestInteractionService;
    private readonly Jk5506Service? _jk5506Service;
    private readonly JxTvmService? _jxTvmService;
    private readonly BluetoothBroadcasterService? _bluetoothBroadcasterService;
    private readonly Dictionary<string, bool> _voltagePhaseResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _voltageControlCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<TestPlanItem> _testPlan;
    private readonly IReadOnlyDictionary<string, int> _testItemIndexes;
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
    private string? _manualDecisionTestId;
    private IPcbaCommandClient? _activeSessionClient;
    private readonly HashSet<string> _automaticDecisionTests = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(
        IScannerService scannerService,
        IPcbaCommandClientFactory pcbaCommandClientFactory,
        IStatusMonitorService voltageMonitorService,
        IStatusMonitorService batterySimulatorService,
        IDatabaseRepository databaseRepository,
        ILogService logService,
        AppConfiguration? configuration = null,
        ManualTestInteractionService? manualTestInteractionService = null,
        Jk5506Service? jk5506Service = null,
        JxTvmService? jxTvmService = null,
        BluetoothBroadcasterService? bluetoothBroadcasterService = null)
    {
        _scannerService = scannerService;
        _pcbaCommandClientFactory = pcbaCommandClientFactory;
        _voltageMonitorService = voltageMonitorService;
        _batterySimulatorService = batterySimulatorService;
        _databaseRepository = databaseRepository;
        _logService = logService;
        _manualTestInteractionService = manualTestInteractionService;
        _jk5506Service = jk5506Service;
        _jxTvmService = jxTvmService;
        _bluetoothBroadcasterService = bluetoothBroadcasterService;
        var appConfiguration = configuration ?? new AppConfiguration();
        _testPlan = BuildActiveTestPlan(appConfiguration);
        _testItemIndexes = _testPlan
            .Select((item, index) => new { item.Id, index })
            .ToDictionary(item => item.Id, item => item.index);

        ScanCommand = new RelayCommand(HandleScan, () => !string.IsNullOrWhiteSpace(ScannerInput));
        StartMockSessionCommand = new RelayCommand(StartMockSession);
        ConfirmManualPassCommand = new RelayCommand(() => SubmitManualDecision(true), () => IsManualDecisionVisible);
        ConfirmManualFailCommand = new RelayCommand(() => SubmitManualDecision(false), () => IsManualDecisionVisible);
        ReadBoardStateCommand = new AsyncRelayCommand(ReadBoardStateAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        StartPhaseOneCommand = new AsyncRelayCommand(StartPhaseOneAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));

        Logs = new ObservableCollection<string>();
        RecentSessions = new ObservableCollection<string>();
        TestItems = new ObservableCollection<TestItemViewModel>(BuildTestItems(_testPlan));
        TestResults = new ObservableCollection<TestResultViewModel>(
            _testPlan
                .Select(item => new TestResultViewModel(item.Id, GetTestDisplayName(item.Id))));
        DirectionalKeys = new ObservableCollection<DirectionalKeyViewModel>
        {
            new("up", "上"), new("down", "下"), new("left", "左"), new("right", "右")
        };
        SelectedTestResult = TestResults.FirstOrDefault();
        TestOverviewColumns = Math.Max(1, TestItems.Count);

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

    public string AppVersion { get; } = GetAppVersion();
    public string WindowTitle => $"检测工作台 {AppVersion}";
    public int TestOverviewColumns { get; }

    public ObservableCollection<string> Logs { get; }
    public ObservableCollection<string> RecentSessions { get; }
    public ObservableCollection<TestItemViewModel> TestItems { get; }
    public ObservableCollection<TestResultViewModel> TestResults { get; }
    public ObservableCollection<DirectionalKeyViewModel> DirectionalKeys { get; }
    public TestResultViewModel? SelectedTestResult
    {
        get => _selectedTestResult;
        set
        {
            if (SetProperty(ref _selectedTestResult, value))
            {
                RaisePropertyChanged(nameof(IsManualDecisionVisible));
                RaisePropertyChanged(nameof(ManualDecisionPrompt));
                RaisePropertyChanged(nameof(IsKeyTestDetailVisible));
                ConfirmManualPassCommand.NotifyCanExecuteChanged();
                ConfirmManualFailCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public RelayCommand ScanCommand { get; }
    public RelayCommand StartMockSessionCommand { get; }
    public RelayCommand ConfirmManualPassCommand { get; }
    public RelayCommand ConfirmManualFailCommand { get; }
    public AsyncRelayCommand ReadBoardStateCommand { get; }
    public AsyncRelayCommand StartPhaseOneCommand { get; }
    public bool IsManualDecisionVisible => _manualDecisionTestId == SelectedTestResult?.TestId;
    public bool IsKeyTestDetailVisible => SelectedTestResult?.TestId == "keys";
    public string ManualDecisionPrompt => _manualDecisionTestId switch
    {
        "hdmi" => "请观察 HDMI 输出是否正常，然后手动选择通过或失败。",
        "keys" => "请依次按下 PCBA 的上、下、左、右方向键；四键均识别后将自动通过。",
        "lcd" => "请观察 SPI LCD：背光正常、RGB 测试图案完整且稳定，无花屏、缺线、闪烁或明显亮暗异常后再判定。",
        _ => string.Empty
    };
    public string ManualPassButtonText => _manualDecisionTestId switch
    {
        "lcd" => "LCD 通过",
        _ => "HDMI 通过"
    };
    public string ManualFailButtonText => _manualDecisionTestId switch
    {
        "lcd" => "LCD 失败",
        _ => "HDMI 失败"
    };

    private static string GetAppVersion()
    {
        var assembly = typeof(MainViewModel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        return $"v{version}";
    }

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
            TestResults = BuildTestResultRecords(),
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
        _activeSessionClient = client;
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            await foreach (var testEvent in client.RunSessionAsync(SessionId, CurrentSn, _testPlan))
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
        finally
        {
            _activeSessionClient = null;
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
            TestResults = BuildTestResultRecords(),
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

        if (testEvent.TestId == "keys" && testEvent.Status == "running")
        {
            ApplyDetectedKeys(testEvent.Data);
        }

        if (testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running")
        {
            HandleTypecChargingReport(testEvent);
        }

        if (testEvent.TestId == "battery_management" && testEvent.Status == "running")
        {
            HandleBatteryDischargeReport(testEvent);
        }

        if (testEvent.TestId is "indicator_led" or "fan" && testEvent.Status == "running")
        {
            HandleVoltageMeasurementReport(testEvent);
        }

        _manualDecisionTestId = testEvent.Status == "running" && testEvent.TestId is "hdmi" or "lcd"
            ? testEvent.TestId
            : testEvent.Status is "passed" or "failed" && testEvent.TestId == _manualDecisionTestId
                    ? null
                    : _manualDecisionTestId;
        RaisePropertyChanged(nameof(IsManualDecisionVisible));
        RaisePropertyChanged(nameof(ManualDecisionPrompt));
        RaisePropertyChanged(nameof(ManualPassButtonText));
        RaisePropertyChanged(nameof(ManualFailButtonText));
        ConfirmManualPassCommand.NotifyCanExecuteChanged();
        ConfirmManualFailCommand.NotifyCanExecuteChanged();

        if (_testItemIndexes.TryGetValue(testEvent.TestId, out var index) && index < TestItems.Count)
        {
            TestItems[index].State = testEvent.Status switch
            {
                "running" => TestItemState.Running,
                "passed" => TestItemState.Passed,
                _ => TestItemState.Failed
            };
        }

        if (testEvent.TestId == "battery_management")
        {
            BatteryStatus = testEvent.Status;
        }

        OperatorInstruction = testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running"
            ? "请插入 TYPE-C 充电器，系统将自动检测充电电压和电流。"
            : testEvent.Status == "running"
            ? $"正在检测：{testEvent.TestId}。"
            : $"{testEvent.TestId}：{testEvent.Status}。";
        AppendLog($"{testEvent.TestId}: {testEvent.Status} ({testEvent.Message})");
    }

    private static string GetTestDisplayName(string testId) => testId switch
    {
        "board_state" => "板状态",
        "hdmi" => "HDMI",
        "keys" => "按键",
        "lcd" => "SPI LCD屏",
        "wifi" => "WiFi",
        "bluetooth" => "蓝牙",
        "fingerprint" => "SPI 指纹模组",
        "typec_fast_charge" => "TYPE-C 快充",
        "typec_camera" => "TYPE-C 相机",
        "tf" => "TF 卡",
        "indicator_led" => "指示灯板",
        "fan" => "风扇",
        "otg" => "USB OTG口",
        "battery_management" => "放电测试",
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

    private void ApplyDetectedKeys(IReadOnlyDictionary<string, object?> data)
    {
        foreach (var keyId in GetStringValues(data, "detectedKeys"))
        {
            SetDirectionalKeyDetected(keyId);
        }

        SetDirectionalKeyDetected(GetDataString(data, "key", string.Empty));
        RaisePropertyChanged(nameof(ManualDecisionPrompt));
    }

    private void SetDirectionalKeyDetected(string keyId)
    {
        var key = DirectionalKeys.FirstOrDefault(item => item.Id.Equals(keyId, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            key.IsDetected = true;
        }
    }

    private bool AreAllDirectionalKeysDetected() => DirectionalKeys.All(key => key.IsDetected);

    private static IEnumerable<string> GetStringValues(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        return value is IEnumerable<string> values ? values : [];
    }

    private async void HandleTypecChargingReport(TestSessionEvent testEvent)
    {
        if (_activeSessionClient is null || !_automaticDecisionTests.Add(testEvent.TestId) || !GetDataBoolean(testEvent.Data, "readyForHostDecision"))
        {
            return;
        }

        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        try
        {
            if (_jk5506Service is not null)
            {
                await _jk5506Service.PrepareChargeTestAsync(GetParameterInt(parameters, "batterySimulationVoltageMv", 7400));
                AppendLog("JK5506 battery simulator set for TYPE-C charging test.");
            }
        }
        catch (Exception ex)
        {
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "battery_simulator_communication_error");
            AppendLog($"JK5506 preparation failed: {ex.Message}");
            return;
        }
        var voltage = GetDataInt(testEvent.Data, "chargeVoltageMv");
        var current = GetDataInt(testEvent.Data, "chargeCurrentMa");
        var passed = GetDataBoolean(testEvent.Data, "pmicCommunicationOk") &&
            GetDataBoolean(testEvent.Data, "chargerConnected") &&
            voltage >= GetParameterInt(parameters, "chargeVoltageMinMv", 7400) &&
            voltage <= GetParameterInt(parameters, "chargeVoltageMaxMv", 8400) &&
            current >= GetParameterInt(parameters, "chargeCurrentMinMa", 500) &&
            current <= GetParameterInt(parameters, "chargeCurrentMaxMa", 3000);
        var reason = passed ? "charge_values_in_range" : "charge_values_out_of_range";
        await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, passed, reason);
        AppendLog($"TYPE-C charging automatic decision: {(passed ? "PASS" : "FAIL")} ({reason})");
    }

    private async void HandleVoltageMeasurementReport(TestSessionEvent testEvent)
    {
        if (_activeSessionClient is null) return;
        if (!GetDataBoolean(testEvent.Data, "measureRequest"))
        {
            if (_voltageControlCommands.Add($"{testEvent.TestId}:high"))
            {
                await _activeSessionClient.SubmitTestControlAsync(SessionId, testEvent.TestId, "high");
                AppendLog($"{testEvent.TestId} control: high");
            }
            return;
        }
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        if (phase is not "high" and not "low") return;
        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        try
        {
            var expected = GetParameterInt(parameters, phase == "high" ? "highCommandExpectedVoltageMv" : "lowCommandExpectedVoltageMv", 0);
            var voltage = ConnectionMode == PcbaConnectionMode.AdbForward && _jxTvmService?.IsEnabled == true
                ? await _jxTvmService.ReadChannelVoltageMvAsync(GetParameterInt(parameters, "channel", 1))
                : expected;
            var passed = Math.Abs(voltage - expected) <= GetParameterInt(parameters, "toleranceMv", 150);
            _voltagePhaseResults[$"{testEvent.TestId}:{phase}"] = passed;
            AppendLog($"{testEvent.TestId} {phase}: {voltage}mV, expected {expected}mV, {(passed ? "PASS" : "FAIL")}");
            if (phase == "low")
            {
                var finalPassed = _voltagePhaseResults.GetValueOrDefault($"{testEvent.TestId}:high") && passed;
                await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, finalPassed, finalPassed ? "voltage_in_range" : "voltage_out_of_range");
            }
            else if (_voltageControlCommands.Add($"{testEvent.TestId}:low"))
            {
                await _activeSessionClient.SubmitTestControlAsync(SessionId, testEvent.TestId, "low");
                AppendLog($"{testEvent.TestId} control: low");
            }
        }
        catch (Exception ex)
        {
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "voltage_meter_communication_error");
            AppendLog($"JX-TVM measurement failed: {ex.Message}");
        }
    }

    private async void HandleBatteryDischargeReport(TestSessionEvent testEvent)
    {
        if (_activeSessionClient is null || !_automaticDecisionTests.Add(testEvent.TestId) || !GetDataBoolean(testEvent.Data, "readyForHostDecision")) return;
        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        var voltage = GetDataInt(testEvent.Data, "dischargeVoltageMv");
        var current = GetDataInt(testEvent.Data, "dischargeCurrentMa");
        var passed = voltage >= GetParameterInt(parameters, "dischargeVoltageMinMv", 7000) &&
            voltage <= GetParameterInt(parameters, "dischargeVoltageMaxMv", 7600) &&
            current >= GetParameterInt(parameters, "dischargeCurrentMinMa", 100) &&
            current <= GetParameterInt(parameters, "dischargeCurrentMaxMa", 1500);
        await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, passed, passed ? "discharge_values_in_range" : "discharge_values_out_of_range");
        AppendLog($"Battery discharge automatic decision: {(passed ? "PASS" : "FAIL")}");
    }

    private static int GetDataInt(IReadOnlyDictionary<string, object?> data, string key)
    {
        return int.TryParse(GetDataString(data, key, "0"), out var value) ? value : 0;
    }

    private static bool GetDataBoolean(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null) return false;
        if (value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False) return element.GetBoolean();
        return bool.TryParse(value.ToString(), out var result) && result;
    }

    private static int GetParameterInt(IReadOnlyDictionary<string, object?> parameters, string key, int fallback) =>
        int.TryParse(GetDataString(parameters, key, fallback.ToString()), out var value) ? value : fallback;

    public async Task InitializeAsync()
    {
        if (_bluetoothBroadcasterService is not null)
        {
            try { await _bluetoothBroadcasterService.ConfigureAsync(); AppendLog("Bluetooth broadcaster configured."); }
            catch (Exception ex) { AppendLog($"Bluetooth broadcaster setup failed: {ex.Message}"); }
        }
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

    private IReadOnlyList<TestResultRecord> BuildTestResultRecords() => TestResults
        .Select(result => new TestResultRecord
        {
            TestId = result.TestId,
            Status = result.StateLabel,
            ResultCode = result.ResultCode,
            Message = result.Message,
            Data = result.Data
        })
        .ToArray();

    private static IReadOnlyList<TestPlanItem> BuildActiveTestPlan(AppConfiguration configuration)
    {
        var enabled = configuration.TestPlan.EnabledTests
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabled = configuration.TestPlan.DisabledTests
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = enabled.Count > 0
            ? AllTestPlan.Where(item => enabled.Contains(item.Id)).ToList()
            : AllTestPlan.Where(item => !disabled.Contains(item.Id)).ToList();

        if (plan.All(item => item.Id != "board_state"))
        {
            plan.Insert(0, AllTestPlan[0]);
        }

        return plan
            .Select(item => new TestPlanItem
            {
                Id = item.Id,
                Parameters = GetTestParameters(configuration, item.Id)
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> GetTestParameters(AppConfiguration configuration, string testId)
    {
        if (!configuration.TestPlan.TestParameters.TryGetValue(testId, out var parameters))
        {
            return new Dictionary<string, object?>();
        }

        var result = parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase);
        if (testId == "bluetooth" && !string.IsNullOrWhiteSpace(configuration.BluetoothBroadcaster.BroadcastName))
        {
            result["targetName"] = configuration.BluetoothBroadcaster.BroadcastName;
        }
        return result;
    }

    private static IReadOnlyList<TestItemViewModel> BuildTestItems(IReadOnlyList<TestPlanItem> testPlan)
    {
        return testPlan
            .Select((item, index) => new TestItemViewModel(GetTestDisplayName(item.Id), index < testPlan.Count - 1))
            .ToArray();
    }

    private void ResetTestItems()
    {
        _manualDecisionTestId = null;
        _automaticDecisionTests.Clear();
        _voltagePhaseResults.Clear();
        _voltageControlCommands.Clear();
        foreach (var key in DirectionalKeys)
        {
            key.IsDetected = false;
        }
        foreach (var item in TestItems)
        {
            item.State = TestItemState.Pending;
        }

        foreach (var result in TestResults)
        {
            result.Reset();
        }

        SelectedTestResult = TestResults.FirstOrDefault();
        RaisePropertyChanged(nameof(IsManualDecisionVisible));
        RaisePropertyChanged(nameof(ManualDecisionPrompt));
        RaisePropertyChanged(nameof(ManualPassButtonText));
        RaisePropertyChanged(nameof(ManualFailButtonText));
        ConfirmManualPassCommand.NotifyCanExecuteChanged();
        ConfirmManualFailCommand.NotifyCanExecuteChanged();
    }

    private async void SubmitManualDecision(bool passed)
    {
        if (!IsManualDecisionVisible || _activeSessionClient is null)
        {
            return;
        }

        var testId = _manualDecisionTestId!;
        _manualDecisionTestId = null;
        var displayName = GetTestDisplayName(testId);
        OperatorInstruction = passed ? $"{displayName} 已确认通过，继续后续测试。" : $"{displayName} 已确认失败，测试将停止。";
        AppendLog($"{testId} manual decision: {(passed ? "PASS" : "FAIL")}");
        RaisePropertyChanged(nameof(IsManualDecisionVisible));
        RaisePropertyChanged(nameof(ManualDecisionPrompt));
        RaisePropertyChanged(nameof(ManualPassButtonText));
        RaisePropertyChanged(nameof(ManualFailButtonText));
        ConfirmManualPassCommand.NotifyCanExecuteChanged();
        ConfirmManualFailCommand.NotifyCanExecuteChanged();

        try
        {
            await _activeSessionClient.SubmitOperatorDecisionAsync(SessionId, testId, passed);
        }
        catch (Exception ex)
        {
            LastResult = "Manual decision send failed";
            OperatorInstruction = $"{displayName} 判定未发送到设备，请检查连接后重新测试。";
            AppendLog($"{testId} operator decision send failed: {ex.Message}");
        }
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
