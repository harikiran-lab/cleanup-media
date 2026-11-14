using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaCleanup.Core.Mkv;

/// <summary>
/// Default audio priority, as in the original script: Telugu 5.1, English 5.1, Telugu stereo,
/// English stereo, Hindi 5.1, Hindi stereo. Any other language never becomes the default.
/// </summary>
public static class AudioRanking
{
    public const int OtherRank = 99;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex TeluguCode = new(@"\b(tel|te)\b|^te", Options);
    private static readonly Regex TeluguName = new(@"\b(telugu|tel)\b", Options);
    private static readonly Regex EnglishCode = new(@"\b(eng|en)\b|^en", Options);
    private static readonly Regex EnglishName = new(@"\b(english|eng)\b", Options);
    private static readonly Regex HindiCode = new(@"\b(hin|hi)\b|^hi", Options);
    private static readonly Regex HindiName = new(@"\b(hindi|hin)\b", Options);

    public static string GetLanguage(string? language, string? languageIetf, string? trackName)
    {
        var combined = $"{language} {languageIetf}";
        var name = trackName ?? "";
        if (TeluguCode.IsMatch(combined) || TeluguName.IsMatch(name)) return "Telugu";
        if (EnglishCode.IsMatch(combined) || EnglishName.IsMatch(name)) return "English";
        if (HindiCode.IsMatch(combined) || HindiName.IsMatch(name)) return "Hindi";
        return "Other";
    }

    public static int GetRank(string language, bool isSurround) => language switch
    {
        "Telugu" => isSurround ? 1 : 3,
        "English" => isSurround ? 2 : 4,
        "Hindi" => isSurround ? 5 : 6,
        _ => OtherRank,
    };

    public static int GetRank(JsonElement track) => GetRank(LanguageOf(track), IsSurround(track));

    public static string Describe(JsonElement track) => $"{LanguageOf(track)} {(IsSurround(track) ? "5.1" : "Stereo")}";

    private static string LanguageOf(JsonElement track) => GetLanguage(
        MkvCleaner.GetString(track, "properties", "language"),
        MkvCleaner.GetString(track, "properties", "language_ietf"),
        MkvCleaner.GetString(track, "properties", "track_name"));

    private static bool IsSurround(JsonElement track) =>
        MkvCleaner.GetLong(track, "properties", "audio_channels") >= 6
        || MkvCleaner.HasSurroundHint(MkvCleaner.GetString(track, "properties", "track_name"));
}
