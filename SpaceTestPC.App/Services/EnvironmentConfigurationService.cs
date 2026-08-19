using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceTestPC.App.Services;

public sealed class EnvironmentConfigurationService
{
    public static string ResolvePath() => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public (string Port, string TargetName, string WifiSsid, string FirmwarePath) Load()
    {
        var root = JsonNode.Parse(File.ReadAllText(ResolvePath()))?.AsObject() ?? new JsonObject();
        var broadcaster = root["bluetoothBroadcaster"]?.AsObject();
        var upgrade = root["upgrade"]?.AsObject();
        var parameters = root["testPlan"]?["testParameters"]?.AsObject();
        return (
            broadcaster?["portName"]?.GetValue<string>() ?? string.Empty,
            broadcaster?["broadcastName"]?.GetValue<string>() ?? string.Empty,
            parameters?["wifi"]?["ssid"]?.GetValue<string>() ?? string.Empty,
            upgrade?["localBinaryPath"]?.GetValue<string>() ?? string.Empty);
    }

    public void Save(string port, string targetName, string wifiSsid, string firmwarePath)
    {
        var path = ResolvePath();
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        var broadcaster = root["bluetoothBroadcaster"] as JsonObject ?? new JsonObject();
        broadcaster["portName"] = port.Trim();
        broadcaster["broadcastName"] = targetName.Trim();
        root["bluetoothBroadcaster"] = broadcaster;
        var upgrade = root["upgrade"] as JsonObject ?? new JsonObject();
        upgrade["localBinaryPath"] = firmwarePath.Trim();
        root["upgrade"] = upgrade;
        var testPlan = root["testPlan"] as JsonObject ?? new JsonObject();
        var parameters = testPlan["testParameters"] as JsonObject ?? new JsonObject();
        var wifi = parameters["wifi"] as JsonObject ?? new JsonObject();
        wifi["ssid"] = wifiSsid.Trim();
        parameters["wifi"] = wifi;
        testPlan["testParameters"] = parameters;
        root["testPlan"] = testPlan;
        File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
