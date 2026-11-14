using System.Text.Json.Serialization;

namespace MediaCleanup.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
public enum MediaKind
{
    Movie,
    TvShow,
}

/// <summary>Which cleanup steps run for a monitored source.</summary>
public sealed class ActionSettings
{
    /// <summary>Strip website names from file and folder names and replace dots with spaces.</summary>
    public bool NameCleanup { get; set; } = true;

    /// <summary>Use MKVToolNix to strip website names from the MKV title and track names, and remove tags.</summary>
    public bool TrackNameCleanup { get; set; } = true;

    /// <summary>Use MKVToolNix to set the default audio track by language priority.</summary>
    public bool SetDefaultAudio { get; set; } = true;

    /// <summary>Clean subtitle file names and remove website adverts from inside text subtitles.</summary>
    public bool SubtitleCleanup { get; set; } = true;

    /// <summary>Movies only: put a loose media file into a folder named after the cleaned file name.</summary>
    public bool MoveIntoFolder { get; set; } = true;

    /// <summary>Delete files with unwanted extensions (.png, .txt, .nfo by default).</summary>
    public bool RemoveUnwantedFiles { get; set; } = true;

    public ActionSettings Clone() => (ActionSettings)MemberwiseClone();
}

/// <summary>One monitored source folder and where its cleaned content goes.</summary>
public sealed class MonitorMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public MediaKind Kind { get; set; } = MediaKind.Movie;
    public string SourcePath { get; set; } = "";

    /// <summary>Destination folder (local or UNC). Empty means clean up in place inside the source.</summary>
    public string DestinationPath { get; set; } = "";

    public bool Active { get; set; } = true;
    public ActionSettings Actions { get; set; } = new();

    public MonitorMapping Clone()
    {
        var copy = (MonitorMapping)MemberwiseClone();
        copy.Actions = Actions.Clone();
        return copy;
    }
}

public sealed class AppConfig
{
    /// <summary>Master switch. When false the service idles and touches nothing.</summary>
    public bool MonitoringEnabled { get; set; } = true;

    /// <summary>How often each source is scanned.</summary>
    public int ScanIntervalSeconds { get; set; } = 30;

    /// <summary>How long a file or folder must stay unchanged before it counts as completely downloaded.</summary>
    public int StableSeconds { get; set; } = 60;

    /// <summary>Folder that holds mkvmerge and mkvpropedit. Empty means search the usual places and PATH.</summary>
    public string MkvToolNixPath { get; set; } = "";

    public List<string> UnwantedExtensions { get; set; } = new() { ".png", ".txt", ".nfo" };

    /// <summary>Extra words to strip from names, for site tags that do not look like a web address.</summary>
    public List<string> CustomRemovalTerms { get; set; } = new();

    public List<MonitorMapping> Mappings { get; set; } = new();

    /// <summary>Clamps numbers to sane ranges and normalises lists after loading or editing.</summary>
    public void Normalize()
    {
        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 5, 3600);
        StableSeconds = Math.Clamp(StableSeconds, 5, 86400);
        MkvToolNixPath = (MkvToolNixPath ?? "").Trim().Trim('"');
        UnwantedExtensions = (UnwantedExtensions ?? new())
            .Select(e => (e ?? "").Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct()
            .ToList();
        CustomRemovalTerms = (CustomRemovalTerms ?? new())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Mappings ??= new();
        foreach (var m in Mappings)
        {
            if (string.IsNullOrWhiteSpace(m.Id)) m.Id = Guid.NewGuid().ToString("N");
            m.Name = (m.Name ?? "").Trim();
            m.SourcePath = PathUtil.NormalizeDir(m.SourcePath);
            m.DestinationPath = PathUtil.NormalizeDir(m.DestinationPath);
            // A destination equal to the source simply means "clean up in place".
            if (PathUtil.PathEquals(m.SourcePath, m.DestinationPath)) m.DestinationPath = "";
            m.Actions ??= new();
            if (m.Kind == MediaKind.TvShow) m.Actions.MoveIntoFolder = false;
        }
    }

    /// <summary>Returns human-readable problems with the configuration. Empty means it is usable.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var m in Mappings)
        {
            var label = string.IsNullOrEmpty(m.Name) ? m.SourcePath : m.Name;
            if (string.IsNullOrEmpty(m.SourcePath))
            {
                problems.Add($"'{label}': the source path is empty.");
                continue;
            }
            foreach (var other in Mappings)
            {
                if (ReferenceEquals(other, m)) continue;
                if (PathUtil.PathEquals(other.SourcePath, m.SourcePath))
                {
                    problems.Add($"'{label}': the source path is used by more than one entry.");
                    break;
                }
            }
        }
        return problems.Distinct().ToList();
    }
}
