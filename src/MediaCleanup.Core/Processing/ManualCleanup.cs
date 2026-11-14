using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Mkv;
using MediaCleanup.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace MediaCleanup.Core.Processing;

public sealed class ManualCleanupOptions
{
    /// <summary>The folder to clean. Every sub-folder and every loose media file in it is one item.</summary>
    public required string Folder { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Movie;

    /// <summary>The steps to run. Callers start from <see cref="NoActions"/> so nothing is on by default.</summary>
    public required ActionSettings Actions { get; init; }

    /// <summary>True to only report what would be done.</summary>
    public bool Preview { get; init; }

    public static ActionSettings NoActions() => new()
    {
        NameCleanup = false,
        TrackNameCleanup = false,
        SetDefaultAudio = false,
        SubtitleCleanup = false,
        MoveIntoFolder = false,
        RemoveUnwantedFiles = false,
    };

    public bool HasAnyAction =>
        Actions.NameCleanup || Actions.TrackNameCleanup || Actions.SetDefaultAudio || Actions.SubtitleCleanup
        || Actions.RemoveUnwantedFiles || (Kind == MediaKind.Movie && Actions.MoveIntoFolder);
}

public sealed record ManualCleanupResult(int Processed, int Skipped, int Failed, bool Cancelled);

/// <summary>
/// Runs the chosen cleanup steps once over an existing folder, in place, without waiting for files
/// to settle and without moving anything to another location. Used to tidy an existing library.
/// </summary>
public static class ManualCleanup
{
    public static async Task<ManualCleanupResult> RunAsync(
        AppConfig config, ManualCleanupOptions options, IMkvToolRunner mkvTools, ILogger log, CancellationToken ct)
    {
        var folder = PathUtil.NormalizeDir(options.Folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder was not found: {folder}");

        var actions = options.Actions.Clone();
        if (options.Kind == MediaKind.TvShow) actions.MoveIntoFolder = false;
        var mapping = new MonitorMapping { Name = "Manual cleanup", Kind = options.Kind, SourcePath = folder, DestinationPath = "", Actions = actions };
        var processor = new ItemProcessor(config, mapping, mkvTools, log, options.Preview);

        int processed = 0, skipped = 0, failed = 0;
        var items = MonitorEngine.Discover(folder).OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
        log.LogInformation("{Mode} {Count} item(s) in {Folder}", options.Preview ? "Previewing" : "Cleaning", items.Count, folder);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return new ManualCleanupResult(processed, skipped, failed, true);
            try
            {
                var snapshot = item.Capture();
                if (!snapshot.HasMedia)
                {
                    skipped++;
                    continue;
                }
                if (snapshot.HasIncompleteFile)
                {
                    skipped++;
                    log.LogWarning("Skipped '{Name}': it contains a file that is still downloading.", Path.GetFileName(item.Key));
                    continue;
                }
                if (!options.Preview && FileReadiness.FirstLockedFile(snapshot.Files) is { } locked)
                {
                    skipped++;
                    log.LogWarning("Skipped '{Name}': '{File}' is in use by another program.", Path.GetFileName(item.Key), Path.GetFileName(locked));
                    continue;
                }

                var final = item.IsDirectory
                    ? await processor.ProcessFolderAsync(item.Key, ct).ConfigureAwait(false)
                    : await processor.ProcessLooseFileAsync(item.Key, item.Companions, ct).ConfigureAwait(false);
                processed++;
                log.LogInformation("Finished: {Final}", final);
            }
            catch (OperationCanceledException)
            {
                return new ManualCleanupResult(processed, skipped, failed, true);
            }
            catch (Exception ex)
            {
                failed++;
                log.LogError("Failed to process '{Item}': {Error}", item.Key, ex.Message);
            }
        }
        return new ManualCleanupResult(processed, skipped, failed, false);
    }
}
