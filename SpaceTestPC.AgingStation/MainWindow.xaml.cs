using System.Windows;
using SpaceTestPC.AgingStation.ViewModels;

namespace SpaceTestPC.AgingStation;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new MainViewModel
        {
            // VM 不直接弹窗，确认交互由窗口提供：停止全部与清理都是不可逆操作。
            Confirm = message => MessageBox.Show(
                this, message, "请确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes
        };

        DataContext = viewModel;
    }
}
