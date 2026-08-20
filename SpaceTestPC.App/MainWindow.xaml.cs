using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;

namespace SpaceTestPC.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _sequenceScrollCancellation;
    private readonly DispatcherTimer _scannerInputIdleTimer;

    private void EnvironmentSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.AppendExternalLog($"Settings dialog requested: focusedElement={Keyboard.FocusedElement?.GetType().Name ?? "none"}.");
            var window = new EnvironmentSettingsWindow { Owner = this };
            window.ShowDialog();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"打开环境配置失败：{exception.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Dispatcher.BeginInvoke(() =>
            {
                ScannerInputTextBox.Focus();
                Keyboard.Focus(ScannerInputTextBox);
                _viewModel.AppendExternalLog("Settings dialog closed; scanner focus restored.");
            }, DispatcherPriority.Input);
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        _scannerInputIdleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _scannerInputIdleTimer.Tick += ScannerInputIdleTimer_OnTick;

        var configuration = new ConfigurationService().Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var manualTestInteractionService = new ManualTestInteractionService();
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var mode = string.IsNullOrWhiteSpace(configuration.TestMode) ? "finished_product" : configuration.TestMode.Trim().ToLowerInvariant();
        var modeConfiguration = configuration.TestModes.TryGetValue(mode, out var configuredMode)
            ? configuredMode
            : new Models.TestModeConfiguration();
        var databaseName = string.IsNullOrWhiteSpace(modeConfiguration.DatabaseName)
            ? mode == "finished_product" ? "space-test-finished-product.db" : "space-test-pcba.db"
            : modeConfiguration.DatabaseName;
        var repository = new SqliteDatabaseRepository(Path.Combine(dataDir, databaseName), mode);
        var pcbaConnection = configuration.PcbaConnection;
        var adbDeviceSerial = string.IsNullOrWhiteSpace(pcbaConnection.AdbDeviceSerial)
            ? null
            : pcbaConnection.AdbDeviceSerial.Trim();
        var discoveryService = new PcbaDiscoveryService(pcbaConnection);
        var adbClient = new AdbPcbaCommandClient(
            adbPath: pcbaConnection.AdbPath,
            localPort: pcbaConnection.Port,
            remotePort: pcbaConnection.Port,
            deviceSerial: adbDeviceSerial,
            upgradeConfiguration: configuration.Upgrade);
        var tcpClient = new AdbPcbaCommandClient(
            adbPath: pcbaConnection.AdbPath,
            localPort: pcbaConnection.Port,
            remotePort: pcbaConnection.Port,
            deviceSerial: adbDeviceSerial,
            useAdbForward: false,
            tcpHost: pcbaConnection.Host,
            tcpPort: pcbaConnection.Port,
            discoveryService: discoveryService,
            upgradeConfiguration: configuration.Upgrade);
        var pcbaClientFactory = new PcbaCommandClientFactory(
            new MockPcbaCommandClient(mockConfiguration: configuration.TestPlan.Mock, manualTestInteractionService: manualTestInteractionService),
            adbClient,
            tcpClient);

        _viewModel = new MainViewModel(
            new ScannerService(),
            pcbaClientFactory,
            new StatusMonitorService("Voltage"),
            new StatusMonitorService("Battery"),
            repository,
            new LogService(configuration.Logging),
            configuration,
            manualTestInteractionService,
            new Jk5506Service(configuration.Jk5506),
            new JxTvmService(configuration.JxTvm),
            new BluetoothBroadcasterService(configuration.BluetoothBroadcaster));
        discoveryService.Log += message => Dispatcher.Invoke(() => _viewModel.AppendExternalLog(message));

        DataContext = _viewModel;
        _viewModel.SequenceAdvanceRequested += SequenceAdvanceRequested;
        _viewModel.HistoryRecordFound += HistoryRecordFound;
        _viewModel.ScanValidationFailed += ScanValidationFailed;
        adbClient.Log += message => Dispatcher.Invoke(() => _viewModel.AppendExternalLog(message));
        tcpClient.Log += message => Dispatcher.Invoke(() => _viewModel.AppendExternalLog(message));
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ScannerInputTextBox.Focus();
        Keyboard.Focus(ScannerInputTextBox);
    }

    private void ScannerInputTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _viewModel.AppendExternalLog($"Scanner UI Enter received: textLength={ScannerInputTextBox.Text.Length}");
        _scannerInputIdleTimer.Stop();
        SubmitScanIfPossible();
        e.Handled = true;
    }

    private void ScannerInputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        _scannerInputIdleTimer.Stop();
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            return;
        }

        if (textBox.Text.Contains('\r') || textBox.Text.Contains('\n'))
        {
            _viewModel.AppendExternalLog($"Scanner UI line ending received: textLength={textBox.Text.Length}");
            SubmitScanIfPossible();
            return;
        }

        _scannerInputIdleTimer.Start();
    }

    private void ScannerInputIdleTimer_OnTick(object? sender, EventArgs e)
    {
        _scannerInputIdleTimer.Stop();
        if (string.IsNullOrWhiteSpace(ScannerInputTextBox.Text))
        {
            return;
        }

        _viewModel.AppendExternalLog($"Scanner UI idle submit: textLength={ScannerInputTextBox.Text.Length}");
        SubmitScanIfPossible();
    }

    private void TestSequenceListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TestSequenceListBox.SelectedItem is TestItemViewModel item)
        {
            _viewModel.SelectTestResult(item.TestId);
        }
    }

    private async void SequenceAdvanceRequested(object? sender, TestItemViewModel testItem)
    {
        _sequenceScrollCancellation?.Cancel();
        var cancellation = _sequenceScrollCancellation = new CancellationTokenSource();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (cancellation.IsCancellationRequested) return;

        if (TestSequenceListBox.ItemContainerGenerator.ContainerFromItem(testItem) is not FrameworkElement item ||
            FindVisualChild<ScrollViewer>(TestSequenceListBox) is not ScrollViewer viewer)
        {
            return;
        }

        var position = item.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        var lowerEdge = position + item.ActualHeight;
        var safeTop = viewer.ViewportHeight * 0.18;
        var safeBottom = viewer.ViewportHeight * 0.82;
        if (position >= safeTop && lowerEdge <= safeBottom)
        {
            return;
        }

        var target = Math.Clamp(viewer.VerticalOffset + position - viewer.ViewportHeight * 0.42, 0, viewer.ScrollableHeight);
        var start = viewer.VerticalOffset;
        for (var frame = 1; frame <= 18 && !cancellation.IsCancellationRequested; frame++)
        {
            var progress = frame / 18d;
            var eased = 1 - Math.Pow(1 - progress, 3);
            viewer.ScrollToVerticalOffset(start + (target - start) * eased);
            await Task.Delay(18, cancellation.Token).ContinueWith(_ => { });
        }
    }

    private void HistoryRecordFound(object? sender, Models.TestSessionRecord record)
    {
        var dialog = new TestRecordDialog(record) { Owner = this };
        dialog.ShowDialog();
    }

    private void ScanValidationFailed(object? sender, string message)
    {
        _viewModel.AppendExternalLog("Scan validation dialog opened.");
        MessageBox.Show(this, message, "扫码校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        _viewModel.ClearScannerInput();
        Dispatcher.BeginInvoke(() =>
        {
            ScannerInputTextBox.Focus();
            Keyboard.Focus(ScannerInputTextBox);
            _viewModel.AppendExternalLog("Scan validation dialog confirmed; scanner input cleared and focus restored.");
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SubmitScanIfPossible()
    {
        _viewModel.AppendExternalLog($"Scanner UI submit requested: canExecute={_viewModel.ScanCommand.CanExecute(null)}, textLength={ScannerInputTextBox.Text.Length}");
        if (_viewModel.ScanCommand.CanExecute(null))
        {
            _viewModel.ScanCommand.Execute(null);
        }

        ScannerInputTextBox.Focus();
        Keyboard.Focus(ScannerInputTextBox);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
