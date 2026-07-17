using System.Text.Json;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.ViewModels;

public sealed class TestResultViewModel : ObservableObject
{
    private TestItemState _state = TestItemState.Pending;
    private int _resultCode;
    private string _message = "Waiting";
    private string _dataText = "No result data";
    private DateTimeOffset? _startedAt;
    private TimeSpan? _duration;

    public TestResultViewModel(string testId, string displayName)
    {
        TestId = testId;
        DisplayName = displayName;
    }

    public string TestId { get; }
    public string DisplayName { get; }

    public TestItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StateLabel));
                RaisePropertyChanged(nameof(StateBrush));
            }
        }
    }

    public int ResultCode
    {
        get => _resultCode;
        private set => SetProperty(ref _resultCode, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string DataText
    {
        get => _dataText;
        private set => SetProperty(ref _dataText, value);
    }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public TimeSpan? Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public IReadOnlyDictionary<string, object?> Data { get; private set; } = new Dictionary<string, object?>();

    public string StateLabel => State switch
    {
        TestItemState.Running => "TESTING",
        TestItemState.Passed => "PASS",
        TestItemState.Failed => "FAIL",
        TestItemState.Skipped => "SKIPPED",
        _ => "PENDING"
    };

    public string StateBrush => State switch
    {
        TestItemState.Running => "#2563EB",
        TestItemState.Passed => "#16A34A",
        TestItemState.Failed => "#DC2626",
        TestItemState.Skipped => "#64748B",
        _ => "#94A3B8"
    };

    public void Apply(TestSessionEvent testEvent)
    {
        if (testEvent.Status == "running")
        {
            StartedAt = testEvent.Timestamp;
        }

        State = testEvent.Status switch
        {
            "running" => TestItemState.Running,
            "passed" => TestItemState.Passed,
            "skipped" => TestItemState.Skipped,
            _ => TestItemState.Failed
        };

        ResultCode = testEvent.ResultCode;
        Message = testEvent.Message;
        Data = testEvent.Data;
        DataText = FormatDataText(testEvent);

        if (StartedAt is { } startedAt && State is TestItemState.Passed or TestItemState.Failed or TestItemState.Skipped)
        {
            Duration = testEvent.Timestamp - startedAt;
        }
    }

    public void Reset()
    {
        State = TestItemState.Pending;
        ResultCode = 0;
        Message = "Waiting";
        DataText = "No result data";
        StartedAt = null;
        Duration = null;
        Data = new Dictionary<string, object?>();
    }

    private string FormatDataText(TestSessionEvent testEvent)
    {
        if (testEvent.Data.Count == 0)
        {
            return "No result data";
        }

        if (TestId == "typec_fast_charge")
        {
            return FormatFastChargeData(testEvent.Data);
        }

        if (TestId == "battery_management")
        {
            return FormatBatteryDischargeData(testEvent.Data);
        }

        return string.Join(Environment.NewLine, testEvent.Data.Select(pair => $"{pair.Key}: {FormatValue(pair.Value)}"));
    }

    private static string FormatFastChargeData(IReadOnlyDictionary<string, object?> data)
    {
        var lines = new List<string>
        {
            $"PMIC通信: {FormatBoolean(data, "pmicCommunicationOk")}",
            $"充电器接入: {FormatBoolean(data, "chargerConnected")}",
            $"正在充电: {FormatBoolean(data, "charging")}",
            $"充电阶段: {GetString(data, "chargeStage", "-")}",
            $"充电电压: {FormatNumber(data, "chargeVoltageMv", "mV")}",
            $"充电电流: {FormatNumber(data, "chargeCurrentMa", "mA")}",
            $"平均充电电流: {FormatNumber(data, "averageChargeCurrentMa", "mA")}",
            $"稳定判定: {FormatBoolean(data, "stable")}",
            $"稳定样本数: {GetString(data, "stableSamples", "-")}"
        };

        AppendIfPresent(lines, data, "voltageMinMv", "电压下限", "mV");
        AppendIfPresent(lines, data, "voltageMaxMv", "电压上限", "mV");
        AppendIfPresent(lines, data, "currentMinMa", "电流下限", "mA");
        AppendIfPresent(lines, data, "currentMaxMa", "电流上限", "mA");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBatteryDischargeData(IReadOnlyDictionary<string, object?> data)
    {
        var lines = new List<string>
        {
            $"禁充命令: {GetString(data, "chargeControlCommand", "-")}",
            $"禁充成功: {FormatBoolean(data, "chargeControlOk")}",
            $"PMIC通信: {FormatBoolean(data, "pmicCommunicationOk")}",
            $"测试时长: {FormatNumber(data, "samplingDurationMs", "ms")}",
            $"已测时长: {FormatNumber(data, "elapsedMs", "ms")}",
            $"实测电压: {FormatNumber(data, "dischargeVoltageMv", "mV")}",
            $"稳定电流: {FormatNumber(data, "dischargeCurrentMa", "mA")}",
            $"最小电流: {FormatNumber(data, "measuredCurrentMinMa", "mA")}",
            $"最大电流: {FormatNumber(data, "measuredCurrentMaxMa", "mA")}",
            $"电流波动: {FormatNumber(data, "currentRippleMa", "mA")}",
            $"采样点数: {GetString(data, "sampleCount", "-")}",
            $"有效点数: {GetString(data, "validSampleCount", "-")}",
            $"异常点数: {GetString(data, "outlierSampleCount", "-")}",
            $"电流中位数: {FormatNumber(data, "rawCurrentMedianMa", "mA")}",
            $"失败原因: {GetString(data, "failureReason", "-")}"
        };

        AppendIfPresent(lines, data, "dischargeVoltageMinMv", "电压下限", "mV");
        AppendIfPresent(lines, data, "dischargeVoltageMaxMv", "电压上限", "mV");
        AppendIfPresent(lines, data, "dischargeCurrentMinMa", "电流下限", "mA");
        AppendIfPresent(lines, data, "dischargeCurrentMaxMa", "电流上限", "mA");
        AppendIfPresent(lines, data, "stabilityToleranceMa", "稳定容差", "mA");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
        }

        if (value is string text)
        {
            return text;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            return string.Join(", ", enumerable.Cast<object?>().Select(FormatValue));
        }

        return value.ToString() ?? string.Empty;
    }

    private static string FormatBoolean(IReadOnlyDictionary<string, object?> data, string key)
    {
        var value = GetString(data, key, string.Empty);
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? "是"
            : value.Equals("false", StringComparison.OrdinalIgnoreCase)
                ? "否"
                : "-";
    }

    private static string FormatNumber(IReadOnlyDictionary<string, object?> data, string key, string unit)
    {
        var value = GetString(data, key, string.Empty);
        return string.IsNullOrWhiteSpace(value) ? "-" : $"{value} {unit}";
    }

    private static void AppendIfPresent(List<string> lines, IReadOnlyDictionary<string, object?> data, string key, string label, string unit)
    {
        var value = GetString(data, key, string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{label}: {value} {unit}");
        }
    }

    private static string GetString(IReadOnlyDictionary<string, object?> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? fallback,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText()
            };
        }

        return value.ToString() ?? fallback;
    }
}
