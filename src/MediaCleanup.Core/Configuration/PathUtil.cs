namespace MediaCleanup.Core.Configuration;

public static class PathUtil
{
    private static readonly char[] Separators = { '\\', '/' };

    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Trims quotes, whitespace and trailing separators (but keeps roots such as C:\ intact).</summary>
    public static string NormalizeDir(string? path)
    {
        var p = (path ?? "").Trim().Trim('"', '\'').Trim();
        if (p.Length == 0) return "";
        var trimmed = p.TrimEnd(Separators);
        if (trimmed.Length == 0) return p[..1];
        if (trimmed.EndsWith(':')) return trimmed + Path.DirectorySeparatorChar;
        return trimmed;
    }

    public static bool PathEquals(string a, string b) =>
        string.Equals(NormalizeDir(a), NormalizeDir(b), Comparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="parent"/> itself or lives beneath it.</summary>
    public static bool IsSameOrInside(string path, string parent)
    {
        var p = NormalizeDir(path);
        var par = NormalizeDir(parent);
        if (p.Length == 0 || par.Length == 0) return false;
        if (string.Equals(p, par, Comparison)) return true;
        var prefix = par.TrimEnd(Separators);
        return p.Length > prefix.Length
            && p.StartsWith(prefix, Comparison)
            && Array.IndexOf(Separators, p[prefix.Length]) >= 0;
    }
}
