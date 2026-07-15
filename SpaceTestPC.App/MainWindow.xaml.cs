using System.IO;
using System.Windows;
using System.Windows.Input;
using SpaceTestPC.App.Services;
using SpaceTestPC.App.ViewModels;

namespace SpaceTestPC.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var configuration = new ConfigurationService().Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var manualTestInteractionService = new ManualTestInteractionService();
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var repository = new SqliteDatabaseRepository(Path.Combine(dataDir, "box-test-records.db"));
        var pcbaClientFactory = new PcbaCommandClientFactory(
            new MockPcbaCommandClient(mockConfiguration: configuration.TestPlan.Mock, manualTestInteractionService: manualTestInteractionService),
            new AdbPcbaCommandClient());

        _viewModel = new MainViewModel(
            new ScannerService(),
            pcbaClientFactory,
            new StatusMonitorService("Voltage"),
            new StatusMonitorService("Battery"),
            repository,
            new LogService(),
            configuration,
            manualTestInteractionService,
            new Jk5506Service(configuration.Jk5506),
            new JxTvmService(configuration.JxTvm),
            new BluetoothBroadcasterService(configuration.BluetoothBroadcaster));

        DataContext = _viewModel;
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

        if (_viewModel.ScanCommand.CanExecute(null))
        {
            _viewModel.ScanCommand.Execute(null);
            e.Handled = true;
        }
    }
}
