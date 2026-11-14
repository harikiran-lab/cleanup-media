using System.Text.RegularExpressions;

namespace MediaCleanup.Core.Naming;

/// <summary>Regular expressions that recognise website names and web addresses.</summary>
public static class SitePatterns
{
    /// <summary>Top-level domains from the original cleanup script.</summary>
    public const string Tlds =
        "com|org|net|in|me|to|vip|lat|cc|co|biz|info|xyz|club|site|top|online|tech|store|stream|link|click|" +
        "company|media|video|download|app|tv|lol|mov|is|ws|pro|pm|cat|guru|icu|monster|zone|work|uno|tube|mx|" +
        "ag|gg|sh|io|eu|us|uk|ca|se|nl|ru|pl|ch|ai|live|today|world|wiki|best|space|fun|host|website";

    /// <summary>
    /// Top-level domains that are not also everyday words. Only these are trusted where a dot is
    /// ambiguous, such as "Movie.In.Time" file names or a sentence with a missing space in a subtitle.
    /// </summary>
    public const string StrongTlds = "com|org|net|xyz|biz|info|cc|vip|icu|lat|ws";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>
    /// The URL pattern of the original script, used for MKV titles and track names where text is
    /// separated by spaces rather than dots.
    /// </summary>
    public static readonly Regex TrackUrl = new(
        @"\b(?:https?://|www\.)\S+|\b[a-z0-9-]+(\.[a-z0-9-]+)*\.(" + Tlds + @")\b", Options);

    /// <summary>A stricter pattern for subtitle dialogue: explicit links, or domains with an unambiguous ending.</summary>
    public static readonly Regex SubtitleUrl = new(
        @"(?:https?://|www\.)[a-z0-9-]+\.[a-z0-9-]|(?<![a-z0-9@.-])[a-z0-9-]{3,}\.(?:" + StrongTlds + @")(?![a-z0-9])",
        Options);

    public static Regex Create(string pattern) => new(pattern, Options);
}
