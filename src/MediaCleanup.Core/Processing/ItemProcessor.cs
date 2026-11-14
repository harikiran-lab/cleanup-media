using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Mkv;
using MediaCleanup.Core.Naming;
using MediaCleanup.Core.Subtitles;
using Microsoft.Extensions.Logging;

namespace MediaCleanup.Core.Processing;

/// <summary>Runs the enabled cleanup steps on one finished download and moves it to its destination.</summary>
public sealed class ItemProcessor
{
    private readonly AppConfig _config;
    private readonly MonitorMapping _mapping;
    private readonly ILogger _log;
    private readonly NameCleaner _names;
    private readonly SubtitleCleaner _subtitles;
    private readonly MkvCleaner _mkv;
    private readonly HashSet<string> _unwanted;
    private readonly bool _preview;

    /// <param name="preview">True to log every step that would be taken without changing anything on disk.</param>
    public ItemProcessor(AppConfig config, MonitorMapping mapping, IMkvToolRunner mkvTools, ILogger logger, bool preview = false)
    {
        _preview = preview;
        _config = config;
        _mapping = mapping;
        _log = logger;
        _names = new NameCleaner(config.CustomRemovalTerms);
        _subtitles = new SubtitleCleaner(config.CustomRemovalTerms);
        _mkv = new MkvCleaner(mkvTools, config.CustomRemovalTerms);
        _unwanted = new HashSet<string>(config.UnwantedExtensions, StringComparer.OrdinalIgnoreCase);
    }

    private ActionSettings Actions => _mapping.Actions;
    private bool RenameSubtitles => Actions.NameCleanup || Actions.SubtitleCleanup;
    private string TargetRoot => _mapping.DestinationPath.Length > 0 ? _mapping.DestinationPath : _mapping.SourcePath;

    /// <summary>Processes a download that arrived as a folder. Returns the folder's final location.</summary>
    public async Task<string> ProcessFolderAsync(string folder, CancellationToken ct)
    {
        _log.LogInformation("Processing folder: {Folder}", folder);

        foreach (var file in AllFiles(folder).Where(MediaTypes.IsMkv))
            await CleanTracksAsync(file, ct).ConfigureAwait(false);

        if (Actions.RemoveUnwantedFiles)
        {
            foreach (var file in AllFiles(folder).Where(IsUnwanted))
                DeleteUnwanted(file);
            if (!_preview) FileOps.DeleteEmptyDirectories(folder, includeRoot: false);
        }

        if (Actions.SubtitleCleanup)
        {
            foreach (var file in AllFiles(folder).Where(SubtitleCleaner.CanClean))
                CleanSubtitleContent(file);
        }

        foreach (var file in AllFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var cleaned = CleanedFileName(name);
            if (cleaned == name) continue;
            if (_preview && Actions.RemoveUnwantedFiles && IsUnwanted(file)) continue; // Already reported as removed.
            var moved = _preview ? cleaned : FileOps.MoveFile(file, Path.Combine(Path.GetDirectoryName(file)!, cleaned));
            _log.LogInformation("  Renamed '{Old}' -> '{New}'", name, Path.GetFileName(moved));
        }

        if (Actions.NameCleanup)
            RenameSubfolders(folder);

        var folderName = Path.GetFileName(folder);
        var finalName = Actions.NameCleanup ? _names.CleanFolderName(folderName) : folderName;
        var target = Path.Combine(TargetRoot, finalName);
        var final = _preview ? target : FileOps.MoveDirectory(folder, target);
        if (!string.Equals(final, folder, StringComparison.Ordinal))
            _log.LogInformation("  Moved folder to: {Target}", final);
        return final;
    }

