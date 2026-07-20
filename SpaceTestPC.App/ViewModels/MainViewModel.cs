using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SpaceTestPC.App.Models;
using SpaceTestPC.App.Services;

namespace SpaceTestPC.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public event EventHandler<TestItemViewModel>? SequenceAdvanceRequested;
    public event EventHandler<TestSessionRecord>? HistoryRecordFound;
    private static bool UseUnifiedSessionProtocol => true;
    private static readonly IReadOnlyList<TestPlanItem> AllTestPlan =
    [
        new() { Id = "board_state" }, new() { Id = "hdmi" }, new() { Id = "keys" }, new() { Id = "lcd" },
        new() { Id = "ethernet" }, new() { Id = "wifi" }, new() { Id = "bluetooth" }, new() { Id = "fingerprint" },
        new() { Id = "battery_management" }, new() { Id = "typec_fast_charge" }, new() { Id = "typec_camera" }, new() { Id = "tf" }, new() { Id = "usb2_3" },
        new() { Id = "pcba_test_points" }, new() { Id = "indicator_led" }, new() { Id = "fan" }, new() { Id = "otg" }
    ];

    // Change this value during deployment; operators do not choose the transport mode.
    private const PcbaConnectionMode ConnectionMode = PcbaConnectionMode.AdbForward;
    private const string BoardStateItemName = "板状态";
    private const string BluetoothItemName = "蓝牙";
    private const string WifiItemName = "WiFi";
    private const string EthernetItemName = "网线";
    private const string BatteryItemName = "鐢垫睜";

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
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _hostDecisionData = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _voltageControlCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _keyCountdownTimer;
    private readonly IReadOnlyList<TestPlanItem> _testPlan;
    private readonly IReadOnlyDictionary<string, int> _testItemIndexes;
    private readonly bool _allowSnMismatchForDebug;
    private readonly int _keyTestTimeoutMs;
    private readonly BluetoothScanRequest _bluetoothRequest = new()
    {
        TargetName = "yctc_bt_test_01",
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
    private bool _isHistoryLoaded;
    private bool _isQueryPage;
    private string _queryText = string.Empty;
    private TestItemViewModel? _currentTestItem;
    private bool _isMockSession;
    private string _historySummary = string.Empty;
    private string? _manualDecisionTestId;
    private IPcbaCommandClient? _activeSessionClient;
    private readonly HashSet<string> _automaticDecisionTests = new(StringComparer.OrdinalIgnoreCase);
    private bool _isContinuousTestEnabled;
    private bool _isSessionRunning;
    private DateTimeOffset? _keyDeadline;
    private TestSessionEvent? _latestKeyTestEvent;

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
        _allowSnMismatchForDebug = appConfiguration.TestPlan.AllowSnMismatchForDebug;
        _keyTestTimeoutMs = GetConfiguredKeyTimeoutMs(appConfiguration);
        _keyCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _keyCountdownTimer.Tick += (_, _) =>
        {
            if (_latestKeyTestEvent is null || _latestKeyTestEvent.Status != "running")
            {
                _keyCountdownTimer.Stop();
                return;
            }

            OperatorInstruction = BuildKeyTestInstruction(_latestKeyTestEvent);
        };
        _isContinuousTestEnabled = appConfiguration.TestPlan.Continuous.EnabledByDefault;
        _testPlan = BuildActiveTestPlan(appConfiguration);
        _testItemIndexes = _testPlan
            .Select((item, index) => new { item.Id, index })
            .ToDictionary(item => item.Id, item => item.index);

        ScanCommand = new AsyncRelayCommand(() => HandleScanAsync(isMockSession: false), () => !string.IsNullOrWhiteSpace(ScannerInput));
        StartMockSessionCommand = new RelayCommand(StartMockSession);
        ToggleContinuousTestCommand = new RelayCommand(ToggleContinuousTest);
        ConfirmManualPassCommand = new RelayCommand(() => SubmitManualDecision(true), () => IsManualDecisionVisible);
        ConfirmManualFailCommand = new RelayCommand(() => SubmitManualDecision(false), () => IsManualDecisionVisible);
        ReadBoardStateCommand = new AsyncRelayCommand(ReadBoardStateAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        StartPhaseOneCommand = new AsyncRelayCommand(StartPhaseOneAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        ShowTestPageCommand = new RelayCommand(() => IsQueryPage = false);
        ShowQueryPageCommand = new AsyncRelayCommand(ShowQueryPageAsync);
        QueryRecordsCommand = new AsyncRelayCommand(RefreshQueryRecordsAsync);

        Logs = new ObservableCollection<string>();
        RecentSessions = new ObservableCollection<string>();
        QuerySessions = new ObservableCollection<TestSessionRecord>();
        TestItems = new ObservableCollection<TestItemViewModel>(BuildTestItems(_testPlan));
        CurrentTestItem = TestItems.FirstOrDefault();
        TestResults = new ObservableCollection<TestResultViewModel>(
            _testPlan
                .Select(item => new TestResultViewModel(item.Id, GetTestDisplayName(item.Id))));
        DirectionalKeys = new ObservableCollection<DirectionalKeyViewModel>
        {
            new("up", "上"), new("down", "下"), new("left", "左"), new("right", "右"), new("confirm", "确认")
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
        private set
        {
            if (SetProperty(ref _lastResult, value))
            {
                RaisePropertyChanged(nameof(FinalVerdictDisplay));
                RaisePropertyChanged(nameof(FinalVerdictForeground));
            }
        }
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

    public string LogsText => string.Join(Environment.NewLine, Logs.Reverse());

    public string AppVersion { get; } = GetAppVersion();
    public string WindowTitle => $"妫€娴嬪伐浣滃彴 {AppVersion}";
    public int TestOverviewColumns { get; }
    public string SnPolicyModeName => _allowSnMismatchForDebug ? "开发模式" : "生产模式";
    public string SnPolicyModeDescription => _allowSnMismatchForDebug
        ? "允许板端 SN 与扫码 SN 不一致时继续测试，但不会覆盖已写入的板端 SN。"
        : "要求板端 SN 与扫码 SN 一致；不一致时禁止继续测试。";
    public string SnPolicyModeForeground => _allowSnMismatchForDebug ? "#F97316" : "#16A34A";
    public string SnPolicyModeBackground => _allowSnMismatchForDebug ? "#FFF7ED" : "#ECFDF3";
    public bool IsMockVisible => _allowSnMismatchForDebug;
    public string FinalVerdictDisplay => LastResult switch
    {
        "Stage 1 passed" => "PASS",
        "Stage 1 failed" => "FAIL",
        _ => "WAIT"
    };
    public string FinalVerdictForeground => FinalVerdictDisplay switch
    {
        "PASS" => "#15803D",
        "FAIL" => "#B42318",
        _ => "#344054"
    };

    public ObservableCollection<string> Logs { get; }
    public ObservableCollection<string> RecentSessions { get; }
    public ObservableCollection<TestSessionRecord> QuerySessions { get; }
    public ObservableCollection<TestItemViewModel> TestItems { get; }
    public ObservableCollection<TestResultViewModel> TestResults { get; }
    public ObservableCollection<DirectionalKeyViewModel> DirectionalKeys { get; }
    public bool IsHistoryLoaded
    {
        get => _isHistoryLoaded;
        private set => SetProperty(ref _isHistoryLoaded, value);
    }
    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }
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
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand StartMockSessionCommand { get; }
    public RelayCommand ToggleContinuousTestCommand { get; }
    public RelayCommand ConfirmManualPassCommand { get; }
    public RelayCommand ConfirmManualFailCommand { get; }
    public AsyncRelayCommand ReadBoardStateCommand { get; }
    public AsyncRelayCommand StartPhaseOneCommand { get; }
    public RelayCommand ShowTestPageCommand { get; }
    public AsyncRelayCommand ShowQueryPageCommand { get; }
    public AsyncRelayCommand QueryRecordsCommand { get; }
    public bool IsQueryPage
    {
        get => _isQueryPage;
        private set
        {
            if (SetProperty(ref _isQueryPage, value)) RaisePropertyChanged(nameof(IsTestPage));
        }
    }
    public bool IsTestPage => !IsQueryPage;
    public bool IsContinuousTestEnabled
    {
        get => _isContinuousTestEnabled;
        private set
        {
            if (SetProperty(ref _isContinuousTestEnabled, value))
            {
                RaisePropertyChanged(nameof(ContinuousTestButtonText));
                RaisePropertyChanged(nameof(ContinuousTestStatusText));
            }
        }
    }
    public string ContinuousTestButtonText => IsContinuousTestEnabled ? "关闭连续过板" : "开启连续过板";
    public string ContinuousTestStatusText => IsContinuousTestEnabled
        ? (_isSessionRunning ? "连续过板已开启，当前板测试中" : "连续过板已开启，等待下一块扫码")
        : (_isSessionRunning ? "单板模式，当前板测试中" : "单板模式");
    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value);
    }
    public TestItemViewModel? CurrentTestItem
    {
        get => _currentTestItem;
        private set => SetProperty(ref _currentTestItem, value);
    }

    public void SelectTestResult(string testId)
    {
        var result = TestResults.FirstOrDefault(item => item.TestId == testId);
        if (result is not null)
        {
            SelectedTestResult = result;
            OperatorInstruction = BuildInstructionForSelectedResult(result);
        }
    }
    public bool IsManualDecisionVisible => _manualDecisionTestId == SelectedTestResult?.TestId;
    public bool IsKeyTestDetailVisible => SelectedTestResult?.TestId == "keys";
    public string ManualDecisionPrompt => _manualDecisionTestId switch
    {
        "hdmi" => "请观察 HDMI 输出是否正常，然后手动选择通过或失败。",
        "keys" => "请依次按下 PCBA 的上、下、左、右方向键和确认键；五键均识别后将自动通过。",
        "lcd" => "请观察 LCD：背光正常、RGB 测试图案完整且稳定，无花屏、缺线、闪烁或明显亮暗异常后再判定。",
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
        _ = HandleScanAsync(isMockSession: true);
    }

    private void ToggleContinuousTest()
    {
        IsContinuousTestEnabled = !IsContinuousTestEnabled;
        AppendLog(IsContinuousTestEnabled ? "Continuous line mode enabled." : "Continuous line mode disabled.");
        OperatorInstruction = IsContinuousTestEnabled
            ? "连续过板模式已开启。当前板结束后将保留记录，并等待下一块扫码。"
            : "连续过板模式已关闭。";
        RaisePropertyChanged(nameof(ContinuousTestStatusText));
        UpdateDebugOutput();
    }

    private async Task HandleScanAsync(bool isMockSession)
    {
        var sn = _scannerService.Normalize(ScannerInput);
        if (string.IsNullOrWhiteSpace(sn))
        {
            return;
        }

        CurrentSn = sn;
        _isMockSession = isMockSession;
        SessionId = Guid.NewGuid().ToString("N");
        LastResult = "SN scanned";
        OperatorInstruction = "SN 已确认，正在自动执行检测。请保持产品连接稳定。";
        ScannerInput = string.Empty;
        ResetTestItems();
        IsHistoryLoaded = false;
        HistorySummary = string.Empty;
        _isSessionRunning = true;
        RaisePropertyChanged(nameof(ContinuousTestStatusText));
        AppendLog($"Scan received: {CurrentSn}");
        AppendLog($"Session created: {SessionId}");
        UpdateDebugOutput();

        var history = await _databaseRepository.GetLatestSessionBySnAsync(CurrentSn);
        if (history is not null && ConnectionMode == PcbaConnectionMode.Mock)
        {
            HistoryRecordFound?.Invoke(this, history);
            return;
        }
        if (history is not null)
        {
            AppendLog("History exists. ADB mode will start a new test session.");
        }

        AppendLog(history is null ? "No history found. Auto-starting Stage 1." : "Auto-starting Stage 1.");
        StartPhaseOneCommand.Execute(null);
    }

    private void LoadHistoryRecord(TestSessionRecord record)
    {
        IsHistoryLoaded = true;
        HistorySummary = $"Latest record: {record.Session.FinalVerdict} 路 {record.Session.EndTime?.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
        LastResult = $"History: {record.Session.FinalVerdict}";
        OperatorInstruction = "Historical test result loaded. Review the details or select Re-test to start a new session.";
        if (record.BoardState is not null) ApplyBoardState(record.BoardState);

        ResetTestItems();
        foreach (var result in record.TestResults)
        {
            var status = string.Equals(result.Status, "PASS", StringComparison.OrdinalIgnoreCase)
                ? "passed"
                : string.Equals(result.Status, "SKIPPED", StringComparison.OrdinalIgnoreCase)
                    ? "skipped"
                    : "failed";
            ApplyTestReport(new TestSessionEvent
            {
                Event = "test.report",
                TestId = result.TestId,
                Status = status,
                ResultCode = result.ResultCode,
                Message = result.Message,
                Data = result.Data,
                Timestamp = record.Session.EndTime ?? record.Session.StartTime
            });
        }

        SelectedTestResult = TestResults.FirstOrDefault(item => item.State == TestItemState.Failed)
            ?? TestResults.FirstOrDefault(item => item.State == TestItemState.Passed)
            ?? TestResults.FirstOrDefault();
        AppendLog($"Historical record loaded: {record.Session.SessionId}");
        UpdateDebugOutput();
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
            await EnsureBoardSnAsync(client, state);
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
        if (IsHistoryLoaded)
        {
            IsHistoryLoaded = false;
            HistorySummary = string.Empty;
            SessionId = Guid.NewGuid().ToString("N");
            ResetTestItems();
            LastResult = "Re-test started";
            AppendLog($"Re-test requested for SN: {CurrentSn}");
        }

        if (string.IsNullOrWhiteSpace(SessionId))
        {
            SessionId = Guid.NewGuid().ToString("N");
        }

        if (UseUnifiedSessionProtocol)
        {
            await RunUnifiedSessionAsync();
        }
        else
        {
            await RunLegacyPhaseOneAsync();
        }

        _isSessionRunning = false;
        PrepareForNextBoard();
        RaisePropertyChanged(nameof(ContinuousTestStatusText));
        UpdateDebugOutput();
    }

    private async Task RunLegacyPhaseOneAsync()
    {
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            SessionId = Guid.NewGuid().ToString("N");
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
            state = await EnsureBoardSnAsync(client, state);
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
            OperatorInstruction = BuildSessionCompletionInstruction(stagePassed ? "Pass" : "Fail");
        }
        catch (Exception ex)
        {
            LastResult = "Stage 1 failed";
            OperatorInstruction = "通信异常，当前记录将保存。请重新连接 OTG/ADB 后继续扫描下一块。";
            AppendLog($"Stage 1 execution failed: {ex.Message}");
        }
        finally
        {
            UpdateDebugOutput();
        }

        finalVerdict = ResolveFinalVerdict(finalVerdict);
        LastResult = finalVerdict == "Pass" ? "Stage 1 passed" : "Stage 1 failed";
        OperatorInstruction = BuildSessionCompletionInstruction(finalVerdict);

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
        if (state is not null)
        {
            try
            {
                await client.SyncSessionSummaryAsync(SessionId, CurrentSn, state.BoardId, finalVerdict, record.TestResults);
                AppendLog("Board summary synced.");
            }
            catch (Exception ex)
            {
                AppendLog($"Board summary sync failed: {ex.Message}");
            }
        }
        await LoadRecentSessionsAsync();
        await SetMockQueryDefaultAsync();
        AppendLog("Session persisted.");
        UpdateDebugOutput();
    }

    private async Task RunUnifiedSessionAsync()
    {
        AppendLog($"Session start requested: mode={ConnectionMode}, session={SessionId}, sn={CurrentSn}, tests={string.Join(",", _testPlan.Select(item => item.Id))}");
        LastResult = "Stage 1 running";
        OperatorInstruction = "正在接收底层测试结果，请勿断开产品连接。";

        var client = _pcbaCommandClientFactory.Create(ConnectionMode);
        _activeSessionClient = client;
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            AppendLog("ADB/sys.get_board_state request sent.");
            state = await client.GetBoardStateAsync(SessionId, CurrentSn);
            AppendLog($"ADB/sys.get_board_state response: boardId={state.BoardId}, boardSn={state.BoardSn}, mode={state.TestMode}, state={state.CurrentState}");
            ApplyBoardState(state);
            state = await EnsureBoardSnAsync(client, state);
            SetTestItemState(BoardStateItemName, TestItemState.Passed);

            AppendLog("ADB/session.start request sent.");
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
                    if (testEvent.Status == "failed")
                    {
                        ApplySessionFailureFallback(testEvent);
                    }
                    LastResult = finalVerdict == "Pass" ? "Stage 1 passed" : "Stage 1 failed";
                    OperatorInstruction = BuildSessionCompletionInstruction(finalVerdict);
                    AppendLog($"Session completed: status={testEvent.Status}, code={testEvent.ResultCode}, message={testEvent.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LastResult = "Stage 1 failed";
            OperatorInstruction = "通信异常，当前记录将保存。请重新连接 OTG/ADB 后继续扫描下一块。";
            AppendLog($"Session exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _activeSessionClient = null;
        }

        finalVerdict = ResolveFinalVerdict(finalVerdict);
        LastResult = finalVerdict == "Pass" ? "Stage 1 passed" : "Stage 1 failed";
        OperatorInstruction = BuildSessionCompletionInstruction(finalVerdict);

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
        if (state is not null)
        {
            try
            {
                await client.SyncSessionSummaryAsync(SessionId, CurrentSn, state.BoardId, finalVerdict, record.TestResults);
                AppendLog("Board summary synced.");
            }
            catch (Exception ex)
            {
                AppendLog($"Board summary sync failed: {ex.Message}");
            }
        }
        await LoadRecentSessionsAsync();
        await SetMockQueryDefaultAsync();
        AppendLog("Session persisted.");
        UpdateDebugOutput();
    }

    private void PrepareForNextBoard()
    {
        if (string.IsNullOrWhiteSpace(CurrentSn))
        {
            return;
        }

        if (IsContinuousTestEnabled)
        {
            CurrentSn = string.Empty;
            SessionId = string.Empty;
            BoardId = "-";
            BoardState = "Waiting";
            TestMode = "Ready";
            ScannerInput = string.Empty;
            OperatorInstruction = "上一块记录已保存。请插入下一块并扫描 SN。";
            AppendLog("Ready for next board scan.");
            UpdateDebugOutput();
        }
    }

    private async Task<BoardState> EnsureBoardSnAsync(IPcbaCommandClient client, BoardState state)
    {
        if (string.Equals(state.BoardSn, CurrentSn, StringComparison.Ordinal))
        {
            AppendLog($"Board SN already matches scanned SN: {CurrentSn}");
            return state;
        }

        if (!string.IsNullOrWhiteSpace(state.BoardSn))
        {
            if (_allowSnMismatchForDebug)
            {
                AppendLog($"DEBUG SN mismatch allowed: boardSn={state.BoardSn}, scannedSn={CurrentSn}. Board SN will not be overwritten.");
                OperatorInstruction = $"调试模式：板端 SN({state.BoardSn}) 与扫码 SN({CurrentSn}) 不一致，已允许继续测试。";
                return state;
            }

            throw new InvalidOperationException($"Board already has a different SN ({state.BoardSn}); scanned SN is {CurrentSn}.");
        }

        AppendLog($"Writing scanned SN to board: {CurrentSn}");
        var response = await client.WriteSnAsync(SessionId, CurrentSn, state.BoardId);
        if (response.ResultCode != 0)
        {
            throw new InvalidOperationException($"Board SN write failed ({response.ResultCode}): {response.Message}");
        }

        var updatedState = await client.GetBoardStateAsync(SessionId, CurrentSn);
        if (!string.Equals(updatedState.BoardSn, CurrentSn, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Board SN verification failed: expected {CurrentSn}, got {updatedState.BoardSn}.");
        }

        ApplyBoardState(updatedState);
        AppendLog($"Board SN written and verified: {CurrentSn}");
        return updatedState;
    }

    private void ApplySessionFailureFallback(TestSessionEvent sessionCompletedEvent)
    {
        var failedTestId = TryExtractFirstFailedTestId(sessionCompletedEvent.Message);
        if (!string.IsNullOrWhiteSpace(failedTestId))
        {
            ForceFailTestItem(failedTestId, sessionCompletedEvent);
            return;
        }

        if (CurrentTestItem is not null)
        {
            ForceFailTestItem(CurrentTestItem.TestId, sessionCompletedEvent);
            return;
        }

        var runningResult = TestResults.FirstOrDefault(result => result.State == TestItemState.Running);
        if (runningResult is not null)
        {
            ForceFailTestItem(runningResult.TestId, sessionCompletedEvent);
        }
    }

    private void ForceFailTestItem(string testId, TestSessionEvent sessionCompletedEvent)
    {
        var result = TestResults.FirstOrDefault(item => item.TestId == testId);
        if (result is null || result.State != TestItemState.Running)
        {
            return;
        }

        var failedEvent = new TestSessionEvent
        {
            Event = "test.report",
            TestId = testId,
            Status = "failed",
            ResultCode = sessionCompletedEvent.ResultCode,
            Message = string.IsNullOrWhiteSpace(sessionCompletedEvent.Message)
                ? "Session failed before final test item status was reported"
                : sessionCompletedEvent.Message,
            Timestamp = sessionCompletedEvent.Timestamp,
            Data = new Dictionary<string, object?>
            {
                ["failureSource"] = "session_completed_fallback"
            }
        };

        ApplyTestReport(failedEvent);
    }

    private static string? TryExtractFirstFailedTestId(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        const string marker = "first failed:";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = message[(index + marker.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var commaIndex = value.IndexOf(',');
        if (commaIndex >= 0)
        {
            value = value[..commaIndex].Trim();
        }

        return value.Length == 0 ? null : value;
    }

    private void ApplyTestReport(TestSessionEvent testEvent)
    {
        if (testEvent.TestId == "battery_management" &&
            testEvent.Status is "passed" or "failed" &&
            _hostDecisionData.TryGetValue(testEvent.TestId, out var hostData))
        {
            var merged = hostData.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in testEvent.Data)
            {
                merged[pair.Key] = pair.Value;
            }

            testEvent = new TestSessionEvent
            {
                Event = testEvent.Event,
                TestId = testEvent.TestId,
                Status = testEvent.Status,
                ResultCode = testEvent.ResultCode,
                Message = testEvent.Message,
                Timestamp = testEvent.Timestamp,
                Data = merged
            };
        }

        var result = TestResults.FirstOrDefault(item => item.TestId == testEvent.TestId);
        result?.Apply(testEvent);
        if (result is not null && ShouldSelectTestResult(testEvent, result))
        {
            SelectedTestResult = result;
        }

        if (testEvent.TestId == "keys" && testEvent.Status == "running")
        {
            if (IsInitialKeyTestReport(testEvent.Data))
            {
                ResetDirectionalKeys();
            }
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
                "skipped" => TestItemState.Skipped,
                _ => TestItemState.Failed
            };
            if (testEvent.Status == "running" && !ReferenceEquals(CurrentTestItem, TestItems[index]))
            {
                CurrentTestItem = TestItems[index];
                SequenceAdvanceRequested?.Invoke(this, CurrentTestItem);
            }
        }

        if (testEvent.TestId == "battery_management")
        {
            BatteryStatus = testEvent.Status;
        }

        if (testEvent.TestId == "keys")
        {
            _logService.Event("test.report", testEvent);
            _latestKeyTestEvent = testEvent;
            if (testEvent.Status == "running")
            {
                var remainingMs = GetDataInt(testEvent.Data, "remainingMs");
                if (remainingMs <= 0) remainingMs = GetDataInt(testEvent.Data, "timeoutMs");
                if (remainingMs <= 0) remainingMs = _keyTestTimeoutMs;
                _keyDeadline = DateTimeOffset.Now.AddMilliseconds(remainingMs);
                if (!_keyCountdownTimer.IsEnabled)
                {
                    _keyCountdownTimer.Start();
                }
            }
            else
            {
                _keyDeadline = null;
                _keyCountdownTimer.Stop();
            }
            OperatorInstruction = BuildKeyTestInstruction(testEvent);
            AppendLog(FormatTestEventLog(testEvent));
            return;
        }

        if (testEvent.TestId == "bluetooth")
        {
            _logService.Event("test.report", testEvent);
            OperatorInstruction = BuildBluetoothInstruction(testEvent);
            AppendLog(FormatTestEventLog(testEvent));
            return;
        }

        if (testEvent.TestId == "wifi")
        {
            _logService.Event("test.report", testEvent);
            OperatorInstruction = BuildWifiInstruction(testEvent);
            AppendLog(FormatTestEventLog(testEvent));
            return;
        }

        if (testEvent.TestId == "battery_management")
        {
            _logService.Event("test.report", testEvent);
            OperatorInstruction = BuildBatteryDischargeInstruction(testEvent);
            AppendLog(FormatTestEventLog(testEvent));
            return;
        }

        if (testEvent.TestId == "ethernet")
        {
            _logService.Event("test.report", testEvent);
            OperatorInstruction = BuildEthernetInstruction(testEvent);
            AppendLog(FormatTestEventLog(testEvent));
            return;
        }

        _logService.Event("test.report", testEvent);
        OperatorInstruction = BuildGeneralTestInstruction(testEvent);
        AppendLog(FormatTestEventLog(testEvent));
    }

    private string BuildGeneralTestInstruction(TestSessionEvent testEvent)
    {
        if (testEvent.TestId == "hdmi")
        {
            return testEvent.Status switch
            {
                "running" => "正在检测HDMI。",
                "passed" => "HDMI测试完成。",
                "failed" => "HDMI测试失败。",
                "skipped" => "HDMI：本轮不测试。",
                _ => "HDMI状态更新。"
            };
        }

        return testEvent.TestId == "usb2_3" && testEvent.Status == "running"
            ? "请确保测试前已经使用 2.0 U盘和 3.0 U盘插入过需要测试的 USB 口，并已经生成 USB 汇总文件。"
            : testEvent.TestId == "pcba_test_points" && testEvent.Status == "running"
            ? "正在读取 PCBA 32 通道测试点电压，系统将自动判断是否在阈值范围内。"
            : testEvent.TestId == "ethernet" && testEvent.Status == "running" && testEvent.Message.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            ? "网口测试完成，请拔掉网线，准备进行 Wi-Fi 测试。"
            : testEvent.TestId == "ethernet" && testEvent.Status == "running"
            ? "请插入网线，系统将关闭 Wi-Fi 并检测有线网络。"
            : testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running" &&
              GetDataString(testEvent.Data, "phase", string.Empty) is "wait_ready" or "ready" or "wait_manual_charger_insert" or "wait_charger"
            ? BuildFastChargeWaitingInstruction(testEvent.Data)
            : testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running" && GetDataString(testEvent.Data, "phase", string.Empty) == "charger_detected"
            ? "已检测到充电器，正在采样充电电压和电流。"
            : testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running" && !GetDataBoolean(testEvent.Data, "readyForHostDecision")
            ? "正在准备快充测试，系统将自动允许充电并等待充电器接入。"
            : testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running"
            ? "请插入 TYPE-C 充电器，系统将按 7.4V 电池模拟条件采样充电电流，并等待上位机判定。"
            : testEvent.TestId == "typec_camera" && testEvent.Status == "running" && GetDataString(testEvent.Data, "phase", string.Empty) == "wait_camera"
            ? "请插入 TYPE-C 相机，系统检测到 /dev/video 节点后会自动继续测试。"
            : testEvent.TestId == "typec_camera" && testEvent.Status == "running" && GetDataString(testEvent.Data, "phase", string.Empty) == "camera_detected"
            ? "已检测到 TYPE-C 相机，正在进行相机拉流测试。"
            : testEvent.Status == "skipped"
            ? $"{GetTestDisplayName(testEvent.TestId)}：本轮不测试。"
            : testEvent.Status == "running"
            ? $"正在检测：{GetTestDisplayName(testEvent.TestId)}。"
            : testEvent.Status == "passed"
            ? $"{GetTestDisplayName(testEvent.TestId)}测试完成。"
            : testEvent.Status == "failed"
            ? $"{GetTestDisplayName(testEvent.TestId)}测试失败。"
            : $"{GetTestDisplayName(testEvent.TestId)}：{testEvent.Status}。";
    }

    private string BuildInstructionForSelectedResult(TestResultViewModel result)
    {
        var testEvent = new TestSessionEvent
        {
            Event = "test.report",
            TestId = result.TestId,
            Status = result.State switch
            {
                TestItemState.Running => "running",
                TestItemState.Passed => "passed",
                TestItemState.Failed => "failed",
                TestItemState.Skipped => "skipped",
                _ => "pending"
            },
            ResultCode = result.ResultCode,
            Message = result.Message,
            Timestamp = result.StartedAt ?? DateTimeOffset.Now,
            Data = result.Data
        };

        return result.TestId switch
        {
            "keys" => BuildKeyTestInstruction(testEvent),
            "bluetooth" => BuildBluetoothInstruction(testEvent),
            "wifi" => BuildWifiInstruction(testEvent),
            "battery_management" => BuildBatteryDischargeInstruction(testEvent),
            "ethernet" => BuildEthernetInstruction(testEvent),
            _ => BuildGeneralTestInstruction(testEvent)
        };
    }

    private static string BuildFastChargeWaitingInstruction(IReadOnlyDictionary<string, object?> data)
    {
        var phase = GetDataString(data, "phase", string.Empty);
        var elapsedMs = GetDataInt(data, "elapsedMs");
        var seconds = elapsedMs > 0 ? elapsedMs / 1000 : 0;
        var waitReadySeconds = Math.Max(1, GetDataInt(data, "waitReadyTimeoutMs") / 1000);
        var waitChargerSeconds = Math.Max(1, GetDataInt(data, "waitChargerTimeoutMs") / 1000);
        var vbusType = GetDataString(data, "vbusType", string.Empty);
        var onlyOtgPower = vbusType is "usb_sdp" or "usb_cdp" or "otg_mode" or "powered_from_vbus";

        if (phase == "wait_ready")
        {
            var needsEthernet = GetDataBoolean(data, "requiresEthernetUnplug");
            var needsCamera = GetDataBoolean(data, "requiresCameraUnplug");
            if (needsEthernet && needsCamera)
            {
                return $"请先拔掉网线和相机，再开始板快充测试。已等待 {seconds}/{waitReadySeconds} 秒。";
            }

            if (needsEthernet)
            {
                return $"请先拔掉网线，再开始板快充测试。已等待 {seconds}/{waitReadySeconds} 秒。";
            }

            if (needsCamera)
            {
                return $"请先拔掉相机，再开始板快充测试。已等待 {seconds}/{waitReadySeconds} 秒。";
            }
        }

        if (phase == "ready")
        {
            return "已确认网线和相机均已拔掉，正在切换到板快充测试模式。";
        }

        if (phase == "wait_manual_charger_insert")
        {
            var manualWaitSeconds = Math.Max(1, GetDataInt(data, "manualInsertWaitMs") / 1000);
            return $"请插入 TYPE-C 充电线，系统将在 {manualWaitSeconds} 秒准备期结束后开始自动检测。已等待 {seconds}/{manualWaitSeconds} 秒。";
        }

        if (onlyOtgPower)
        {
            return seconds > 0
                ? $"当前仅检测到 OTG/主机口供电，请插入独立充电器。已等待 {seconds}/{waitChargerSeconds} 秒。"
                : "当前仅检测到 OTG/主机口供电，请插入独立充电器。";
        }
        return seconds > 0
            ? $"请插入 TYPE-C 充电器，系统正在等待接入。已等待 {seconds}/{waitChargerSeconds} 秒。"
            : "请插入 TYPE-C 充电器，系统正在等待接入。";
    }

    private bool ShouldSelectTestResult(TestSessionEvent testEvent, TestResultViewModel result)
    {
        if (testEvent.Status == "failed")
        {
            return true;
        }

        if (testEvent.Status != "running")
        {
            return false;
        }

        return SelectedTestResult?.State != TestItemState.Failed || SelectedTestResult.TestId == result.TestId;
    }

    private static string FormatTestEventLog(TestSessionEvent testEvent)
    {
        var data = FormatEventData(testEvent.Data);
        var hint = FormatFailureHint(testEvent);
        return data.Length == 0
            ? string.IsNullOrWhiteSpace(hint)
                ? $"[{testEvent.TestId}] {testEvent.Status}, code={testEvent.ResultCode}, msg={testEvent.Message}"
                : $"[{testEvent.TestId}] {testEvent.Status}, code={testEvent.ResultCode}, msg={testEvent.Message}, {hint}"
            : string.IsNullOrWhiteSpace(hint)
                ? $"[{testEvent.TestId}] {testEvent.Status}, code={testEvent.ResultCode}, msg={testEvent.Message}, data={data}"
                : $"[{testEvent.TestId}] {testEvent.Status}, code={testEvent.ResultCode}, msg={testEvent.Message}, {hint}, data={data}";
    }

    private static string FormatEventData(IReadOnlyDictionary<string, object?> data)
    {
        if (data.Count == 0) return string.Empty;
        return JsonSerializer.Serialize(data);
    }

    private static string FormatFailureHint(TestSessionEvent testEvent) =>
        testEvent.Status == "failed"
            ? testEvent.TestId switch
            {
                "keys" => testEvent.ResultCode switch
                {
                    4000 => "hint=3576 无法打开按键输入设备",
                    4001 => "hint=按键测试超时",
                    4002 => "hint=3576 读取按键事件失败",
                    _ => string.Empty
                },
                "bluetooth" => BuildBluetoothFailureHint(testEvent.Data),
                "wifi" => BuildWifiFailureHint(testEvent.Data),
                _ => string.Empty
            }
            : string.Empty;

    private static string BuildBluetoothFailureHint(IReadOnlyDictionary<string, object?> data)
    {
        var reason = GetDataString(data, "failureReason", string.Empty);
        var found = GetDataBoolean(data, "found");
        var matchedRssi = GetDataInt(data, "matchedRssi");
        var minRssi = GetDataInt(data, "minRssi");
        var bestSeenName = GetDataString(data, "bestSeenName", string.Empty);
        var bestSeenRssi = GetDataInt(data, "bestSeenRssi");
        return $"hint=reason:{reason}, found:{found}, matchedRssi:{matchedRssi}, minRssi:{minRssi}, bestSeen:{bestSeenName}/{bestSeenRssi}";
    }

    private static string BuildWifiFailureHint(IReadOnlyDictionary<string, object?> data)
    {
        var reason = GetDataString(data, "failureReason", string.Empty);
        var iface = GetDataString(data, "interfaceName", string.Empty);
        var ip = GetDataString(data, "ip", string.Empty);
        var connected = GetDataBoolean(data, "connected");
        var pingOk = GetDataBoolean(data, "pingOk");
        var ethernetLinkUp = GetDataBoolean(data, "ethernetLinkUp");
        return $"hint=reason:{reason}, iface:{iface}, ip:{ip}, connected:{connected}, pingOk:{pingOk}, ethernetLinkUp:{ethernetLinkUp}";
    }

    private string BuildKeyTestInstruction(TestSessionEvent testEvent)
    {
        var remainingSeconds = GetRemainingKeySeconds(testEvent);
        if (testEvent.Status == "passed")
        {
            return "按键测试通过，五个按键均已识别。";
        }

        if (testEvent.Status == "failed")
        {
            return testEvent.ResultCode switch
            {
                4000 => "按键测试失败：3576 无法打开按键输入设备，请检查 gpio-keys 与 pwrkey 节点。",
                4001 => $"按键测试失败：{FormatKeyTimeoutSeconds()} 秒内未完成上、下、左、右、确认五个按键输入。",
                4002 => "按键测试失败：3576 读取按键输入事件异常，请检查 evdev 驱动和输入节点。",
                _ => $"按键测试失败：resultCode={testEvent.ResultCode}，message={testEvent.Message}"
            };
        }

        var detected = DirectionalKeys.Where(key => key.IsDetected).Select(key => key.Label).ToArray();
        var missing = DirectionalKeys.Where(key => !key.IsDetected).Select(key => key.Label).ToArray();
        var detectedText = detected.Length == 0 ? "无" : string.Join("、", detected);
        var missingText = missing.Length == 0 ? "无" : string.Join("、", missing);
        return $"按键测试：请在 {FormatKeyTimeoutSeconds()} 秒内依次按上、下、左、右、确认键。已识别：{detectedText}；剩余：{missingText}；倒计时：{remainingSeconds} 秒。";
    }

    private int GetRemainingKeySeconds(TestSessionEvent testEvent)
    {
        if (_keyDeadline.HasValue)
        {
            var seconds = (int)Math.Ceiling((_keyDeadline.Value - DateTimeOffset.Now).TotalSeconds);
            if (seconds > 0)
            {
                return seconds;
            }
        }

        var remainingMs = GetDataInt(testEvent.Data, "remainingMs");
        if (remainingMs > 0)
        {
            return Math.Max(1, (int)Math.Ceiling(remainingMs / 1000d));
        }

        return Math.Max(1, _keyTestTimeoutMs / 1000);
    }

    private static string BuildBluetoothInstruction(TestSessionEvent testEvent)
    {
        if (testEvent.Status == "running")
        {
            return $"正在检测：蓝牙。目标名 {GetDataString(testEvent.Data, "targetName", "-")}，最小 RSSI {GetDataInt(testEvent.Data, "minRssi")}。";
        }

        if (testEvent.Status == "passed")
        {
            return $"蓝牙测试通过：名称 {GetDataString(testEvent.Data, "name", "-")}，RSSI {GetDataInt(testEvent.Data, "rssi")}。";
        }

        var reason = GetDataString(testEvent.Data, "failureReason", string.Empty);
        var matchedName = GetDataString(testEvent.Data, "matchedName", string.Empty);
        var matchedRssi = GetDataInt(testEvent.Data, "matchedRssi");
        var minRssi = GetDataInt(testEvent.Data, "minRssi");
        var bestSeenName = GetDataString(testEvent.Data, "bestSeenName", string.Empty);
        var bestSeenRssi = GetDataInt(testEvent.Data, "bestSeenRssi");
        return $"蓝牙测试失败：reason={reason}，matched={matchedName}/{matchedRssi}，bestSeen={bestSeenName}/{bestSeenRssi}，minRssi={minRssi}。";
    }

    private static string BuildWifiInstruction(TestSessionEvent testEvent)
    {
        if (testEvent.Status == "running" && GetDataBoolean(testEvent.Data, "requiresCableUnplug"))
        {
            return "请先拔掉网线，系统将在检测到网线断开后自动继续 Wi‑Fi 测试。";
        }

        if (testEvent.Status == "running")
        {
            return $"正在检测：Wi‑Fi。SSID {GetDataString(testEvent.Data, "ssid", "-")}，目标网关 {GetDataString(testEvent.Data, "routerIp", "-")}。";
        }

        if (testEvent.Status == "passed")
        {
            return $"Wi‑Fi 测试通过：IP {GetDataString(testEvent.Data, "ip", "-")}，平均延时 {GetDataInt(testEvent.Data, "avgDelayMs")} ms。";
        }

        return $"Wi‑Fi 测试失败：reason={GetDataString(testEvent.Data, "failureReason", string.Empty)}，ip={GetDataString(testEvent.Data, "ip", string.Empty)}，iface={GetDataString(testEvent.Data, "interfaceName", string.Empty)}，pingOk={GetDataBoolean(testEvent.Data, "pingOk")}。";
    }

    private static string BuildBatteryDischargeInstruction(TestSessionEvent testEvent)
    {
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var elapsedSeconds = Math.Max(0, GetDataInt(testEvent.Data, "elapsedMs") / 1000);
        var totalWaitSeconds = Math.Max(1, GetDataInt(testEvent.Data, "waitReadyTimeoutMs") / 1000);
        var samplingSeconds = Math.Max(1, GetDataInt(testEvent.Data, "samplingDurationMs") / 1000);

        if (testEvent.Status == "running" && phase == "wait_ready")
        {
            var needsEthernet = GetDataBoolean(testEvent.Data, "requiresEthernetUnplug");
            var needsCamera = GetDataBoolean(testEvent.Data, "requiresCameraUnplug");

            if (needsEthernet && needsCamera)
            {
                return $"请先拔掉网线和相机，再开始板放电测试。已等待 {elapsedSeconds}/{totalWaitSeconds} 秒。";
            }

            if (needsEthernet)
            {
                return $"请先拔掉网线，再开始板放电测试。已等待 {elapsedSeconds}/{totalWaitSeconds} 秒。";
            }

            if (needsCamera)
            {
                return $"请先拔掉相机，再开始板放电测试。已等待 {elapsedSeconds}/{totalWaitSeconds} 秒。";
            }
        }

        if (testEvent.Status == "running" && phase == "ready")
        {
            return "已确认网线和相机均已拔掉，正在切换到板放电测试模式。";
        }

        if (testEvent.Status == "running" && phase == "ready_for_host_decision")
        {
            return $"板放电测试进行中，正在采样并等待上位机判定。检测时长 {elapsedSeconds}/{samplingSeconds} 秒。";
        }

        if (testEvent.Status == "running")
        {
            return $"板放电测试进行中，上位机将采样电流并完成判定。检测时长 {elapsedSeconds}/{samplingSeconds} 秒。";
        }

        if (testEvent.Status == "passed")
        {
            return "板放电测试完成。";
        }

        return "板放电测试失败。";
    }

    private static string BuildEthernetInstruction(TestSessionEvent testEvent)
    {
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var elapsedMs = GetDataInt(testEvent.Data, "elapsedMs");
        var elapsedSeconds = elapsedMs > 0 ? elapsedMs / 1000 : 0;
        var routerIp = GetDataString(testEvent.Data, "routerIp", "-");
        var ip = GetDataString(testEvent.Data, "ip", "-");
        var reason = GetDataString(testEvent.Data, "failureReason", string.Empty);

        if (testEvent.Status == "running")
        {
            return phase switch
            {
                "wait_cable" => elapsedSeconds > 0
                    ? $"请插入网线，系统正在等待连接。已等待 {elapsedSeconds} 秒。"
                    : "请插入网线，系统正在等待连接。",
                "link_up" => "已检测到网线，正在开始网口检测。",
                "dhcp" => "网口已连通，正在获取 IP。",
                "ping" => $"网口已获取 IP，正在连通路由器 {routerIp}。",
                "ping_ok" => "网口连通正常，请拔掉网线，准备进行 Wi‑Fi 测试。",
                _ => "正在检测：网口。"
            };
        }

        if (testEvent.Status == "passed")
        {
            return $"网口测试完成：IP {ip}，路由器 {routerIp}。";
        }

        return reason switch
        {
            "ethernet_insert_timeout" => "网口测试失败：等待插入网线超时。",
            "ethernet_disconnect_wifi_failed" => "网口测试失败：断开 Wi‑Fi 连接失败。",
            "ethernet_disable_wifi_failed" => "网口测试失败：断开 Wi‑Fi 连接失败。",
            "ethernet_no_ip" => "网口测试失败：未获取到 IP。",
            "ethernet_ping_failed" => $"网口测试失败：无法连通路由器 {routerIp}。",
            "ethernet_cable_not_inserted" => "网口测试失败：未检测到网线连接。",
            _ => $"网口测试失败：reason={reason}，ip={ip}。"
        };
    }

    private static string GetTestDisplayName(string testId) => testId switch
    {
        "board_state" => "板状态",
        "hdmi" => "HDMI",
        "keys" => "按键",
        "lcd" => "LCD",
        "ethernet" => "网口",
        "wifi" => "WiFi",
        "bluetooth" => "蓝牙",
        "fingerprint" => "指纹模组",
        "typec_fast_charge" => "板快充",
        "typec_camera" => "TYPE-C 相机",
        "tf" => "TF 卡",
        "usb2_3" => "USB2.0&3.0",
        "pcba_test_points" => "PCBA测试点",
        "indicator_led" => "指示灯板",
        "fan" => "风扇",
        "otg" => "USB OTG口",
        "battery_management" => "板放电测试",
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

    private void ResetDirectionalKeys()
    {
        foreach (var key in DirectionalKeys)
        {
            key.IsDetected = false;
        }
    }

    private static bool IsInitialKeyTestReport(IReadOnlyDictionary<string, object?> data)
    {
        var detectedKeys = GetStringValues(data, "detectedKeys").ToArray();
        var currentKey = GetDataString(data, "key", string.Empty);
        var detectedMask = GetDataInt(data, "detectedMask");
        return detectedKeys.Length == 0 &&
               string.IsNullOrWhiteSpace(currentKey) &&
               detectedMask == 0;
    }

    private void SetDirectionalKeyDetected(string keyId)
    {
        if (string.Equals(keyId, "ok", StringComparison.OrdinalIgnoreCase)) keyId = "confirm";
        var key = DirectionalKeys.FirstOrDefault(item => item.Id.Equals(keyId, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            key.IsDetected = true;
        }
    }

    private bool AreAllDirectionalKeysDetected() => DirectionalKeys.All(key => key.IsDetected);

    private static int GetConfiguredKeyTimeoutMs(AppConfiguration configuration)
    {
        if (configuration.TestPlan.TestParameters.TryGetValue("keys", out var parameters) &&
            parameters.TryGetValue("timeoutMs", out var value) &&
            TryGetInt(value, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return 45000;
    }

    private string FormatKeyTimeoutSeconds() => Math.Max(1, _keyTestTimeoutMs / 1000).ToString();

    private static bool TryGetInt(object? value, out int result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case int intValue:
                result = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int)longValue;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var jsonValue):
                result = jsonValue;
                return true;
            default:
                return int.TryParse(value.ToString(), out result);
        }
    }

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
        if (_activeSessionClient is null || !GetDataBoolean(testEvent.Data, "readyForHostDecision") || !_automaticDecisionTests.Add(testEvent.TestId))
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
        var current = GetDataInt(testEvent.Data, "averageChargeCurrentMa");
        var passed =
            GetDataBoolean(testEvent.Data, "chargerConnected") &&
            voltage >= GetParameterInt(parameters, "chargeVoltageMinMv", 7400) &&
            voltage <= GetParameterInt(parameters, "chargeVoltageMaxMv", 8400) &&
            current >= GetParameterInt(parameters, "chargeCurrentMinMa", 500) &&
            current <= GetParameterInt(parameters, "chargeCurrentMaxMa", 3000);
        var reason = passed ? "charge_values_in_range"
            : !GetDataBoolean(testEvent.Data, "chargerConnected") ? "charger_not_connected"
            : voltage < GetParameterInt(parameters, "chargeVoltageMinMv", 7400) ? "charge_voltage_too_low"
            : voltage > GetParameterInt(parameters, "chargeVoltageMaxMv", 8400) ? "charge_voltage_too_high"
            : current < GetParameterInt(parameters, "chargeCurrentMinMa", 500) ? "charge_current_too_low"
            : "charge_current_too_high";
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
        if (_activeSessionClient is null || !GetDataBoolean(testEvent.Data, "readyForHostDecision") || !_automaticDecisionTests.Add(testEvent.TestId)) return;
        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        var sampleIntervalMs = Math.Max(100, GetParameterInt(parameters, "sampleIntervalMs", 500));
        var samplingDurationMs = Math.Max(sampleIntervalMs, GetParameterInt(parameters, "samplingDurationMs", 10000));
        var stabilityToleranceMa = Math.Max(0, GetParameterInt(parameters, "stabilityToleranceMa", 80));
        var voltageMinMv = GetParameterInt(parameters, "dischargeVoltageMinMv", 0);
        var voltageMaxMv = GetParameterInt(parameters, "dischargeVoltageMaxMv", int.MaxValue);
        var currentMinMv = GetParameterInt(parameters, "dischargeCurrentMinMa", 0);
        var currentMaxMv = GetParameterInt(parameters, "dischargeCurrentMaxMa", int.MaxValue);

        if (_jk5506Service is null || !GetDataBoolean(testEvent.Data, "chargeControlOk"))
        {
            var reason = _jk5506Service is null ? "battery_simulator_not_configured" : "charge_disable_failed";
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, reason);
            AppendLog($"Battery discharge automatic decision: FAIL ({reason})");
            return;
        }

        AppendLog("Battery discharge ready. JK5506 will sample voltage/current for 10 seconds and then return host decision.");

        var deadline = DateTimeOffset.Now.AddMilliseconds(samplingDurationMs);
        var samples = new List<(int VoltageMv, int CurrentMa)>();

        try
        {
            while (DateTimeOffset.Now < deadline)
            {
                var voltageMv = await _jk5506Service.ReadOutputVoltageMvAsync();
                var currentMa = await _jk5506Service.ReadOutputCurrentMaAsync();
                samples.Add((voltageMv, currentMa));

                var elapsedMs = Math.Min(samplingDurationMs, (int)(DateTimeOffset.Now - testEvent.Timestamp).TotalMilliseconds);
                _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
                {
                    ["phase"] = "sampling",
                    ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "disable_charge"),
                    ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
                    ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                    ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
                    ["samplingDurationMs"] = samplingDurationMs,
                    ["elapsedMs"] = elapsedMs,
                    ["sampleCount"] = samples.Count,
                    ["dischargeVoltageMv"] = voltageMv,
                    ["dischargeCurrentMa"] = currentMa,
                    ["dischargeVoltageMinMv"] = voltageMinMv,
                    ["dischargeVoltageMaxMv"] = voltageMaxMv,
                    ["dischargeCurrentMinMa"] = currentMinMv,
                    ["dischargeCurrentMaxMa"] = currentMaxMv,
                    ["stabilityToleranceMa"] = stabilityToleranceMa
                };
                ApplyTestReport(new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = testEvent.TestId,
                    Status = "running",
                    ResultCode = 0,
                    Message = $"板放电测试进行中，已采样 {samples.Count} 次",
                    Timestamp = testEvent.Timestamp,
                    Data = new Dictionary<string, object?>
                    {
                        ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "disable_charge"),
                        ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
                        ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                        ["samplingDurationMs"] = samplingDurationMs,
                        ["elapsedMs"] = elapsedMs,
                        ["sampleCount"] = samples.Count,
                        ["dischargeVoltageMv"] = voltageMv,
                        ["dischargeCurrentMa"] = currentMa,
                        ["dischargeVoltageMinMv"] = voltageMinMv,
                        ["dischargeVoltageMaxMv"] = voltageMaxMv,
                        ["dischargeCurrentMinMa"] = currentMinMv,
                        ["dischargeCurrentMaxMa"] = currentMaxMv,
                        ["stabilityToleranceMa"] = stabilityToleranceMa
                    }
                });

                await Task.Delay(sampleIntervalMs);
            }

            var avgVoltageMv = (int)Math.Round(samples.Average(item => item.VoltageMv));
            var rawCurrents = samples.Select(item => item.CurrentMa).OrderBy(value => value).ToArray();
            var medianCurrentMa = rawCurrents[rawCurrents.Length / 2];
            var filteredCurrents = rawCurrents
                .Where(value => Math.Abs(value - medianCurrentMa) <= Math.Max(stabilityToleranceMa * 2, 200))
                .ToArray();
            if (filteredCurrents.Length == 0)
            {
                filteredCurrents = rawCurrents;
            }

            var avgCurrentMa = (int)Math.Round(filteredCurrents.Average());
            var measuredCurrentMin = filteredCurrents.Min();
            var measuredCurrentMax = filteredCurrents.Max();
            var rippleMa = measuredCurrentMax - measuredCurrentMin;
            var outlierCount = rawCurrents.Length - filteredCurrents.Length;

            var passed = avgVoltageMv >= voltageMinMv &&
                avgVoltageMv <= voltageMaxMv &&
                avgCurrentMa >= currentMinMv &&
                avgCurrentMa <= currentMaxMv;

            var failureReason = passed
                ? "discharge_current_in_range"
                : avgVoltageMv < voltageMinMv ? "discharge_voltage_too_low"
                : avgVoltageMv > voltageMaxMv ? "discharge_voltage_too_high"
                : avgCurrentMa < currentMinMv ? "discharge_current_too_low"
                : avgCurrentMa > currentMaxMv ? "discharge_current_too_high"
                : "discharge_current_out_of_range";

            _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
            {
                ["phase"] = "sampling_completed",
                ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "disable_charge"),
                ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
                ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
                ["samplingDurationMs"] = samplingDurationMs,
                ["elapsedMs"] = samplingDurationMs,
                ["sampleCount"] = samples.Count,
                ["validSampleCount"] = filteredCurrents.Length,
                ["outlierSampleCount"] = outlierCount,
                ["dischargeVoltageMv"] = avgVoltageMv,
                ["dischargeCurrentMa"] = avgCurrentMa,
                ["measuredCurrentMinMa"] = measuredCurrentMin,
                ["measuredCurrentMaxMa"] = measuredCurrentMax,
                ["currentRippleMa"] = rippleMa,
                ["rawCurrentMedianMa"] = medianCurrentMa,
                ["dischargeVoltageMinMv"] = voltageMinMv,
                ["dischargeVoltageMaxMv"] = voltageMaxMv,
                ["dischargeCurrentMinMa"] = currentMinMv,
                ["dischargeCurrentMaxMa"] = currentMaxMv,
                ["stabilityToleranceMa"] = stabilityToleranceMa,
                ["failureReason"] = failureReason
            };

            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, passed, failureReason);
            AppendLog($"Battery discharge automatic decision: {(passed ? "PASS" : "FAIL")} ({failureReason}), voltage={avgVoltageMv}mV, current={avgCurrentMa}mA, ripple={rippleMa}mA, outliers={outlierCount}");
        }
        catch (Exception ex)
        {
            _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
            {
                ["phase"] = "sampling_failed",
                ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "disable_charge"),
                ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
                ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
                ["samplingDurationMs"] = samplingDurationMs,
                ["failureReason"] = "jk5506_read_error"
            };
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "jk5506_read_error");
            AppendLog($"Battery discharge measurement failed: {ex.Message}");
        }
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

    private async Task ShowQueryPageAsync()
    {
        IsQueryPage = true;
        await RefreshQueryRecordsAsync();
    }

    private async Task RefreshQueryRecordsAsync()
    {
        var filter = QueryText.Trim();
        var sessions = await _databaseRepository.GetRecentSessionsAsync(200);
        var filtered = string.IsNullOrEmpty(filter)
            ? sessions
            : sessions.Where(session => session.Session.Sn.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || session.BoardState?.BoardId.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);

        QuerySessions.Clear();
        foreach (var session in filtered) QuerySessions.Add(session);
    }

    private async Task SetMockQueryDefaultAsync()
    {
        QueryText = CurrentSn;
        if (IsQueryPage) await RefreshQueryRecordsAsync();
    }

    private void AppendLog(string message)
    {
        _logService.Info(message);
        Logs.Clear();
        foreach (var entry in _logService.Snapshot().Take(40))
        {
            Logs.Add(entry);
        }
        RaisePropertyChanged(nameof(LogsText));
    }

    private string BuildSessionCompletionInstruction(string finalVerdict)
    {
        if (string.Equals(finalVerdict, "Pass", StringComparison.OrdinalIgnoreCase))
        {
            return "检测完成，全部项目通过。请取下产品并扫描下一台。";
        }

        var failedResults = TestResults
            .Where(result => result.State == TestItemState.Failed)
            .Select(result => result.TestId)
            .ToArray();

        if (failedResults.Length == 0)
        {
            return "检测完成，本轮结果异常。请检查记录后重新扫描当前 SN 或继续下一台。";
        }

        if (failedResults.Length == 1)
        {
            return BuildSingleFailureInstruction(failedResults[0]);
        }

        var failedNames = failedResults
            .Select(GetTestDisplayName)
            .ToArray();

        return $"检测完成，失败项目：{string.Join("、", failedNames)}。请处理对应异常后重新扫描当前 SN 或继续下一台。";
    }

    private static string BuildSingleFailureInstruction(string testId) => testId switch
    {
        "board_state" => "检测完成，板状态读取失败。请检查设备连接状态后重新扫描当前 SN。",
        "hdmi" => "检测完成，HDMI 测试未通过。请检查显示输出后重新扫描当前 SN。",
        "lcd" => "检测完成，LCD 测试未通过。请检查屏幕显示后重新扫描当前 SN。",
        "keys" => "检测完成，按键测试未通过。请检查按键输入后重新扫描当前 SN。",
        "usb2_3" => "检测完成，USB 测试未通过。请检查 U 盘与 USB 口后重新扫描当前 SN。",
        "indicator_led" => "检测完成，指示灯测试未通过。请检查指示灯状态后重新扫描当前 SN。",
        "fan" => "检测完成，风扇测试未通过。请检查风扇与供电后重新扫描当前 SN。",
        "fingerprint" => "检测完成，指纹测试未通过。请检查指纹模组后重新扫描当前 SN。",
        "pcba_test_points" => "检测完成，PCBA 测试点未通过。请检查测试点电压后重新扫描当前 SN。",
        "battery_management" => "检测完成，放电测试未通过。请检查放电电流与治具连接后重新扫描当前 SN。",
        "typec_fast_charge" => "检测完成，快充测试未通过。请检查充电器与充电电流后重新扫描当前 SN。",
        "bluetooth" => "检测完成，蓝牙测试未通过。请检查广播设备与信号后重新扫描当前 SN。",
        "ethernet" => "检测完成，网口测试未通过。请检查网线与网络连接后重新扫描当前 SN。",
        "wifi" => "检测完成，Wi-Fi 测试未通过。请检查路由器与无线连接后重新扫描当前 SN。",
        _ => $"检测完成，{GetTestDisplayName(testId)}未通过。请处理异常后重新扫描当前 SN。"
    };

    private string ResolveFinalVerdict(string sessionVerdict)
    {
        if (TestResults.Any(result => result.State == TestItemState.Failed))
        {
            return "Fail";
        }

        return string.Equals(sessionVerdict, "Pass", StringComparison.OrdinalIgnoreCase) ? "Pass" : "Fail";
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
                Skip = configuration.TestPlan.SkippedTests.ContainsKey(item.Id),
                SkipReason = configuration.TestPlan.SkippedTests.TryGetValue(item.Id, out var reason) ? reason : null,
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
            // The upper PC configures the BLE broadcaster name and the 3576
            // scans for that exact name.  Override bluetooth.targetName here so
            // production only changes bluetoothBroadcaster.broadcastName.
            result["targetName"] = configuration.BluetoothBroadcaster.BroadcastName;
        }
        return result;
    }

    private static IReadOnlyList<TestItemViewModel> BuildTestItems(IReadOnlyList<TestPlanItem> testPlan)
    {
        return testPlan
            .Select((item, index) => new TestItemViewModel(item.Id, GetTestDisplayName(item.Id), index < testPlan.Count - 1))
            .ToArray();
    }

    private void ResetTestItems()
    {
        _manualDecisionTestId = null;
        _automaticDecisionTests.Clear();
        _hostDecisionData.Clear();
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

        CurrentTestItem = TestItems.FirstOrDefault();

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
        OperatorInstruction = passed ? $"{displayName} 已确认通过，继续后续测试。" : $"{displayName} 已确认失败，记录失败并继续后续测试。";
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
        BoardState = BuildBoardStateDisplay(state.CurrentState, state.LastVerdict);
        TestMode = state.TestMode;
    }

    private static string BuildBoardStateDisplay(string currentState, string lastVerdict)
    {
        if (string.Equals(lastVerdict, "Pass", StringComparison.OrdinalIgnoreCase))
        {
            return "测试完成";
        }

        if (string.Equals(lastVerdict, "Fail", StringComparison.OrdinalIgnoreCase))
        {
            return "测试失败";
        }

        if (string.IsNullOrWhiteSpace(currentState))
        {
            return "待机";
        }

        return currentState.Trim().ToLowerInvariant() switch
        {
            "idle" => "待机",
            "ready" => "待机",
            "waiting" => "待机",
            "completed" => "测试完成",
            "passed" => "测试完成",
            "failed" => "测试失败",
            _ => "测试中"
        };
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
            $"ContinuousTest: {IsContinuousTestEnabled}\n" +
            $"SessionRunning: {_isSessionRunning}\n" +
            $"VoltageStatus: {VoltageStatus}\n" +
            $"BatteryStatus: {BatteryStatus}\n" +
            $"LastResult: {LastResult}";
    }
}

