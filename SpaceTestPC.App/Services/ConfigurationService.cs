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
            if (!configuration.TestModes.TryGetValue(mode, out var currentMode))
            {
                configuration.TestModes[mode] = selectedMode;
                return;
            }

            // Mode files define the authoritative test sequence, skipped items
            // and disabled items. Preserve explicit values saved in
            // appsettings.json, but fill them from the mode template when they
            // are missing.
            if (currentMode.TestOrder.Length == 0 && selectedMode.TestOrder.Length > 0)
            {
                currentMode.TestOrder = selectedMode.TestOrder;
            }

            if (currentMode.SkippedTests.Count == 0 && selectedMode.SkippedTests.Count > 0)
            {
                currentMode.SkippedTests = new Dictionary<string, string>(selectedMode.SkippedTests, StringComparer.OrdinalIgnoreCase);
            }

            if (currentMode.DisabledTests.Length == 0 && selectedMode.DisabledTests.Length > 0)
            {
                currentMode.DisabledTests = selectedMode.DisabledTests;
            }

            // The mode file is a template. Values saved through the settings
            // dialog in the active appsettings.json must take precedence.
            foreach (var parameterGroup in selectedMode.TestParameters)
            {
                if (!currentMode.TestParameters.TryGetValue(parameterGroup.Key, out var currentParameters))
                {
                    currentMode.TestParameters[parameterGroup.Key] = parameterGroup.Value;
                    continue;
                }

                foreach (var parameter in parameterGroup.Value)
                {
                    currentParameters.TryAdd(parameter.Key, parameter.Value);
                }
            }
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