    /// <summary>
    /// Processes a media file that sits directly in the source folder, together with files that share
    /// its name (subtitles and so on). Returns the final location: a folder when the file was given its
    /// own folder, otherwise the media file itself.
    /// </summary>
    public async Task<string> ProcessLooseFileAsync(string mediaFile, IReadOnlyList<string> companions, CancellationToken ct)
    {
        _log.LogInformation("Processing file: {File}", mediaFile);

        if (MediaTypes.IsMkv(mediaFile))
            await CleanTracksAsync(mediaFile, ct).ConfigureAwait(false);

        var kept = new List<string>();
        foreach (var companion in companions)
        {
            if (Actions.RemoveUnwantedFiles && IsUnwanted(companion)) DeleteUnwanted(companion);
            else kept.Add(companion);
        }

        if (Actions.SubtitleCleanup)
        {
            foreach (var file in kept.Where(SubtitleCleaner.CanClean))
                CleanSubtitleContent(file);
        }

        var mediaName = Path.GetFileName(mediaFile);
        var newMediaName = CleanedFileName(mediaName);
        var wrap = _mapping.Kind == MediaKind.Movie && Actions.MoveIntoFolder;
        var targetDirectory = wrap
            ? Path.Combine(TargetRoot, Path.GetFileNameWithoutExtension(newMediaName))
            : TargetRoot;

        ct.ThrowIfCancellationRequested();
        var finalMedia = Move(mediaFile, Path.Combine(targetDirectory, newMediaName));
        if (!string.Equals(finalMedia, mediaFile, StringComparison.Ordinal))
            _log.LogInformation("  Moved to: {Target}", finalMedia);

        foreach (var companion in kept)
        {
            var name = Path.GetFileName(companion);
            var moved = Move(companion, Path.Combine(targetDirectory, CleanedFileName(name)));
            if (!string.Equals(moved, companion, StringComparison.Ordinal))
                _log.LogInformation("  Also moved: {Name} -> {Target}", name, moved);
        }

        return wrap ? targetDirectory : finalMedia;
    }

    private string Move(string source, string destination) => _preview ? destination : FileOps.MoveFile(source, destination);

    private string CleanedFileName(string name)
    {
        var rename = MediaTypes.IsSubtitle(name) ? RenameSubtitles : Actions.NameCleanup;
        return rename ? _names.CleanFileName(name) : name;
    }

    private async Task CleanTracksAsync(string file, CancellationToken ct)
    {
        if (!Actions.TrackNameCleanup && !Actions.SetDefaultAudio) return;
        if (!_mkv.IsAvailable)
        {
            _log.LogWarning("  MKVToolNix was not found, so track cleanup is skipped for {File}. Set its folder in Settings.", Path.GetFileName(file));
            return;
        }

        var result = await _mkv.CleanAsync(file, Actions.TrackNameCleanup, Actions.SetDefaultAudio, ct, apply: !_preview).ConfigureAwait(false);
        foreach (var note in result.Notes)
            _log.LogInformation("  [{File}] {Note}", Path.GetFileName(file), note);
    }

    private void CleanSubtitleContent(string file)
    {
        try
        {
            var removed = _subtitles.CleanFile(file, write: !_preview);
            if (removed > 0)
                _log.LogInformation("  Removed {Count} website line(s) from subtitle {File}", removed, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("  Could not clean subtitle {File}: {Error}", Path.GetFileName(file), ex.Message);
        }
    }

    private void RenameSubfolders(string root)
    {
        // Deepest folders first, so renaming a parent never invalidates a path still to be handled.
        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            var name = Path.GetFileName(folder);
            var cleaned = _names.CleanFolderName(name);
            if (cleaned == name) continue;
            if (!_preview) FileOps.MoveDirectory(folder, Path.Combine(Path.GetDirectoryName(folder)!, cleaned));
            _log.LogInformation("  Renamed folder '{Old}' -> '{New}'", name, cleaned);
        }
    }

    private bool IsUnwanted(string file) =>
        _unwanted.Contains(Path.GetExtension(file)) && !MediaTypes.IsMedia(file);

    private void DeleteUnwanted(string file)
    {
        if (!_preview) FileOps.DeleteFile(file);
        _log.LogInformation("  Removed unwanted file: {File}", Path.GetFileName(file));
    }

    private static List<string> AllFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
}
