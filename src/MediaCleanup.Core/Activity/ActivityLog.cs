using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaCleanup.Core.Activity;

public enum ActivityKind
{
    Service,
    Waiting,
    Start,
    Rename,
    Tracks,
    Subtitle,
    Delete,
    Move,
    Done,
    Warning,
    Error,
}

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Message)
{
    public bool IsProblem => Kind is ActivityKind.Warning or ActivityKind.Error;
}

public enum ItemResult
{
    InProgress,
    Done,
    Failed,
    Interrupted,
}

/// <summary>Everything the service did to one download, from "Processing" to "Finished".</summary>
public sealed class ItemActivity
{
    public required DateTime Started { get; init; }

    /// <summary>The original file or folder name, as it was found in the source.</summary>
    public required string Name { get; init; }

    public required string SourcePath { get; init; }
    public ItemResult Result { get; set; } = ItemResult.InProgress;

    /// <summary>Where the item ended up, when it finished.</summary>
    public string? FinalPath { get; set; }

    public List<ActivityEntry> Steps { get; } = new();

    /// <summary>A one-line description such as "2 renamed, tracks cleaned, 1 file removed, moved".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var renamed = Steps.Count(s => s.Kind == ActivityKind.Rename);
            var removed = Steps.Count(s => s.Kind == ActivityKind.Delete);
            var subtitles = Steps.Count(s => s.Kind == ActivityKind.Subtitle);
            var moved = Steps.Count(s => s.Kind == ActivityKind.Move);
            if (renamed > 0) parts.Add($"{renamed} renamed");
            if (Steps.Any(s => s.Kind == ActivityKind.Tracks)) parts.Add("tracks cleaned");
            if (subtitles > 0) parts.Add(subtitles == 1 ? "1 subtitle cleaned" : $"{subtitles} subtitles cleaned");
            if (removed > 0) parts.Add(removed == 1 ? "1 file removed" : $"{removed} files removed");
            if (moved > 0) parts.Add(moved == 1 ? "moved" : $"{moved} files moved");
            var problems = Steps.Count(s => s.IsProblem);
            if (problems > 0) parts.Add(problems == 1 ? "1 problem" : $"{problems} problems");
            return parts.Count > 0 ? string.Join(", ", parts) : "nothing needed changing";
        }
    }
}

