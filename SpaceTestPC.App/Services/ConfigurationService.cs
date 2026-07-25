using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AppConfiguration Load(string path)
    {
        var resolvedPath = ResolvePath(path);
        if (resolvedPath is null)
        {
            return new AppConfiguration();
        }

        var json = File.ReadAllText(resolvedPath);
        try
        {
            var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions) ?? new AppConfiguration();
            ApplyModeConfiguration(configuration, resolvedPath);
            return configuration;
        }
        catch
        {
            // Keep the upgrade gate usable even when an unrelated legacy config entry is malformed.
            var fallback = new AppConfiguration();
            var upgradeMatch = Regex.Match(json, "\\\"upgrade\\\"\\s*:\\s*(\\{[^{}]*\\})", RegexOptions.Singleline);
            if (upgradeMatch.Success)
            {
                try
                {
                    fallback.Upgrade = JsonSerializer.Deserialize<UpgradeConfiguration>(upgradeMatch.Groups[1].Value, JsonOptions)
                        ?? new UpgradeConfiguration();
                }
                catch
                {
                    // Use the safe defaults when even the isolated upgrade block is invalid.
                }
            }
            return fallback;
        }
    }

    private static void ApplyModeConfiguration(AppConfiguration configuration, string basePath)
    {
        var mode = configuration.TestMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode)) return;

        var directory = Path.GetDirectoryName(basePath) ?? string.Empty;
        var modePath = Path.Combine(directory, $"appsettings.{mode}.json");
        if (!File.Exists(modePath)) return;

        var modeConfiguration = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(modePath), JsonOptions);
        if (modeConfiguration?.TestModes.TryGetValue(mode, out var selectedMode) == true)
        {
            configuration.TestModes[mode] = selectedMode;
        }
    }

    private static string? ResolvePath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            return File.Exists(path) ? path : null;
        }

        var jsoncPath = Path.Combine(directory, $"{fileNameWithoutExtension}.jsonc");
        if (File.Exists(jsoncPath))
        {
            return jsoncPath;
        }

        return File.Exists(path) ? path : null;
    }
}
