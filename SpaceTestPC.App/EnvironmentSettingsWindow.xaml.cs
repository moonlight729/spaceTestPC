using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpaceTestPC.App;

public partial class EnvironmentSettingsWindow : Window
{
    private readonly Services.EnvironmentConfigurationService _service = new();
    public EnvironmentSettingsWindow()
    {
        InitializeComponent();
        try { PortComboBox.ItemsSource = SerialPort.GetPortNames().OrderBy(port => port).ToArray(); } catch { PortComboBox.ItemsSource = Array.Empty<string>(); }
        var current = _service.Load();
        ModeComboBox.SelectedItem = ModeComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, current.Mode));
        if (ModeComboBox.SelectedIndex < 0) ModeComboBox.SelectedIndex = 0;
        PortComboBox.Text = current.Port; TargetNameTextBox.Text = current.TargetName; WifiSsidTextBox.Text = current.WifiSsid; FirmwarePathTextBox.Text = current.FirmwarePath;
        SummaryTextBlock.Text = $"当前连接：{current.ConnectionMode} / {current.ConnectionHost}:{current.ConnectionPort}\n当前测试项：{current.TestCount} 项\n配置文件：{Services.EnvironmentConfigurationService.ResolvePath()}";
    }
    private void BrowseFirmwarePath_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Title = "选择 RK3576 固件程序", CheckFileExists = true }; if (dialog.ShowDialog() == true) FirmwarePathTextBox.Text = dialog.FileName; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var mode = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "finished_product";
        if (string.IsNullOrWhiteSpace(PortComboBox.Text) || string.IsNullOrWhiteSpace(TargetNameTextBox.Text) || string.IsNullOrWhiteSpace(WifiSsidTextBox.Text)) { MessageBox.Show(this, "蓝牙串口、蓝牙目标名称和 Wi-Fi SSID 不能为空。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { _service.Save(mode, PortComboBox.Text, TargetNameTextBox.Text, WifiSsidTextBox.Text, FirmwarePathTextBox.Text); MessageBox.Show(this, "配置已安全保存，并生成了备份文件。\n请重启应用使工作模式和测试计划完全生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information); DialogResult = true; }
        catch (Exception exception) { MessageBox.Show(this, $"保存配置失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