/// <summary>Reads what the service did from its daily log files, for display in the configuration app.</summary>
public static class ActivityLog
{
    private static readonly Regex LinePattern = new(
        @"^\uFEFF?(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(\w+)\] (.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the most recent entries, newest first. Reads only as many of the newest log files as
    /// needed, and never fails because of a file it cannot open.
    /// </summary>
    public static List<ActivityEntry> ReadRecent(string logDirectory, int maxEntries)
    {
        var result = new List<ActivityEntry>();
        if (maxEntries <= 0 || !Directory.Exists(logDirectory)) return result;

        // Names start with the date (mediacleanup-yyyyMMdd...), so name order is date order.
        var files = Directory.EnumerateFiles(logDirectory, "mediacleanup-*.log")
            .OrderByDescending(f => Path.GetFileName(f)[..Math.Min(Path.GetFileName(f).Length, "mediacleanup-yyyyMMdd".Length)], StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perDay = new List<ActivityEntry>();
        string? currentDay = null;
        foreach (var file in files)
        {
            var day = Path.GetFileName(file)[..Math.Min(Path.GetFileName(file).Length, "mediacleanup-yyyyMMdd".Length)];
            if (currentDay != null && day != currentDay)
            {
                Flush(perDay, result);
                if (result.Count >= maxEntries) break;
            }
            currentDay = day;
            perDay.AddRange(ParseFile(file));
        }
        Flush(perDay, result);

        return result.Count > maxEntries ? result.GetRange(0, maxEntries) : result;
    }

    /// <summary>
    /// Returns the most recently processed downloads, newest first, each with the steps done to it,
    /// plus the newest warning or error that does not belong to a download (such as an unreachable share).
    /// </summary>
    public static (List<ItemActivity> Items, ActivityEntry? LatestProblem) ReadRecentItems(string logDirectory, int maxItems, int maxEntries = 20000)
    {
        var entries = ReadRecent(logDirectory, maxEntries);
        entries.Reverse();
        var (items, loose) = GroupByItem(entries);
        items.Reverse();
        if (items.Count > maxItems) items.RemoveRange(maxItems, items.Count - maxItems);
        return (items, loose.LastOrDefault(e => e.IsProblem));
    }

    /// <summary>
    /// Groups entries (oldest first) into downloads. The service handles one download at a time, so
    /// every line between "Processing ..." and "Finished: ..." belongs to that download.
    /// </summary>
    public static (List<ItemActivity> Items, List<ActivityEntry> Loose) GroupByItem(IEnumerable<ActivityEntry> chronological)
    {
        var items = new List<ItemActivity>();
        var loose = new List<ActivityEntry>();
        ItemActivity? current = null;

        foreach (var entry in chronological)
        {
            if (entry.Kind == ActivityKind.Start)
            {
                if (current != null) current.Result = ItemResult.Interrupted;
                var path = entry.Message[(entry.Message.IndexOf(':') + 1)..].Trim();
                current = new ItemActivity { Started = entry.Time, SourcePath = path, Name = LastSegment(path) };
                items.Add(current);
                continue;
            }

            if (current == null)
            {
                loose.Add(entry);
                continue;
            }

            if (entry.Kind == ActivityKind.Done)
            {
                current.FinalPath = entry.Message[(entry.Message.IndexOf(':') + 1)..].Trim();
                current.Result = ItemResult.Done;
                current = null;
            }
            else if (entry.Kind == ActivityKind.Error && entry.Message.StartsWith("Failed to process", StringComparison.Ordinal))
            {
                current.Steps.Add(entry);
                current.Result = ItemResult.Failed;
                current = null;
            }
            else if (entry.Kind == ActivityKind.Service && entry.Message.StartsWith("Media Cleanup Service", StringComparison.Ordinal))
            {
                current.Result = ItemResult.Interrupted; // The service stopped or restarted mid-way.
                current = null;
                loose.Add(entry);
            }
            else
            {
                current.Steps.Add(entry);
            }
        }
        return (items, loose);
    }

    // Log paths are Windows paths, but split on both separators so this also works elsewhere.
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    // Entries of one day (possibly from two files written by different accounts) are merged by time.
    private static void Flush(List<ActivityEntry> perDay, List<ActivityEntry> result)
    {
        // A stable sort keeps the written order of lines that share the same second.
        var ordered = perDay.Select((entry, index) => (entry, index))
            .OrderByDescending(x => x.entry.Time)
            .ThenByDescending(x => x.index)
            .Select(x => x.entry);
        result.AddRange(ordered);
        perDay.Clear();
    }

    private static List<ActivityEntry> ParseFile(string file)
    {
        try
        {
            // The service has the file open for writing, so allow that while reading.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(ReadLines(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<ActivityEntry>();
        }
    }

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>Turns log lines into entries. Lines without a timestamp continue the previous entry.</summary>
    public static List<ActivityEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<ActivityEntry>();
        foreach (var line in lines)
        {
            var match = LinePattern.Match(line);
            if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                var message = match.Groups[3].Value.Trim();
                entries.Add(new ActivityEntry(time, Classify(match.Groups[2].Value, message), message));
            }
            else if (entries.Count > 0 && line.Trim().Length > 0)
            {
                var last = entries[^1];
                // Keep only the first continuation line (the exception message), not a whole stack trace.
                if (!last.Message.Contains('\n'))
                    entries[^1] = last with { Message = last.Message + "\n" + line.Trim() };
            }
        }
        return entries;
    }

    public static ActivityKind Classify(string level, string message)
    {
        switch (level.ToUpperInvariant())
        {
            case "ERROR":
            case "FATAL":
                return ActivityKind.Error;
            case "WARN":
                return ActivityKind.Warning;
        }

        if (message.StartsWith("Renamed", StringComparison.Ordinal)) return ActivityKind.Rename;
        if (message.StartsWith("Moved", StringComparison.Ordinal) || message.StartsWith("Also moved", StringComparison.Ordinal)) return ActivityKind.Move;
        if (message.StartsWith("Removed unwanted", StringComparison.Ordinal)) return ActivityKind.Delete;
        if (message.StartsWith("Removed", StringComparison.Ordinal) && message.Contains("subtitle", StringComparison.OrdinalIgnoreCase)) return ActivityKind.Subtitle;
        if (message.StartsWith('[')) return ActivityKind.Tracks;
        if (message.StartsWith("Processing", StringComparison.Ordinal)) return ActivityKind.Start;
        if (message.StartsWith("Finished", StringComparison.Ordinal)) return ActivityKind.Done;
        if (message.StartsWith("Found", StringComparison.Ordinal)) return ActivityKind.Waiting;
        return ActivityKind.Service;
    }
}
