namespace SpaceTestPC.App.ViewModels;

public sealed class PressureStationSlotViewModel : ObservableObject
{
    private string _state = "未配置";
    private string _detail = "不参与测试";

    public PressureStationSlotViewModel(int index, string name, bool configured, string serial, int localPort, string sn)
    {
        Index = index;
        Name = string.IsNullOrWhiteSpace(name) ? $"槽位 {index}" : name;
        IsConfigured = configured;
        Serial = serial;
        LocalPort = localPort;
        Sn = sn;
        if (configured)
        {
            State = "待启动";
            Detail = string.IsNullOrWhiteSpace(sn) ? "等待扫码 SN" : $"SN：{sn}";
        }
    }

    public int Index { get; }
    public string Name { get; }
    public bool IsConfigured { get; }
    public string Serial { get; }
    public int LocalPort { get; }
    public string Sn { get; }
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string StateForeground => IsConfigured ? State switch
    {
        "运行中" => "#175CD3",
        "通过" => "#15803D",
        "失败" => "#B42318",
        _ => "#667085"
    } : "#98A2B3";
}
