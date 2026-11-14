using MediaCleanup.Core.Activity;

namespace MediaCleanup.Tests;

public class ActivityLogTests
{
    [Fact]
    public void Parses_and_classifies_service_log_lines()
    {
        var entries = ActivityLog.Parse(new[]
        {
            "\uFEFF2026-11-14 18:27:51 [INFO] Media Cleanup Service started. Configuration: C:\\x\\config.json",
            "2026-11-14 18:27:51 [INFO] Found 'a.mkv' in \\\\nas\\in; waiting until it has finished copying.",
            "2026-11-14 18:27:56 [INFO] Processing file: \\\\nas\\in\\a.mkv",
            "2026-11-14 18:27:56 [INFO]   [a.mkv] Title 'www.x.com - A' -> 'A'",
            "2026-11-14 18:27:56 [INFO]   Renamed 'a.b.srt' -> 'a b.srt'",
            "2026-11-14 18:27:56 [INFO]   Removed 2 website line(s) from subtitle a.srt",
            "2026-11-14 18:27:56 [INFO]   Removed unwanted file: a.nfo",
            "2026-11-14 18:27:57 [INFO]   Moved to: \\\\nas\\out\\A\\A.mkv",
            "2026-11-14 18:27:57 [INFO] Finished: \\\\nas\\out\\A",
            "2026-11-14 18:28:00 [WARN] Source folder is not reachable: \\\\nas\\in.",
            "2026-11-14 18:28:01 [ERROR] Unexpected error during a scan; the service keeps running.",
            "System.IO.IOException: The network path was not found.",
            "   at Some.Stack.Frame()",
        });

        Assert.Equal(new[]
        {
            ActivityKind.Service, ActivityKind.Waiting, ActivityKind.Start, ActivityKind.Tracks, ActivityKind.Rename,
            ActivityKind.Subtitle, ActivityKind.Delete, ActivityKind.Move, ActivityKind.Done, ActivityKind.Warning, ActivityKind.Error,
        }, entries.Select(e => e.Kind).ToArray());
        Assert.Equal("Renamed 'a.b.srt' -> 'a b.srt'", entries[4].Message);
        Assert.Equal(new DateTime(2026, 11, 14, 18, 27, 57), entries[8].Time);
        Assert.Equal("Unexpected error during a scan; the service keeps running.\nSystem.IO.IOException: The network path was not found.", entries[10].Message);
        Assert.True(entries[10].IsProblem);
    }

    [Fact]
    public void Reads_newest_first_across_days_and_stops_at_the_limit()
    {
        var dir = Directory.CreateTempSubdirectory("mc-log").FullName;
        try
        {
            File.WriteAllLines(Path.Combine(dir, "mediacleanup-20261112.log"), new[] { "2026-11-12 10:00:00 [INFO] old" });
            File.WriteAllLines(Path.Combine(dir, "mediacleanup-20261113.log"), new[] { "2026-11-13 10:00:00 [INFO] one", "2026-11-13 10:00:00 [INFO] two" });
            File.WriteAllLines(Path.Combine(dir, "mediacleanup-20261114.log"), new[] { "2026-11-14 09:00:00 [INFO] three", "2026-11-14 11:00:00 [INFO] five" });
            File.WriteAllLines(Path.Combine(dir, "mediacleanup-20261114-svc.log"), new[] { "2026-11-14 10:00:00 [INFO] four" });
            File.WriteAllText(Path.Combine(dir, "other.txt"), "ignored");

            // A file the service is still writing must be readable.
            using var writer = new FileStream(Path.Combine(dir, "mediacleanup-20261114.log"), FileMode.Open, FileAccess.Write, FileShare.Read);

            Assert.Equal(new[] { "five", "four", "three", "two", "one" },
                ActivityLog.ReadRecent(dir, 5).Select(e => e.Message).ToArray());
            Assert.Equal(new[] { "five", "four" }, ActivityLog.ReadRecent(dir, 2).Select(e => e.Message).ToArray());
            Assert.Equal(6, ActivityLog.ReadRecent(dir, 100).Count);
            Assert.Empty(ActivityLog.ReadRecent(Path.Combine(dir, "missing"), 10));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Groups_steps_by_download()
    {
        var entries = ActivityLog.Parse(new[]
        {
            "2026-11-14 18:00:00 [INFO] Media Cleanup Service started. Configuration: C:\\x\\config.json",
            "2026-11-14 18:00:01 [WARN] Source folder is not reachable: \\\\nas\\tv.",
            "2026-11-14 18:27:51 [INFO] Found 'tamilmv.us - Don.2003.Web-DL.mkv' in \\\\nas\\in; waiting until it has finished copying.",
            "2026-11-14 18:27:56 [INFO] Processing file: \\\\nas\\in\\tamilmv.us - Don.2003.Web-DL.mkv",
            "2026-11-14 18:27:56 [INFO]   [tamilmv.us - Don.2003.Web-DL.mkv] Removed tags",
            "2026-11-14 18:27:56 [INFO]   Removed unwanted file: tamilmv.us - Don.2003.Web-DL.nfo",
            "2026-11-14 18:27:57 [INFO]   Moved to: \\\\nas\\out\\Don 2003 Web-DL\\Don 2003 Web-DL.mkv",
            "2026-11-14 18:27:57 [INFO] Finished: \\\\nas\\out\\Don 2003 Web-DL",
            "2026-11-14 18:30:00 [INFO] Processing folder: \\\\nas\\in\\www.x.xyz - House 2020",
            "2026-11-14 18:30:01 [INFO]   Renamed 'www.x.xyz - House S01E01.mkv' -> 'House S01E01.mkv'",
            "2026-11-14 18:30:02 [ERROR] Failed to process '\\\\nas\\in\\www.x.xyz - House 2020' (attempt 1): mkvpropedit failed. It stays where it is and is retried in 1 minute(s).",
            "2026-11-14 18:31:00 [INFO] Processing file: \\\\nas\\in\\Clean 2001.mp4",
        });

        var (items, loose) = ActivityLog.GroupByItem(entries);

        Assert.Equal(3, items.Count);
        Assert.Equal("tamilmv.us - Don.2003.Web-DL.mkv", items[0].Name);
        Assert.Equal(ItemResult.Done, items[0].Result);
        Assert.Equal("\\\\nas\\out\\Don 2003 Web-DL", items[0].FinalPath);
        Assert.Equal("tracks cleaned, 1 file removed, moved", items[0].Summary);
        Assert.Equal(3, items[0].Steps.Count);

        Assert.Equal("www.x.xyz - House 2020", items[1].Name);
        Assert.Equal(ItemResult.Failed, items[1].Result);
        Assert.Equal("1 renamed, 1 problem", items[1].Summary);

        Assert.Equal(ItemResult.InProgress, items[2].Result);
        Assert.Equal("nothing needed changing", items[2].Summary);

        Assert.Equal(3, loose.Count);
        Assert.Equal(ActivityKind.Warning, loose.Last(e => e.IsProblem).Kind);
    }
}
