using System.Text.Json;
using MediaCleanup.Core.Mkv;

namespace MediaCleanup.Tests;

public sealed class FakeMkvTools : IMkvToolRunner
{
    public string Json { get; set; } = "{}";
    public int PropEditExitCode { get; set; }
    public List<IReadOnlyList<string>> PropEditCalls { get; } = new();
    public bool IsAvailable { get; set; } = true;
    public string? Location => "fake";

    public Task<ToolResult> RunMkvMergeAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        Task.FromResult(new ToolResult(0, Json));

    public Task<ToolResult> RunMkvPropEditAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        PropEditCalls.Add(arguments);
        return Task.FromResult(new ToolResult(PropEditExitCode, PropEditExitCode == 0 ? "Done." : "Error: boom"));
    }
}

public class MkvCleanerTests
{
    private const string Sample = """
    {
      "container": { "properties": { "title": "www.site.com - Mummy 2001" } },
      "global_tags": [ { "num_entries": 2 } ],
      "track_tags": [],
      "tracks": [
        { "id": 0, "type": "video", "properties": { "uid": 111, "track_name": "site.xyz" } },
        { "id": 1, "type": "audio", "properties": { "uid": 18446744073709551615, "language": "hin", "audio_channels": 6, "default_track": true, "track_name": "Hindi 5.1 - tamilmv.us" } },
        { "id": 2, "type": "audio", "properties": { "uid": 333, "language": "eng", "audio_channels": 2, "default_track": false } },
        { "id": 3, "type": "audio", "properties": { "uid": 444, "language": "tel", "audio_channels": 2, "default_track": false, "track_name": "Telugu" } },
        { "id": 4, "type": "subtitles", "properties": { "uid": 555, "language": "eng", "track_name": "English" } }
      ]
    }
    """;

    private static List<string> Build(string json, bool names = true, bool audio = true)
    {
        using var doc = JsonDocument.Parse(json);
        return new MkvCleaner(new FakeMkvTools()).BuildActions(doc.RootElement, names, audio, new List<string>());
    }

    [Fact]
    public void Builds_the_same_edits_as_the_original_script()
    {
        Assert.Equal(new[]
        {
            "--edit", "info", "--set", "title=Mummy 2001",
            "--edit", "track:=111", "--delete", "name",
            "--edit", "track:=18446744073709551615", "--set", "name=Hindi 5.1",
            // Telugu stereo outranks English stereo and Hindi 5.1.
            "--edit", "track:=444", "--set", "flag-default=1",
            "--edit", "track:=333", "--set", "flag-default=0",
            "--edit", "track:=18446744073709551615", "--set", "flag-default=0",
            "--tags", "all:",
        }, Build(Sample));
    }

    [Fact]
    public void Each_step_can_be_switched_off()
    {
        Assert.DoesNotContain("flag-default=1", Build(Sample, audio: false));
        var audioOnly = Build(Sample, names: false);
        Assert.DoesNotContain("--tags", audioOnly);
        Assert.DoesNotContain("title=Mummy 2001", audioOnly);
        Assert.Contains("flag-default=1", audioOnly);
    }

    [Fact]
    public void A_clean_file_needs_no_edits()
    {
        const string clean = """
        { "container": { "properties": { "title": "Mummy 2001" } }, "global_tags": [], "track_tags": [],
          "tracks": [
            { "id": 0, "type": "video", "properties": { "uid": 1 } },
            { "id": 1, "type": "audio", "properties": { "uid": 2, "language": "tel", "audio_channels": 6, "default_track": true } },
            { "id": 2, "type": "audio", "properties": { "uid": 3, "language": "eng", "audio_channels": 6, "default_track": false } } ] }
        """;
        Assert.Empty(Build(clean));
    }

    [Fact]
    public void Leaves_default_audio_alone_when_no_preferred_language_exists()
    {
        const string json = """
        { "global_tags": [], "track_tags": [], "tracks": [
            { "id": 1, "type": "audio", "properties": { "uid": 2, "language": "tam", "audio_channels": 6, "default_track": false } },
            { "id": 2, "type": "audio", "properties": { "uid": 3, "language": "kor", "audio_channels": 2, "default_track": true } } ] }
        """;
        Assert.Empty(Build(json));
    }

    [Theory]
    [InlineData("tel", null, null, 6, 1)]
    [InlineData("eng", null, null, 6, 2)]
    [InlineData("und", "te", null, 2, 3)]
    [InlineData("und", null, "English AAC", 2, 4)]
    [InlineData("hin", null, "Hindi DD 5.1", 2, 5)]
    [InlineData("hin", null, null, 2, 6)]
    [InlineData("tam", null, null, 6, 99)]
    public void Ranks_audio_like_the_script(string lang, string? ietf, string? name, int channels, int expected)
    {
        var language = AudioRanking.GetLanguage(lang, ietf, name);
        var surround = channels >= 6 || MkvCleaner.HasSurroundHint(name);
        Assert.Equal(expected, AudioRanking.GetRank(language, surround));
    }

    [Fact]
    public async Task Runs_mkvpropedit_with_the_file_first_and_reports_failure()
    {
        var tools = new FakeMkvTools { Json = Sample };
        var cleaner = new MkvCleaner(tools);
        var result = await cleaner.CleanAsync("movie.mkv", true, true, CancellationToken.None);
        Assert.True(result.Changed);
        Assert.Equal("movie.mkv", tools.PropEditCalls.Single()[0]);

        tools.PropEditExitCode = 2;
        await Assert.ThrowsAsync<MkvException>(() => cleaner.CleanAsync("movie.mkv", true, true, CancellationToken.None));
    }
}
