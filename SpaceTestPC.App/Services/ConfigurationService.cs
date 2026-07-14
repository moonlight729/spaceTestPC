using System.IO;
using System.Text.Json;
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

        try
        {
            var json = File.ReadAllText(resolvedPath);
            return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions) ?? new AppConfiguration();
        }
        catch
        {
            return new AppConfiguration();
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
