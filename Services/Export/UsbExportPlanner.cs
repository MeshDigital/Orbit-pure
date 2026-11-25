using System;
using System.Collections.Generic;
using System.IO;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

/// <summary>One track to place in a USB export tree.</summary>
public sealed record UsbExportTrack(string Hash, string SourcePath, Track Metadata);

/// <summary>One planned copy in the export tree.</summary>
public sealed record UsbExportPlanItem(string Hash, string SourcePath, string RelativeTargetPath, long SizeBytes);

/// <summary>The planned USB export tree: per-item relative paths, deduped within the layout, with sizes.</summary>
public sealed record UsbExportPlan(IReadOnlyList<UsbExportPlanItem> Items, long TotalBytes);

/// <summary>Human-readable summary of a planned USB export tree for the export dialog preview.</summary>
public sealed record UsbExportPreviewSummary(
    int TrackCount,
    int FolderCount,
    long TotalBytes,
    string TotalSizeDisplay,
    IReadOnlyList<string> FolderLines);

/// <summary>
/// LF7 (#349): builds the folder-layout export plan — for each track the FAT32-safe folder + filename,
/// deduped by hash within one layout, with the size of each source file and the total. The copy step
/// consumes this plan, so the preview and the real export never disagree.
/// </summary>
public static class UsbExportPlanner
{
    public static UsbExportPlan Plan(
        IReadOnlyList<UsbExportTrack> tracks,
        UsbLayoutMode mode,
        Func<string, string?>? playlistName = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<UsbExportPlanItem>();
        long total = 0;

        foreach (var t in tracks)
        {
            if (!seen.Add(t.Hash)) continue; // dedupe within one layout

            var folder = UsbExportLayout.GetFolder(t.Metadata, mode, playlistName?.Invoke(t.Hash));
            var baseName = UsbExportLayout.SafeFolder(Path.GetFileNameWithoutExtension(t.Metadata.Filename ?? t.Metadata.Title ?? "Unknown"));
            var extension = Path.GetExtension(t.SourcePath);
            var relative = Path.Combine(folder, baseName + extension);

            if (!usedPaths.Add(relative))
            {
                var hashSuffix = string.IsNullOrEmpty(t.Hash)
                    ? ""
                    : " [" + t.Hash[..Math.Min(8, t.Hash.Length)] + "]";
                var disambiguated = string.IsNullOrEmpty(hashSuffix)
                    ? relative
                    : Path.Combine(folder, UsbExportLayout.SafeFolder(baseName + hashSuffix) + extension);

                var counter = 2;
                while (!usedPaths.Add(disambiguated))
                {
                    disambiguated = Path.Combine(folder, UsbExportLayout.SafeFolder($"{baseName} ({counter})") + extension);
                    counter++;
                }

                relative = disambiguated;
            }

            long size = 0;
            if (File.Exists(t.SourcePath))
                size = new FileInfo(t.SourcePath).Length;

            items.Add(new UsbExportPlanItem(t.Hash, t.SourcePath, relative, size));
            total += size;
        }

        return new UsbExportPlan(items, total);
    }

    /// <summary>
    /// Builds a compact folder-tree and total-size preview for the export dialog (LF7 #349).
    /// When <paramref name="mode"/> is null, previews the default <c>OrbitAudio/&lt;playlist&gt;/</c> folder.
    /// </summary>
    public static UsbExportPreviewSummary BuildPreviewSummary(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        UsbLayoutMode? mode)
    {
        if (tracks.Count == 0)
            return new UsbExportPreviewSummary(0, 0, 0, "0 B", Array.Empty<string>());

        if (mode is null)
        {
            long totalBytes = 0;
            var count = 0;
            foreach (var t in tracks)
            {
                if (string.IsNullOrEmpty(t.ResolvedFilePath)) continue;
                count++;
                if (File.Exists(t.ResolvedFilePath))
                    totalBytes += new FileInfo(t.ResolvedFilePath).Length;
            }
            var folderName = $"OrbitAudio/{UsbExportLayout.SafeFolder(playlistName)}/";
            return new UsbExportPreviewSummary(
                count,
                1,
                totalBytes,
                FormatBytes(totalBytes),
                new[] { $"{folderName}  ({count} {(count == 1 ? "track" : "tracks")})" });
        }

        var input = new List<UsbExportTrack>(tracks.Count);
        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.ResolvedFilePath)) continue;
            input.Add(new UsbExportTrack(
                string.IsNullOrEmpty(t.TrackUniqueHash) ? t.ResolvedFilePath : t.TrackUniqueHash,
                t.ResolvedFilePath,
                new Track
                {
                    MusicalKey = t.MusicalKey,
                    BPM = t.BPM,
                    Genres = t.Genres,
                    Filename = t.ResolvedFilePath,
                    Title = t.Title,
                    Artist = t.Artist,
                }));
        }

        var plan = Plan(input, mode.Value, _ => playlistName);
        var byFolder = new SortedDictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items)
        {
            var folder = Path.GetDirectoryName(item.RelativeTargetPath) ?? "Unknown";
            byFolder.TryGetValue(folder, out var agg);
            byFolder[folder] = (agg.Count + 1, agg.Bytes + item.SizeBytes);
        }

        var lines = new List<string>(byFolder.Count);
        foreach (var kv in byFolder)
        {
            var label = kv.Value.Count == 1 ? "track" : "tracks";
            lines.Add($"{kv.Key}/  ({kv.Value.Count} {label}, {FormatBytes(kv.Value.Bytes)})");
        }

        return new UsbExportPreviewSummary(
            plan.Items.Count,
            byFolder.Count,
            plan.TotalBytes,
            FormatBytes(plan.TotalBytes),
            lines);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.##} GB";
    }
}
