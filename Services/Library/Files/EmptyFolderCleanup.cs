using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF5 (#347): removes directories a reorganization emptied, bottom-up, stopping at (and never
/// removing) a caller-supplied root — so only folders ORBIT emptied are cleaned, never the download
/// or library root itself.
/// </summary>
public static class EmptyFolderCleanup
{
    /// <summary>Removes empty parent directories of <paramref name="filePath"/> up to <paramref name="stopAtRoot"/>; returns what was removed.</summary>
    public static IReadOnlyList<string> RemoveEmptyParents(string filePath, string stopAtRoot)
    {
        var removed = new List<string>();
        var root = Path.GetFullPath(stopAtRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.GetDirectoryName(filePath);

        while (!string.IsNullOrEmpty(dir))
        {
            var full = Path.GetFullPath(dir);
            if (!IsWithin(full, root)) break;
            if (!Directory.Exists(full)) break;
            if (Directory.EnumerateFileSystemEntries(full).Any()) break; // not empty — stop

            Directory.Delete(full);
            removed.Add(full);
            dir = Path.GetDirectoryName(full);
        }

        return removed;
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return false; // never remove the root itself
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
    }
}
