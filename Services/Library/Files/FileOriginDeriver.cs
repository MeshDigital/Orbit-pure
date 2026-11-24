using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services;

/// <summary>
/// Classifies how a library file entered the library (LF4 #346). Used to backfill / lazily derive
/// <c>LibraryEntryEntity.FileOrigin</c> and to keep the relocator from auto-moving linked folders.
/// </summary>
public static class FileOriginDeriver
{
    public const string Downloaded = "Downloaded";
    public const string LinkedFolder = "LinkedFolder";
    public const string Imported = "Imported";

    /// <summary>
    /// Returns <see cref="Downloaded"/> when <paramref name="filePath"/> is under the download
    /// directory, <see cref="LinkedFolder"/> when it is under a watched library-folder root, and
    /// <see cref="Imported"/> otherwise. The download directory wins when both match.
    /// </summary>
    public static string Derive(string? filePath, IEnumerable<string>? libraryFolderRoots, string? downloadDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return Imported;

        var fullPath = Path.GetFullPath(filePath);

        if (!string.IsNullOrWhiteSpace(downloadDirectory) && IsUnder(fullPath, downloadDirectory))
            return Downloaded;

        if (libraryFolderRoots != null)
        {
            foreach (var root in libraryFolderRoots)
            {
                if (!string.IsNullOrWhiteSpace(root) && IsUnder(fullPath, root))
                    return LinkedFolder;
            }
        }

        return Imported;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
    }
}
