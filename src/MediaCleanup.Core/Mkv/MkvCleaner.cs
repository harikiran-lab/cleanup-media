using System.Text.Json;
using System.Text.RegularExpressions;
using MediaCleanup.Core.Naming;

namespace MediaCleanup.Core.Mkv;

public sealed class MkvException : Exception
{
    public MkvException(string message) : base(message) { }
}

public sealed record MkvCleanResult(bool Changed, IReadOnlyList<string> Notes);

/// <summary>
/// Cleans the header of an MKV file in place with mkvpropedit: strips website names from the title
/// and track names, removes tags, and picks the default audio track. Nothing is re-encoded or remuxed.
/// </summary>
public sealed class MkvCleaner
{
    private static readonly char[] NameTrimChars = { ' ', '-', '_', '[', ']', '(', ')', ':', '|', '"', '\'' };
    private static readonly Regex SurroundHint = new(@"5\.1|7\.1|6ch|8ch", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IMkvToolRunner _runner;
    private readonly List<Regex> _customTerms;

    public MkvCleaner(IMkvToolRunner runner, IEnumerable<string>? customRemovalTerms = null)
    {
        _runner = runner;
        _customTerms = (customRemovalTerms ?? Enumerable.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => SitePatterns.Create(@"(?<![a-z0-9])" + Regex.Escape(t.Trim()) + @"(?![a-z0-9])"))
            .ToList();
    }

    public bool IsAvailable => _runner.IsAvailable;

    public async Task<MkvCleanResult> CleanAsync(string file, bool cleanNames, bool setDefaultAudio, CancellationToken ct)
    {
        if (!cleanNames && !setDefaultAudio) return new MkvCleanResult(false, Array.Empty<string>());

        var identify = await _runner.RunMkvMergeAsync(new[] { "-J", file }, ct).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(identify.Output);
        }
        catch (JsonException)
        {
            throw new MkvException($"mkvmerge could not read the file (exit code {identify.ExitCode}): {Shorten(identify.AllText)}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (identify.ExitCode >= 2)
            {
                var detail = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", errors.EnumerateArray().Select(e => e.ToString()))
                    : identify.AllText;
                throw new MkvException($"mkvmerge could not read the file: {Shorten(detail)}");
            }

            var notes = new List<string>();
            var actions = BuildActions(root, cleanNames, setDefaultAudio, notes);
            if (actions.Count == 0) return new MkvCleanResult(false, notes);

            var arguments = new List<string> { file };
            arguments.AddRange(actions);
            var edit = await _runner.RunMkvPropEditAsync(arguments, ct).ConfigureAwait(false);
            if (edit.ExitCode >= 2)
                throw new MkvException($"mkvpropedit failed (exit code {edit.ExitCode}): {Shorten(edit.AllText)}");
            if (edit.ExitCode == 1)
                notes.Add("mkvpropedit finished with warnings: " + Shorten(edit.AllText));
            return new MkvCleanResult(true, notes);
        }
    }

    /// <summary>Works out the mkvpropedit arguments for a file from the JSON that "mkvmerge -J" prints.</summary>
    public List<string> BuildActions(JsonElement root, bool cleanNames, bool setDefaultAudio, List<string> notes)
    {
        var actions = new List<string>();
        var tracks = root.TryGetProperty("tracks", out var tracksElement) && tracksElement.ValueKind == JsonValueKind.Array
            ? tracksElement.EnumerateArray().ToList()
            : new List<JsonElement>();

        if (cleanNames)
        {
            var title = GetString(root, "container", "properties", "title");
            if (TryCleanName(title, out var cleanTitle))
            {
                if (cleanTitle.Length == 0)
                {
                    actions.AddRange(new[] { "--edit", "info", "--delete", "title" });
                    notes.Add($"Removed title '{title}'");
                }
                else
                {
                    actions.AddRange(new[] { "--edit", "info", "--set", "title=" + cleanTitle });
                    notes.Add($"Title '{title}' -> '{cleanTitle}'");
                }
            }

            foreach (var track in tracks)
            {
                var name = GetString(track, "properties", "track_name");
                var uid = GetRaw(track, "properties", "uid");
                if (uid == null || !TryCleanName(name, out var cleanName)) continue;
                var label = $"track {GetRaw(track, "id")} ({GetString(track, "type")})";
                if (cleanName.Length == 0)
                {
                    actions.AddRange(new[] { "--edit", "track:=" + uid, "--delete", "name" });
                    notes.Add($"Removed name '{name}' from {label}");
                }
                else
                {
                    actions.AddRange(new[] { "--edit", "track:=" + uid, "--set", "name=" + cleanName });
                    notes.Add($"Renamed {label} '{name}' -> '{cleanName}'");
                }
            }
        }

        if (setDefaultAudio)
            AddDefaultAudioActions(tracks, actions, notes);

        if (cleanNames && HasTags(root))
        {
            actions.AddRange(new[] { "--tags", "all:" });
            notes.Add("Removed tags");
        }

        return actions;
    }

    private bool TryCleanName(string? name, out string cleaned)
    {
        cleaned = "";
        if (string.IsNullOrEmpty(name)) return false;
        var result = SitePatterns.TrackUrl.Replace(name, "");
        foreach (var term in _customTerms) result = term.Replace(result, "");
        if (result == name) return false;
        cleaned = Regex.Replace(result, @"\s{2,}", " ").Trim(NameTrimChars);
        return true;
    }

    private static void AddDefaultAudioActions(List<JsonElement> tracks, List<string> actions, List<string> notes)
    {
        var audio = tracks
            .Where(t => GetString(t, "type") == "audio" && GetRaw(t, "properties", "uid") != null)
            .Select(t => new
            {
                Track = t,
                Uid = GetRaw(t, "properties", "uid")!,
                Id = GetLong(t, "id"),
                Rank = AudioRanking.GetRank(t),
                IsDefault = GetBool(t, "properties", "default_track"),
            })
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Id)
            .ToList();
        if (audio.Count == 0) return;

        var best = audio[0];
        if (best.Rank >= AudioRanking.OtherRank) return; // No Telugu, English or Hindi audio: keep what is there.

        var alreadyCorrect = audio.All(t => t.IsDefault == (t.Uid == best.Uid));
        if (alreadyCorrect) return;

        foreach (var track in audio)
            actions.AddRange(new[] { "--edit", "track:=" + track.Uid, "--set", "flag-default=" + (track.Uid == best.Uid ? "1" : "0") });
        notes.Add($"Default audio -> track {best.Id} ({AudioRanking.Describe(best.Track)})");
    }

