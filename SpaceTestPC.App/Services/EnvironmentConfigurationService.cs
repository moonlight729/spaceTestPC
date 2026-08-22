using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceTestPC.App.Services;

public sealed record EnvironmentSettingsData(
    string Mode,
    string Port,
    string TargetName,
    string WifiSsid,
    string EthernetPingIp,
    int EthernetLedObservationMs,
    string FirmwarePath,
    string ConnectionHost,
    int ConnectionPort,
    string AdapterId,
    string AdapterName,
    string LocalIp,
    int TestCount,
    int FinishedProductWifiMinRssi,
    int PcbaWifiMinRssi,
    int FinishedProductBluetoothMinRssi,
    int PcbaBluetoothMinRssi,
    BatteryDischargeSettings FinishedProductBattery,
    BatteryDischargeSettings PcbaBattery,
    FastChargeSettings FinishedProductFastCharge,
    FastChargeSettings PcbaFastCharge);

public sealed record FastChargeSettings(
    int VoltageMinMv,
    int VoltageMaxMv,
    int CurrentMinMa,
    int CurrentMaxMa);

public sealed record BatteryDischargeSettings(
    string ChargerStatusPath,
    string CurrentPath,
    string VoltagePath,
    string RequiredStatus,
    int VoltageMinMv,
    int VoltageMaxMv,
    int CurrentMinMa,
    int CurrentMaxMa,
    int SamplingDurationMs,
    int SampleIntervalMs,
    int MinimumValidSamples,
    int CurrentStabilityToleranceMa,
    int OperatorConfirmationTimeoutMs);

public sealed class EnvironmentConfigurationService
{
    public static string ResolvePath() => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public EnvironmentSettingsData Load()
    {
        var root = ReadRoot(ResolvePath());
        var broadcaster = root["bluetoothBroadcaster"]?.AsObject();
        var upgrade = root["upgrade"]?.AsObject();
        var connection = root["pcbaConnection"]?.AsObject();
        var testPlan = root["testPlan"]?.AsObject();
        var parameters = testPlan?["testParameters"]?.AsObject();
        return new EnvironmentSettingsData(
            root["testMode"]?.GetValue<string>() ?? "finished_product",
            broadcaster?["portName"]?.GetValue<string>() ?? string.Empty,
            broadcaster?["broadcastName"]?.GetValue<string>() ?? string.Empty,
            parameters?["wifi"]?["ssid"]?.GetValue<string>() ?? string.Empty,
            parameters?["ethernet"]?["routerIp"]?.GetValue<string>() ?? "192.168.31.1",
            parameters?["ethernet_led"]?["phaseDurationMs"]?.GetValue<int>() ?? 2000,
            upgrade?["localBinaryPath"]?.GetValue<string>() ?? string.Empty,
            connection?["host"]?.GetValue<string>() ?? "auto",
            connection?["port"]?.GetValue<int>() ?? 19001,
            connection?["adapterId"]?.GetValue<string>() ?? string.Empty,
            connection?["adapterName"]?.GetValue<string>() ?? string.Empty,
            connection?["localIp"]?.GetValue<string>() ?? string.Empty,
            testPlan?["enabledTests"]?.AsArray().Count ?? 0,
            ReadRssi(root, "finished_product", "wifi", -40),
            ReadRssi(root, "pcba", "wifi", -40),
            ReadRssi(root, "finished_product", "bluetooth", -60),
            ReadRssi(root, "pcba", "bluetooth", -60),
            ReadBattery(root, "finished_product", 7600, 80),
            ReadBattery(root, "pcba", 7000, 100),
            ReadFastCharge(root, "finished_product"),
            ReadFastCharge(root, "pcba"));
    }

