using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceTestPC.App.Services;

public sealed class EnvironmentConfigurationService
{
    public static string ResolvePath() => Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    public (string Mode, string Port, string TargetName, string WifiSsid, string FirmwarePath, string ConnectionMode, string ConnectionHost, int ConnectionPort, int TestCount) Load()
    {
        var root = ReadRoot(ResolvePath()); var broadcaster = root["bluetoothBroadcaster"]?.AsObject(); var upgrade = root["upgrade"]?.AsObject(); var connection = root["pcbaConnection"]?.AsObject(); var testPlan = root["testPlan"]?.AsObject(); var parameters = testPlan?["testParameters"]?.AsObject();
        return (root["testMode"]?.GetValue<string>() ?? "finished_product", broadcaster?["portName"]?.GetValue<string>() ?? string.Empty, broadcaster?["broadcastName"]?.GetValue<string>() ?? string.Empty, parameters?["wifi"]?["ssid"]?.GetValue<string>() ?? string.Empty, upgrade?["localBinaryPath"]?.GetValue<string>() ?? string.Empty, connection?["mode"]?.GetValue<string>() ?? "adbForward", connection?["host"]?.GetValue<string>() ?? "auto", connection?["port"]?.GetValue<int>() ?? 19001, testPlan?["enabledTests"]?.AsArray().Count ?? 0);
    }
    public void Save(string mode, string port, string targetName, string wifiSsid, string firmwarePath)
    {
        var path = ResolvePath(); var root = ReadRoot(path); root["testMode"] = mode.Trim(); var broadcaster = root["bluetoothBroadcaster"] as JsonObject ?? new JsonObject(); broadcaster["portName"] = port.Trim(); broadcaster["broadcastName"] = targetName.Trim(); root["bluetoothBroadcaster"] = broadcaster; var upgrade = root["upgrade"] as JsonObject ?? new JsonObject(); upgrade["localBinaryPath"] = firmwarePath.Trim(); root["upgrade"] = upgrade; var testPlan = root["testPlan"] as JsonObject ?? new JsonObject(); var parameters = testPlan["testParameters"] as JsonObject ?? new JsonObject(); var wifi = parameters["wifi"] as JsonObject ?? new JsonObject(); wifi["ssid"] = wifiSsid.Trim(); parameters["wifi"] = wifi; testPlan["testParameters"] = parameters; root["testPlan"] = testPlan;
        var temporaryPath = path + ".tmp"; File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); _ = JsonNode.Parse(File.ReadAllText(temporaryPath)) ?? throw new InvalidDataException("保存后的配置不是有效 JSON。"); if (File.Exists(path)) File.Copy(path, path + ".bak", true); File.Move(temporaryPath, path, true);
    }
    private static JsonObject ReadRoot(string path) => !File.Exists(path) ? new JsonObject() : JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject() ?? new JsonObject();
}
