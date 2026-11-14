using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Processing;
using Microsoft.Extensions.Logging;

namespace MediaCleanup.Tests;

public sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{logLevel}: {formatter(state, exception)}");
}

/// <summary>End-to-end tests that run the engine against real folders in a temporary directory.</summary>
public sealed class EngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mc-engine").FullName;
    private readonly ListLogger _log = new();
    private readonly FakeMkvTools _tools = new() { Json = """{ "global_tags": [], "track_tags": [], "tracks": [] }""" };
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly AppConfig _config = new() { StableSeconds = 60 };
    private readonly MonitorEngine _engine;

    public EngineTests()
    {
        _engine = new MonitorEngine(_log, new ProcessedLedger(), _ => _tools, () => _now);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static void Touch(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private MonitorMapping AddMapping(MediaKind kind, string source, string destination = "")
    {
        var mapping = new MonitorMapping { Name = source, Kind = kind, SourcePath = Dir(source), DestinationPath = destination.Length > 0 ? Dir(destination) : "" };
        _config.Mappings.Add(mapping);
        _config.Normalize();
        return mapping;
    }

    /// <summary>Scans twice with the clock moved past the stability window, like two timer ticks.</summary>
    private async Task<int> ScanUntilStableAsync()
    {
        var first = await _engine.ScanOnceAsync(_config, CancellationToken.None);
        _now = _now.AddSeconds(61);
        return first + await _engine.ScanOnceAsync(_config, CancellationToken.None);
    }

    private string[] Tree(string name) => Directory
        .EnumerateFileSystemEntries(Path.Combine(_root, name), "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(Path.Combine(_root, name), p).Replace('\\', '/') + (Directory.Exists(p) ? "/" : ""))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public async Task Movie_examples_from_the_request()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        Touch(Path.Combine(m.SourcePath, "www.movieRuz.xyz - Mummy 2001 Dual Tel+Eng.mkv"));
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.mp4"));
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nwww.tamilmv.us\n\n2\n00:00:03,000 --> 00:00:04,000\nHi\n");
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.nfo"));
        Touch(Path.Combine(m.SourcePath, "Devil.2005.Web-DL.mp4"));
        Touch(Path.Combine(m.SourcePath, "unrelated notes.txt"));

        Assert.Equal(3, await ScanUntilStableAsync());

        Assert.Equal(new[]
        {
            "Devil 2005 Web-DL/",
            "Devil 2005 Web-DL/Devil 2005 Web-DL.mp4",
            "Don 2003 Web-DL/",
            "Don 2003 Web-DL/Don 2003 Web-DL.en.srt",
            "Don 2003 Web-DL/Don 2003 Web-DL.mp4",
            "Mummy 2001 Dual Tel+Eng/",
            "Mummy 2001 Dual Tel+Eng/Mummy 2001 Dual Tel+Eng.mkv",
        }, Tree("movies"));
        Assert.Equal("1\n00:00:03,000 --> 00:00:04,000\nHi\n", File.ReadAllText(Path.Combine(_root, "movies", "Don 2003 Web-DL", "Don 2003 Web-DL.en.srt")));
        // Files that do not belong to a movie are never touched.
        Assert.Equal(new[] { "unrelated notes.txt" }, Tree("downloads"));
        Assert.Empty(_tools.PropEditCalls); // The header is already clean, so mkvpropedit is not run.
    }

    [Theory]
    [InlineData("www.movieRuz.xyz - House 2020 Dual Tel+Eng", "www.movieRuz.xyz - ")]
    [InlineData("www.movieRuz.xyz - House 2020 Dual Tel+Eng", "")]
    [InlineData("House 2020 Dual Tel+Eng", "www.movieRuz.xyz - ")]
    [InlineData("House 2020 Dual Tel+Eng", "")]
    public async Task Tv_show_examples_from_the_request(string folder, string episodePrefix)
    {
        var m = AddMapping(MediaKind.TvShow, "tv-in", "tv");
        Touch(Path.Combine(m.SourcePath, folder, episodePrefix + "House S01E01.mkv"));
        Touch(Path.Combine(m.SourcePath, folder, episodePrefix + "House S01E02.mkv"));
        Touch(Path.Combine(m.SourcePath, folder, "poster.png"));
        Touch(Path.Combine(m.SourcePath, folder, "Visit www.movieRuz.xyz.txt"));

        Assert.Equal(1, await ScanUntilStableAsync());

        Assert.Equal(new[]
        {
            "House 2020 Dual Tel+Eng/",
            "House 2020 Dual Tel+Eng/House S01E01.mkv",
            "House 2020 Dual Tel+Eng/House S01E02.mkv",
        }, Tree("tv"));
        Assert.Empty(Tree("tv-in"));
    }

    [Fact]
    public async Task New_episodes_merge_into_an_existing_show_folder()
    {
        var m = AddMapping(MediaKind.TvShow, "tv-in", "tv");
        Touch(Path.Combine(m.DestinationPath, "House 2020", "House S01E01.mkv"), "episode one");
        Touch(Path.Combine(m.SourcePath, "site.com - House 2020", "House S01E01.mkv"), "episode 1!!"); // same size: duplicate
        Touch(Path.Combine(m.SourcePath, "site.com - House 2020", "House S01E02.mkv"), "two");

        await ScanUntilStableAsync();

        Assert.Equal(new[] { "House 2020/", "House 2020/House S01E01.mkv", "House 2020/House S01E02.mkv" }, Tree("tv"));
        Assert.Equal("episode one", File.ReadAllText(Path.Combine(m.DestinationPath, "House 2020", "House S01E01.mkv")));
        Assert.Empty(Tree("tv-in"));
    }

    [Fact]
    public async Task Waits_until_the_file_stops_changing()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        var file = Path.Combine(m.SourcePath, "Devil.2005.Web-DL.mp4");
        Touch(file, "partial");

        Assert.Equal(0, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        _now = _now.AddSeconds(45);
        File.AppendAllText(file, " more data");                        // still growing: the wait starts again
        Assert.Equal(0, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        _now = _now.AddSeconds(45);
        Assert.Equal(0, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        _now = _now.AddSeconds(20);
        Assert.Equal(1, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(m.DestinationPath, "Devil 2005 Web-DL", "Devil 2005 Web-DL.mp4")));
    }

    [Fact]
    public async Task Waits_while_a_file_is_open_in_another_program_or_has_a_partial_extension()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        var file = Path.Combine(m.SourcePath, "Devil.2005.Web-DL.mp4");
        Touch(file);
        Touch(Path.Combine(m.SourcePath, "Pack", "Movie.2019.mkv"));
        Touch(Path.Combine(m.SourcePath, "Pack", "Movie.2019.Extras.mkv.part"));

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(0, await ScanUntilStableAsync());
        }
        Assert.Equal(1, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        Assert.Equal(new[] { "Pack/", "Pack/Movie.2019.Extras.mkv.part", "Pack/Movie.2019.mkv" }, Tree("downloads"));
    }

    [Fact]
    public async Task Inactive_sources_and_the_master_switch_are_respected()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        Touch(Path.Combine(m.SourcePath, "Devil.2005.Web-DL.mp4"));

        m.Active = false;
        Assert.Equal(0, await ScanUntilStableAsync());
        m.Active = true;
        _config.MonitoringEnabled = false;
        Assert.Equal(0, await ScanUntilStableAsync());
        Assert.Empty(Tree("movies"));

        _config.MonitoringEnabled = true;
        Assert.Equal(1, await ScanUntilStableAsync());
    }

    [Fact]
    public async Task Each_action_can_be_disabled()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        m.Actions = new ActionSettings { NameCleanup = false, MoveIntoFolder = false, RemoveUnwantedFiles = false, SubtitleCleanup = false, TrackNameCleanup = false, SetDefaultAudio = false };
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.mkv"));
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.nfo"));
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.srt"), "1\n00:00:01,000 --> 00:00:02,000\nwww.tamilmv.us\n");

        await ScanUntilStableAsync();

        Assert.Equal(new[] { "tamilmv.us - Don.2003.Web-DL.mkv", "tamilmv.us - Don.2003.Web-DL.nfo", "tamilmv.us - Don.2003.Web-DL.srt" }, Tree("movies"));
        Assert.Contains("www.tamilmv.us", File.ReadAllText(Path.Combine(m.DestinationPath, "tamilmv.us - Don.2003.Web-DL.srt")));
    }

    [Fact]
    public async Task Without_a_destination_items_are_cleaned_in_place_exactly_once()
    {
        var m = AddMapping(MediaKind.Movie, "downloads");
        Touch(Path.Combine(m.SourcePath, "tamilmv.us - Don.2003.Web-DL.mkv"));
        Touch(Path.Combine(m.SourcePath, "www.site.com - Old.Movie.1999", "www.site.com - Old.Movie.1999.mkv"));
        _tools.Json = """{ "global_tags": [ { "num_entries": 1 } ], "tracks": [] }""";

        Assert.Equal(2, await ScanUntilStableAsync());
        Assert.Equal(new[]
        {
            "Don 2003 Web-DL/", "Don 2003 Web-DL/Don 2003 Web-DL.mkv",
            "Old Movie 1999/", "Old Movie 1999/Old Movie 1999.mkv",
        }, Tree("downloads"));
        Assert.Equal(2, _tools.PropEditCalls.Count);

        Assert.Equal(0, await ScanUntilStableAsync());
        Assert.Equal(0, await ScanUntilStableAsync());
        Assert.Equal(2, _tools.PropEditCalls.Count);
    }

    [Fact]
    public async Task A_failed_track_cleanup_leaves_the_file_in_place_and_retries_later()
    {
        var m = AddMapping(MediaKind.Movie, "downloads", "movies");
        Touch(Path.Combine(m.SourcePath, "Devil.2005.Web-DL.mkv"));
        _tools.Json = """{ "global_tags": [ { "num_entries": 1 } ], "tracks": [] }""";
        _tools.PropEditExitCode = 2;

        Assert.Equal(0, await ScanUntilStableAsync());
        Assert.Equal(new[] { "Devil.2005.Web-DL.mkv" }, Tree("downloads"));
        Assert.Contains(_log.Lines, l => l.StartsWith("Error") && l.Contains("mkvpropedit failed"));

        _tools.PropEditExitCode = 0;
        Assert.Equal(0, await _engine.ScanOnceAsync(_config, CancellationToken.None)); // still backing off
        _now = _now.AddMinutes(2);
        Assert.Equal(1, await _engine.ScanOnceAsync(_config, CancellationToken.None));
        Assert.Empty(Tree("downloads"));
    }

    [Fact]
    public async Task A_destination_inside_another_source_is_not_treated_as_a_download()
    {
        var movies = AddMapping(MediaKind.Movie, "share");
        movies.DestinationPath = Dir(Path.Combine("share", "Sorted Movies"));
        var problems = _config.Validate();
        Assert.Empty(problems);
        Touch(Path.Combine(movies.DestinationPath, "Old 1999", "Old 1999.mkv"));
        Touch(Path.Combine(movies.SourcePath, "Devil.2005.Web-DL.mp4"));

        Assert.Equal(1, await ScanUntilStableAsync());
        Assert.Equal(new[]
        {
            "Sorted Movies/", "Sorted Movies/Devil 2005 Web-DL/", "Sorted Movies/Devil 2005 Web-DL/Devil 2005 Web-DL.mp4",
            "Sorted Movies/Old 1999/", "Sorted Movies/Old 1999/Old 1999.mkv",
        }, Tree("share"));
    }

    [Fact]
    public void Config_round_trips_and_validates()
    {
        var file = Path.Combine(_root, "cfg", "config.json");
        var config = new AppConfig { UnwantedExtensions = new() { "PNG", ".txt", " nfo " } };
        config.Mappings.Add(new MonitorMapping { Kind = MediaKind.TvShow, SourcePath = @"\\nas\downloads\tv\", DestinationPath = "\"\\\\nas\\media\\TV\"" });
        config.Mappings.Add(new MonitorMapping { SourcePath = @"\\nas\media\TV\incoming", DestinationPath = @"\\nas\media\TV" });
        ConfigStore.Save(config, file);

        var loaded = ConfigStore.Load(file);
        Assert.Equal(new[] { ".png", ".txt", ".nfo" }, loaded.UnwantedExtensions);
        Assert.Equal(@"\\nas\downloads\tv", loaded.Mappings[0].SourcePath);
        Assert.Equal(@"\\nas\media\TV", loaded.Mappings[0].DestinationPath);
        Assert.Equal(MediaKind.TvShow, loaded.Mappings[0].Kind);
        Assert.False(loaded.Mappings[0].Actions.MoveIntoFolder);
        Assert.Contains("\"kind\": \"TvShow\"", File.ReadAllText(file));
        Assert.Empty(loaded.Validate());

        loaded.Mappings[1].SourcePath = loaded.Mappings[0].SourcePath;
        Assert.Single(loaded.Validate());

        loaded.Mappings[1].SourcePath = "relative";
        loaded.Mappings[1].DestinationPath = "relative";
        loaded.Normalize();
        Assert.Equal("", loaded.Mappings[1].DestinationPath);
    }
}

