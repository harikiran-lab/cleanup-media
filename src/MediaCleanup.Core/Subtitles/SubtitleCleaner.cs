using System.Text;
using System.Text.RegularExpressions;
using MediaCleanup.Core.Naming;

namespace MediaCleanup.Core.Subtitles;

/// <summary>
/// Removes website adverts from inside text subtitles (.srt, .vtt, .ass, .ssa). A whole cue is
/// dropped when it mentions a web address, since such cues are credits or adverts, not dialogue.
/// </summary>
public sealed class SubtitleCleaner
{
    private static readonly HashSet<string> BlockFormats = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt" };
    private static readonly HashSet<string> ScriptFormats = new(StringComparer.OrdinalIgnoreCase) { ".ass", ".ssa" };
    private static readonly Regex BlankLineSplit = new(@"(?:\r?\n[ \t]*){2,}", RegexOptions.Compiled);
    private static readonly Regex Timing = new(@"\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3}\s*-->", RegexOptions.Compiled);
    private static readonly Regex AssOverride = new(@"\{[^}]*\}", RegexOptions.Compiled);

    private readonly List<Regex> _patterns = new() { SitePatterns.SubtitleUrl };

    public SubtitleCleaner(IEnumerable<string>? customRemovalTerms = null)
    {
        foreach (var term in customRemovalTerms ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            _patterns.Add(SitePatterns.Create(@"(?<![a-z0-9])" + Regex.Escape(term.Trim()) + @"(?![a-z0-9])"));
        }
    }

    public static bool CanClean(string path)
    {
        var extension = Path.GetExtension(path);
        return BlockFormats.Contains(extension) || ScriptFormats.Contains(extension);
    }

    /// <summary>Cleans a subtitle file in place. Returns how many cues were removed.</summary>
    /// <param name="write">False to only count the cues that would be removed.</param>
    public int CleanFile(string path, bool write = true)
    {
        if (!CanClean(path)) return 0;
        var bytes = File.ReadAllBytes(path);
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        var (cleaned, removed) = ScriptFormats.Contains(Path.GetExtension(path))
            ? CleanScript(text)
            : CleanBlocks(text, renumber: Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase));
        if (removed == 0 || !write) return removed;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, preambleLength);
        var body = encoding.GetBytes(cleaned);
        stream.Write(body, 0, body.Length);
        return removed;
    }

    /// <summary>Cleans SubRip or WebVTT text: cues are blocks separated by blank lines.</summary>
    public (string Text, int Removed) CleanBlocks(string text, bool renumber)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var blocks = BlankLineSplit.Split(text.Trim('\r', '\n'));
        var kept = new List<string>();
        var removed = 0;
        var number = 0;

        foreach (var block in blocks)
        {
            if (block.Trim().Length == 0) continue;
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var timingIndex = lines.FindIndex(l => Timing.IsMatch(l));
            if (timingIndex < 0)
            {
                kept.Add(string.Join(newline, lines)); // A header, style or note block: leave it alone.
                continue;
            }

            if (lines.Skip(timingIndex + 1).Any(IsAdvert))
            {
                removed++;
                continue;
            }

            number++;
            if (renumber && timingIndex == 1 && int.TryParse(lines[0].Trim(), out _))
                lines[0] = number.ToString();
            kept.Add(string.Join(newline, lines));
        }

        if (removed == 0) return (text, 0);
        return (string.Join(newline + newline, kept) + newline, removed);
    }

    /// <summary>Cleans Advanced SubStation text: every cue is one "Dialogue:" line.</summary>
    public (string Text, int Removed) CleanScript(string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var kept = new List<string>(lines.Count);
        var removed = 0;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                // The spoken text is the tenth comma separated field and may itself contain commas.
                var fields = line.Split(',', 10);
                var spoken = AssOverride.Replace(fields[^1], "");
                if (fields.Length == 10 && IsAdvert(spoken))
                {
                    removed++;
                    continue;
                }
            }
            kept.Add(line);
        }
        return removed == 0 ? (text, 0) : (string.Join(newline, kept), removed);
    }

    public bool IsAdvert(string line) => _patterns.Any(p => p.IsMatch(line));

    /// <summary>
    /// Picks an encoding that round-trips the file byte for byte. Files that are not valid UTF-8 are
    /// read as Latin-1, which maps every byte to one character, so untouched cues are written back unchanged.
    /// </summary>
    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (ArgumentException)
        {
            return (Encoding.Latin1, 0);
        }
    }
}
