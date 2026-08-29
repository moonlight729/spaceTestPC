namespace SpaceTestPC.App.ViewModels;

public sealed class PcbaTestPointViewModel : ObservableObject
{
    private string _status = "pending";
    private double? _voltage;
    public string Id { get; init; } = "TP01";
    public string Name { get; init; } = "";
    public int Channel { get; init; }
    public double MinMv { get; init; }
    public double MaxMv { get; init; }
    public string Unit { get; init; } = "mV";
    public double? VoltageMv { get => _voltage; private set { if (SetProperty(ref _voltage, value)) RaisePropertyChanged(nameof(ValueDisplay)); } }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) { RaisePropertyChanged(nameof(Background)); RaisePropertyChanged(nameof(Foreground)); RaisePropertyChanged(nameof(StatusDisplay)); } } }
    public string ValueDisplay => VoltageMv is null ? "--" : $"{VoltageMv:0.###} {Unit}";
    public string RangeDisplay => $"{MinMv:0.###} ~ {MaxMv:0.###} mV";
    public string StatusDisplay => Status switch { "passed" => "PASS", "failed" => "FAIL", "running" => "TEST", "error" => "ERROR", _ => "WAIT" };
    public string Background => Status switch { "passed" => "#16A34A", "failed" => "#667085", "running" => "#2563EB", "error" => "#344054", _ => "#E5E7EB" };
    public string Foreground => Status is "pending" ? "#344054" : "White";
    public void Reset() { VoltageMv = null; Status = "pending"; }
    public void Apply(double? voltageMv, string status) { VoltageMv = voltageMv; Status = status; }
}
