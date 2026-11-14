using MediaCleanup.Core.Configuration;

namespace MediaCleanup.Core.Processing;

/// <summary>File moves that never overwrite a different file and work across drives and network shares.</summary>
public static class FileOps
{
    /// <summary>Extension used while a file is being copied to another drive or share.</summary>
    public const string PartialExtension = ".mcpartial";

    /// <summary>Returns <paramref name="path"/> or, when taken, the same name with " (1)", " (2)" and so on.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Moves or renames a file and returns where it ended up. If the target already holds a file of
    /// the same size it is treated as the same download and the source copy is dropped; a different
    /// file is kept and the new one gets a numbered name.
    /// </summary>
    public static string MoveFile(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal)) return source;

        if (PathUtil.PathEquals(source, destination))
        {
            File.Move(source, destination); // Same file, only the letter case changes.
            return destination;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length == new FileInfo(source).Length)
            {
                DeleteFile(source);
                return destination;
            }
            destination = UniquePath(destination);
        }

        if (SameVolume(source, destination))
        {
            File.Move(source, destination);
        }
        else
        {
            // Copy under a temporary name so media servers watching the destination never see half a file.
            var partial = destination + PartialExtension;
            File.Move(source, partial, overwrite: true);
            File.Move(partial, destination);
        }
        return destination;
    }

    /// <summary>
    /// Moves a folder, merging into the destination when it already exists. Returns the final folder.
    /// </summary>
    public static string MoveDirectory(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.Ordinal)) return source;

        if (PathUtil.PathEquals(source, destination))
        {
            var temporary = UniquePath(source + ".renaming");
            Directory.Move(source, temporary);
            Directory.Move(temporary, destination);
            return destination;
        }

        if (!Directory.Exists(destination) && !File.Exists(destination) && SameVolume(source, destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                Directory.Move(source, destination);
                return destination;
            }
            catch (IOException)
            {
                // Fall back to moving file by file.
            }
        }

        if (File.Exists(destination)) destination = UniquePath(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(source, file);
            MoveFile(file, Path.Combine(destination, relative));
        }
        DeleteEmptyDirectories(source, includeRoot: true);
        return destination;
    }

    public static void DeleteFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if (info.IsReadOnly) info.IsReadOnly = false;
        info.Delete();
    }

    /// <summary>Removes folders that hold no files at all, deepest first.</summary>
    public static void DeleteEmptyDirectories(string root, bool includeRoot)
    {
        if (!Directory.Exists(root)) return;
        foreach (var child in Directory.EnumerateDirectories(root).ToList())
            DeleteEmptyDirectories(child, includeRoot: true);
        if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }

    private static bool SameVolume(string a, string b)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(a)) ?? "";
        var rootB = Path.GetPathRoot(Path.GetFullPath(b)) ?? "";
        return string.Equals(rootA, rootB, PathUtil.Comparison);
    }
}
