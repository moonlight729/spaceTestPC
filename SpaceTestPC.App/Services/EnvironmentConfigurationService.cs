using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceTestPC.App.Services;

public sealed record EnvironmentSettingsData(
    string Mode,
    string Port,
    string TargetName,
    string WifiSsid,
    string FirmwarePath,
    string ConnectionHost,
    int ConnectionPort,
    string AdapterId,
    string AdapterName,
    string LocalIp,
    int TestCount);

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
            upgrade?["localBinaryPath"]?.GetValue<string>() ?? string.Empty,
            connection?["host"]?.GetValue<string>() ?? "auto",
            connection?["port"]?.GetValue<int>() ?? 19001,
            connection?["adapterId"]?.GetValue<string>() ?? string.Empty,
            connection?["adapterName"]?.GetValue<string>() ?? string.Empty,
            connection?["localIp"]?.GetValue<string>() ?? string.Empty,
            testPlan?["enabledTests"]?.AsArray().Count ?? 0);
    }

    public void Save(
        string mode,
        string port,
        string targetName,
        string wifiSsid,
        string firmwarePath,
        EthernetAdapterInfo adapter,
        string connectionHost,
        int connectionPort)
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
        testPlan["testParameters"] = parameters;
        root["testPlan"] = testPlan;

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _ = JsonNode.Parse(File.ReadAllText(temporaryPath)) ?? throw new InvalidDataException("保存后的配置不是有效 JSON。");
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temporaryPath, path, true);
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