public sealed class ManualCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mc-manual").FullName;
    private readonly ListLogger _log = new();
    private readonly FakeMkvTools _tools = new() { Json = """{ "global_tags": [ { "num_entries": 1 } ], "tracks": [] }""" };

    public void Dispose() => Directory.Delete(_root, true);

    private void Touch(string relative, string content = "data")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string[] Tree() => Directory
        .EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/') + (Directory.Exists(p) ? "/" : ""))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    private void Seed()
    {
        Touch("www.site.com - Old.Movie.1999/www.site.com - Old.Movie.1999.mkv");
        Touch("www.site.com - Old.Movie.1999/www.site.com - Old.Movie.1999.srt", "1\n00:00:01,000 --> 00:00:02,000\nwww.site.com\n");
        Touch("www.site.com - Old.Movie.1999/info.nfo");
        Touch("tamilmv.us - Don.2003.Web-DL.mp4");
    }

    private Task<ManualCleanupResult> Run(ActionSettings actions, bool preview = false, MediaKind kind = MediaKind.Movie) =>
        ManualCleanup.RunAsync(new AppConfig(), new ManualCleanupOptions { Folder = _root, Kind = kind, Actions = actions, Preview = preview },
            _tools, _log, CancellationToken.None);

    [Fact]
    public async Task Nothing_is_selected_by_default_and_then_nothing_changes()
    {
        Seed();
        var before = Tree();
        var options = new ManualCleanupOptions { Folder = _root, Actions = ManualCleanupOptions.NoActions() };
        Assert.False(options.HasAnyAction);

        var result = await Run(ManualCleanupOptions.NoActions());

        Assert.Equal(2, result.Processed);
        Assert.Equal(before, Tree());
        Assert.Empty(_tools.PropEditCalls);
        Assert.Contains("www.site.com", File.ReadAllText(Path.Combine(_root, "www.site.com - Old.Movie.1999", "www.site.com - Old.Movie.1999.srt")));
    }

    [Fact]
    public async Task Only_the_selected_actions_run()
    {
        Seed();
        var actions = ManualCleanupOptions.NoActions();
        actions.NameCleanup = true;

        var result = await Run(actions);

        Assert.Equal(new ManualCleanupResult(2, 0, 0, false), result);
        Assert.Equal(new[]
        {
            "Don 2003 Web-DL.mp4",
            "Old Movie 1999/", "Old Movie 1999/Old Movie 1999.mkv", "Old Movie 1999/Old Movie 1999.srt", "Old Movie 1999/info.nfo",
        }, Tree());
        Assert.Empty(_tools.PropEditCalls);
        Assert.Contains("www.site.com", File.ReadAllText(Path.Combine(_root, "Old Movie 1999", "Old Movie 1999.srt")));
    }

    [Fact]
    public async Task Preview_reports_every_step_but_changes_nothing()
    {
        Seed();
        var before = Tree();
        var all = new ActionSettings();

        var result = await Run(all, preview: true);

        Assert.Equal(2, result.Processed);
        Assert.Equal(before, Tree());
        Assert.Empty(_tools.PropEditCalls);
        Assert.Contains("www.site.com", File.ReadAllText(Path.Combine(_root, "www.site.com - Old.Movie.1999", "www.site.com - Old.Movie.1999.srt")));
        Assert.Contains(_log.Lines, l => l.Contains("Removed tags"));
        Assert.Contains(_log.Lines, l => l.Contains("Removed unwanted file: info.nfo"));
        Assert.Contains(_log.Lines, l => l.Contains("Removed 1 website line(s)"));
        Assert.Contains(_log.Lines, l => l.Contains("Renamed 'www.site.com - Old.Movie.1999.mkv' -> 'Old Movie 1999.mkv'"));
        Assert.Contains(_log.Lines, l => l.Contains("Moved folder to:") && l.Contains("Old Movie 1999"));
        Assert.Contains(_log.Lines, l => l.Contains("Moved to:") && l.Contains(Path.Combine("Don 2003 Web-DL", "Don 2003 Web-DL.mp4")));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("Renamed 'info.nfo'"));

        // The real run then does exactly what the preview said.
        await Run(all);
        Assert.Equal(new[]
        {
            "Don 2003 Web-DL/", "Don 2003 Web-DL/Don 2003 Web-DL.mp4",
            "Old Movie 1999/", "Old Movie 1999/Old Movie 1999.mkv", "Old Movie 1999/Old Movie 1999.srt",
        }, Tree());
    }

    [Fact]
    public async Task A_failure_on_one_item_does_not_stop_the_rest()
    {
        Seed();
        Touch("Bad.Movie.2001.mkv");
        _tools.PropEditExitCode = 2;
        var actions = ManualCleanupOptions.NoActions();
        actions.TrackNameCleanup = true;
        actions.NameCleanup = true;

        var result = await Run(actions, kind: MediaKind.TvShow);

        Assert.Equal(1, result.Processed);   // the .mp4, which has no MKV header to edit
        Assert.Equal(2, result.Failed);
        Assert.Contains("Don 2003 Web-DL.mp4", Tree());
        Assert.Contains("Bad.Movie.2001.mkv", Tree());
    }
}
