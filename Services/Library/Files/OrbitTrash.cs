using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF6 (#348): the quality-upgrade trash. The old (lower-quality) file moves into a hidden
/// <c>.orbit-trash</c> folder under the download directory, with a sidecar recording its original
/// path so <see cref="Restore"/> can put it back. Retention/sweep is a separate concern.
/// </summary>
public static class OrbitTrash
{
    public const string TrashFolderName = ".orbit-trash";
    public const string OriginSuffix = ".origin";

    public static string GetTrashDirectory(string downloadDir)
    {
        if (string.IsNullOrWhiteSpace(downloadDir))
            throw new ArgumentException("A download directory is required.", nameof(downloadDir));
        return Path.Combine(downloadDir, TrashFolderName);
    }

    /// <summary>Moves <paramref name="filePath"/> into the trash and returns its trashed path.</summary>
    public static string TrashFile(string filePath, string downloadDir)
    {
        var trashDir = GetTrashDirectory(downloadDir);
        Directory.CreateDirectory(trashDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var target = Path.Combine(trashDir, $"{stamp}-{Path.GetFileName(filePath)}");

        File.Move(filePath, target);
        File.WriteAllText(target + OriginSuffix, filePath); // original path, for restore
        return target;
    }

    /// <summary>Moves a trashed file back to its original path (recreating the folder). Returns the restored path.</summary>
    public static string Restore(string trashedPath)
    {
        var origin = File.ReadAllText(trashedPath + OriginSuffix).Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        File.Move(trashedPath, origin);
        File.Delete(trashedPath + OriginSuffix);
        return origin;
    }

    /// <summary>Deletes trashed files (and their .origin sidecars) older than <paramref name="retention"/>; returns what was removed.</summary>
    public static IReadOnlyList<string> SweepExpired(string downloadDir, TimeSpan retention)
    {
        var trashDir = GetTrashDirectory(downloadDir);
        if (!Directory.Exists(trashDir)) return Array.Empty<string>();

        var cutoff = DateTime.UtcNow - retention;
        var removed = new List<string>();
        foreach (var file in Directory.GetFiles(trashDir))
        {
            if (file.EndsWith(OriginSuffix, StringComparison.OrdinalIgnoreCase)) continue; // handled with its file
            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            File.Delete(file);
            var sidecar = file + OriginSuffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
            removed.Add(file);
        }
        return removed;
    }
}
