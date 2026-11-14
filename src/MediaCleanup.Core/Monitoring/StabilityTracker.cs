using System.Security.Cryptography;
using System.Text;
using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Naming;

namespace MediaCleanup.Core.Monitoring;

/// <summary>The files that make up one download: a folder tree, or a loose media file with its companions.</summary>
public sealed class ItemSnapshot
{
    public required IReadOnlyList<string> Files { get; init; }
    public required string Signature { get; init; }
    public bool HasIncompleteFile { get; init; }
    public bool HasEmptyMediaFile { get; init; }
    public bool HasMedia { get; init; }

    /// <summary>Builds a snapshot whose signature changes whenever a file is added, removed, grows or is rewritten.</summary>
    public static ItemSnapshot Capture(IEnumerable<string> files)
    {
        var list = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        bool incomplete = false, emptyMedia = false, hasMedia = false;
        foreach (var file in list)
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            sb.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            if (MediaTypes.IsIncomplete(file)) incomplete = true;
            if (MediaTypes.IsMedia(file))
            {
                hasMedia = true;
                if (info.Length == 0) emptyMedia = true;
            }
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new ItemSnapshot
        {
            Files = list,
            Signature = hash,
            HasIncompleteFile = incomplete,
            HasEmptyMediaFile = emptyMedia,
            HasMedia = hasMedia,
        };
    }

    public static ItemSnapshot CaptureDirectory(string directory) =>
        Capture(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
}

/// <summary>Remembers how long each item has stayed unchanged between scans.</summary>
public sealed class StabilityTracker
{
    private readonly Dictionary<string, (string Signature, DateTime Since)> _seen = new(PathUtil.Comparer);

    /// <summary>
    /// Records the current signature and returns true once it has been identical for at least
    /// <paramref name="window"/>.
    /// </summary>
    public bool IsStable(string key, string signature, TimeSpan window, DateTime utcNow)
    {
        if (_seen.TryGetValue(key, out var entry) && entry.Signature == signature)
            return utcNow - entry.Since >= window;

        _seen[key] = (signature, utcNow);
        return window <= TimeSpan.Zero;
    }

    public void Forget(string key) => _seen.Remove(key);

    /// <summary>Drops entries for items that no longer exist so the table does not grow forever.</summary>
    public void Prune(string keyPrefix, ISet<string> liveKeys)
    {
        var stale = _seen.Keys
            .Where(k => PathUtil.IsSameOrInside(k, keyPrefix) && !liveKeys.Contains(k))
            .ToList();
        foreach (var key in stale) _seen.Remove(key);
    }
}

public static class FileReadiness
{
    /// <summary>
    /// True when nothing else holds the file open. Download clients and copy tools keep a file open
    /// while they write it, so an exclusive open succeeding is the final proof that the copy is done.
    /// </summary>
    public static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? FirstLockedFile(IEnumerable<string> files) =>
        files.FirstOrDefault(f => File.Exists(f) && !CanOpenExclusively(f));
}
