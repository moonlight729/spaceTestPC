using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.IO;
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
        new() { Id = "battery_management" }, new() { Id = "typec_fast_charge" }, new() { Id = "typec_camera" }, new() { Id = "tf" }, new() { Id = "emmc" }, new() { Id = "ddr" }, new() { Id = "usb2" }, new() { Id = "usb3" },
        new() { Id = "pcba_test_points" }, new() { Id = "ethernet_led" }, new() { Id = "indicator_led" }, new() { Id = "fan" }, new() { Id = "otg" }, new() { Id = "reset_button" }
    ];

    private const string BoardStateItemName = "板状态";
    private const int BoardStateTimeoutSeconds = 10;
    private const string ApplicationUpgradeItemId = "application_upgrade";
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
    private readonly UpgradeConfiguration _upgradeConfiguration;
    private readonly TestModeConfiguration _testModeConfiguration;
    private readonly string _testProfileMode;
    private readonly PcbaConnectionMode _connectionMode;
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
    private readonly HashSet<string> _submittedManualDecisionTests = new(StringComparer.OrdinalIgnoreCase);
    private bool _isContinuousTestEnabled;
    private bool _isSessionRunning;
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset? _sessionEndedAt;
    private DateTimeOffset? _keyDeadline;
    private TestSessionEvent? _latestKeyTestEvent;
    private TaskCompletionSource<bool?>? _upgradeDecisionSource;
    private readonly DispatcherTimer _upgradeCountdownTimer;
    private readonly DispatcherTimer _adbUpgradeMonitorTimer;
    private readonly DispatcherTimer _statusBarTimer;
    private readonly SemaphoreSlim _upgradeCheckGate = new(1, 1);
    private int _upgradeCountdownSeconds;
    private bool _isUpgradePromptVisible;
    private string _deviceApplicationMd5 = string.Empty;
    private string _deviceApplicationVersion = string.Empty;
    private bool _deviceApplicationVersionAvailable;
    private bool _applicationUpgradeCheckCompleted;
    private bool _applicationUpgradeCheckInProgress;
    private string _hostApplicationMd5 = string.Empty;
    private string _localUpgradeBinaryPath = string.Empty;
    private bool _upgradePackageReady;

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
        _upgradeConfiguration = appConfiguration.Upgrade;
        _connectionMode = ParseConnectionMode(appConfiguration.PcbaConnection.Mode);
        _testProfileMode = string.IsNullOrWhiteSpace(appConfiguration.TestMode) ? "finished_product" : appConfiguration.TestMode.Trim().ToLowerInvariant();
        _testModeConfiguration = appConfiguration.TestModes.TryGetValue(_testProfileMode, out var modeConfiguration)
            ? modeConfiguration
            : new TestModeConfiguration();
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
        _upgradeCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _upgradeCountdownTimer.Tick += (_, _) =>
        {
            _upgradeCountdownSeconds--;
            RaisePropertyChanged(nameof(UpgradePromptText));
            if (_upgradeCountdownSeconds <= 0)
            {
                _upgradeCountdownTimer.Stop();
                _upgradeDecisionSource?.TrySetResult(true);
            }
        };
        _adbUpgradeMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _adbUpgradeMonitorTimer.Tick += async (_, _) =>
        {
            if (_applicationUpgradeCheckCompleted || _applicationUpgradeCheckInProgress || !_upgradePackageReady)
                return;

            _applicationUpgradeCheckInProgress = true;
            try
            {
                if (await EnsureApplicationUpgradeAsync(_pcbaCommandClientFactory.Create(_connectionMode)))
                    _adbUpgradeMonitorTimer.Stop();
            }
            finally
            {
                _applicationUpgradeCheckInProgress = false;
            }
        };
        _statusBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusBarTimer.Tick += (_, _) =>
        {
            RaisePropertyChanged(nameof(StatusBarElapsedTime));
            RaisePropertyChanged(nameof(StatusBarCurrentTime));
        };
        _statusBarTimer.Start();
        _isContinuousTestEnabled = appConfiguration.TestPlan.Continuous.EnabledByDefault;
        _testPlan = BuildActiveTestPlan(appConfiguration);
        _testItemIndexes = _testPlan
            .Select((item, index) => new { item.Id, index })
            .ToDictionary(item => item.Id, item => item.index);

        ScanCommand = new AsyncRelayCommand(() => HandleScanAsync(isMockSession: false), () => !string.IsNullOrWhiteSpace(ScannerInput) && _upgradePackageReady && !_isSessionRunning);
        StartMockSessionCommand = new RelayCommand(StartMockSession);
        ToggleContinuousTestCommand = new RelayCommand(ToggleContinuousTest);
        ConfirmManualPassCommand = new RelayCommand(() => SubmitManualDecision(true), () => IsManualDecisionVisible);
        ConfirmManualFailCommand = new RelayCommand(() => SubmitManualDecision(false), () => IsManualDecisionVisible);
        UpgradeNowCommand = new RelayCommand(() => _upgradeDecisionSource?.TrySetResult(true), () => IsUpgradePromptVisible);
        SkipUpgradeCommand = new RelayCommand(() => _upgradeDecisionSource?.TrySetResult(false), () => IsUpgradePromptVisible);
        ReadBoardStateCommand = new AsyncRelayCommand(ReadBoardStateAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        StartPhaseOneCommand = new AsyncRelayCommand(StartPhaseOneAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        ShowTestPageCommand = new RelayCommand(() => IsQueryPage = false);
        ShowQueryPageCommand = new AsyncRelayCommand(ShowQueryPageAsync);
        QueryRecordsCommand = new AsyncRelayCommand(RefreshQueryRecordsAsync);

        Logs = new ObservableCollection<string>();
        RecentSessions = new ObservableCollection<string>();
        QuerySessions = new ObservableCollection<TestSessionRecord>();
        TestItems = new ObservableCollection<TestItemViewModel>(new[]
        {
            new TestItemViewModel(ApplicationUpgradeItemId, GetTestDisplayName(ApplicationUpgradeItemId))
        }.Concat(BuildTestItems(_testPlan)));
        CurrentTestItem = TestItems.FirstOrDefault();
        TestResults = new ObservableCollection<TestResultViewModel>(new[]
        {
            new TestResultViewModel(ApplicationUpgradeItemId, GetTestDisplayName(ApplicationUpgradeItemId))
        }.Concat(_testPlan.Select(item => new TestResultViewModel(item.Id, GetTestDisplayName(item.Id)))));
        DirectionalKeys = new ObservableCollection<DirectionalKeyViewModel>
        {
            new("up", "上"), new("down", "下"), new("left", "左"), new("right", "右"), new("confirm", "确认"), new("recovery", "Recovery")
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
                RaisePropertyChanged(nameof(StatusBarSn));
                RaisePropertyChanged(nameof(StatusBarRunState));
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

    public bool IsUpgradePromptVisible
    {
        get => _isUpgradePromptVisible;
        private set
        {
            if (SetProperty(ref _isUpgradePromptVisible, value))
            {
                UpgradeNowCommand.NotifyCanExecuteChanged();
                SkipUpgradeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string UpgradePromptText =>
        $"检测到设备程序需要升级。\n设备 MD5：{_deviceApplicationMd5}\n本地 MD5：{_hostApplicationMd5}\n{Math.Max(0, _upgradeCountdownSeconds)} 秒后自动升级。";

    public bool IsUpgradePackageReady => _upgradePackageReady;
    public string UpgradePackageStatusText => _upgradePackageReady ? "升级软件：已准备" : "升级软件异常，禁止开始测试";
    public string UpgradePackagePathText => string.IsNullOrWhiteSpace(_localUpgradeBinaryPath)
        ? "路径：未配置"
        : $"路径：{_localUpgradeBinaryPath}";
    public string UpgradePackageMd5Text => string.IsNullOrWhiteSpace(_hostApplicationMd5)
        ? "MD5：无法读取"
        : $"MD5：{_hostApplicationMd5}";
    public string UpgradePackageStatusForeground => _upgradePackageReady ? "#15803D" : "#B42318";
    public string UpgradePackageStatusBackground => _upgradePackageReady ? "#ECFDF3" : "#FEF3F2";
    public string TestModeBannerText => _testProfileMode == "finished_product" ? "整机测试" : "PCBA测试";
    public string TestModeBannerBackground => _testProfileMode == "finished_product" ? "#9A3412" : "#0B4A8B";
    public string TestModeDatabaseName => string.IsNullOrWhiteSpace(_testModeConfiguration.DatabaseName)
        ? (_testProfileMode == "finished_product" ? "space-test-finished-product.db" : "space-test-pcba.db")
        : _testModeConfiguration.DatabaseName;
    public string ApplicationUpgradeStatusText => _applicationUpgradeCheckCompleted ? "设备程序：已确认" : "设备程序：待检查";
    public string ApplicationUpgradeDetailText => _applicationUpgradeCheckCompleted
        ? $"升级校验完成\n路径：{_upgradeConfiguration.RemoteBinaryPath}\nMD5：{_deviceApplicationMd5}"
        : "ADB 连接后自动检查设备程序 MD5";
    public string ApplicationUpgradeStatusForeground => _applicationUpgradeCheckCompleted ? "#15803D" : "#667085";

    public string LogsText => string.Join(Environment.NewLine, Logs.Reverse());

    public string AppVersion { get; } = GetAppVersion();
    public string WindowTitle => $"SpaceTest PC - v{AppVersion}";
    public string StatusBarRunState => _isSessionRunning
        ? $"状态：{CurrentTestItem?.Name ?? "测试中"}"
        : string.IsNullOrWhiteSpace(CurrentSn) ? "状态：等待扫码" : "状态：就绪";
    public string StatusBarProgress => $"进度：{TestItems.Count(item => item.State is TestItemState.Passed or TestItemState.Failed or TestItemState.Skipped)} / {TestItems.Count}";
    public string StatusBarSn => $"当前 SN：{(string.IsNullOrWhiteSpace(CurrentSn) ? "--" : CurrentSn)}";
    public string StatusBarElapsedTime
    {
        get
        {
            var elapsed = _sessionStartedAt.HasValue
                ? (_sessionEndedAt ?? DateTimeOffset.Now) - _sessionStartedAt.Value
                : TimeSpan.Zero;
            return $"已用时：{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        }
    }
    public string StatusBarDatabase => "数据库：正常";
    public string StatusBarLog => "日志：正常";
    public string StatusBarCurrentTime => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
    public RelayCommand UpgradeNowCommand { get; }
    public RelayCommand SkipUpgradeCommand { get; }
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
        private set
        {
            if (SetProperty(ref _currentTestItem, value))
                RaisePropertyChanged(nameof(StatusBarRunState));
        }
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
        "keys" => "请依次按下上、下、左、右方向键和确认键，再按下 Recovery 实体键；六键全部识别后自动通过。",
        "lcd" => "请观察 LCD：背光正常、RGB 测试图案完整且稳定，无花屏、缺线、闪烁或明显亮暗异常后再判定。",
        "ethernet_led" => "请观察网口灯：看到黄色和绿色灯亮即可选择 PASS，否则选择 FAIL。",
        "indicator_led" => "请依次观察红灯、绿灯、蓝灯各亮 2 秒，最后确认绿灯正常后选择 PASS 或 FAIL。",
        "reset_button" => "请按下设备复位键，确认 LCD 屏幕已经息屏后选择 PASS 或 FAIL。",
        "fan" => "风扇正在自动检测，请等待下位机读取 tach_rpm 并返回结果。",
        _ => string.Empty
    };
    public string ManualPassButtonText => _manualDecisionTestId switch
    {
        "lcd" => "LCD 通过",
        "ethernet_led" => "网口灯通过",
        "indicator_led" => "指示灯通过",
        _ => "HDMI 通过"
    };
    public string ManualFailButtonText => _manualDecisionTestId switch
    {
        "lcd" => "LCD 失败",
        "ethernet_led" => "网口灯失败",
        "indicator_led" => "指示灯失败",
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

        if (_isSessionRunning)
        {
            AppendLog($"Duplicate scan ignored while session is running: {sn}");
            ScannerInput = string.Empty;
            OperatorInstruction = "当前测试仍在进行中，请等待本次测试完成后再扫描下一块。";
            UpdateDebugOutput();
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
        _sessionStartedAt = DateTimeOffset.Now;
        _sessionEndedAt = null;
        ScanCommand.NotifyCanExecuteChanged();
        RaisePropertyChanged(nameof(ContinuousTestStatusText));
        RaisePropertyChanged(nameof(StatusBarRunState));
        RaisePropertyChanged(nameof(StatusBarElapsedTime));
        AppendLog($"Scan received: {CurrentSn}");
        AppendLog($"Session created: {SessionId}");
        UpdateDebugOutput();

        var history = await _databaseRepository.GetLatestSessionBySnAsync(CurrentSn);
        if (history is not null && _connectionMode == PcbaConnectionMode.Mock)
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
            var client = _pcbaCommandClientFactory.Create(_connectionMode);
            AppendLog("Reading board state...");
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            var state = await GetBoardStateWithTimeoutAsync(client);
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

        var upgradeClient = _pcbaCommandClientFactory.Create(_connectionMode);
        if (!await EnsureApplicationUpgradeAsync(upgradeClient))
        {
            LastResult = "Application upgrade failed";
            OperatorInstruction = "设备程序升级失败，测试未启动。";
            return;
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
        _sessionEndedAt = DateTimeOffset.Now;
        ScanCommand.NotifyCanExecuteChanged();
        PrepareForNextBoard();
        RaisePropertyChanged(nameof(ContinuousTestStatusText));
        RaisePropertyChanged(nameof(StatusBarRunState));
        RaisePropertyChanged(nameof(StatusBarElapsedTime));
        UpdateDebugOutput();
    }

    private async Task<bool> EnsureApplicationUpgradeAsync(IPcbaCommandClient client)
    {
        await _upgradeCheckGate.WaitAsync();
        try
        {
            return await EnsureApplicationUpgradeCoreAsync(client);
        }
        finally
        {
            _upgradeCheckGate.Release();
        }
    }

    private async Task<bool> EnsureApplicationUpgradeCoreAsync(IPcbaCommandClient client)
    {
        var upgradeCheckTimer = Stopwatch.StartNew();
        if (!_upgradeConfiguration.Enabled || _connectionMode == PcbaConnectionMode.Mock) return true;
        if (_applicationUpgradeCheckCompleted) return true;
        var upgradeResult = TestResults.FirstOrDefault(item => item.TestId == ApplicationUpgradeItemId);
        upgradeResult?.ApplyLocalResult(TestItemState.Running, "正在检查设备程序 MD5。", new Dictionary<string, object?>
        {
            ["remotePath"] = _upgradeConfiguration.RemoteBinaryPath,
            ["localPath"] = _localUpgradeBinaryPath
        });
        SetTestItemState(ApplicationUpgradeItemId, TestItemState.Running);

        var localPath = Path.IsPathRooted(_upgradeConfiguration.LocalBinaryPath)
            ? _upgradeConfiguration.LocalBinaryPath
            : Path.Combine(AppContext.BaseDirectory, _upgradeConfiguration.LocalBinaryPath);
        if (!File.Exists(localPath))
        {
            AppendLog($"Application upgrade package missing: {localPath}");
            OperatorInstruction = $"升级软件不存在，测试未启动：{localPath}";
            return false;
        }

        try
        {
            var localMd5Timer = Stopwatch.StartNew();
            await using var stream = File.OpenRead(localPath);
            _hostApplicationMd5 = Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant();
            AppendLog($"Upgrade timing: local MD5={localMd5Timer.ElapsedMilliseconds}ms");
            ApplicationMd5Info? deviceInfo = null;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var deviceMd5Timer = Stopwatch.StartNew();
                    deviceInfo = await client.GetApplicationMd5Async(_upgradeConfiguration.RemoteBinaryPath);
                    AppendLog($"Upgrade timing: device MD5 attempt={attempt}, elapsed={deviceMd5Timer.ElapsedMilliseconds}ms");
                    break;
                }
                catch (Exception ex) when (attempt < 5)
                {
                    lastError = ex;
                    AppendLog($"Upgrade timing: device MD5 attempt={attempt}, failed={ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            if (deviceInfo is null)
                throw new InvalidOperationException("ADB device is not ready.", lastError);
            _deviceApplicationMd5 = deviceInfo.Md5;
            if (!string.IsNullOrWhiteSpace(_upgradeConfiguration.ApplicationVersion))
            {
                try
                {
                    var versionTimer = Stopwatch.StartNew();
                    var versionInfo = await client.GetApplicationVersionAsync();
                    AppendLog($"Upgrade timing: device version elapsed={versionTimer.ElapsedMilliseconds}ms");
                    _deviceApplicationVersion = versionInfo.Version;
                    _deviceApplicationVersionAvailable = versionInfo.VersionAvailable && !string.IsNullOrWhiteSpace(versionInfo.Version);
                }
                catch (Exception ex)
                {
                    _deviceApplicationVersion = string.Empty;
                    _deviceApplicationVersionAvailable = false;
                    AppendLog($"Upgrade timing: device version failed={ex.Message}");
                }
            }
            else
            {
                _deviceApplicationVersion = string.Empty;
                _deviceApplicationVersionAvailable = false;
                AppendLog("Upgrade timing: version check skipped because applicationVersion is empty.");
            }
            var versionCheckEnabled = !string.IsNullOrWhiteSpace(_upgradeConfiguration.ApplicationVersion) && _deviceApplicationVersionAvailable;
            var md5Matches = string.Equals(_hostApplicationMd5, _deviceApplicationMd5, StringComparison.OrdinalIgnoreCase);
            var versionMatches = !versionCheckEnabled || string.Equals(_upgradeConfiguration.ApplicationVersion, _deviceApplicationVersion, StringComparison.OrdinalIgnoreCase);
            AppendLog($"Application identity: hostVersion={_upgradeConfiguration.ApplicationVersion}, deviceVersion={_deviceApplicationVersion}, versionAvailable={_deviceApplicationVersionAvailable}, hostMd5={_hostApplicationMd5}, deviceMd5={_deviceApplicationMd5}, md5Match={md5Matches}, versionMatch={versionMatches}");
            AppendLog($"Upgrade timing: check total before decision={upgradeCheckTimer.ElapsedMilliseconds}ms");
            if (md5Matches && versionMatches)
            {
                _applicationUpgradeCheckCompleted = true;
                upgradeResult?.ApplyLocalResult(TestItemState.Passed, "设备程序已是最新，无需升级。", new Dictionary<string, object?>
                {
                    ["localPath"] = localPath, ["remotePath"] = _upgradeConfiguration.RemoteBinaryPath,
                    ["localMd5"] = _hostApplicationMd5, ["deviceMd5"] = _deviceApplicationMd5
                });
                SetTestItemState(ApplicationUpgradeItemId, TestItemState.Passed);
                PrepareBoardStateForScan();
                RaiseApplicationUpgradeStatusChanged();
                return true;
            }

            _upgradeCountdownSeconds = Math.Max(1, _upgradeConfiguration.AutoUpgradeDelaySeconds);
            _upgradeDecisionSource = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsUpgradePromptVisible = true;
            RaisePropertyChanged(nameof(UpgradePromptText));
            _upgradeCountdownTimer.Start();
            var shouldUpgrade = await _upgradeDecisionSource.Task;
            _upgradeCountdownTimer.Stop();
            IsUpgradePromptVisible = false;
            if (shouldUpgrade != true)
            {
                AppendLog("Application upgrade skipped by operator.");
                _applicationUpgradeCheckCompleted = true;
                upgradeResult?.ApplyLocalResult(TestItemState.Skipped, "操作员跳过升级。", new Dictionary<string, object?>
                {
                    ["localMd5"] = _hostApplicationMd5, ["deviceMd5"] = _deviceApplicationMd5
                });
                SetTestItemState(ApplicationUpgradeItemId, TestItemState.Skipped);
                PrepareBoardStateForScan();
                RaiseApplicationUpgradeStatusChanged();
                return true;
            }

            OperatorInstruction = "正在升级 PCBA 程序，请勿断开设备连接。";
            var result = await client.UpgradeApplicationAsync(localPath, _hostApplicationMd5,
                _upgradeConfiguration.ServiceName, _upgradeConfiguration.RemoteBinaryPath);
            AppendLog($"Application upgrade result: success={result.Success}, message={result.Message}, finalMd5={result.FinalMd5}");
            _applicationUpgradeCheckCompleted = result.Success;
            if (result.Success)
            {
                _deviceApplicationMd5 = result.FinalMd5;
                OperatorInstruction = $"PCBA 程序已升级成功。\n路径：{_upgradeConfiguration.RemoteBinaryPath}\nMD5：{result.FinalMd5}";
                RaiseApplicationUpgradeStatusChanged();
                upgradeResult?.ApplyLocalResult(TestItemState.Passed, "PCBA 程序升级成功。", new Dictionary<string, object?>
                {
                    ["localPath"] = localPath, ["remotePath"] = _upgradeConfiguration.RemoteBinaryPath,
                    ["localMd5"] = _hostApplicationMd5, ["deviceMd5Before"] = _deviceApplicationMd5,
                    ["deviceMd5After"] = result.FinalMd5, ["service"] = _upgradeConfiguration.ServiceName
                });
                SetTestItemState(ApplicationUpgradeItemId, TestItemState.Passed);
                PrepareBoardStateForScan();
            }
            return result.Success;
        }
        catch (Exception ex)
        {
            _upgradeCountdownTimer.Stop();
            IsUpgradePromptVisible = false;
            AppendLog($"Application upgrade check failed: {ex.Message}");
            upgradeResult?.ApplyLocalResult(TestItemState.Failed, $"设备程序检查/升级失败：{ex.Message}", new Dictionary<string, object?>
            {
                ["localPath"] = localPath, ["remotePath"] = _upgradeConfiguration.RemoteBinaryPath
            });
            SetTestItemState(ApplicationUpgradeItemId, TestItemState.Failed);
            SelectedTestResult = upgradeResult;
            return false;
        }
    }

    private void PrepareBoardStateForScan()
    {
        var boardStateResult = TestResults.FirstOrDefault(item => item.TestId == BoardStateItemName);
        if (boardStateResult is not null)
        {
            SelectedTestResult = boardStateResult;
        }

        OperatorInstruction = "请扫描二维码。";
        AppendLog("Upgrade check completed; waiting for QR code scan before reading board state.");
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

        var client = _pcbaCommandClientFactory.Create(_connectionMode);
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            state = await GetBoardStateWithTimeoutAsync(client);
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

            if (_connectionMode == PcbaConnectionMode.Mock)
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
        AppendLog($"Session start requested: mode={_connectionMode}, session={SessionId}, sn={CurrentSn}, tests={string.Join(",", _testPlan.Select(item => item.Id))}");
        LastResult = "Stage 1 running";
        OperatorInstruction = "正在接收底层测试结果，请勿断开产品连接。";

        var client = _pcbaCommandClientFactory.Create(_connectionMode);
        _activeSessionClient = client;
        var finalVerdict = "Fail";
        BoardState? state = null;

        try
        {
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            AppendLog("ADB/sys.get_board_state request sent.");
            state = await GetBoardStateWithTimeoutAsync(client);
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

    private async Task<BoardState> GetBoardStateWithTimeoutAsync(IPcbaCommandClient client)
    {
        try
        {
            return await client.GetBoardStateAsync(SessionId, CurrentSn)
                .WaitAsync(TimeSpan.FromSeconds(BoardStateTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"板状态读取超时（{BoardStateTimeoutSeconds} 秒）。");
        }
    }

    private async Task<BoardState> EnsureBoardSnAsync(IPcbaCommandClient client, BoardState state)
    {
        if (string.Equals(state.BoardSn, CurrentSn, StringComparison.Ordinal))
        {
            AppendLog($"Board SN already matches scanned SN: {CurrentSn}");
            return state;
        }

        AppendLog($"Board SN differs; writing scanned SN to board: old={state.BoardSn}, new={CurrentSn}");
        OperatorInstruction = $"正在将板端 SN 更新为扫码 SN：{CurrentSn}";
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

        if (testEvent.TestId == "wifi" && testEvent.Status is "passed" or "failed")
        {
            var attempt = Math.Max(1, GetDataInt(testEvent.Data, "attempt"));
            if (_hostDecisionData.TryGetValue($"{testEvent.TestId}:{attempt}", out var wifiHostData))
            {
                var merged = wifiHostData.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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
            UpdateRecoveryKeyState(testEvent);
        }
        else if (testEvent.TestId == "keys" && (testEvent.Status is "passed" or "failed"))
        {
            UpdateRecoveryKeyState(testEvent);
        }

        if (testEvent.TestId == "typec_fast_charge" && testEvent.Status == "running")
        {
            HandleTypecChargingReport(testEvent);
        }

        if (testEvent.TestId == "battery_management" && testEvent.Status == "running")
        {
            HandleBatteryDischargeReport(testEvent);
        }

        if (testEvent.TestId == "wifi" && testEvent.Status == "running")
        {
            HandleWifiReport(testEvent);
        }

        if (testEvent.TestId is "indicator_led" or "fan" && testEvent.Status == "running" &&
            !(testEvent.TestId == "indicator_led" && _testProfileMode == "finished_product"))
        {
            HandleVoltageMeasurementReport(testEvent);
        }

        if (testEvent.Status is "passed" or "failed")
        {
            _submittedManualDecisionTests.Remove(testEvent.TestId);
        }

        _manualDecisionTestId = testEvent.Status == "running" &&
            !_submittedManualDecisionTests.Contains(testEvent.TestId) &&
            (testEvent.TestId is "hdmi" or "lcd" or "ethernet_led" or "reset_button" ||
             testEvent.TestId == "indicator_led" && _testProfileMode == "finished_product")
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

        var testItem = TestItems.FirstOrDefault(item =>
            string.Equals(item.TestId, testEvent.TestId, StringComparison.OrdinalIgnoreCase));
        if (testItem is not null)
        {
            testItem.State = testEvent.Status switch
            {
                "running" => TestItemState.Running,
                "passed" => TestItemState.Passed,
                "skipped" => TestItemState.Skipped,
                _ => TestItemState.Failed
            };
            if (testEvent.Status == "running" && !ReferenceEquals(CurrentTestItem, testItem))
            {
                CurrentTestItem = testItem;
                SequenceAdvanceRequested?.Invoke(this, CurrentTestItem);
            }
            RaisePropertyChanged(nameof(StatusBarProgress));
            RaisePropertyChanged(nameof(StatusBarRunState));
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
                var phase = GetDataString(testEvent.Data, "phase", string.Empty);
                var remainingMs = GetDataInt(testEvent.Data, "remainingMs");
                if (string.Equals(phase, "recovery", StringComparison.OrdinalIgnoreCase))
                {
                    var timeoutMs = GetDataInt(testEvent.Data, "timeoutMs");
                    var elapsedMs = GetDataInt(testEvent.Data, "elapsedMs");
                    if (timeoutMs > 0 && elapsedMs >= 0)
                        remainingMs = Math.Max(0, timeoutMs - elapsedMs);
                }
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

        return testEvent.TestId is "usb2" or "usb3" && testEvent.Status == "running"
            ? $"请先通过 HDMI 网页完成 USB{(testEvent.TestId == "usb2" ? "2.0" : "3.0")} 联通性预检（两个端口分别正插、反插，共 4 次），再接入 ADB。当前测试将读取对应模式的预检结果文件。"
            : testEvent.TestId == "pcba_test_points" && testEvent.Status == "running"
            ? "正在读取 PCBA 32 通道测试点电压，系统将自动判断是否在阈值范围内。"
            : testEvent.TestId == "ethernet" && testEvent.Status == "running"
            ? "请插入网线，系统将检测有线网络。"
            : testEvent.TestId == "ethernet_led" && testEvent.Status == "running"
            ? BuildEthernetLedInstruction(testEvent)
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
            ? "已检测到相机，正在进行相机拉流和同步信号测试。"
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
            "board_state" => "请扫描二维码。",
            "keys" => BuildKeyTestInstruction(testEvent),
            "bluetooth" => BuildBluetoothInstruction(testEvent),
            "wifi" => BuildWifiInstruction(testEvent),
            "battery_management" => BuildBatteryDischargeInstruction(testEvent),
            "ethernet" => BuildEthernetInstruction(testEvent),
            "ethernet_led" => BuildEthernetLedInstruction(testEvent),
            _ => BuildGeneralTestInstruction(testEvent)
        };
    }

    private static string BuildEthernetLedInstruction(TestSessionEvent testEvent)
    {
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var iface = GetDataString(testEvent.Data, "interfaceName", "end0");
        var elapsedSeconds = Math.Max(0, GetDataInt(testEvent.Data, "elapsedMs") / 1000);

        if (testEvent.Status == "running")
        {
            return phase switch
            {
                "wait_cable" => elapsedSeconds > 0
                    ? $"请插入网线，系统正在等待网口灯测试。已等待 {elapsedSeconds} 秒。"
                    : "请插入网线，系统正在等待网口灯测试。",
                "show_100m" => $"正在切换 {iface} 到百兆模式，请观察绿色网口灯是否亮起。",
                "show_1000m" => $"正在切换 {iface} 到千兆模式，请观察黄色网口灯是否亮起。",
                "awaiting_operator" => "网口灯切换已完成，正在等待人工判定。",
                _ => "请观察网口灯：看到黄色和绿色灯亮即可选择 PASS。"
            };
        }

        if (testEvent.Status == "passed")
        {
            return "网口灯测试完成。";
        }

        return "网口灯测试失败，请检查网线、网口灯和 ethtool 切速率是否正常。";
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
        var attempt = GetDataInt(data, "attempt");
        var rssi = GetDataInt(data, "rssi");
        var minRssi = GetDataInt(data, "minRssi");
        var found = GetDataBoolean(data, "found");
        return $"hint=reason:{reason}, iface:{iface}, attempt:{attempt}, found:{found}, rssi:{rssi}, minRssi:{minRssi}";
    }

    private string BuildKeyTestInstruction(TestSessionEvent testEvent)
    {
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        if (string.Equals(phase, "recovery", StringComparison.OrdinalIgnoreCase))
        {
            var rawValue = GetDataInt(testEvent.Data, "rawValue");
            var stableCount = GetDataInt(testEvent.Data, "stableCount");
            var stableRequired = Math.Max(1, GetDataInt(testEvent.Data, "stableRequired"));
            var threshold = GetDataInt(testEvent.Data, "pressThreshold");
            var recoveryRemainingSeconds = GetRemainingKeySeconds(testEvent);
            if (testEvent.Status == "passed")
                return $"Recovery 按键通过：ADC={rawValue}，连续 {stableRequired} 次小于 {threshold}。";
            if (testEvent.Status == "failed")
                return $"Recovery 按键失败：10 秒倒计时结束，未检测到 ADC 小于 {threshold}。";
            return $"请按下 Recovery 按键；倒计时：{recoveryRemainingSeconds} 秒，当前 ADC={rawValue}，判定阈值 <{threshold}，稳定采样 {stableCount}/{stableRequired}。";
        }

        var remainingSeconds = GetRemainingKeySeconds(testEvent);
        if (testEvent.Status == "passed")
        {
            return "六键测试通过：上、下、左、右、确认和 Recovery 均已识别。";
        }

        if (testEvent.Status == "failed")
        {
            return testEvent.ResultCode switch
            {
                4000 => "按键测试失败：3576 无法打开按键输入设备，请检查 gpio-keys 与 pwrkey 节点。",
                4001 => $"按键测试失败：{FormatKeyTimeoutSeconds()} 秒内未完成上、下、左、右、确认五个输入按键。",
                4002 => "按键测试失败：3576 读取按键输入事件异常，请检查 evdev 驱动和输入节点。",
                _ => $"按键测试失败：resultCode={testEvent.ResultCode}，message={testEvent.Message}"
            };
        }

        var detected = DirectionalKeys.Where(key => key.IsDetected).Select(key => key.Label).ToArray();
        var missing = DirectionalKeys.Where(key => !key.IsDetected).Select(key => key.Label).ToArray();
        var detectedText = detected.Length == 0 ? "无" : string.Join("、", detected);
        var missingText = missing.Length == 0 ? "无" : string.Join("、", missing);
        return $"六键测试第一阶段：请在 {FormatKeyTimeoutSeconds()} 秒内依次按上、下、左、右、确认键。五键完成后再按 Recovery，底层将通过 ADC 判定。已识别：{detectedText}；剩余：{missingText}；倒计时：{remainingSeconds} 秒。";
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
        var attempt = Math.Max(1, GetDataInt(testEvent.Data, "attempt"));
        var maxRetryCount = Math.Max(attempt, GetDataInt(testEvent.Data, "maxRetryCount"));
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var targetName = GetDataString(testEvent.Data, "targetName", "-");
        var rssi = GetDataInt(testEvent.Data, "rssi");
        var minRssi = GetDataInt(testEvent.Data, "minRssi");
        if (testEvent.Status == "running")
        {
            return phase == "retry_wait"
                ? $"蓝牙扫描未通过，2 秒后自动重试。当前第 {attempt}/{maxRetryCount} 次。"
                : $"正在检测：蓝牙。目标名 {targetName}，第 {attempt}/{maxRetryCount} 次，最小 RSSI {minRssi} dBm。";
        }

        if (testEvent.Status == "passed")
        {
            return $"蓝牙测试通过：名称 {GetDataString(testEvent.Data, "name", "-")}，第 {attempt}/{maxRetryCount} 次，RSSI {rssi} dBm。";
        }

        var reason = GetDataString(testEvent.Data, "failureReason", string.Empty);
        var matchedName = GetDataString(testEvent.Data, "matchedName", string.Empty);
        var matchedRssi = GetDataInt(testEvent.Data, "matchedRssi");
        var bestSeenName = GetDataString(testEvent.Data, "bestSeenName", string.Empty);
        var bestSeenRssi = GetDataInt(testEvent.Data, "bestSeenRssi");
        return $"蓝牙测试失败：第 {attempt}/{maxRetryCount} 次，reason={reason}，matched={matchedName}/{matchedRssi}，bestSeen={bestSeenName}/{bestSeenRssi}，minRssi={minRssi}。";
    }

    private static string BuildWifiInstruction(TestSessionEvent testEvent)
    {
        var attempt = Math.Max(1, GetDataInt(testEvent.Data, "attempt"));
        var maxRetryCount = Math.Max(attempt, GetDataInt(testEvent.Data, "maxRetryCount"));
        var ssid = GetDataString(testEvent.Data, "ssid", "-");
        var rssi = GetDataInt(testEvent.Data, "rssi");
        var minRssi = GetDataInt(testEvent.Data, "minRssi");
        var found = GetDataBoolean(testEvent.Data, "found");
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var reason = GetDataString(testEvent.Data, "failureReason", string.Empty);

        if (testEvent.Status == "running")
        {
            return phase switch
            {
                "retry_wait" => $"Wi‑Fi 扫描未通过，2 秒后自动重试。当前第 {attempt}/{maxRetryCount} 次，RSSI {rssi} dBm。",
                "scan_completed" when found => $"Wi‑Fi 扫描完成，等待上位机判定。SSID {ssid}，第 {attempt}/{maxRetryCount} 次，RSSI {rssi} dBm，阈值 {minRssi} dBm。",
                "scan_completed" => $"Wi‑Fi 扫描完成，等待上位机判定。SSID {ssid}，第 {attempt}/{maxRetryCount} 次，未找到目标热点。",
                _ => $"正在检测：Wi‑Fi。SSID {ssid}，第 {attempt}/{maxRetryCount} 次扫描。"
            };
        }

        if (testEvent.Status == "passed")
        {
            return $"Wi‑Fi 测试通过：SSID {ssid}，第 {attempt}/{maxRetryCount} 次，RSSI {rssi} dBm。";
        }

        return $"Wi‑Fi 测试失败：reason={reason}，SSID {ssid}，第 {attempt}/{maxRetryCount} 次，found={found}，RSSI {rssi} dBm，阈值 {minRssi} dBm。";
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
                "ping_ok" => "网口连通正常。",
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
            "ethernet_disable_wifi_failed" => "网口测试失败：断开 Wi‑Fi 连接失败。",
            "ethernet_no_ip" => "网口测试失败：未获取到 IP。",
            "ethernet_ping_failed" => $"网口测试失败：无法连通路由器 {routerIp}。",
            "ethernet_cable_not_inserted" => "网口测试失败：未检测到网线连接。",
            _ => $"网口测试失败：reason={reason}，ip={ip}。"
        };
    }

    private static string GetTestDisplayName(string testId) => testId switch
    {
        ApplicationUpgradeItemId => "设备程序升级",
        "board_state" => "板状态",
        "emmc" => "EMMC",
        "ddr" => "DDR",
        "hdmi" => "HDMI",
        "keys" => "六键测试",
        "lcd" => "LCD",
        "reset_button" => "复位按键",
        "ethernet" => "网口",
        "ethernet_led" => "网口灯",
        "wifi" => "WiFi",
        "bluetooth" => "蓝牙",
        "fingerprint" => "指纹模组",
        "typec_fast_charge" => "板快充",
        "typec_camera" => "相机测试&同步信号测试",
        "tf" => "TF 卡",
        "usb2" => "USB2.0 测试",
        "usb3" => "USB3.0 测试",
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
            key.IsChecking = false;
            key.IsFailed = false;
        }
    }

    private void UpdateRecoveryKeyState(TestSessionEvent testEvent)
    {
        var recovery = DirectionalKeys.FirstOrDefault(item => item.Id == "recovery");
        if (recovery is null) return;
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        if (testEvent.Status == "passed" && string.Equals(phase, "recovery", StringComparison.OrdinalIgnoreCase))
        {
            recovery.IsChecking = false;
            recovery.IsFailed = false;
            recovery.IsDetected = true;
        }
        else if (testEvent.Status == "failed" && (string.Equals(phase, "recovery", StringComparison.OrdinalIgnoreCase) || testEvent.ResultCode == 4003))
        {
            recovery.IsChecking = false;
            recovery.IsDetected = false;
            recovery.IsFailed = true;
        }
        else if (testEvent.Status == "running" && string.Equals(phase, "recovery", StringComparison.OrdinalIgnoreCase))
        {
            recovery.IsDetected = false;
            recovery.IsFailed = false;
            recovery.IsChecking = true;
        }
    }

    private static bool IsInitialKeyTestReport(IReadOnlyDictionary<string, object?> data)
    {
        if (string.Equals(GetDataString(data, "phase", string.Empty), "recovery", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
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
        var samplingDurationMs = Math.Max(100, GetParameterInt(parameters, "timeoutMs", 10000));
        var currentMinMa = GetParameterInt(parameters, "chargeCurrentMinMa", 0);
        var currentMaxMa = GetParameterInt(parameters, "chargeCurrentMaxMa", int.MaxValue);

        var rawCurrents = GetIntValues(testEvent.Data, "rawCurrentSamplesMa");
        var rawVoltages = GetIntValues(testEvent.Data, "rawVoltageSamplesMv");

        if (rawCurrents.Length == 0)
        {
            var singleCurrent = GetDataInt(testEvent.Data, "chargeCurrentMa");
            if (singleCurrent > 0)
            {
                rawCurrents = [singleCurrent];
            }
        }

        if (rawVoltages.Length == 0)
        {
            var singleVoltage = GetDataInt(testEvent.Data, "chargeVoltageMv");
            if (singleVoltage > 0)
            {
                rawVoltages = [singleVoltage];
            }
        }

        if (!GetDataBoolean(testEvent.Data, "chargeControlOk"))
        {
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "charge_enable_failed");
            AppendLog("TYPE-C charging automatic decision: FAIL (charge_enable_failed)");
            return;
        }

        if (rawCurrents.Length == 0)
        {
            _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
            {
                ["phase"] = "sampling_failed",
                ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "enable_charge"),
                ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
                ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
                ["samplingDurationMs"] = samplingDurationMs,
                ["failureReason"] = "missing_charge_samples"
            };
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "missing_charge_samples");
            AppendLog("TYPE-C charging automatic decision: FAIL (missing_charge_samples)");
            return;
        }

        var orderedRawCurrents = rawCurrents.OrderBy(value => value).ToArray();
        var filteredCurrents = FilterStableCurrentSamples(orderedRawCurrents);
        var medianCurrentMa = filteredCurrents[filteredCurrents.Length / 2];
        var avgCurrentMa = (int)Math.Round(filteredCurrents.Average());
        var measuredCurrentMin = filteredCurrents.Min();
        var measuredCurrentMax = filteredCurrents.Max();
        var rippleMa = measuredCurrentMax - measuredCurrentMin;
        var outlierCount = orderedRawCurrents.Length - filteredCurrents.Length;
        var avgVoltageMv = rawVoltages.Length == 0 ? 0 : (int)Math.Round(rawVoltages.Average());

        var passed = avgCurrentMa >= currentMinMa && avgCurrentMa <= currentMaxMa;
        var reason = passed
            ? "charge_current_in_range"
            : avgCurrentMa < currentMinMa ? "charge_current_too_low"
            : "charge_current_too_high";

        _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
        {
            ["phase"] = "sampling_completed",
            ["chargeControlCommand"] = GetDataString(testEvent.Data, "chargeControlCommand", "enable_charge"),
            ["chargeControlOk"] = GetDataBoolean(testEvent.Data, "chargeControlOk"),
            ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
            ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
            ["samplingDurationMs"] = GetDataInt(testEvent.Data, "samplingDurationMs") > 0 ? GetDataInt(testEvent.Data, "samplingDurationMs") : samplingDurationMs,
            ["elapsedMs"] = GetDataInt(testEvent.Data, "samplingDurationMs") > 0 ? GetDataInt(testEvent.Data, "samplingDurationMs") : samplingDurationMs,
            ["sampleCount"] = orderedRawCurrents.Length,
            ["validSampleCount"] = filteredCurrents.Length,
            ["outlierSampleCount"] = outlierCount,
            ["rawCurrentSamplesMa"] = orderedRawCurrents,
            ["rawVoltageSamplesMv"] = rawVoltages,
            ["chargeVoltageMv"] = avgVoltageMv,
            ["averageChargeCurrentMa"] = avgCurrentMa,
            ["measuredCurrentMinMa"] = measuredCurrentMin,
            ["measuredCurrentMaxMa"] = measuredCurrentMax,
            ["currentRippleMa"] = rippleMa,
            ["rawCurrentMedianMa"] = medianCurrentMa,
            ["chargeCurrentMinMa"] = currentMinMa,
            ["chargeCurrentMaxMa"] = currentMaxMa,
            ["failureReason"] = reason
        };

        await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, passed, reason);
        AppendLog($"TYPE-C charging automatic decision: {(passed ? "PASS" : "FAIL")} ({reason}), samples={orderedRawCurrents.Length}, current={avgCurrentMa}mA, ripple={rippleMa}mA, outliers={outlierCount}");
    }

    private async void HandleWifiReport(TestSessionEvent testEvent)
    {
        if (_activeSessionClient is null || !GetDataBoolean(testEvent.Data, "readyForHostDecision"))
        {
            return;
        }

        var attempt = Math.Max(1, GetDataInt(testEvent.Data, "attempt"));
        var decisionKey = $"{testEvent.TestId}:{attempt}";
        if (!_automaticDecisionTests.Add(decisionKey))
        {
            return;
        }

        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        var minRssi = GetParameterInt(parameters, "minRssi", -75);
        var ssid = GetDataString(testEvent.Data, "ssid", string.Empty);
        var iface = GetDataString(testEvent.Data, "interfaceName", string.Empty);
        var found = GetDataBoolean(testEvent.Data, "found");
        var rssi = GetDataInt(testEvent.Data, "rssi");
        var passed = found && rssi >= minRssi;
        var reason = !found
            ? GetDataString(testEvent.Data, "failureReason", "ssid_not_found")
            : rssi < minRssi ? "rssi_too_low" : "rssi_in_range";

        _hostDecisionData[decisionKey] = new Dictionary<string, object?>
        {
            ["phase"] = "host_decision_completed",
            ["ssid"] = ssid,
            ["interfaceName"] = iface,
            ["attempt"] = attempt,
            ["maxRetryCount"] = Math.Max(attempt, GetDataInt(testEvent.Data, "maxRetryCount")),
            ["found"] = found,
            ["rssi"] = rssi,
            ["minRssi"] = minRssi,
            ["readyForHostDecision"] = true,
            ["failureReason"] = reason
        };

        await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, passed, reason);
        AppendLog($"Wi-Fi automatic decision: {(passed ? "PASS" : "FAIL")} ({reason}), ssid={ssid}, iface={iface}, attempt={attempt}, rssi={rssi}dBm, minRssi={minRssi}dBm");
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
            var voltage = _connectionMode == PcbaConnectionMode.AdbForward && _jxTvmService?.IsEnabled == true
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
            var filteredCurrents = FilterStableCurrentSamples(rawCurrents);
            var medianCurrentMa = filteredCurrents[filteredCurrents.Length / 2];

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

    private static int[] FilterStableCurrentSamples(int[] rawCurrents)
    {
        var hardFilteredCurrents = rawCurrents
            .Where(value => value > 0 && value <= 1000)
            .ToArray();
        if (hardFilteredCurrents.Length == 0)
        {
            hardFilteredCurrents = rawCurrents;
        }

        var filteredCurrents = hardFilteredCurrents;
        for (var pass = 0; pass < 2; pass++)
        {
            var medianCurrentMa = filteredCurrents[filteredCurrents.Length / 2];
            var medianToleranceMa = Math.Max(30, medianCurrentMa / 5);
            var nextFilteredCurrents = filteredCurrents
                .Where(value => Math.Abs(value - medianCurrentMa) <= medianToleranceMa)
                .ToArray();

            if (nextFilteredCurrents.Length == 0 || nextFilteredCurrents.Length == filteredCurrents.Length)
            {
                break;
            }

            filteredCurrents = nextFilteredCurrents;
        }

        return filteredCurrents;
    }

    private static int[] GetIntValues(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
                .Select(item => item.GetInt32())
                .ToArray();
        }

        if (value is IEnumerable<int> intValues)
        {
            return intValues.ToArray();
        }

        if (value is IEnumerable<object?> objectValues)
        {
            return objectValues
                .Select(item => TryGetInt(item, out var parsed) ? parsed : (int?)null)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToArray();
        }

        return [];
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

    private static PcbaConnectionMode ParseConnectionMode(string? value)
    {
        if (string.Equals(value, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return PcbaConnectionMode.Mock;
        }

        if (string.Equals(value, "tcp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "network", StringComparison.OrdinalIgnoreCase))
        {
            return PcbaConnectionMode.Tcp;
        }

        return PcbaConnectionMode.AdbForward;
    }

    public async Task InitializeAsync()
    {
        await ValidateUpgradePackageAsync();
        if (_upgradePackageReady && _connectionMode != PcbaConnectionMode.Mock)
        {
            OperatorInstruction = _connectionMode == PcbaConnectionMode.Tcp
                ? "升级软件已准备，正在通过网线检查设备程序。"
                : "升级软件已准备，正在通过 ADB 检查设备程序。";
            var upgradeClient = _pcbaCommandClientFactory.Create(_connectionMode);
            if (!await EnsureApplicationUpgradeAsync(upgradeClient))
            {
                OperatorInstruction = "设备程序升级失败或无法确认，测试暂不可开始。";
            }
            else
            {
                _adbUpgradeMonitorTimer.Stop();
            }

            _adbUpgradeMonitorTimer.Start();
        }
        if (_bluetoothBroadcasterService is not null)
        {
            try { await _bluetoothBroadcasterService.ConfigureAsync(); AppendLog("Bluetooth broadcaster configured."); }
            catch (Exception ex) { AppendLog($"Bluetooth broadcaster setup failed: {ex.Message}"); }
        }
        await LoadRecentSessionsAsync();
    }

    private async Task ValidateUpgradePackageAsync()
    {
        if (!_upgradeConfiguration.Enabled)
        {
            _upgradePackageReady = true;
            _localUpgradeBinaryPath = "升级检查已关闭";
            var disabledResult = TestResults.FirstOrDefault(item => item.TestId == ApplicationUpgradeItemId);
            disabledResult?.ApplyLocalResult(TestItemState.Skipped, "升级检查已关闭。", new Dictionary<string, object?>
            {
                ["status"] = "disabled"
            });
            SetTestItemState(ApplicationUpgradeItemId, TestItemState.Skipped);
        }
        else
        {
            _localUpgradeBinaryPath = Path.IsPathRooted(_upgradeConfiguration.LocalBinaryPath)
                ? _upgradeConfiguration.LocalBinaryPath
                : Path.Combine(AppContext.BaseDirectory, _upgradeConfiguration.LocalBinaryPath);
            try
            {
                if (!File.Exists(_localUpgradeBinaryPath))
                    throw new FileNotFoundException("未找到升级软件");
                await using var stream = File.OpenRead(_localUpgradeBinaryPath);
                _hostApplicationMd5 = Convert.ToHexString(await MD5.HashDataAsync(stream)).ToLowerInvariant();
                _upgradePackageReady = !string.IsNullOrWhiteSpace(_hostApplicationMd5);
                AppendLog($"Upgrade package ready: path={_localUpgradeBinaryPath}, md5={_hostApplicationMd5}");
            }
            catch (Exception ex)
            {
                _upgradePackageReady = false;
                AppendLog($"Upgrade package validation failed: path={_localUpgradeBinaryPath}, error={ex.Message}");
                var failedResult = TestResults.FirstOrDefault(item => item.TestId == ApplicationUpgradeItemId);
                failedResult?.ApplyLocalResult(TestItemState.Failed, $"升级软件校验失败：{ex.Message}", new Dictionary<string, object?>
                {
                    ["path"] = _localUpgradeBinaryPath,
                    ["error"] = ex.Message,
                    ["reason"] = "upgrade_package_validation_failed"
                });
                SetTestItemState(ApplicationUpgradeItemId, TestItemState.Failed);
                SelectedTestResult = failedResult;
            }
        }

        RaisePropertyChanged(nameof(IsUpgradePackageReady));
        RaisePropertyChanged(nameof(UpgradePackageStatusText));
        RaisePropertyChanged(nameof(UpgradePackagePathText));
        RaisePropertyChanged(nameof(UpgradePackageMd5Text));
        RaisePropertyChanged(nameof(UpgradePackageStatusForeground));
        RaisePropertyChanged(nameof(UpgradePackageStatusBackground));
        ScanCommand.NotifyCanExecuteChanged();
    }

    private void RaiseApplicationUpgradeStatusChanged()
    {
        RaisePropertyChanged(nameof(ApplicationUpgradeStatusText));
        RaisePropertyChanged(nameof(ApplicationUpgradeDetailText));
        RaisePropertyChanged(nameof(ApplicationUpgradeStatusForeground));
        UpdateDebugOutput();
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

    public void AppendExternalLog(string message) => AppendLog(message);

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
        "emmc" => "检测完成，EMMC 测试未通过。请检查存储器件和焊接后重新扫描当前 SN。",
        "ddr" => "检测完成，DDR 测试未通过。请检查内存器件和焊接后重新扫描当前 SN。",
        "hdmi" => "检测完成，HDMI 测试未通过。请检查显示输出后重新扫描当前 SN。",
        "lcd" => "检测完成，LCD 测试未通过。请检查屏幕显示后重新扫描当前 SN。",
        "keys" => "检测完成，按键测试未通过。请检查按键输入后重新扫描当前 SN。",
        "usb2" => "检测完成，USB2.0 测试未通过。请检查 U 盘与 USB2.0 口后重新扫描当前 SN。",
        "usb3" => "检测完成，USB3.0 测试未通过。请检查 U 盘与 USB3.0 口后重新扫描当前 SN。",
        "indicator_led" => "检测完成，指示灯测试未通过。请检查指示灯状态后重新扫描当前 SN。",
        "fan" => "检测完成，风扇测试未通过。请检查风扇与供电后重新扫描当前 SN。",
        "fingerprint" => "检测完成，指纹测试未通过。请检查指纹模组后重新扫描当前 SN。",
        "pcba_test_points" => "检测完成，PCBA 测试点未通过。请检查测试点电压后重新扫描当前 SN。",
        "battery_management" => "检测完成，放电测试未通过。请检查放电电流与治具连接后重新扫描当前 SN。",
        "typec_fast_charge" => "检测完成，快充测试未通过。请检查充电器与充电电流后重新扫描当前 SN。",
        "bluetooth" => "检测完成，蓝牙测试未通过。请检查广播设备与信号后重新扫描当前 SN。",
        "ethernet" => "检测完成，网口测试未通过。请检查网线与网络连接后重新扫描当前 SN。",
        "ethernet_led" => "检测完成，网口灯测试未通过。请检查网线、网口灯和速率切换后重新扫描当前 SN。",
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
        .Where(result => result.TestId != ApplicationUpgradeItemId)
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
        var mode = string.IsNullOrWhiteSpace(configuration.TestMode) ? "finished_product" : configuration.TestMode.Trim();
        var modeConfiguration = configuration.TestModes.TryGetValue(mode, out var configuredMode) ? configuredMode : null;
        var enabledSource = modeConfiguration is not null && modeConfiguration.EnabledTests.Length > 0
            ? modeConfiguration.EnabledTests
            : configuration.TestPlan.EnabledTests;
        var disabledSource = modeConfiguration is not null && modeConfiguration.DisabledTests.Length > 0
            ? modeConfiguration.DisabledTests
            : configuration.TestPlan.DisabledTests;
        var skippedSource = modeConfiguration is not null
            ? modeConfiguration.SkippedTests
            : configuration.TestPlan.SkippedTests;
        var enabled = enabledSource
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabled = disabledSource
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plan = enabled.Count > 0
            ? AllTestPlan.Where(item => enabled.Contains(item.Id)).ToList()
            : AllTestPlan.Where(item => !disabled.Contains(item.Id)).ToList();

        if (modeConfiguration?.TestOrder is { Length: > 0 } order)
        {
            var orderIndex = order
                .Select((id, index) => new { id, index })
                .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
            plan = plan
                .OrderBy(item => orderIndex.TryGetValue(item.Id, out var index) ? index : int.MaxValue)
                .ToList();
        }

        if (plan.All(item => item.Id != "board_state"))
        {
            plan.Insert(0, AllTestPlan[0]);
        }

        return plan
            .Select(item => new TestPlanItem
            {
                Id = item.Id,
                Skip = skippedSource.ContainsKey(item.Id),
                SkipReason = skippedSource.TryGetValue(item.Id, out var reason) ? reason : null,
                Parameters = GetTestParameters(configuration, item.Id, mode)
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> GetTestParameters(AppConfiguration configuration, string testId, string mode)
    {
        var result = configuration.TestPlan.TestParameters.TryGetValue(testId, out var parameters)
            ? parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        result["mode"] = mode;
        if (testId == "bluetooth" && !string.IsNullOrWhiteSpace(configuration.BluetoothBroadcaster.BroadcastName))
        {
            // The upper PC configures the BLE broadcaster name and the 3576
            // scans for that exact name.  Override bluetooth.targetName here so
            // production only changes bluetoothBroadcaster.broadcastName.
            result["targetName"] = configuration.BluetoothBroadcaster.BroadcastName;
        }
        if (testId == "indicator_led")
        {
            result.TryAdd("phaseDurationMs", 2000);
            result.TryAdd("redGreenOverlapMs", 200);
            result.TryAdd("i2cTimeoutMs", 3000);
            result.TryAdd("i2cRetryIntervalMs", 100);
        }
        if (testId == "ethernet_led")
        {
            result.TryAdd("interfaceName", "end0");
            result.TryAdd("waitCableTimeoutMs", 15000);
            result.TryAdd("cycleCount", 2);
            result.TryAdd("phaseDurationMs", 2000);
            result.TryAdd("settleMs", 2000);
            result.TryAdd("speedWaitTimeoutMs", 10000);
            result.TryAdd("manualDecisionTimeoutMs", 15000);
            result.TryAdd("timeoutMs", 15000);
            result.TryAdd("reconnectDelayMs", 8000);
        }
        if (testId == "emmc")
        {
            result.TryAdd("emmcDevice", "mmcblk0");
            result.TryAdd("emmcMinCapacityGiB", 115);
            result.TryAdd("emmcTestDirectory", "/userdata/factory_test");
            result.TryAdd("emmcTestFileMiB", 64);
            result.TryAdd("timeoutMs", 30000);
        }
        if (testId == "ddr")
        {
            result.TryAdd("ddrMinMemTotalMiB", 3200);
            result.TryAdd("ddrStressMiB", 256);
            result.TryAdd("ddrStressLoops", 2);
            result.TryAdd("timeoutMs", 30000);
        }
        return result;
    }

    private static IReadOnlyList<TestItemViewModel> BuildTestItems(IReadOnlyList<TestPlanItem> testPlan)
    {
        var visibleItems = testPlan
            .Where(item => !item.Skip)
            .ToArray();

        return visibleItems
            .Select((item, index) => new TestItemViewModel(item.Id, GetTestDisplayName(item.Id), index < visibleItems.Length - 1))
            .ToArray();
    }

    private void ResetTestItems()
    {
        _manualDecisionTestId = null;
        _automaticDecisionTests.Clear();
        _submittedManualDecisionTests.Clear();
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
        RaisePropertyChanged(nameof(StatusBarProgress));
        RaisePropertyChanged(nameof(StatusBarRunState));
    }

    private async void SubmitManualDecision(bool passed)
    {
        if (!IsManualDecisionVisible || _activeSessionClient is null)
        {
            return;
        }

        var testId = _manualDecisionTestId!;
        var displayName = GetTestDisplayName(testId);
        OperatorInstruction = passed ? $"{displayName} 已确认通过，继续后续测试。" : $"{displayName} 已确认失败，记录失败并继续后续测试。";
        AppendLog($"{testId} manual decision: {(passed ? "PASS" : "FAIL")}");

        try
        {
            await _activeSessionClient.SubmitOperatorDecisionAsync(SessionId, testId, passed);
            _manualDecisionTestId = null;
            _submittedManualDecisionTests.Add(testId);
            RaisePropertyChanged(nameof(IsManualDecisionVisible));
            RaisePropertyChanged(nameof(ManualDecisionPrompt));
            RaisePropertyChanged(nameof(ManualPassButtonText));
            RaisePropertyChanged(nameof(ManualFailButtonText));
            ConfirmManualPassCommand.NotifyCanExecuteChanged();
            ConfirmManualFailCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _manualDecisionTestId = testId;
            OperatorInstruction = $"{displayName} 判定未发送到设备，请检查连接后重新测试。";
            AppendLog($"{testId} operator decision send failed: {ex.Message}");
            RaisePropertyChanged(nameof(IsManualDecisionVisible));
            RaisePropertyChanged(nameof(ManualDecisionPrompt));
            RaisePropertyChanged(nameof(ManualPassButtonText));
            RaisePropertyChanged(nameof(ManualFailButtonText));
            ConfirmManualPassCommand.NotifyCanExecuteChanged();
            ConfirmManualFailCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetTestItemState(string name, TestItemState state)
    {
        var item = TestItems.FirstOrDefault(x => x.TestId == name || x.Name == name);
        if (item is not null)
        {
            item.State = state;
            RaisePropertyChanged(nameof(StatusBarProgress));
            RaisePropertyChanged(nameof(StatusBarRunState));
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
            $"Mode: {_connectionMode}\n" +
            $"SessionId: {SessionId}\n" +
            $"SN: {CurrentSn}\n" +
            $"BoardId: {BoardId}\n" +
            $"BoardState: {BoardState}\n" +
            $"TestMode: {TestMode}\n" +
            $"ContinuousTest: {IsContinuousTestEnabled}\n" +
            $"SessionRunning: {_isSessionRunning}\n" +
            $"ApplicationUpgrade: {ApplicationUpgradeStatusText}\n" +
            $"ApplicationMd5: {_deviceApplicationMd5}\n" +
            $"VoltageStatus: {VoltageStatus}\n" +
            $"BatteryStatus: {BatteryStatus}\n" +
            $"LastResult: {LastResult}";
    }
}

