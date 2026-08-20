using System.Net;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using SpaceTestPC.App.Services;

namespace SpaceTestPC.App;

public partial class EnvironmentSettingsWindow : Window
{
    private readonly EnvironmentConfigurationService _service = new();
    private readonly EnvironmentSettingsData _current;

    public EnvironmentSettingsWindow()
    {
        InitializeComponent();
        try
        {
            PortComboBox.ItemsSource = SerialPort.GetPortNames().OrderBy(port => port).ToArray();
        }
        catch
        {
            PortComboBox.ItemsSource = Array.Empty<string>();
        }

        _current = _service.Load();
        ModeComboBox.SelectedItem = ModeComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, _current.Mode));
        if (ModeComboBox.SelectedIndex < 0) ModeComboBox.SelectedIndex = 0;

        PortComboBox.Text = _current.Port;
        TargetNameTextBox.Text = _current.TargetName;
        WifiSsidTextBox.Text = _current.WifiSsid;
        FirmwarePathTextBox.Text = _current.FirmwarePath;
        ConnectionHostTextBox.Text = string.IsNullOrWhiteSpace(_current.ConnectionHost) ? "auto" : _current.ConnectionHost;
        ConnectionPortTextBox.Text = _current.ConnectionPort.ToString();
        LoadBatterySettings("Finished", _current.FinishedProductBattery);
        LoadBatterySettings("Pcba", _current.PcbaBattery);

        LoadAdapters(_current.AdapterId, _current.LocalIp);
        UpdateSummary();
    }

    private void LoadAdapters(string? preferredId = null, string? preferredIp = null)
    {
        var adapters = EthernetAdapterService.GetAvailableAdapters();
        AdapterComboBox.ItemsSource = adapters;
        AdapterComboBox.SelectedItem = adapters.FirstOrDefault(adapter =>
                !string.IsNullOrWhiteSpace(preferredId) && string.Equals(adapter.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter =>
                !string.IsNullOrWhiteSpace(preferredIp) && string.Equals(adapter.Address.ToString(), preferredIp, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault();
    }

    private void AdapterComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSummary();

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e)
    {
        var selected = AdapterComboBox.SelectedItem as EthernetAdapterInfo;
        LoadAdapters(selected?.Id, selected?.Address.ToString());
        UpdateSummary();
    }

    private async void ProbeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterComboBox.SelectedItem is not EthernetAdapterInfo adapter)
        {
            MessageBox.Show(this, "请先选择设备通信网卡。", "设备探测", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ConnectionPortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "设备端口必须是 1 至 65535 之间的整数。", "设备探测", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var host = string.IsNullOrWhiteSpace(ConnectionHostTextBox.Text) ? "auto" : ConnectionHostTextBox.Text.Trim();
        ProbeDeviceButton.IsEnabled = false;
        ProbeDeviceButton.Content = "探测中";
        try
        {
            var configuration = new Models.PcbaConnectionConfiguration
            {
                Mode = "tcp",
                Host = host,
                Port = port,
                EthernetOnly = true,
                AdapterId = adapter.Id,
                AdapterName = adapter.Name,
                LocalIp = adapter.Address.ToString(),
                Discovery = new Models.PcbaDiscoveryConfiguration
                {
                    Enabled = true,
                    Mode = "selectedAdapter",
                    Subnet = adapter.Cidr,
                    ConnectTimeoutMs = 500,
                    MaxParallel = 32
                }
            };
            var discovery = new PcbaDiscoveryService(configuration);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resolvedHost = await discovery.ResolveHostAsync(timeout.Token);
            MessageBox.Show(this, $"设备探测成功。\n\n网卡：{adapter.Name}\n本机 IP：{adapter.Address}\n设备 IP：{resolvedHost}\n端口：{port}", "设备探测", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"未能从选定网卡探测到设备。\n\n网卡：{adapter.Name}\n网段：{adapter.Cidr}\n原因：{exception.Message}", "设备探测失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ProbeDeviceButton.Content = "探测设备";
            ProbeDeviceButton.IsEnabled = true;
        }
    }

    private void UpdateSummary()
    {
        var adapterText = AdapterComboBox.SelectedItem is EthernetAdapterInfo adapter
            ? $"{adapter.Name}\n本机 IP：{adapter.Address}\n探测网段：{adapter.Cidr}"
            : "未找到可用物理以太网卡，请检查网线连接。";
        SummaryTextBlock.Text =
            $"通信方式：TCP 网线（禁止 WLAN/ADB）\n设备网卡：{adapterText}\n当前测试项：{_current.TestCount} 项\n配置文件：{EnvironmentConfigurationService.ResolvePath()}";
    }

    private void BrowseFirmwarePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 RK3576 固件程序", CheckFileExists = true };
        if (dialog.ShowDialog() == true) FirmwarePathTextBox.Text = dialog.FileName;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var mode = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "finished_product";
        if (AdapterComboBox.SelectedItem is not EthernetAdapterInfo adapter)
        {
            MessageBox.Show(this, "请选择已连接且具有 IPv4 地址的物理以太网卡。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(PortComboBox.Text) ||
            string.IsNullOrWhiteSpace(TargetNameTextBox.Text) ||
            string.IsNullOrWhiteSpace(WifiSsidTextBox.Text))
        {
            MessageBox.Show(this, "蓝牙串口、蓝牙目标名称和 Wi-Fi SSID 不能为空。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ConnectionPortTextBox.Text, out var connectionPort) || connectionPort is < 1 or > 65535)
        {
            MessageBox.Show(this, "设备端口必须是 1 至 65535 之间的整数。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var host = string.IsNullOrWhiteSpace(ConnectionHostTextBox.Text) ? "auto" : ConnectionHostTextBox.Text.Trim();
        if (!string.Equals(host, "auto", StringComparison.OrdinalIgnoreCase) &&
            (!IPAddress.TryParse(host, out var address) || !adapter.Contains(address)))
        {
            MessageBox.Show(this, $"固定设备 IP 必须位于选定网卡网段 {adapter.Cidr} 内。", "配置校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var finishedBattery = ReadBatterySettings("Finished", "整机");
            var pcbaBattery = ReadBatterySettings("Pcba", "PCBA");
            _service.Save(
                mode,
                PortComboBox.Text,
                TargetNameTextBox.Text,
                WifiSsidTextBox.Text,
                FirmwarePathTextBox.Text,
                adapter,
                host,
                connectionPort,
                finishedBattery,
                pcbaBattery);
            MessageBox.Show(this, "配置已保存。请重启应用，使网卡绑定和探测范围完全生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"保存配置失败：{exception.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadBatterySettings(string prefix, BatteryDischargeSettings settings)
    {
        FindTextBox($"{prefix}ChargerStatusPathTextBox").Text = settings.ChargerStatusPath;
        FindTextBox($"{prefix}CurrentPathTextBox").Text = settings.CurrentPath;
        FindTextBox($"{prefix}VoltagePathTextBox").Text = settings.VoltagePath;
        FindTextBox($"{prefix}RequiredStatusTextBox").Text = settings.RequiredStatus;
        FindTextBox($"{prefix}VoltageMinTextBox").Text = settings.VoltageMinMv.ToString();
        FindTextBox($"{prefix}VoltageMaxTextBox").Text = settings.VoltageMaxMv.ToString();
        FindTextBox($"{prefix}CurrentMinTextBox").Text = settings.CurrentMinMa.ToString();
        FindTextBox($"{prefix}CurrentMaxTextBox").Text = settings.CurrentMaxMa.ToString();
        FindTextBox($"{prefix}SamplingDurationTextBox").Text = settings.SamplingDurationMs.ToString();
        FindTextBox($"{prefix}SampleIntervalTextBox").Text = settings.SampleIntervalMs.ToString();
        FindTextBox($"{prefix}MinimumSamplesTextBox").Text = settings.MinimumValidSamples.ToString();
        FindTextBox($"{prefix}ToleranceTextBox").Text = settings.CurrentStabilityToleranceMa.ToString();
        FindTextBox($"{prefix}ConfirmationTimeoutTextBox").Text = settings.OperatorConfirmationTimeoutMs.ToString();
    }

    private BatteryDischargeSettings ReadBatterySettings(string prefix, string displayName)
    {
        var statusPath = RequiredText(prefix, "ChargerStatusPath", displayName);
        var currentPath = RequiredText(prefix, "CurrentPath", displayName);
        var voltagePath = RequiredText(prefix, "VoltagePath", displayName);
        var requiredStatus = RequiredText(prefix, "RequiredStatus", displayName);
        var voltageMin = PositiveInt(prefix, "VoltageMin", displayName);
        var voltageMax = PositiveInt(prefix, "VoltageMax", displayName);
        var currentMin = PositiveInt(prefix, "CurrentMin", displayName);
        var currentMax = PositiveInt(prefix, "CurrentMax", displayName);
        if (voltageMin >= voltageMax || currentMin >= currentMax)
            throw new InvalidDataException($"{displayName}放电参数的最小值必须小于最大值。");

        return new BatteryDischargeSettings(
            statusPath, currentPath, voltagePath, requiredStatus,
            voltageMin, voltageMax, currentMin, currentMax,
            PositiveInt(prefix, "SamplingDuration", displayName),
            PositiveInt(prefix, "SampleInterval", displayName),
            PositiveInt(prefix, "MinimumSamples", displayName),
            PositiveInt(prefix, "Tolerance", displayName),
            PositiveInt(prefix, "ConfirmationTimeout", displayName));
    }

    private string RequiredText(string prefix, string field, string displayName)
    {
        var value = FindTextBox($"{prefix}{field}TextBox").Text.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{displayName}放电参数不能为空。")
            : value;
    }

    private int PositiveInt(string prefix, string field, string displayName) =>
        int.TryParse(FindTextBox($"{prefix}{field}TextBox").Text, out var value) && value > 0
            ? value
            : throw new InvalidDataException($"{displayName}放电参数必须是正整数。");

    private TextBox FindTextBox(string name) =>
        FindElement<TextBox>(this, name) ?? throw new InvalidOperationException($"未找到设置控件 {name}。");

    private static T? FindElement<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T current && string.Equals(current.Name, name, StringComparison.Ordinal)) return current;
        foreach (var logicalChild in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            var nested = FindElement<T>(logicalChild, name);
            if (nested is not null) return nested;
        }
        if (parent is not Visual && parent is not Visual3D) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            var nested = FindElement<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }
}
