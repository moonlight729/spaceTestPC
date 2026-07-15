using System.Windows;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App;

public partial class TestRecordDialog : Window
{
    public TestSession Session { get; }
    public IReadOnlyList<QueryTestItem> Items { get; }

    public TestRecordDialog(TestSessionRecord record)
    {
        InitializeComponent();
        Session = record.Session;
        Items = record.TestResults
            .Select(result => new QueryTestItem(GetChineseTestName(result.TestId), result.Status, result.ResultCode))
            .ToArray();
        DataContext = this;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private static string GetChineseTestName(string testId) => testId switch
    {
        "board_state" => "板状态",
        "hdmi" => "HDMI 输出",
        "keys" => "方向按键",
        "lcd" => "LCD 显示屏",
        "wifi" => "Wi-Fi",
        "bluetooth" => "蓝牙",
        "fingerprint" => "指纹模块",
        "typec_fast_charge" => "Type-C 快充",
        "typec_camera" => "Type-C 摄像头",
        "tf" => "TF 卡",
        "indicator_led" => "指示灯",
        "fan" => "风扇",
        "otg" => "USB OTG",
        "battery_management" => "电池管理",
        _ => testId
    };

    public sealed record QueryTestItem(string TestName, string Status, int ResultCode);
}
