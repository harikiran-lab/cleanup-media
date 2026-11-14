using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Mkv;
using MediaCleanup.Core.Monitoring;
using MediaCleanup.Core.Naming;
using Microsoft.Extensions.Logging;

namespace MediaCleanup.Core.Processing;

/// <summary>One download waiting in a source folder.</summary>
public sealed class SourceItem
{
    public required string Key { get; init; }
    public required bool IsDirectory { get; init; }
    public IReadOnlyList<string> Companions { get; init; } = Array.Empty<string>();

    public ItemSnapshot Capture() => IsDirectory
        ? ItemSnapshot.CaptureDirectory(Key)
        : ItemSnapshot.Capture(Companions.Prepend(Key));
}

/// <summary>
/// Scans the configured sources, waits until each download has finished copying, then hands it to
/// <see cref="ItemProcessor"/>. Call <see cref="ScanOnceAsync"/> on a timer.
/// </summary>
public sealed class MonitorEngine
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan RepeatWarningAfter = TimeSpan.FromMinutes(15);
    private static readonly string[] IgnoredFolderNames = { "$RECYCLE.BIN", "System Volume Information", "#recycle", "@eaDir", "lost+found" };

    private readonly ILogger _log;
    private readonly ProcessedLedger _ledger;
    private readonly Func<AppConfig, IMkvToolRunner> _toolFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly StabilityTracker _tracker = new();
    private readonly Dictionary<string, (int Attempts, DateTime NextRetry)> _failures = new(PathUtil.Comparer);
    private readonly Dictionary<string, DateTime> _lastWarned = new();
    private readonly HashSet<string> _announced = new(PathUtil.Comparer);

    public MonitorEngine(
        ILogger logger,
        ProcessedLedger? ledger = null,
        Func<AppConfig, IMkvToolRunner>? toolFactory = null,
        Func<DateTime>? utcNow = null)
    {
        _log = logger;
        _ledger = ledger ?? new ProcessedLedger();
        _toolFactory = toolFactory ?? (config => new MkvToolRunner(config.MkvToolNixPath));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Scans every active source once. Returns how many downloads were processed.</summary>
    public async Task<int> ScanOnceAsync(AppConfig config, CancellationToken ct)
    {
        if (!config.MonitoringEnabled) return 0;

        var processed = 0;
        IMkvToolRunner? tools = null;
        foreach (var mapping in config.Mappings.Where(m => m.Active))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!IsUsable(mapping)) continue;
                tools ??= _toolFactory(config);
                processed += await ScanMappingAsync(config, mapping, tools, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WarnThrottled("scan:" + mapping.Id, "Could not scan '{Source}': {Error}", mapping.SourcePath, ex.Message);
            }
        }
        return processed;
    }

    private bool IsUsable(MonitorMapping mapping)
    {
        if (mapping.SourcePath.Length == 0) return false;
        if (!Directory.Exists(mapping.SourcePath))
        {
            WarnThrottled("source:" + mapping.Id, "Source folder is not reachable: {Source}. {Hint}", mapping.SourcePath,
                "If it is a network share, make sure the service runs under an account that can open it.");
            return false;
        }
        if (mapping.DestinationPath.Length > 0 && !Directory.Exists(mapping.DestinationPath))
        {
            try
            {
                Directory.CreateDirectory(mapping.DestinationPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WarnThrottled("destination:" + mapping.Id, "Destination folder is not reachable: {Destination} ({Error})",
                    mapping.DestinationPath, ex.Message);
                return false;
            }
        }
        return true;
    }

    private async Task<int> ScanMappingAsync(AppConfig config, MonitorMapping mapping, IMkvToolRunner tools, CancellationToken ct)
    {
        var items = Discover(mapping.SourcePath, ProtectedPaths(config, mapping));
        var liveKeys = new HashSet<string>(items.Select(i => i.Key), PathUtil.Comparer);
        _tracker.Prune(mapping.SourcePath, liveKeys);
        _ledger.Prune(mapping.SourcePath, liveKeys);
        foreach (var gone in _failures.Keys.Where(k => PathUtil.IsSameOrInside(k, mapping.SourcePath) && !liveKeys.Contains(k)).ToList())
            _failures.Remove(gone);
        _announced.RemoveWhere(k => PathUtil.IsSameOrInside(k, mapping.SourcePath) && !liveKeys.Contains(k));

        var window = TimeSpan.FromSeconds(config.StableSeconds);
        var inPlace = mapping.DestinationPath.Length == 0;
        var processed = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var now = _utcNow();

            ItemSnapshot snapshot;
            try
            {
                snapshot = item.Capture();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // The item is changing under us; look again on the next scan.
            }

            if (inPlace && _ledger.IsProcessed(item.Key, snapshot.Signature)) continue;
            if (!snapshot.HasMedia && !snapshot.HasIncompleteFile) continue;
            if (_failures.TryGetValue(item.Key, out var failure) && now < failure.NextRetry) continue;

            if (_announced.Add(item.Key))
                _log.LogInformation("Found '{Name}' in {Source}; waiting until it has finished copying.", Path.GetFileName(item.Key), mapping.SourcePath);

            var stable = _tracker.IsStable(item.Key, snapshot.Signature, window, now);
            if (!stable || !snapshot.HasMedia || snapshot.HasIncompleteFile || snapshot.HasEmptyMediaFile) continue;

            var locked = FileReadiness.FirstLockedFile(snapshot.Files);
            if (locked != null)
            {
                _log.LogDebug("'{File}' is still in use by another program; waiting.", locked);
                continue;
            }

            try
            {
                var processor = new ItemProcessor(config, mapping, tools, _log);
                var final = item.IsDirectory
                    ? await processor.ProcessFolderAsync(item.Key, ct).ConfigureAwait(false)
                    : await processor.ProcessLooseFileAsync(item.Key, item.Companions, ct).ConfigureAwait(false);

                processed++;
                _failures.Remove(item.Key);
                _tracker.Forget(item.Key);
                _log.LogInformation("Finished: {Final}", final);

                if (inPlace) MarkProcessedInPlace(mapping, config, final);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var attempts = (_failures.TryGetValue(item.Key, out var previous) ? previous.Attempts : 0) + 1;
                var delay = TimeSpan.FromMinutes(Math.Min(MaxBackoff.TotalMinutes, Math.Pow(2, attempts - 1)));
                _failures[item.Key] = (attempts, now + delay);
                _log.LogError("Failed to process '{Item}' (attempt {Attempt}): {Error} It stays where it is and is retried in {Minutes:0} minute(s).",
                    item.Key, attempts, ex.Message, delay.TotalMinutes);
            }
        }
        return processed;
    }

    /// <summary>Records the cleaned item so that the next scan does not treat it as a new download.</summary>
    private void MarkProcessedInPlace(MonitorMapping mapping, AppConfig config, string finalPath)
    {
        try
        {
            var item = Discover(mapping.SourcePath, ProtectedPaths(config, mapping))
                .FirstOrDefault(i => PathUtil.PathEquals(i.Key, finalPath));
            if (item != null) _ledger.Mark(item.Key, item.Capture().Signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not record '{Item}' as processed: {Error}", finalPath, ex.Message);
        }
    }

    /// <summary>Folders of other entries that must never be treated as a download of this source.</summary>
    private static List<string> ProtectedPaths(AppConfig config, MonitorMapping mapping)
    {
        var paths = new List<string>();
        foreach (var other in config.Mappings)
        {
            if (other.DestinationPath.Length > 0) paths.Add(other.DestinationPath);
            if (!ReferenceEquals(other, mapping) && other.SourcePath.Length > 0) paths.Add(other.SourcePath);
        }
        return paths;
    }

    /// <summary>
    /// Lists the downloads in a source folder: every sub-folder, and every loose media file together
    /// with the non-media files that share its name.
    /// </summary>
    public static List<SourceItem> Discover(string source, IReadOnlyCollection<string>? protectedPaths = null)
    {
        var items = new List<SourceItem>();
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || IgnoredFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (protectedPaths != null && protectedPaths.Any(p => PathUtil.IsSameOrInside(p, directory))) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.System) != 0) continue;
            }
            catch (IOException)
            {
                continue;
            }
            items.Add(new SourceItem { Key = directory, IsDirectory = true });
        }

        var files = Directory.EnumerateFiles(source).ToList();
        var media = files.Where(MediaTypes.IsMedia).ToList();
        var companions = media.ToDictionary(m => m, _ => new List<string>(), PathUtil.Comparer);
        foreach (var file in files.Where(f => !MediaTypes.IsMedia(f)))
        {
            // A companion belongs to the media file with the longest matching name, so "Don.2.srt"
            // goes with "Don.2.mkv" rather than "Don.mkv".
            var name = Path.GetFileName(file);
            var owner = media
                .Where(m => name.StartsWith(Path.GetFileNameWithoutExtension(m) + ".", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => Path.GetFileNameWithoutExtension(m).Length)
                .FirstOrDefault();
            if (owner != null) companions[owner].Add(file);
        }
        foreach (var file in media)
            items.Add(new SourceItem { Key = file, IsDirectory = false, Companions = companions[file] });

        return items;
    }

    private void WarnThrottled(string key, string message, params object?[] args)
    {
        var now = _utcNow();
        if (_lastWarned.TryGetValue(key, out var last) && now - last < RepeatWarningAfter) return;
        _lastWarned[key] = now;
        _log.LogWarning(message, args);
    }
}
