using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;

namespace SpaceTestPC.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _sequenceScrollCancellation;

    public MainWindow()
    {
        InitializeComponent();

        var configuration = new ConfigurationService().Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var manualTestInteractionService = new ManualTestInteractionService();
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var mode = string.IsNullOrWhiteSpace(configuration.TestMode) ? "pcba" : configuration.TestMode.Trim().ToLowerInvariant();
        var modeConfiguration = configuration.TestModes.TryGetValue(mode, out var configuredMode)
            ? configuredMode
            : new Models.TestModeConfiguration();
        var databaseName = string.IsNullOrWhiteSpace(modeConfiguration.DatabaseName)
            ? mode == "finished_product" ? "space-test-finished-product.db" : "space-test-pcba.db"
            : modeConfiguration.DatabaseName;
        var repository = new SqliteDatabaseRepository(Path.Combine(dataDir, databaseName));
        var pcbaClientFactory = new PcbaCommandClientFactory(
            new MockPcbaCommandClient(mockConfiguration: configuration.TestPlan.Mock, manualTestInteractionService: manualTestInteractionService),
            new AdbPcbaCommandClient());

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

        DataContext = _viewModel;
        _viewModel.SequenceAdvanceRequested += SequenceAdvanceRequested;
        _viewModel.HistoryRecordFound += HistoryRecordFound;
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

        SubmitScanIfPossible();
        e.Handled = true;
    }

    private void ScannerInputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (!textBox.Text.Contains('\r') && !textBox.Text.Contains('\n'))
        {
            return;
        }

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

    private void SubmitScanIfPossible()
    {
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
