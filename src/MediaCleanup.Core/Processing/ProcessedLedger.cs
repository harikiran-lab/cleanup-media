using System.Text.Json;
using MediaCleanup.Core.Configuration;

namespace MediaCleanup.Core.Processing;

/// <summary>
/// Remembers items that were cleaned in place (sources without a destination), so they are not
/// processed again on every scan. An item is processed again only if its files change.
/// </summary>
public sealed class ProcessedLedger
{
    private readonly string? _file;
    private Dictionary<string, string> _entries = new(PathUtil.Comparer);

    /// <param name="file">Where to persist the ledger, or null to keep it in memory only.</param>
    public ProcessedLedger(string? file = null)
    {
        _file = file;
        if (file == null || !File.Exists(file)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (loaded != null) _entries = new Dictionary<string, string>(loaded, PathUtil.Comparer);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged ledger only means items are checked once more; cleanup steps are repeatable.
        }
    }

    public bool IsProcessed(string key, string signature) =>
        _entries.TryGetValue(key, out var known) && known == signature;

    public void Mark(string key, string signature)
    {
        _entries[key] = signature;
        Save();
    }

    /// <summary>Forgets entries under <paramref name="root"/> whose item no longer exists.</summary>
    public void Prune(string root, ISet<string> liveKeys)
    {
        var stale = _entries.Keys
            .Where(k => PathUtil.IsSameOrInside(k, root) && !liveKeys.Contains(k))
            .ToList();
        if (stale.Count == 0) return;
        foreach (var key in stale) _entries.Remove(key);
        Save();
    }

    private void Save()
    {
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the in-memory copy still protects against reprocessing until the next restart.
        }
    }
}
