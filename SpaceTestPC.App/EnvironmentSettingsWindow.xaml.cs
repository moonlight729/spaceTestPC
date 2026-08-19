using System.IO.Ports;
using System.Windows;
using Microsoft.Win32;

namespace SpaceTestPC.App;

public partial class EnvironmentSettingsWindow : Window
{
    private readonly Services.EnvironmentConfigurationService _service = new();
    public EnvironmentSettingsWindow()
    {
        InitializeComponent();
        PortComboBox.ItemsSource = SerialPort.GetPortNames().OrderBy(x => x).ToArray();
        var current = _service.Load();
        PortComboBox.SelectedItem = current.Port;
        TargetNameTextBox.Text = current.TargetName;
        WifiSsidTextBox.Text = current.WifiSsid;
        FirmwarePathTextBox.Text = current.FirmwarePath;
    }
    private void BrowseFirmwarePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择默认固件目录" };
        if (dialog.ShowDialog() == true) FirmwarePathTextBox.Text = dialog.FolderName;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (PortComboBox.SelectedItem is null || string.IsNullOrWhiteSpace(TargetNameTextBox.Text) || string.IsNullOrWhiteSpace(WifiSsidTextBox.Text))
        { MessageBox.Show(this, "蓝牙串口、蓝牙目标名称和 Wi-Fi SSID 不能为空。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _service.Save(PortComboBox.SelectedItem.ToString()!, TargetNameTextBox.Text, WifiSsidTextBox.Text, FirmwarePathTextBox.Text);
        MessageBox.Show(this, "配置已保存，下一轮测试生效。\n原文件已备份为 appsettings.json.bak。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }
}
