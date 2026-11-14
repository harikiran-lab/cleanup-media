using System.Text.Json;

namespace MediaCleanup.Core.Configuration;

/// <summary>Where the service and the configuration app keep their shared files.</summary>
public static class AppPaths
{
    public const string ServiceName = "MediaCleanupService";
    public const string ServiceDisplayName = "Media Cleanup Service";

    /// <summary>
    /// %ProgramData%\MediaCleanup on Windows. Set the MEDIACLEANUP_HOME environment variable to override.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("MEDIACLEANUP_HOME");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(root, "MediaCleanup");
        }
    }

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string LedgerFile => Path.Combine(DataDirectory, "processed.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads the configuration, returning defaults when the file does not exist yet.</summary>
    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            fresh.Normalize();
            return fresh;
        }
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        config.Normalize();
        return config;
    }

    /// <summary>Writes the configuration through a temporary file so a reader never sees half a file.</summary>
    public static void Save(AppConfig config, string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        config.Normalize();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
