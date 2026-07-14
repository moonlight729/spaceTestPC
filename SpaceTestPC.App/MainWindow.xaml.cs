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

        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var repository = new FileDatabaseRepository(Path.Combine(dataDir, "stage1-db.json"));
        var pcbaClientFactory = new PcbaCommandClientFactory(
            new MockPcbaCommandClient(),
            new AdbPcbaCommandClient());

        _viewModel = new MainViewModel(
            new ScannerService(),
            pcbaClientFactory,
            new StatusMonitorService("Voltage"),
            new StatusMonitorService("Battery"),
            repository,
            new LogService());

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