    /// <summary>
    /// The original script always strips tags. Skipping the edit when mkvmerge reports none avoids
    /// rewriting the header of a file that is already clean.
    /// </summary>
    private static bool HasTags(JsonElement root)
    {
        var known = false;
        foreach (var property in new[] { "global_tags", "track_tags" })
        {
            if (!root.TryGetProperty(property, out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
            known = true;
            if (tags.GetArrayLength() > 0) return true;
        }
        return !known;
    }

    internal static JsonElement? Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current)) return null;
        }
        return current;
    }

    internal static string? GetString(JsonElement element, params string[] path) =>
        Get(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Reads a number as text so 64-bit track UIDs are never rounded.</summary>
    internal static string? GetRaw(JsonElement element, params string[] path) =>
        Get(element, path) is { ValueKind: JsonValueKind.Number or JsonValueKind.String } value
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
            : null;

    internal static long GetLong(JsonElement element, params string[] path) =>
        Get(element, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : 0;

    internal static bool GetBool(JsonElement element, params string[] path) =>
        Get(element, path) is { ValueKind: JsonValueKind.True };

    internal static bool HasSurroundHint(string? trackName) => trackName != null && SurroundHint.IsMatch(trackName);

    private static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length <= 500 ? text : text[..500] + "...";
    }
}