    public void Save(
        string mode,
        string port,
        string targetName,
        string wifiSsid,
        string ethernetPingIp,
        int ethernetLedObservationMs,
        string firmwarePath,
        EthernetAdapterInfo adapter,
        string connectionHost,
        int connectionPort,
        int finishedProductWifiMinRssi,
        int pcbaWifiMinRssi,
        int finishedProductBluetoothMinRssi,
        int pcbaBluetoothMinRssi,
        BatteryDischargeSettings finishedProductBattery,
        BatteryDischargeSettings pcbaBattery,
        FastChargeSettings finishedProductFastCharge,
        FastChargeSettings pcbaFastCharge)
    {
        var path = ResolvePath();
        var root = ReadRoot(path);
        root["testMode"] = mode.Trim();

        var broadcaster = root["bluetoothBroadcaster"] as JsonObject ?? new JsonObject();
        broadcaster["portName"] = port.Trim();
        broadcaster["broadcastName"] = targetName.Trim();
        root["bluetoothBroadcaster"] = broadcaster;

        var upgrade = root["upgrade"] as JsonObject ?? new JsonObject();
        upgrade["localBinaryPath"] = firmwarePath.Trim();
        root["upgrade"] = upgrade;

        var connection = root["pcbaConnection"] as JsonObject ?? new JsonObject();
        connection["mode"] = "tcp";
        connection["host"] = string.IsNullOrWhiteSpace(connectionHost) ? "auto" : connectionHost.Trim();
        connection["port"] = connectionPort;
        connection["ethernetOnly"] = true;
        connection["adapterId"] = adapter.Id;
        connection["adapterName"] = adapter.Name;
        connection["localIp"] = adapter.Address.ToString();
        var discovery = connection["discovery"] as JsonObject ?? new JsonObject();
        discovery["enabled"] = true;
        discovery["mode"] = "selectedAdapter";
        discovery["subnet"] = adapter.Cidr;
        discovery["startIp"] = string.Empty;
        discovery["endIp"] = string.Empty;
        connection["discovery"] = discovery;
        root["pcbaConnection"] = connection;

        var testPlan = root["testPlan"] as JsonObject ?? new JsonObject();
        var parameters = testPlan["testParameters"] as JsonObject ?? new JsonObject();
        var wifi = parameters["wifi"] as JsonObject ?? new JsonObject();
        wifi["ssid"] = wifiSsid.Trim();
        parameters["wifi"] = wifi;
        var ethernet = parameters["ethernet"] as JsonObject ?? new JsonObject();
        ethernet["routerIp"] = ethernetPingIp.Trim();
        parameters["ethernet"] = ethernet;
        var ethernetLed = parameters["ethernet_led"] as JsonObject ?? new JsonObject();
        ethernetLed["cycleCount"] = 1;
        ethernetLed["phaseDurationMs"] = ethernetLedObservationMs;
        parameters["ethernet_led"] = ethernetLed;
        testPlan["testParameters"] = parameters;
        root["testPlan"] = testPlan;

        WriteRssi(root, "finished_product", "wifi", finishedProductWifiMinRssi);
        WriteRssi(root, "pcba", "wifi", pcbaWifiMinRssi);
        WriteRssi(root, "finished_product", "bluetooth", finishedProductBluetoothMinRssi);
        WriteRssi(root, "pcba", "bluetooth", pcbaBluetoothMinRssi);

        WriteBattery(root, "finished_product", finishedProductBattery);
        WriteBattery(root, "pcba", pcbaBattery);
        WriteFastCharge(root, "finished_product", finishedProductFastCharge);
        WriteFastCharge(root, "pcba", pcbaFastCharge);

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _ = JsonNode.Parse(File.ReadAllText(temporaryPath)) ?? throw new InvalidDataException("保存后的配置不是有效 JSON。");
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temporaryPath, path, true);
    }

    private static BatteryDischargeSettings ReadBattery(JsonObject root, string mode, int defaultVoltageMinMv, int defaultToleranceMa)
    {
        var battery = root["testModes"]?[mode]?["testParameters"]?["battery_management"] as JsonObject;
        return new BatteryDischargeSettings(
            battery?["chargerStatusPath"]?.GetValue<string>() ?? "/sys/class/power_supply/bq2579x-charger/status",
            battery?["currentPath"]?.GetValue<string>() ?? "/sys/class/power_supply/cw221X-bat/current_now",
            battery?["voltagePath"]?.GetValue<string>() ?? "/sys/class/power_supply/cw221X-bat/voltage_now",
            battery?["requiredStatus"]?.GetValue<string>() ?? "Discharging",
            battery?["voltageMinMv"]?.GetValue<int>() ?? defaultVoltageMinMv,
            battery?["voltageMaxMv"]?.GetValue<int>() ?? 8400,
            battery?["dischargeCurrentMinMa"]?.GetValue<int>() ?? 100,
            battery?["dischargeCurrentMaxMa"]?.GetValue<int>() ?? 500,
            battery?["samplingDurationMs"]?.GetValue<int>() ?? 4000,
            battery?["sampleIntervalMs"]?.GetValue<int>() ?? 500,
            battery?["minimumValidSamples"]?.GetValue<int>() ?? 6,
            battery?["currentStabilityToleranceMa"]?.GetValue<int>() ?? defaultToleranceMa,
            battery?["operatorConfirmationTimeoutMs"]?.GetValue<int>() ?? 120000);
    }

    private static int ReadRssi(JsonObject root, string mode, string testId, int fallback) =>
        root["testModes"]?[mode]?["testParameters"]?[testId]?["minRssi"]?.GetValue<int>() ??
        root["testPlan"]?["testParameters"]?[testId]?["minRssi"]?.GetValue<int>() ?? fallback;

    private static void WriteRssi(JsonObject root, string mode, string testId, int value)
    {
        var modes = root["testModes"] as JsonObject ?? new JsonObject();
        var modeNode = modes[mode] as JsonObject ?? new JsonObject();
        var parameters = modeNode["testParameters"] as JsonObject ?? new JsonObject();
        var test = parameters[testId] as JsonObject ?? new JsonObject();
        test["minRssi"] = value;
        parameters[testId] = test;
        modeNode["testParameters"] = parameters;
        modes[mode] = modeNode;
        root["testModes"] = modes;
    }

    private static void WriteBattery(JsonObject root, string mode, BatteryDischargeSettings settings)
    {
        var modes = root["testModes"] as JsonObject ?? new JsonObject();
        var modeNode = modes[mode] as JsonObject ?? new JsonObject();
        var testParameters = modeNode["testParameters"] as JsonObject ?? new JsonObject();
        var battery = testParameters["battery_management"] as JsonObject ?? new JsonObject();
        battery["strategy"] = "board_sysfs";
        battery["chargerStatusPath"] = settings.ChargerStatusPath.Trim();
        battery["currentPath"] = settings.CurrentPath.Trim();
        battery["voltagePath"] = settings.VoltagePath.Trim();
        battery["requiredStatus"] = settings.RequiredStatus.Trim();
        battery["voltageMinMv"] = settings.VoltageMinMv;
        battery["voltageMaxMv"] = settings.VoltageMaxMv;
        battery["dischargeCurrentMinMa"] = settings.CurrentMinMa;
        battery["dischargeCurrentMaxMa"] = settings.CurrentMaxMa;
        battery["samplingDurationMs"] = settings.SamplingDurationMs;
        battery["sampleIntervalMs"] = settings.SampleIntervalMs;
        battery["minimumValidSamples"] = settings.MinimumValidSamples;
        battery["currentStabilityToleranceMa"] = settings.CurrentStabilityToleranceMa;
        battery["operatorConfirmationTimeoutMs"] = settings.OperatorConfirmationTimeoutMs;
        testParameters["battery_management"] = battery;
        modeNode["testParameters"] = testParameters;
        modes[mode] = modeNode;
        root["testModes"] = modes;
    }

    private static FastChargeSettings ReadFastCharge(JsonObject root, string mode)
    {
        var modeSettings = root["testModes"]?[mode]?["testParameters"]?["typec_fast_charge"] as JsonObject;
        var globalSettings = root["testPlan"]?["testParameters"]?["typec_fast_charge"] as JsonObject;
        return new FastChargeSettings(
            ReadModeOrGlobalInt(modeSettings, globalSettings, "chargeVoltageMinMv", 7400),
            ReadModeOrGlobalInt(modeSettings, globalSettings, "chargeVoltageMaxMv", 8400),
            ReadModeOrGlobalInt(modeSettings, globalSettings, "chargeCurrentMinMa", 1800),
            ReadModeOrGlobalInt(modeSettings, globalSettings, "chargeCurrentMaxMa", 2300));
    }

    private static int ReadModeOrGlobalInt(JsonObject? modeSettings, JsonObject? globalSettings, string name, int fallback) =>
        modeSettings?[name]?.GetValue<int>() ?? globalSettings?[name]?.GetValue<int>() ?? fallback;

    private static void WriteFastCharge(JsonObject root, string mode, FastChargeSettings settings)
    {
        var modes = root["testModes"] as JsonObject ?? new JsonObject();
        var modeNode = modes[mode] as JsonObject ?? new JsonObject();
        var testParameters = modeNode["testParameters"] as JsonObject ?? new JsonObject();
        var fastCharge = testParameters["typec_fast_charge"] as JsonObject ?? new JsonObject();
        fastCharge["chargeVoltageMinMv"] = settings.VoltageMinMv;
        fastCharge["chargeVoltageMaxMv"] = settings.VoltageMaxMv;
        fastCharge["chargeCurrentMinMa"] = settings.CurrentMinMa;
        fastCharge["chargeCurrentMaxMa"] = settings.CurrentMaxMa;
        testParameters["typec_fast_charge"] = fastCharge;
        modeNode["testParameters"] = testParameters;
        modes[mode] = modeNode;
        root["testModes"] = modes;
    }

    private static JsonObject ReadRoot(string path) =>
        !File.Exists(path)
            ? new JsonObject()
            : JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                })?.AsObject() ?? new JsonObject();
}
