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
    public event EventHandler<string>? ScanValidationFailed;
    public event EventHandler? BatteryDischargePreparationRequested;
    public event EventHandler? ChargerNotConnectedRequested;
    private const int RequiredSnLength = 20;
    private static bool UseUnifiedSessionProtocol => true;
    private static readonly IReadOnlyList<TestPlanItem> AllTestPlan =
    [
        new() { Id = "board_state" }, new() { Id = "hdmi" }, new() { Id = "keys" }, new() { Id = "lcd" },
        new() { Id = "wifi" }, new() { Id = "bluetooth" },
        new() { Id = "battery_management" }, new() { Id = "typec_fast_charge" }, new() { Id = "tf" }, new() { Id = "emmc" }, new() { Id = "ddr" }, new() { Id = "typec_camera" }, new() { Id = "usb2" }, new() { Id = "usb3" },
        new() { Id = "pcba_test_points" }, new() { Id = "ethernet_led" }, new() { Id = "indicator_led" }, new() { Id = "fan" }
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
    private readonly VersionValidationSettings _versionValidation;
    private readonly Dictionary<string, bool> _voltagePhaseResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _hostDecisionData = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _voltageControlCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _keyCountdownTimer;
    private readonly IReadOnlyList<TestPlanItem> _testPlan;
    private readonly IReadOnlyDictionary<string, int> _testItemIndexes;
    private readonly bool _allowSnMismatchForDebug;
    private readonly int _keyTestTimeoutMs;
    private readonly UpgradeConfiguration _upgradeConfiguration;
    private readonly TestLifecycleConfiguration _testLifecycleConfiguration;
    private readonly TestModeConfiguration _testModeConfiguration;
    private readonly string _testProfileMode;
    private bool _loadingTestSelection;
    private readonly string _operationMode;
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
    private string _jxTvmStatus = "未启用";
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
    private string? _manualDecisionSessionId;
    private string _manualDecisionPhase = string.Empty;
    private IPcbaCommandClient? _activeSessionClient;
    private readonly HashSet<string> _automaticDecisionTests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _submittedManualDecisionTests = new(StringComparer.OrdinalIgnoreCase);
    private bool _chargerNotConnectedDialogShown;
    private bool _isContinuousTestEnabled;
    private bool _isSessionRunning;
    private bool _isRetestLifecycleActive;
    private bool _isRetestRunning;
    private string _rootSessionId = string.Empty;
    private int _attemptNo = 1;
    private BoardState? _latestBoardState;
    private readonly Dictionary<string, string> _testResultSourceSessions = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset? _sessionEndedAt;
    private DateTimeOffset? _keyDeadline;
    private TestSessionEvent? _latestKeyTestEvent;
    private TaskCompletionSource<bool?>? _upgradeDecisionSource;
    private readonly DispatcherTimer _upgradeCountdownTimer;
    private readonly DispatcherTimer _adbUpgradeMonitorTimer;
    private readonly DispatcherTimer _statusBarTimer;
    private readonly DispatcherTimer _tfRemovalPromptTimer;
    private readonly SemaphoreSlim _upgradeCheckGate = new(1, 1);
    private int _upgradeCountdownSeconds;
    private bool _isUpgradePromptVisible;
    private string _deviceApplicationMd5 = string.Empty;
    private string _deviceApplicationVersion = string.Empty;
    private bool _deviceApplicationVersionAvailable;
    private bool _applicationUpgradeCheckCompleted;
    private bool _applicationUpgradeCheckInProgress;
    private string _connectionStatus = "连接：未连接";
    private string _bluetoothConnectionStatus = "蓝牙：未启用";
    private string _hostApplicationMd5 = string.Empty;
    private string _localUpgradeBinaryPath = string.Empty;
    private bool _upgradePackageReady;
    private bool _batteryPreparationPromptActive;
    private bool _isTfRemovalPromptVisible;

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
        if (_jxTvmService is not null) _jxTvmService.Log = AppendLog;
        _bluetoothBroadcasterService = bluetoothBroadcasterService;
        if (_bluetoothBroadcasterService?.IsEnabled == true)
        {
            _bluetoothConnectionStatus = "蓝牙：检测中";
        }
        var appConfiguration = configuration ?? new AppConfiguration();
        if (appConfiguration.TestPlan.TestParameters.TryGetValue("wifi", out var wifiParameters))
        {
            if (wifiParameters.TryGetValue("ssid", out var configuredSsid) &&
                configuredSsid.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(configuredSsid.GetString()))
            {
                _wifiRequest.Ssid = configuredSsid.GetString()!;
            }
        }
        if (appConfiguration.TestPlan.TestParameters.TryGetValue("ethernet", out var ethernetParameters) &&
            ethernetParameters.TryGetValue("routerIp", out var configuredRouterIp) &&
            configuredRouterIp.ValueKind == System.Text.Json.JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(configuredRouterIp.GetString()))
        {
            _ethernetRequest.RouterIp = configuredRouterIp.GetString()!;
            _ethernetRequest.TargetIp = configuredRouterIp.GetString()!;
        }
        _keyTestTimeoutMs = GetConfiguredKeyTimeoutMs(appConfiguration);
        _upgradeConfiguration = appConfiguration.Upgrade;
        _testLifecycleConfiguration = appConfiguration.TestLifecycle;
        _connectionMode = ParseConnectionMode(appConfiguration.PcbaConnection.Mode);
        _testProfileMode = string.IsNullOrWhiteSpace(appConfiguration.TestMode) ? "finished_product" : appConfiguration.TestMode.Trim().ToLowerInvariant();
        _operationMode = string.Equals(appConfiguration.OperationMode, "developer", StringComparison.OrdinalIgnoreCase) ? "developer" : "production";
        var versionParameters = GetTestParameters(appConfiguration, "board_state", _testProfileMode);
        _versionValidation = new VersionValidationSettings(
            GetConfigurationBoolean(versionParameters, "versionValidationEnabled", true),
            GetConfigurationString(versionParameters, "expectedUbootVersion"),
            GetConfigurationString(versionParameters, "expectedKernelVersion"),
            GetConfigurationString(versionParameters, "expectedRootfsVersion"),
            GetConfigurationString(versionParameters, "expectedGen1AppVersion"));
        // Debug-only bypasses must never leak into production mode.
        _allowSnMismatchForDebug = _operationMode == "developer" && appConfiguration.TestPlan.AllowSnMismatchForDebug;
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
        _tfRemovalPromptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tfRemovalPromptTimer.Tick += (_, _) =>
        {
            _tfRemovalPromptTimer.Stop();
            IsTfRemovalPromptVisible = false;
        };
        _isContinuousTestEnabled = appConfiguration.TestPlan.Continuous.EnabledByDefault;
        _testPlan = BuildActiveTestPlan(appConfiguration);
        _testItemIndexes = _testPlan
            .Select((item, index) => new { item.Id, index })
            .ToDictionary(item => item.Id, item => item.index);

        TestSelections = new ObservableCollection<TestSelectionItemViewModel>(_testPlan
            .Where(item => !item.Skip)
            .Select(item => new TestSelectionItemViewModel(item.Id, GetTestDisplayName(item.Id), true, OnTestSelectionChanged)));
        LoadSavedTestSelection();

        ScanCommand = new AsyncRelayCommand(() => HandleScanAsync(isMockSession: false), () => !string.IsNullOrWhiteSpace(ScannerInput) && HasSelectedTests && !_isSessionRunning && !_isRetestLifecycleActive);
        StartMockSessionCommand = new RelayCommand(StartMockSession);
        ToggleContinuousTestCommand = new RelayCommand(ToggleContinuousTest);
        ConfirmManualPassCommand = new RelayCommand(() => SubmitManualDecision(true), () => IsManualDecisionVisible);
        ConfirmManualFailCommand = new RelayCommand(() => SubmitManualDecision(false), () => IsManualDecisionVisible);
        UpgradeNowCommand = new RelayCommand(() => _upgradeDecisionSource?.TrySetResult(true), () => IsUpgradePromptVisible);
        SkipUpgradeCommand = new RelayCommand(() => _upgradeDecisionSource?.TrySetResult(false), () => IsUpgradePromptVisible);
        ReadBoardStateCommand = new AsyncRelayCommand(ReadBoardStateAsync, () => !string.IsNullOrWhiteSpace(CurrentSn));
        StartPhaseOneCommand = new AsyncRelayCommand(StartPhaseOneAsync, () => !string.IsNullOrWhiteSpace(CurrentSn) && HasSelectedTests);
        SelectAllTestsCommand = new RelayCommand(SelectAllTests, () => IsTestSelectionEnabled);
        DeselectAllTestsCommand = new RelayCommand(DeselectAllTests, () => IsTestSelectionEnabled);
        SelectWifiOnlyCommand = new RelayCommand(SelectWifiOnly, () => IsTestSelectionEnabled);
        ShowTestPageCommand = new RelayCommand(() => IsQueryPage = false);
        ShowQueryPageCommand = new AsyncRelayCommand(ShowQueryPageAsync);
        QueryRecordsCommand = new AsyncRelayCommand(RefreshQueryRecordsAsync);
        RetestCommand = new AsyncRelayCommand<string>(RetestSingleItemAsync, CanRetestItem);
        EndFailedBoardCommand = new RelayCommand(EndFailedBoardLifecycle, () => _isRetestLifecycleActive && !_isRetestRunning);

        Logs = new ObservableCollection<string>();
        LogStartupConfiguration(appConfiguration);
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
            new("up", "上"), new("down", "下"), new("left", "左"), new("right", "右"), new("confirm", "确认")
        };
        if (_testProfileMode == "finished_product")
            DirectionalKeys.Add(new DirectionalKeyViewModel("recovery", "Recovery"));
        Usb2TestSteps = CreateUsbTestSteps();
        Usb3TestSteps = CreateUsbTestSteps();
        var pcbaPointSpecs = new (string Name, double Min, double Max)[]
        {
            ("VDD_DDR_S0",720,730),("VDDQ_DDR_S0",505,510),("MASKROM",1610,1620),("5V",5000,5100),
            ("TS",2500,2700),("2V",2200,2300),("VCC5V0_SYS",5000,5100),("VCC_1V8_S3",1800,1800),
            ("VCC_3V3_S3",3200,3300),("GND",0,0),("VBUS5V0_TYPEC",19000,21000),("VDD2H_DDR_S3",1050,1050),
            ("RECOVERY",1780,1780),("GND",0,0),("VDD_CPU_LIT_S0",710,710),("VBUS5V0_TYPEC",19000,21000),
            ("VCC_SYS",6000,8950),("VDD_CPU_BIG_S0",710,710),("VDD_GPU_S0",0,710),("VCC-RTC",3300,3300),
            ("VDD_LOGIC_S0",750,750),("VDD_NPU_S0",0,750),("VBUSIN_VCC",19000,21000),("RXD",3300,3300),
            ("TXD",3300,3300),("BLED",0,2500),("RLED",0,1100),("GLED",0,2700),
            ("LEDVDD",4650,4650),("VBUS1_TYPEC",5000,5000),("FAN-PWM",3300,3300),("FG",0,5000)
        };
        PcbaTestPoints = new ObservableCollection<PcbaTestPointViewModel>(pcbaPointSpecs.Select((spec, i) => new PcbaTestPointViewModel
        {
            Id = $"TP{i + 1:00}", Name = spec.Name, Channel = i, MinMv = spec.Min, MaxMv = spec.Max
        }));
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
    public string UbootVersion { get; private set; } = "--";
    public string KernelVersion { get; private set; } = "--";
    public string RootfsVersion { get; private set; } = "--";
    public string Gen1AppVersion { get; private set; } = "--";
    public string VersionValidationStatus { get; private set; } = "版本校验：待检测";
    public System.Windows.Media.Brush VersionValidationForeground { get; private set; } = System.Windows.Media.Brushes.Gray;

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

    public string JxTvmStatus
    {
        get => _jxTvmStatus;
        private set => SetProperty(ref _jxTvmStatus, value);
    }

    public bool IsTfRemovalPromptVisible
    {
        get => _isTfRemovalPromptVisible;
        private set => SetProperty(ref _isTfRemovalPromptVisible, value);
    }

    private void ShowTfRemovalPrompt()
    {
        _tfRemovalPromptTimer.Stop();
        IsTfRemovalPromptVisible = true;
        _tfRemovalPromptTimer.Start();
    }
    public string StatusBarDatabase => "数据库：正常";
    public string StatusBarLog => "日志：正常";
    public string StatusBarJxTvm => $"电压检测仪：{JxTvmStatus}";
    public string StatusBarConnection => _connectionStatus;
    public string StatusBarBluetooth => _bluetoothConnectionStatus;
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
    public ObservableCollection<TestSelectionItemViewModel> TestSelections { get; }
    public ObservableCollection<DirectionalKeyViewModel> DirectionalKeys { get; }
    public int KeyGridColumns => DirectionalKeys.Count;
    public ObservableCollection<UsbTestStepViewModel> Usb2TestSteps { get; }
    public ObservableCollection<UsbTestStepViewModel> Usb3TestSteps { get; }
    public ObservableCollection<PcbaTestPointViewModel> PcbaTestPoints { get; }
    public ObservableCollection<UsbTestStepViewModel> SelectedUsbTestSteps =>
        SelectedTestResult?.TestId == "usb3" ? Usb3TestSteps : Usb2TestSteps;
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
                RaisePropertyChanged(nameof(IsUsbTestDetailVisible));
                RaisePropertyChanged(nameof(IsPcbaTestPointsDetailVisible));
                RaisePropertyChanged(nameof(SelectedUsbTestSteps));
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
    public AsyncRelayCommand<string> RetestCommand { get; }
    public RelayCommand EndFailedBoardCommand { get; }
    public RelayCommand SelectAllTestsCommand { get; }
    public RelayCommand DeselectAllTestsCommand { get; }
    public RelayCommand SelectWifiOnlyCommand { get; }
    public bool HasSelectedTests => TestSelections.Any(item => item.IsSelected);
    public bool IsDeveloperMode => _operationMode == "developer";
    public string OperationModeDisplayName => IsDeveloperMode ? "开发者模式" : "生产模式";
    public string OperationModeBackground => IsDeveloperMode ? "#0B4A8B" : "#166534";
    // Test-item selection is available in both finished-product and PCBA
    // environments.  Operation mode controls permissions/diagnostics, not
    // whether the configured test plan can be edited for the next run.
    // Test-point selection is a developer-only capability in both PCBA and
    // finished-product modes. Production mode always runs the configured plan.
    public bool IsTestSelectionEnabled => IsDeveloperMode && !_isSessionRunning && !_isRetestLifecycleActive;
    public string TestSelectionSummary
    {
        get
        {
            var selected = TestSelections.Where(item => item.IsSelected).ToArray();
            if (selected.Length == TestSelections.Count)
            {
                return $"全流程（{selected.Length} 项）";
            }

            return selected.Length == 0
                ? "未选择测试点"
                : $"已选 {selected.Length} 项：{string.Join("、", selected.Select(item => item.DisplayName))}";
        }
    }
    public bool IsRetestLifecycleActive => _isRetestLifecycleActive;
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
    public bool IsManualDecisionVisible => !string.IsNullOrWhiteSpace(_manualDecisionTestId);
    public bool IsKeyTestDetailVisible => SelectedTestResult?.TestId == "keys";
    public bool IsUsbTestDetailVisible => SelectedTestResult?.TestId is "usb2" or "usb3";
    public bool IsPcbaTestPointsDetailVisible => SelectedTestResult?.TestId == "pcba_test_points";
    public string ManualDecisionPrompt => _manualDecisionTestId switch
    {
        "hdmi" => "请观察 HDMI 输出是否正常，然后手动选择通过或失败。",
        "keys" => _testProfileMode == "finished_product"
            ? "请依次按下上、下、左、右方向键和确认键，再按下 Recovery 实体键；六键全部识别后自动通过。"
            : "请依次按下上、下、左、右方向键和确认键；五键全部识别后自动通过。",
        "lcd" => "请观察 LCD：背光正常、RGB 测试图案完整且稳定，无花屏、缺线、闪烁或明显亮暗异常后再判定。",
        "ethernet_led" when _manualDecisionPhase == "operator_confirm_sequence" => "网口已恢复通信，请确认切换到 100M 后绿灯是否正常点亮。",
        "ethernet_led" => "请等待网口灯切换完成后再判定。",
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

    private void SelectAllTests()
    {
        foreach (var item in TestSelections)
        {
            item.IsSelected = true;
        }
    }

    private void SelectWifiOnly()
    {
        foreach (var item in TestSelections)
        {
            item.IsSelected = string.Equals(item.TestId, "wifi", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void DeselectAllTests()
    {
        foreach (var item in TestSelections)
        {
            item.IsSelected = false;
        }
    }

    private void OnTestSelectionChanged()
    {
        if (!_loadingTestSelection) SaveTestSelection();
        RaisePropertyChanged(nameof(HasSelectedTests));
        RaisePropertyChanged(nameof(TestSelectionSummary));
        ScanCommand?.NotifyCanExecuteChanged();
        StartPhaseOneCommand?.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<TestPlanItem> GetSelectedTestPlan()
    {
        var selectedIds = TestSelections
            .Where(item => item.IsSelected)
            .Select(item => item.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _testPlan.Where(item => selectedIds.Contains(item.Id)).ToArray();
    }

    private string TestSelectionFilePath => Path.Combine(AppContext.BaseDirectory, $"test-selection-{_testProfileMode}.json");

    private void LoadSavedTestSelection()
    {
        try
        {
            if (!File.Exists(TestSelectionFilePath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(TestSelectionFilePath)) ?? Array.Empty<string>();
            var selected = saved.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _loadingTestSelection = true;
            foreach (var item in TestSelections) item.IsSelected = selected.Contains(item.TestId);
        }
        catch (Exception exception)
        {
            AppendLog($"Saved test selection load failed: {exception.Message}");
        }
        finally { _loadingTestSelection = false; }
    }

    private void SaveTestSelection()
    {
        try
        {
            var selected = TestSelections.Where(item => item.IsSelected).Select(item => item.TestId).ToArray();
            File.WriteAllText(TestSelectionFilePath, JsonSerializer.Serialize(selected));
        }
        catch (Exception exception) { AppendLog($"Test selection save failed: {exception.Message}"); }
    }

    private void ApplyTestSelectionToResults(IReadOnlyList<TestPlanItem> selectedPlan)
    {
        var selectedIds = selectedPlan.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in TestItems.Where(item => item.TestId != ApplicationUpgradeItemId && !selectedIds.Contains(item.TestId)))
        {
            item.State = TestItemState.Skipped;
        }

        foreach (var result in TestResults.Where(result => result.TestId != ApplicationUpgradeItemId && !selectedIds.Contains(result.TestId)))
        {
            result.ApplyLocalResult(TestItemState.Skipped, "本次未选择该测试点。", new Dictionary<string, object?>
            {
                ["reason"] = "not_selected"
            });
        }

        RaisePropertyChanged(nameof(StatusBarProgress));
    }

    private async Task HandleScanAsync(bool isMockSession)
    {
        AppendLog($"Scan validation entered: rawLength={ScannerInput.Length}, raw={ScannerInput.Replace("\r", "<CR>").Replace("\n", "<LF>")}");
        var sn = _scannerService.Normalize(ScannerInput);
        AppendLog($"Scan normalized: length={sn.Length}, value={sn}");
        if (string.IsNullOrWhiteSpace(sn))
        {
            AppendLog("Scan ignored: normalized SN is empty.");
            return;
        }

        if (sn.Length != RequiredSnLength || sn.Any(character => !char.IsLetterOrDigit(character)))
        {
            var reason = sn.Length != RequiredSnLength
                ? $"SN 长度必须为 {RequiredSnLength} 个字符，当前为 {sn.Length} 个字符。"
                : "SN 只能包含英文字母和数字。";
            AppendLog($"Scan rejected: {sn} ({reason})");
            OperatorInstruction = "SN 格式不正确，请重新扫描。";
            UpdateDebugOutput();
            ScanValidationFailed?.Invoke(this, $"SN 扫码错误\n\n当前 SN：{sn}\n{reason}\n\n请检查条码后重新扫描。\n要求：20 位英文字母或数字。"
            );
            return;
        }

        if (!_upgradePackageReady)
        {
            ScannerInput = string.Empty;
            OperatorInstruction = "升级软件未准备好，暂时不能开始测试。";
            UpdateDebugOutput();
            ScanValidationFailed?.Invoke(this, "当前无法开始测试\n\n升级软件未准备好或校验失败。\n请检查固件路径和升级软件后重试。\n\n本次未创建测试会话。"
            );
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
        _rootSessionId = SessionId;
        _attemptNo = 1;
        _latestBoardState = null;
        VersionValidationStatus = "版本校验：待检测";
        VersionValidationForeground = System.Windows.Media.Brushes.Gray;
        RaisePropertyChanged(nameof(VersionValidationStatus));
        RaisePropertyChanged(nameof(VersionValidationForeground));
        _testResultSourceSessions.Clear();
        SetRetestLifecycleActive(false);
        LastResult = "SN scanned";
        OperatorInstruction = "SN 已确认，正在自动执行检测。请保持产品连接稳定。";
        ScannerInput = string.Empty;
        ResetTestItems();
        IsHistoryLoaded = false;
        HistorySummary = string.Empty;
        _isSessionRunning = true;
        RaisePropertyChanged(nameof(IsTestSelectionEnabled));
        SelectAllTestsCommand.NotifyCanExecuteChanged();
        DeselectAllTestsCommand.NotifyCanExecuteChanged();
        SelectWifiOnlyCommand.NotifyCanExecuteChanged();
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
            await LoadBoardVersionsAsync(client);
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
        if (TestResults.Any(result => result.State == TestItemState.Failed && result.TestId != ApplicationUpgradeItemId))
        {
            SetRetestLifecycleActive(true);
            OperatorInstruction = "本次测试存在失败项。请处理异常后点击对应项目的“重新测试”，完成后点击“结束本机测试”。";
        }
        else
        {
            SetRetestLifecycleActive(false);
            PrepareForNextBoard();
        }
        ScanCommand.NotifyCanExecuteChanged();
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
            AppendLog($"Board state ok: {BoardId} / {BoardState} / {TestMode}");
            await LoadBoardVersionsAsync(client);
            state = await EnsureBoardSnAsync(client, state);
            _latestBoardState = state;

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
                FinalVerdict = finalVerdict,
                RootSessionId = _rootSessionId,
                AttemptNo = 1,
                RecordType = "initial"
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
        RecordResultSources(SessionId);
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
        var selectedTestPlan = GetSelectedTestPlan();
        if (selectedTestPlan.Count == 0)
        {
            LastResult = "No tests selected";
            OperatorInstruction = "请至少选择一个测试点后再开始测试。";
            return;
        }

        ApplyTestSelectionToResults(selectedTestPlan);
        AppendLog($"Session start requested: mode={_connectionMode}, session={SessionId}, sn={CurrentSn}, tests={string.Join(",", selectedTestPlan.Select(item => item.Id))}");
        LastResult = "Stage 1 running";
        OperatorInstruction = "正在接收底层测试结果，请勿断开产品连接。";

        var client = _pcbaCommandClientFactory.Create(_connectionMode);
        _activeSessionClient = client;
        var finalVerdict = "Fail";
        var communicationInterrupted = false;
        BoardState? state = null;

        try
        {
            SetTestItemState(BoardStateItemName, TestItemState.Running);
            AppendLog("ADB/sys.get_board_state request sent.");
            state = await GetBoardStateWithTimeoutAsync(client);
            AppendLog($"ADB/sys.get_board_state response: boardId={state.BoardId}, boardSn={state.BoardSn}, mode={state.TestMode}, state={state.CurrentState}");
            ApplyBoardState(state);
            await LoadBoardVersionsAsync(client);
            state = await EnsureBoardSnAsync(client, state);
            _latestBoardState = state;
            SetTestItemState(BoardStateItemName, TestItemState.Passed);

            try
            {
                var lcdResponse = await client.StartLcdDisplayAsync(SessionId, CurrentSn, state.BoardId);
                AppendLog($"LCD test display start requested: code={lcdResponse.ResultCode}, message={lcdResponse.Message}");
            }
            catch (Exception ex)
            {
                // LCD display is auxiliary and must not block the test run.
                AppendLog($"LCD test display start failed (continuing test): {ex.Message}");
            }

            AppendLog("ADB/session.start request sent.");
            await foreach (var testEvent in client.RunSessionAsync(SessionId, CurrentSn, selectedTestPlan))
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
            communicationInterrupted = true;
            finalVerdict = "Aborted";
            foreach (var result in TestResults)
            {
                if (result.State == TestItemState.Running)
                {
                    result.ApplyLocalResult(TestItemState.Aborted, "测试因上位机连接中断而终止。", new Dictionary<string, object?>
                    {
                        ["reason"] = "host_disconnected",
                        ["resultCode"] = 3998
                    });
                }
                else if (result.State == TestItemState.Pending)
                {
                    result.ApplyLocalResult(TestItemState.Skipped, "测试会话中断，尚未执行。", new Dictionary<string, object?>
                    {
                        ["reason"] = "session_interrupted"
                    });
                }
            }
            LastResult = "Stage 1 aborted";
            OperatorInstruction = "测试已中断，记录为 ABORTED；请重新扫描后开始新的测试。";
            AppendLog($"Session exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _activeSessionClient = null;
            if (_testLifecycleConfiguration.Enabled && _testLifecycleConfiguration.PoweroffAfterTest && _connectionMode != PcbaConnectionMode.Mock)
            {
                try { AppendLog("Test finished; powering off device."); await client.ShutdownDeviceAsync(); }
                catch (Exception ex) { AppendLog($"Device poweroff failed: {ex.Message}"); }
            }
        }

        finalVerdict = ResolveFinalVerdict(finalVerdict);
        LastResult = finalVerdict == "Pass" ? "Stage 1 passed" : finalVerdict == "Aborted" ? "Stage 1 aborted" : "Stage 1 failed";
        if (!communicationInterrupted)
        {
            OperatorInstruction = BuildSessionCompletionInstruction(finalVerdict);
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
                FinalVerdict = finalVerdict,
                RootSessionId = _rootSessionId,
                AttemptNo = 1,
                RecordType = "initial"
            },
            BoardState = state,
            TestResults = BuildTestResultRecords(),
            Logs = _logService.Snapshot().Select(message => new LogEntry { Message = message }).ToArray()
        };

        await _databaseRepository.SaveSessionAsync(record);
        RecordResultSources(SessionId);
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

    private bool CanRetestItem(string testId) =>
        _isRetestLifecycleActive &&
        !_isRetestRunning &&
        _testPlan.Any(item => string.Equals(item.Id, testId, StringComparison.OrdinalIgnoreCase)) &&
        TestResults.Any(result => string.Equals(result.TestId, testId, StringComparison.OrdinalIgnoreCase) && result.State == TestItemState.Failed);

    private async Task RetestSingleItemAsync(string testId)
    {
        if (!CanRetestItem(testId))
        {
            return;
        }

        var testPlanItem = _testPlan.First(item => string.Equals(item.Id, testId, StringComparison.OrdinalIgnoreCase));
        var testItem = TestItems.First(item => string.Equals(item.TestId, testId, StringComparison.OrdinalIgnoreCase));
        var retestSessionId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.Now;
        _attemptNo++;
        _isRetestRunning = true;
        _isSessionRunning = true;
        SessionId = retestSessionId;
        testItem.IsRetesting = true;
        RefreshRetestAvailability();
        EndFailedBoardCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        OperatorInstruction = $"正在重新测试：{testItem.Name}。请按测试提示操作。";
        AppendLog($"Retest started: rootSession={_rootSessionId}, session={retestSessionId}, attempt={_attemptNo}, test={testId}");

        var client = _pcbaCommandClientFactory.Create(_connectionMode);
        _activeSessionClient = client;
        var terminalReportReceived = false;
        try
        {
            await foreach (var testEvent in client.RunSessionAsync(retestSessionId, CurrentSn, [testPlanItem]))
            {
                if (testEvent.Event == "test.report")
                {
                    ApplyTestReport(testEvent);
                    if (testEvent.Status is "passed" or "failed" or "skipped")
                    {
                        terminalReportReceived = true;
                    }
                }
                else if (testEvent.Event == "session.completed")
                {
                    AppendLog($"Retest session completed: test={testId}, status={testEvent.Status}, code={testEvent.ResultCode}, message={testEvent.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ApplyTestReport(new TestSessionEvent
            {
                Event = "test.report",
                TestId = testId,
                Status = "failed",
                ResultCode = 3999,
                Message = $"复测通信异常：{ex.Message}",
                Timestamp = DateTimeOffset.Now,
                Data = new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name }
            });
            terminalReportReceived = true;
            AppendLog($"Retest failed with exception: test={testId}, error={ex.Message}");
        }
        finally
        {
            _activeSessionClient = null;
        }

        if (!terminalReportReceived)
        {
            ApplyTestReport(new TestSessionEvent
            {
                Event = "test.report",
                TestId = testId,
                Status = "failed",
                ResultCode = 3998,
                Message = "复测未收到测试项最终结果。",
                Timestamp = DateTimeOffset.Now,
                Data = new Dictionary<string, object?> { ["terminalReportReceived"] = false }
            });
        }

        _testResultSourceSessions[testId] = retestSessionId;
        var finalVerdict = ResolveFinalVerdict("Pass");
        var record = new TestSessionRecord
        {
            Session = new TestSession
            {
                SessionId = retestSessionId,
                Sn = CurrentSn,
                ProductModel = "PCBA_X1",
                StationCode = "ST01",
                StartTime = startedAt,
                EndTime = DateTimeOffset.Now,
                FinalVerdict = finalVerdict,
                RootSessionId = _rootSessionId,
                AttemptNo = _attemptNo,
                RecordType = "retest",
                RetestTestId = testId
            },
            BoardState = _latestBoardState ?? new BoardState
            {
                BoardId = BoardId,
                BoardSn = CurrentSn,
                TestMode = TestMode,
                CurrentState = BoardState
            },
            TestResults = BuildRetestResultRecords(testId, retestSessionId),
            Logs = _logService.Snapshot().Select(message => new LogEntry { Message = message }).ToArray()
        };

        await _databaseRepository.SaveSessionAsync(record);
        try
        {
            await client.SyncSessionSummaryAsync(retestSessionId, CurrentSn, record.BoardState?.BoardId ?? BoardId, finalVerdict, record.TestResults);
        }
        catch (Exception ex)
        {
            AppendLog($"Retest board summary sync failed: {ex.Message}");
        }

        await LoadRecentSessionsAsync();
        testItem.IsRetesting = false;
        _isRetestRunning = false;
        _isSessionRunning = false;
        RefreshRetestAvailability();
        EndFailedBoardCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        LastResult = finalVerdict == "Pass" ? "Retest passed" : "Retest completed with failures";
        OperatorInstruction = finalVerdict == "Pass"
            ? "所有测试项当前均已通过。请确认后点击“结束本机测试”。"
            : "仍有失败项目，可继续点击对应项目的“重新测试”；完成后点击“结束本机测试”。";
        AppendLog($"Retest persisted: session={retestSessionId}, overall={finalVerdict}");
        UpdateDebugOutput();
    }

    private void EndFailedBoardLifecycle()
    {
        if (!_isRetestLifecycleActive || _isRetestRunning)
        {
            return;
        }

        SetRetestLifecycleActive(false);
        SessionId = string.Empty;
        ScannerInput = string.Empty;
        OperatorInstruction = "本机测试已结束，请扫描下一台产品 SN。";
        AppendLog($"Failed-board lifecycle ended manually: sn={CurrentSn}, rootSession={_rootSessionId}, attempts={_attemptNo}");
        UpdateDebugOutput();
    }

    private void SetRetestLifecycleActive(bool value)
    {
        if (_isRetestLifecycleActive == value)
        {
            RefreshRetestAvailability();
            RaisePropertyChanged(nameof(IsTestSelectionEnabled));
            SelectAllTestsCommand.NotifyCanExecuteChanged();
            DeselectAllTestsCommand.NotifyCanExecuteChanged();
            SelectWifiOnlyCommand.NotifyCanExecuteChanged();
            return;
        }

        _isRetestLifecycleActive = value;
        RaisePropertyChanged(nameof(IsRetestLifecycleActive));
        RefreshRetestAvailability();
        EndFailedBoardCommand.NotifyCanExecuteChanged();
        RetestCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        RaisePropertyChanged(nameof(IsTestSelectionEnabled));
        SelectAllTestsCommand.NotifyCanExecuteChanged();
        DeselectAllTestsCommand.NotifyCanExecuteChanged();
        SelectWifiOnlyCommand.NotifyCanExecuteChanged();
    }

    private void RefreshRetestAvailability()
    {
        foreach (var item in TestItems)
        {
            item.CanRetest = _isRetestLifecycleActive && !_isRetestRunning && item.State == TestItemState.Failed &&
                             _testPlan.Any(planItem => string.Equals(planItem.Id, item.TestId, StringComparison.OrdinalIgnoreCase));
        }

        RetestCommand.NotifyCanExecuteChanged();
    }

    private void RecordResultSources(string sessionId)
    {
        foreach (var result in TestResults.Where(result => result.TestId != ApplicationUpgradeItemId))
        {
            _testResultSourceSessions[result.TestId] = sessionId;
        }
    }

    private IReadOnlyList<TestResultRecord> BuildRetestResultRecords(string retestTestId, string retestSessionId) => TestResults
        .Where(result => result.TestId != ApplicationUpgradeItemId)
        .Select(result =>
        {
            var executed = string.Equals(result.TestId, retestTestId, StringComparison.OrdinalIgnoreCase);
            var data = result.Data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            data["recordOrigin"] = executed ? "executed" : "inherited";
            data["executedInThisRecord"] = executed;
            data["rootSessionId"] = _rootSessionId;
            data["attemptNo"] = _attemptNo;
            data["sourceSessionId"] = executed
                ? retestSessionId
                : _testResultSourceSessions.GetValueOrDefault(result.TestId, _rootSessionId);
            return new TestResultRecord
            {
                TestId = result.TestId,
                Status = result.StateLabel,
                ResultCode = result.ResultCode,
                Message = result.Message,
                Data = data
            };
        })
        .ToArray();

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

    private async Task LoadBoardVersionsAsync(IPcbaCommandClient client)
    {
        VersionValidationStatus = "版本校验：检测中";
        VersionValidationForeground = System.Windows.Media.Brushes.DodgerBlue;
        RaisePropertyChanged(nameof(VersionValidationStatus));
        RaisePropertyChanged(nameof(VersionValidationForeground));
        try
        {
            AppendLog("ADB/sys.get_versions request sent.");
            var versions = await client.GetBoardVersionsAsync(SessionId, CurrentSn);
            UbootVersion = SimplifyVersion(versions.UbootVersion);
            KernelVersion = SimplifyVersion(versions.KernelVersion);
            RootfsVersion = SimplifyVersion(versions.RootfsVersion);
            Gen1AppVersion = SimplifyVersion(versions.Gen1AppVersion);
            AppendLog($"Board versions loaded: U-Boot={UbootVersion}, Kernel={KernelVersion}, RootFS={RootfsVersion}, Gen1App={Gen1AppVersion}");

            if (_versionValidation.Enabled)
            {
                var mismatches = new List<string>();
                var expectedUboot = SimplifyVersion(_versionValidation.Uboot);
                var expectedKernel = SimplifyVersion(_versionValidation.Kernel);
                var expectedRootfs = SimplifyVersion(_versionValidation.Rootfs);
                var expectedGen1App = SimplifyVersion(_versionValidation.Gen1App);
                if (!string.Equals(UbootVersion, expectedUboot, StringComparison.Ordinal))
                    mismatches.Add($"U-Boot 实际 {UbootVersion}，要求 {expectedUboot}");
                if (!string.Equals(KernelVersion, expectedKernel, StringComparison.Ordinal))
                    mismatches.Add($"Kernel 实际 {KernelVersion}，要求 {expectedKernel}");
                if (!string.Equals(RootfsVersion, expectedRootfs, StringComparison.Ordinal))
                    mismatches.Add($"RootFS 实际 {RootfsVersion}，要求 {expectedRootfs}");
                if (!string.Equals(Gen1AppVersion, expectedGen1App, StringComparison.Ordinal))
                    mismatches.Add($"Gen1 App 实际 {Gen1AppVersion}，要求 {expectedGen1App}");
                if (!versions.Gen1AppInstalled) mismatches.Add("Gen1 App 未正常安装");
                if (mismatches.Count > 0)
                    throw new InvalidDataException(string.Join("；", mismatches));
            }

            VersionValidationStatus = _versionValidation.Enabled ? "版本校验：通过" : "版本校验：未启用";
            VersionValidationForeground = _versionValidation.Enabled ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.Gray;
        }
        catch (Exception ex)
        {
            VersionValidationStatus = $"版本校验失败：{ex.Message}";
            VersionValidationForeground = System.Windows.Media.Brushes.Firebrick;
            AppendLog($"Version query failed: {ex.Message}");
            RaisePropertyChanged(nameof(UbootVersion));
            RaisePropertyChanged(nameof(KernelVersion));
            RaisePropertyChanged(nameof(RootfsVersion));
            RaisePropertyChanged(nameof(Gen1AppVersion));
            RaisePropertyChanged(nameof(VersionValidationStatus));
            RaisePropertyChanged(nameof(VersionValidationForeground));
            throw;
        }

        RaisePropertyChanged(nameof(UbootVersion));
        RaisePropertyChanged(nameof(KernelVersion));
        RaisePropertyChanged(nameof(RootfsVersion));
        RaisePropertyChanged(nameof(Gen1AppVersion));
        RaisePropertyChanged(nameof(VersionValidationStatus));
        RaisePropertyChanged(nameof(VersionValidationForeground));
    }

    private static string GetConfigurationString(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : value?.ToString()?.Trim() ?? string.Empty;

    private static string SimplifyVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "--";
        var trimmed = version.Trim();
        var suffixIndex = trimmed.IndexOf('-');
        return suffixIndex > 0 ? trimmed[..suffixIndex] : trimmed;
    }

    private static bool GetConfigurationBoolean(IReadOnlyDictionary<string, object?> parameters, string key, bool fallback) =>
        parameters.TryGetValue(key, out var value) && value is JsonElement element && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : value is bool boolean ? boolean : fallback;

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
        if (string.Equals(testEvent.Event, "session.completed", StringComparison.OrdinalIgnoreCase))
        {
            ShowTfRemovalPrompt();
        }

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
            if (_hostDecisionData.TryGetValue($"{SessionId}:{testEvent.TestId}:{attempt}", out var wifiHostData))
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
        if (testEvent.TestId == "hdmi")
        {
            AppendLog($"HDMI event applied: status={testEvent.Status}, code={testEvent.ResultCode}, message={testEvent.Message}, session={SessionId}, activeClient={_activeSessionClient?.GetType().Name ?? "null"}");
        }
        result?.Apply(testEvent);
        if (testEvent.TestId == "pcba_test_points") UpdatePcbaTestPoints(testEvent);
        if (testEvent.TestId == "pcba_test_points" && testEvent.Status == "running" && GetDataBoolean(testEvent.Data, "readyForHostDecision"))
            _ = HandlePcbaTestPointsMeasurementAsync(testEvent);
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

        if (testEvent.TestId is "usb2" or "usb3")
        {
            UpdateUsbTestSteps(testEvent);
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

        if (testEvent.TestId == "fan" && testEvent.Status == "running")
        {
            HandleVoltageMeasurementReport(testEvent);
        }

        if (testEvent.Status is "passed" or "failed")
        {
            _submittedManualDecisionTests.Remove(testEvent.TestId);
        }

        var eventPhase = GetDataString(testEvent.Data, "phase", string.Empty);
        // Indicator LED emits preliminary phases while it verifies that the
        // charger cable is connected.  Those phases must not expose PASS/FAIL
        // controls or the LED observation dialog yet.
        var indicatorReadyForManualDecision = testEvent.TestId != "indicator_led" ||
            eventPhase == "rgb_sequence" ||
            GetDataBoolean(testEvent.Data, "manualObserved");
        var requiresManualDecision = testEvent.Status == "running" &&
            indicatorReadyForManualDecision &&
            (testEvent.TestId != "ethernet_led" || eventPhase == "operator_confirm_sequence");

        if (testEvent.TestId == "indicator_led" && testEvent.Status == "running" && !indicatorReadyForManualDecision)
        {
            OperatorInstruction = "请先插入充电线，系统检测到 Charging 后才开始指示灯观察。";
        }
        var indicatorLedWaitingForCharger = testEvent.TestId == "indicator_led" &&
            testEvent.Status == "running" &&
            string.Equals(eventPhase, "wait_charger", StringComparison.OrdinalIgnoreCase);
        var indicatorLedChargerTimedOut = testEvent.TestId == "indicator_led" &&
            testEvent.Status == "failed" &&
            string.Equals(GetDataString(testEvent.Data, "failureReason", string.Empty), "charger_not_connected", StringComparison.OrdinalIgnoreCase);
        if ((indicatorLedWaitingForCharger || indicatorLedChargerTimedOut) &&
            !_chargerNotConnectedDialogShown)
        {
            _chargerNotConnectedDialogShown = true;
            ChargerNotConnectedRequested?.Invoke(this, EventArgs.Empty);
        }

        // Ethernet LED testing has several running phases before the operator can
        // make a decision (most importantly wait_cable). Do not retain a stale
        // manual decision from a previous event while the cable is disconnected
        // or the speed sequence is still running.
        if (testEvent.TestId == "ethernet_led" &&
            testEvent.Status == "running" &&
            eventPhase != "operator_confirm_sequence")
        {
            _manualDecisionTestId = null;
            _manualDecisionSessionId = null;
            _manualDecisionPhase = string.Empty;
        }

        if (requiresManualDecision &&
            (testEvent.TestId is "hdmi" or "lcd" or "ethernet_led" or "reset_button" ||
             testEvent.TestId == "indicator_led"))
        {
            _manualDecisionSessionId = SessionId;
        }

        if (testEvent.TestId == "ethernet_led" && eventPhase == "operator_confirm_sequence")
        {
            AppendLog($"ETHERNET_LED_PASS_VISIBLE utc={DateTimeOffset.UtcNow:O}");
            _submittedManualDecisionTests.Remove(testEvent.TestId);
            _manualDecisionPhase = eventPhase;
        }

        _manualDecisionTestId = requiresManualDecision &&
            !_submittedManualDecisionTests.Contains(testEvent.TestId) &&
            (testEvent.TestId is "hdmi" or "lcd" or "ethernet_led" or "reset_button" ||
             testEvent.TestId == "indicator_led")
            ? testEvent.TestId
            : testEvent.Status is "passed" or "failed" && testEvent.TestId == _manualDecisionTestId
                    ? null
                    : _manualDecisionTestId;
        if (_manualDecisionTestId is null)
        {
            _manualDecisionSessionId = null;
        }
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

        return testEvent.TestId is "usb2" or "usb3"
            ? BuildUsbInstruction(testEvent)
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
            "usb2" or "usb3" => BuildUsbInstruction(testEvent),
            _ => BuildGeneralTestInstruction(testEvent)
        };
    }

    private async Task HandlePcbaTestPointsMeasurementAsync(TestSessionEvent testEvent)
    {
        if (_activeSessionClient is null || _jxTvmService is null || !_jxTvmService.IsEnabled) return;
        try
        {
            AppendLog("JX-TVM PCBA measurement start: COM3, registers=1233-1264");
            var values = await _jxTvmService.ReadAllChannelVoltagesMvAsync();
            var failed = 0;
            for (var i = 0; i < values.Length && i < PcbaTestPoints.Count; i++)
            {
                var point = PcbaTestPoints[i];
                var pass = values[i] >= point.MinMv && values[i] <= point.MaxMv;
                if (!pass) failed++;
                point.Apply(values[i], pass ? "passed" : "failed");
                AppendLog($"JX-TVM channel[{i + 1}] {point.Name}: {values[i]}mV, range={point.MinMv}-{point.MaxMv}, {(pass ? "PASS" : "FAIL")}");
            }
            AppendLog($"JX-TVM PCBA measurement completed: channels=32 failed={failed}");
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, "pcba_test_points", failed == 0,
                failed == 0 ? "jx_tvm_measurement_passed" : "jx_tvm_voltage_out_of_range");
        }
        catch (Exception ex)
        {
            AppendLog($"JX-TVM PCBA measurement failed: {ex.GetType().Name}: {ex.Message}");
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, "pcba_test_points", false, "jx_tvm_communication_error");
        }
    }

    private void UpdatePcbaTestPoints(TestSessionEvent testEvent)
    {
        if (testEvent.Status == "running" && GetDataString(testEvent.Data, "phase", "") is "sampling" or "start")
            foreach (var point in PcbaTestPoints) point.Reset();
        if (!testEvent.Data.TryGetValue("points", out var raw) || raw is not JsonElement array || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var idx) || !idx.TryGetInt32(out var index)) continue;
            var point = PcbaTestPoints.FirstOrDefault(p => p.Channel + 1 == index);
            if (point is null) continue;
            double? voltage = item.TryGetProperty("voltageMv", out var value) && value.TryGetDouble(out var v) ? v : null;
            var passed = item.TryGetProperty("passed", out var ok) && ok.ValueKind == JsonValueKind.True;
            var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var minMv = item.TryGetProperty("minMv", out var minValue) && minValue.TryGetDouble(out var min) ? min : (double?)null;
            var maxMv = item.TryGetProperty("maxMv", out var maxValue) && maxValue.TryGetDouble(out var max) ? max : (double?)null;
            point.ApplyMetadata(name, minMv, maxMv);
            point.Apply(voltage, testEvent.Status == "running" ? "running" : passed ? "passed" : "failed");
        }
    }

    private static string BuildUsbInstruction(TestSessionEvent testEvent)
    {
        var version = testEvent.TestId == "usb2" ? "USB2.0" : "USB3.0";
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var port = GetDataString(testEvent.Data, "port", string.Empty) switch
        {
            "port1" => "接口1",
            "port2" => "接口2",
            _ => "当前接口"
        };
        var direction = GetDataString(testEvent.Data, "direction", string.Empty) switch
        {
            "normal" => "正插",
            "reverse" => "反插",
            _ => string.Empty
        };
        var step = GetDataInt(testEvent.Data, "stepIndex");
        var speed = GetDataInt(testEvent.Data, "actualSpeedMbps");

        if (testEvent.Status == "running")
        {
            var expectedLed = GetDataString(testEvent.Data, "expectedLed", string.Empty);
            var expectedSpeed = GetDataInt(testEvent.Data, "expectedSpeedMbps");
            var actualSpeed = GetDataInt(testEvent.Data, "actualSpeedMbps");
            var phaseIndex = GetDataInt(testEvent.Data, "phaseIndex");
            var phaseCount = GetDataInt(testEvent.Data, "phaseCount");
            var speedText = expectedSpeed > 0 ? $"目标 {expectedSpeed} Mbps" : string.Empty;
            var actualText = actualSpeed > 0 ? $"，当前 {actualSpeed} Mbps" : string.Empty;
            if (expectedLed.Length > 0)
            {
                var progress = phaseIndex > 0 && phaseCount > 0 ? $"（第 {phaseIndex}/{phaseCount} 阶段）" : string.Empty;
                return $"请观察{expectedLed}色网口灯是否亮起，{speedText}{actualText}{progress}。确认后选择 PASS/FAIL。";
            }
            return phase switch
            {
                "wait_remove_before_start" => $"开始 {version} 测试前，请先拔出所有 USB U 盘。",
                "wait_insert" => $"{version} 第 {Math.Max(1, step)}/4 步：请将 U 盘插入{port}，方向为{direction}。",
                "detected" => $"已检测到{port}{direction}，链路速率 {speed} Mbps。请拔出 U 盘后继续下一步。",
                "wait_remove" => $"{version} 第 {Math.Max(1, step)}/4 步检测完成，请拔出{port}上的 U 盘。",
                _ => $"正在执行 {version} 两个接口正反插测试。"
            };
        }

        return testEvent.Status switch
        {
            "passed" => $"{version} 两个接口正反插四步测试完成。",
            "failed" => $"{version} 测试失败，请检查插拔顺序、物理接口和链路速率。",
            "skipped" => $"{version}：本轮不测试。",
            _ => $"{version} 状态更新。"
        };
    }

    private static ObservableCollection<UsbTestStepViewModel> CreateUsbTestSteps() => new()
    {
        new(1, "port1", "normal"),
        new(2, "port1", "reverse"),
        new(3, "port2", "normal"),
        new(4, "port2", "reverse")
    };

    private void UpdateUsbTestSteps(TestSessionEvent testEvent)
    {
        var steps = testEvent.TestId == "usb3" ? Usb3TestSteps : Usb2TestSteps;
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        var stepIndex = GetDataInt(testEvent.Data, "stepIndex");
        var speed = GetDataInt(testEvent.Data, "actualSpeedMbps");

        if (testEvent.Status == "passed")
        {
            foreach (var step in steps) step.SetState("completed", step.ActualSpeedMbps);
            return;
        }

        if (testEvent.Status == "failed")
        {
            var activeStep = steps.FirstOrDefault(step => step.Phase is "wait_insert" or "detected" or "wait_remove");
            var failedIndex = Math.Clamp(stepIndex > 0 ? stepIndex : activeStep?.StepIndex ?? 1, 1, steps.Count);
            for (var index = 0; index < failedIndex - 1; index++)
                steps[index].SetState("completed", steps[index].ActualSpeedMbps);
            steps[failedIndex - 1].SetState("failed", speed);
            return;
        }

        if (testEvent.Status != "running") return;
        if (phase == "wait_remove_before_start")
        {
            foreach (var step in steps) step.Reset();
            return;
        }

        if (stepIndex < 1 || stepIndex > steps.Count) return;
        for (var index = 0; index < stepIndex - 1; index++)
            steps[index].SetState("completed", steps[index].ActualSpeedMbps);

        var visualPhase = phase switch
        {
            "wait_insert" => "wait_insert",
            "detected" => "detected",
            "wait_remove" => "wait_remove",
            _ => "pending"
        };
        steps[stepIndex - 1].SetState(visualPhase, speed);
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
                "prepare_disconnect" => BuildEthernetLedSequenceInstruction(testEvent.Data),
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

    private static string BuildEthernetLedSequenceInstruction(IReadOnlyDictionary<string, object?> data)
    {
        var led100m = GetDataString(data, "led100mColor", "green");
        var led1000m = GetDataString(data, "led1000mColor", "yellow");
        var phaseSeconds = Math.Max(1, GetDataInt(data, "phaseDurationMs") / 1000);
        return $"网口即将暂时断开并自动切速：先观察百兆 {led100m} 色灯，再观察千兆 {led1000m} 色灯；每阶段约 {phaseSeconds} 秒。闪灯完成并重新连接后再分别确认。";
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
        return testEvent.Status is "running" or "failed";
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
            return _testProfileMode == "finished_product"
                ? "六键测试通过：上、下、左、右、确认和 Recovery 均已识别。"
                : "五键测试通过：上、下、左、右、确认均已识别。";
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
        return _testProfileMode == "finished_product"
            ? $"六键测试第一阶段：请在 {FormatKeyTimeoutSeconds()} 秒内依次按上、下、左、右、确认键。五键完成后再按 Recovery，底层将通过 ADC 判定。已识别：{detectedText}；剩余：{missingText}；倒计时：{remainingSeconds} 秒。"
            : $"五键测试：请在 {FormatKeyTimeoutSeconds()} 秒内依次按上、下、左、右、确认键。已识别：{detectedText}；剩余：{missingText}；倒计时：{remainingSeconds} 秒。";
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

    private string GetTestDisplayName(string testId) => testId switch
    {
        ApplicationUpgradeItemId => "设备程序升级",
        "board_state" => "板状态",
        "emmc" => "EMMC",
        "ddr" => "DDR",
        "hdmi" => "HDMI",
        "keys" => _testProfileMode == "finished_product" ? "六键测试" : "五键测试",
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
        var samplingDurationMs = Math.Max(100, GetParameterInt(parameters, "timeoutMs", 4000));
        var currentMinMa = GetParameterInt(parameters, "chargeCurrentMinMa", 1800);
        var currentMaxMa = GetParameterInt(parameters, "chargeCurrentMaxMa", 4500);
        AppendLog($"TYPE-C charging parameters: currentMinMa={currentMinMa}, currentMaxMa={currentMaxMa}");

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

        if (rawVoltages.Length == 0)
        {
            _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
            {
                ["phase"] = "sampling_failed",
                ["pmicCommunicationOk"] = GetDataBoolean(testEvent.Data, "pmicCommunicationOk"),
                ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
                ["samplingDurationMs"] = samplingDurationMs,
                ["failureReason"] = "missing_battery_voltage_sample"
            };
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, testEvent.TestId, false, "missing_battery_voltage_sample");
            AppendLog("TYPE-C charging automatic decision: FAIL (missing_battery_voltage_sample)");
            return;
        }

        var orderedRawCurrents = rawCurrents.OrderBy(value => value).ToArray();
        var filteredCurrents = orderedRawCurrents;
        var medianCurrentMa = filteredCurrents.Length == 0 ? 0 : filteredCurrents[filteredCurrents.Length / 2];
        var avgCurrentMa = filteredCurrents.Length == 0 ? 0 : (int)Math.Round(filteredCurrents.Average());
        var measuredCurrentMin = filteredCurrents.Length == 0 ? 0 : filteredCurrents.Min();
        var measuredCurrentMax = filteredCurrents.Length == 0 ? 0 : filteredCurrents.Max();
        var rippleMa = filteredCurrents.Length == 0 ? 0 : measuredCurrentMax - measuredCurrentMin;
        var outlierCount = 0;
        var avgVoltageMv = rawVoltages.Length == 0 ? 0 : (int)Math.Round(rawVoltages.Average());

        var passed = avgCurrentMa >= currentMinMa && avgCurrentMa <= currentMaxMa;
        var reason = passed
            ? "charge_current_in_range"
            : avgCurrentMa < currentMinMa ? "charge_current_too_low"
            : "charge_current_too_high";

        _hostDecisionData[testEvent.TestId] = new Dictionary<string, object?>
        {
            ["phase"] = "sampling_completed",
            ["readyForHostDecision"] = GetDataBoolean(testEvent.Data, "readyForHostDecision"),
            ["samplingDurationMs"] = GetDataInt(testEvent.Data, "samplingDurationMs") > 0 ? GetDataInt(testEvent.Data, "samplingDurationMs") : samplingDurationMs,
            ["elapsedMs"] = GetDataInt(testEvent.Data, "samplingDurationMs") > 0 ? GetDataInt(testEvent.Data, "samplingDurationMs") : samplingDurationMs,
            ["sampleCount"] = orderedRawCurrents.Length,
            ["rawCurrentSamplesMa"] = orderedRawCurrents,
            ["chargeVoltageMv"] = avgVoltageMv,
            ["averageChargeCurrentMa"] = avgCurrentMa,
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
        var decisionKey = $"{SessionId}:{testEvent.TestId}:{attempt}";
        if (!_automaticDecisionTests.Add(decisionKey))
        {
            return;
        }

        var parameters = _testPlan.First(item => item.Id == testEvent.TestId).Parameters;
        var minRssi = GetParameterInt(parameters, "minRssi", -40);
        var ssid = GetDataString(testEvent.Data, "ssid", string.Empty);
        var iface = GetDataString(testEvent.Data, "interfaceName", string.Empty);
        var found = GetDataBoolean(testEvent.Data, "found");
        var rssi = GetDataInt(testEvent.Data, "rssi");
        var validRssi = rssi > -127;
        var passed = found && validRssi && rssi >= minRssi;
        var reason = !found
            ? "ssid_not_found"
            : !validRssi ? "ssid_not_found" : rssi < minRssi ? "rssi_too_low" : "rssi_in_range";

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

    private void HandleBatteryDischargeReport(TestSessionEvent testEvent)
    {
        var phase = GetDataString(testEvent.Data, "phase", string.Empty);
        if (phase == "wait_unplug_charger" && GetDataBoolean(testEvent.Data, "requiresOperatorConfirmation"))
        {
            if (_batteryPreparationPromptActive) return;
            _batteryPreparationPromptActive = true;
            OperatorInstruction = "请拔掉充电器、相机、HDMI、USB 等外设，网线保持连接，然后在弹窗中确认。";
            AppendLog($"Battery discharge preparation required: chargerStatus={GetDataString(testEvent.Data, "chargerStatus", "unknown")}");
            BatteryDischargePreparationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        foreach (var step in Usb2TestSteps.Concat(Usb3TestSteps))
        {
            step.Reset();
        }

        if (phase == "sampling")
        {
            OperatorInstruction = $"板放电自动检测中：{GetDataInt(testEvent.Data, "voltageMv")}mV / {GetDataInt(testEvent.Data, "dischargeCurrentMa")}mA";
        }
    }

    public async Task ConfirmBatteryDischargePreparationAsync()
    {
        try
        {
            if (_activeSessionClient is null) return;
            await _activeSessionClient.SubmitTestDecisionAsync(SessionId, "battery_management", true, "operator_confirmed_external_devices_removed");
            AppendLog("Battery discharge preparation confirmed; board will recheck charger status.");
            OperatorInstruction = "正在重新检查充电状态，确认进入放电后将自动采样。";
        }
        finally
        {
            _batteryPreparationPromptActive = false;
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

        return PcbaConnectionMode.Tcp;
    }

    public async Task InitializeAsync()
    {
        await ProbeJxTvmAsync();
        await ValidateUpgradePackageAsync();
        if (_upgradePackageReady && _connectionMode != PcbaConnectionMode.Mock && _upgradeConfiguration.Enabled)
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
        else if (!_upgradeConfiguration.Enabled)
        {
            _adbUpgradeMonitorTimer.Stop();
            OperatorInstruction = "升级检查已关闭，请扫描 SN 开始测试。";
            UpdateDebugOutput();
        }
        if (_bluetoothBroadcasterService is not null)
        {
            if (!_bluetoothBroadcasterService.IsEnabled)
            {
                _bluetoothConnectionStatus = "蓝牙：未启用";
            }
            else
            {
                try
                {
                    await _bluetoothBroadcasterService.ConfigureAsync();
                    _bluetoothConnectionStatus = "蓝牙：已连接";
                    AppendLog("Bluetooth broadcaster configured.");
                }
                catch (Exception ex)
                {
                    _bluetoothConnectionStatus = "蓝牙：通信异常";
                    AppendLog($"Bluetooth broadcaster setup failed: {ex.Message}");
                }
            }
            RaisePropertyChanged(nameof(StatusBarBluetooth));
        }
        await LoadRecentSessionsAsync();
    }

    private async Task ValidateUpgradePackageAsync()
    {
        if (!_upgradeConfiguration.Enabled)
        {
            _upgradePackageReady = true;
            _applicationUpgradeCheckCompleted = true;
            _localUpgradeBinaryPath = "升级检查已关闭";
            var disabledResult = TestResults.FirstOrDefault(item => item.TestId == ApplicationUpgradeItemId);
            disabledResult?.ApplyLocalResult(TestItemState.Skipped, "升级检查已关闭。", new Dictionary<string, object?>
            {
                ["status"] = "disabled"
            });
            SetTestItemState(ApplicationUpgradeItemId, TestItemState.Skipped);
            RaiseApplicationUpgradeStatusChanged();
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

    public void AppendExternalLog(string message)
    {
        if (message.Contains("stream closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connect failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection", StringComparison.OrdinalIgnoreCase) && message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _connectionStatus = "连接：已断开";
            RaisePropertyChanged(nameof(StatusBarConnection));
        }
        else if (message.Contains("reconnect scheduled", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("connect attempt", StringComparison.OrdinalIgnoreCase))
        {
            _connectionStatus = "连接：重连中";
            RaisePropertyChanged(nameof(StatusBarConnection));
        }
        else if (message.Contains("TCP connect ok", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            _connectionStatus = "连接：已连接";
            RaisePropertyChanged(nameof(StatusBarConnection));
        }
        AppendLog(message);
    }

    private async Task ProbeJxTvmAsync()
    {
        if (_jxTvmService is null || !_jxTvmService.IsEnabled)
        {
            JxTvmStatus = "未启用";
        }
        else
        {
            try
            {
                var probe = await _jxTvmService.ReadChannelVoltageMvAsync(1);
                JxTvmStatus = "已连接";
                AppendLog($"JX-TVM probe succeeded: register1233={probe}mV, slave=1, baud=9600.");
            }
            catch (UnauthorizedAccessException) { JxTvmStatus = "串口被占用"; }
            catch (Exception ex) { JxTvmStatus = "通信异常"; AppendLog($"JX-TVM probe failed: {ex.Message}"); }
        }
        RaisePropertyChanged(nameof(StatusBarJxTvm));
    }

    public void ClearScannerInput() => ScannerInput = string.Empty;

    private void LogStartupConfiguration(AppConfiguration configuration)
    {
        try
        {
            var settings = new EnvironmentConfigurationService().Load();
            var connection = configuration.PcbaConnection;
            var upgrade = configuration.Upgrade;
            var logging = configuration.Logging;
            AppendLog("Startup configuration loaded: path=" + EnvironmentConfigurationService.ResolvePath());
            AppendLog($"Startup basic settings: mode={settings.Mode}, bluetoothPort={settings.Port}, bluetoothTarget={settings.TargetName}, wifiSsid={settings.WifiSsid}, ethernetPingIp={settings.EthernetPingIp}, ethernetLedObservationMs={settings.EthernetLedObservationMs}");
            AppendLog($"Startup device communication: mode={connection.Mode}, host={connection.Host}, port={connection.Port}, ethernetOnly={connection.EthernetOnly}, adapterId={connection.AdapterId}, adapterName={connection.AdapterName}, localIp={connection.LocalIp}, discoveryEnabled={connection.Discovery.Enabled}, discoveryMode={connection.Discovery.Mode}, subnet={connection.Discovery.Subnet}, connectTimeoutMs={connection.Discovery.ConnectTimeoutMs}, maxParallel={connection.Discovery.MaxParallel}");
            AppendLog($"Startup discharge settings: finished voltage={settings.FinishedProductBattery.VoltageMinMv}-{settings.FinishedProductBattery.VoltageMaxMv}mV, current={settings.FinishedProductBattery.CurrentMinMa}-{settings.FinishedProductBattery.CurrentMaxMa}mA; pcba voltage={settings.PcbaBattery.VoltageMinMv}-{settings.PcbaBattery.VoltageMaxMv}mV, current={settings.PcbaBattery.CurrentMinMa}-{settings.PcbaBattery.CurrentMaxMa}mA");
            AppendLog($"Startup fast-charge settings: finished current={settings.FinishedProductFastCharge.CurrentMinMa}-{settings.FinishedProductFastCharge.CurrentMaxMa}mA; pcba current={settings.PcbaFastCharge.CurrentMinMa}-{settings.PcbaFastCharge.CurrentMaxMa}mA");
            AppendLog($"Startup upgrade settings: enabled={upgrade.Enabled}, transport={upgrade.Transport}, localPath={upgrade.LocalBinaryPath}, remotePath={upgrade.RemoteBinaryPath}, service={upgrade.ServiceName}, applicationVersion={upgrade.ApplicationVersion}, sshUser={upgrade.SshUser}, sshPort={upgrade.SshPort}");
            AppendLog($"Startup logging settings: fileEnabled={logging.FileEnabled}, filePath={logging.FilePath}");
            AppendLog($"Startup test plan: activeMode={_testProfileMode}, testCount={_testPlan.Count}, tests={string.Join(",", _testPlan.Select(item => item.Id))}");
        }
        catch (Exception exception)
        {
            AppendLog($"Startup configuration snapshot failed: {exception.GetType().Name}: {exception.Message}");
        }
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

    private string BuildSingleFailureInstruction(string testId) => testId switch
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
        var isDeveloperMode = string.Equals(configuration.OperationMode, "developer", StringComparison.OrdinalIgnoreCase);
        var enabledSource = isDeveloperMode && modeConfiguration is not null && modeConfiguration.EnabledTests.Length > 0
            ? modeConfiguration.EnabledTests
            : isDeveloperMode ? configuration.TestPlan.EnabledTests : Array.Empty<string>();
        var disabledSource = isDeveloperMode && modeConfiguration is not null && modeConfiguration.DisabledTests.Length > 0
            ? modeConfiguration.DisabledTests
            : isDeveloperMode ? configuration.TestPlan.DisabledTests : Array.Empty<string>();
        var skippedSource = isDeveloperMode && modeConfiguration is not null
            ? modeConfiguration.SkippedTests
            : isDeveloperMode ? configuration.TestPlan.SkippedTests : new Dictionary<string, string>();
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

        // PCBA test-point acquisition is not part of the finished-product flow.
        if (string.Equals(mode, "finished_product", StringComparison.OrdinalIgnoreCase))
        {
            plan.RemoveAll(item => string.Equals(item.Id, "pcba_test_points", StringComparison.OrdinalIgnoreCase));
        }

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
        if (configuration.TestModes.TryGetValue(mode, out var modeConfiguration) &&
            modeConfiguration.TestParameters.TryGetValue(testId, out var modeParameters))
        {
            foreach (var parameter in modeParameters)
            {
                result[parameter.Key] = parameter.Value;
            }
        }
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
            result.TryAdd("chargerStatusPath", "/sys/class/power_supply/bq2579x-charger/status");
            result.TryAdd("chargerRequiredStatus", "Charging");
            result.TryAdd("chargerCheckTimeoutMs", 0);
            result.TryAdd("chargerCheckPollIntervalMs", 250);
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
            result.TryAdd("reconnectDelayMs", 3000);
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

    private IReadOnlyList<TestItemViewModel> BuildTestItems(IReadOnlyList<TestPlanItem> testPlan)
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
        _manualDecisionSessionId = null;
        _manualDecisionPhase = string.Empty;
        _automaticDecisionTests.Clear();
        _submittedManualDecisionTests.Clear();
        _chargerNotConnectedDialogShown = false;
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
            AppendLog($"HDMI decision ignored: visible={IsManualDecisionVisible}, test={_manualDecisionTestId ?? "null"}, client={_activeSessionClient?.GetType().Name ?? "null"}, session={SessionId}");
            return;
        }

        var testId = _manualDecisionTestId!;
        var decisionSessionId = _manualDecisionSessionId ?? SessionId;
        var displayName = GetTestDisplayName(testId);
        OperatorInstruction = passed ? $"{displayName} 已确认通过，继续后续测试。" : $"{displayName} 已确认失败，记录失败并继续后续测试。";
        AppendLog($"{testId} manual decision: {(passed ? "PASS" : "FAIL")}");

        try
        {
            AppendLog($"{testId} operator decision sending: session={decisionSessionId}, passed={passed}");
            await _activeSessionClient.SubmitOperatorDecisionAsync(decisionSessionId, testId, passed);
            AppendLog($"{testId} operator decision written: session={decisionSessionId}");
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
            var currentEnvironment = new EnvironmentConfigurationService().Load();
            if (!string.IsNullOrWhiteSpace(currentEnvironment.WifiSsid))
            {
                _wifiRequest.Ssid = currentEnvironment.WifiSsid.Trim();
            }
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
            // Reload the persisted environment settings immediately before each
            // Ethernet test. The settings dialog can save while this view model
            // is alive; retaining the constructor-time request would otherwise
            // keep using the old/default router IP.
            var currentEnvironment = new EnvironmentConfigurationService().Load();
            if (System.Net.IPAddress.TryParse(currentEnvironment.EthernetPingIp, out var configuredRouterIp) &&
                configuredRouterIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                _ethernetRequest.RouterIp = configuredRouterIp.ToString();
                _ethernetRequest.TargetIp = configuredRouterIp.ToString();
            }
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

