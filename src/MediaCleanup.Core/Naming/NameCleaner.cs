using System.Text.RegularExpressions;

namespace MediaCleanup.Core.Naming;

/// <summary>
/// Removes website names from file and folder names and turns dot or underscore separators into
/// spaces, so "tamilmv.us - Don.2003.Web-DL" becomes "Don 2003 Web-DL".
/// </summary>
public sealed class NameCleaner
{
    private const string Label = "[a-z0-9-]+";
    private const string Tlds = SitePatterns.Tlds;
    private const string Strong = SitePatterns.StrongTlds;

    // "[www.site.com]" or "(site.xyz)" anywhere in the name.
    private static readonly Regex Bracketed = SitePatterns.Create(
        @"[\[\(\{]\s*(?:https?://)?" + Label + @"(?:\." + Label + @")*\.(?:" + Tlds + @")/?\s*[\]\)\}]");

    // "http://anything".
    private static readonly Regex SchemeUrl = SitePatterns.Create(@"https?://[^\s\]\)\}]+");

    // "www.site.com" or "www.site.co.in" with a known ending. A second-level ending is only taken when
    // the name does not carry on with another dot, so "www.site.com.In.Time" keeps its "In".
    private static readonly Regex WwwKnown = SitePatterns.Create(
        @"www\." + Label + @"(?:\." + Label + @")??\.(?:(?:co|com|org|net)\.[a-z]{2}(?=$|[^a-z0-9.])|(?:" + Tlds + @")(?![a-z0-9]))");

    // "www.site.anything": the www prefix alone is proof enough of a web address.
    private static readonly Regex WwwAny = SitePatterns.Create(@"www\." + Label + @"\.[a-z]{2,10}(?![a-z0-9])");

    // "site.us - Movie": a leading address that is followed by a dash style separator.
    private static readonly Regex LeadingWithSeparator = SitePatterns.Create(
        @"^\s*" + Label + @"(?:\." + Label + @")*\.(?:" + Tlds + @")(?=\s*[-–—|:~]\s)");

    // "Movie 2019 - site.me": a trailing address after a dash style separator.
    private static readonly Regex TrailingWithSeparator = SitePatterns.Create(
        @"\s[-–—|:~]\s*" + Label + @"(?:\." + Label + @")*\.(?:" + Tlds + @")\s*$");

    // "site.com" anywhere, limited to endings that are not everyday words.
    private static readonly Regex StrongDomain = SitePatterns.Create(
        @"(?<![a-z0-9])" + Label + @"\.(?:" + Strong + @")(?![a-z0-9])");

    // Dots that must survive: audio channel layouts such as 5.1 and codec names such as H.264.
    private static readonly Regex ChannelLayout = SitePatterns.Create(@"(?<!\d)(?:[1257]\.[01]|6\.1)(?!\d)");
    private static readonly Regex DottedCodec = SitePatterns.Create(@"(?<![a-z0-9])h\.26[45](?![0-9])");

    private static readonly Regex Separators = new(@"[._]+", RegexOptions.Compiled);
    private static readonly Regex EmptyBrackets = new(@"[\[\(\{]\s*[\]\)\}]", RegexOptions.Compiled);
    private static readonly Regex RepeatedDashes = new(@"(?:\s[-–—|:~])+(?=\s[-–—|:~]\s)", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly char[] EdgeChars = { ' ', '-', '–', '—', '_', '.', ',', '|', ':', '~' };
    private const char Guard = '\u0001';

    private readonly List<Regex> _customTerms;

    public NameCleaner(IEnumerable<string>? customRemovalTerms = null)
    {
        _customTerms = (customRemovalTerms ?? Enumerable.Empty<string>())
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .OrderByDescending(t => t.Length)
            .Select(t => SitePatterns.Create(@"(?<![a-z0-9])" + Regex.Escape(t) + @"(?![a-z0-9])"))
            .ToList();
    }

    /// <summary>Cleans a name that has no extension, such as a folder name or a file name stem.</summary>
    public string CleanStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return stem;

        var s = stem;
        foreach (var term in _customTerms) s = term.Replace(s, " ");
        s = Bracketed.Replace(s, " ");
        s = SchemeUrl.Replace(s, " ");
        s = WwwKnown.Replace(s, " ");
        s = WwwAny.Replace(s, " ");
        s = LeadingWithSeparator.Replace(s, " ");
        s = TrailingWithSeparator.Replace(s, " ");
        s = StrongDomain.Replace(s, " ");

        s = ChannelLayout.Replace(s, m => m.Value.Replace('.', Guard));
        s = DottedCodec.Replace(s, m => m.Value.Replace('.', Guard));
        s = Separators.Replace(s, " ");
        s = s.Replace(Guard, '.');

        s = EmptyBrackets.Replace(s, " ");
        s = Whitespace.Replace(s, " ");
        s = RepeatedDashes.Replace(s, "");
        s = s.Trim(EdgeChars);
        s = Whitespace.Replace(s, " ");

        // Never return an empty name: a file called only "site.com.mkv" keeps its original name.
        return s.Length == 0 ? stem : s;
    }

    public string CleanFolderName(string name) => CleanStem(name);

    /// <summary>
    /// Cleans a file name and keeps its extension. Subtitles also keep trailing language and flag
    /// parts, so "site.com - Movie.2019.en.forced.srt" becomes "Movie 2019.en.forced.srt".
    /// </summary>
    public string CleanFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        if (stem.Length == 0) return fileName;

        var suffix = "";
        if (MediaTypes.IsSubtitle(fileName))
            (stem, suffix) = SplitSubtitleSuffix(stem);

        return CleanStem(stem) + suffix + extension;
    }

    /// <summary>Splits "Movie.2019.en.forced" into ("Movie.2019", ".en.forced").</summary>
    public static (string Stem, string Suffix) SplitSubtitleSuffix(string stem)
    {
        var end = stem.Length;
        while (true)
        {
            var dot = stem.LastIndexOf('.', end - 1);
            if (dot <= 0) break;
            var token = stem[(dot + 1)..end];
            if (!MediaTypes.SubtitleSuffixTokens.Contains(token)) break;
            end = dot;
        }
        return (stem[..end], stem[end..]);
    }
}
