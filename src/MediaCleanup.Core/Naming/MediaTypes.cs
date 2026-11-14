namespace MediaCleanup.Core.Naming;

public static class MediaTypes
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".vob", ".divx", ".asf", ".3gp", ".rmvb", ".ogv",
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".vtt", ".ass", ".ssa", ".smi", ".sup",
    };

    /// <summary>Extensions that download and copy tools use while a file is still being written.</summary>
    public static readonly HashSet<string> IncompleteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part", ".partial", ".!qb", ".!ut", ".bc!", ".crdownload", ".download", ".filepart", ".aria2",
        ".tmp", ".temp", ".downloading", ".opdownload", ".mcpartial",
    };

    /// <summary>Language codes and flags that media players read from "Movie.en.forced.srt" style names.</summary>
    public static readonly HashSet<string> SubtitleSuffixTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english", "te", "tel", "telugu", "hi", "hin", "hindi", "ta", "tam", "tamil",
        "ml", "mal", "malayalam", "kn", "kan", "kannada", "bn", "ben", "mr", "mar", "pa", "pan", "gu", "guj",
        "ur", "urd", "es", "spa", "fr", "fre", "fra", "de", "ger", "deu", "it", "ita", "pt", "por", "ru", "rus",
        "ja", "jpn", "ko", "kor", "zh", "chi", "zho", "ar", "ara", "nl", "dut", "nld", "sv", "swe", "tr", "tur",
        "id", "ind", "th", "tha", "vi", "vie", "pl", "pol",
        "forced", "sdh", "cc", "default", "foreign",
    };

    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path));
    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));
    public static bool IsMkv(string path) => Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    public static bool IsIncomplete(string path) => IncompleteExtensions.Contains(Path.GetExtension(path));
}
