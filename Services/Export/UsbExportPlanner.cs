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
        var items = new List<UsbExportPlanItem>();
        long total = 0;

        foreach (var t in tracks)
        {
            if (!seen.Add(t.Hash)) continue; // dedupe within one layout

            var folder = UsbExportLayout.GetFolder(t.Metadata, mode, playlistName?.Invoke(t.Hash));
            var baseName = UsbExportLayout.SafeFolder(Path.GetFileNameWithoutExtension(t.Metadata.Filename ?? t.Metadata.Title ?? "Unknown"));
            var extension = Path.GetExtension(t.SourcePath);
            var relative = Path.Combine(folder, baseName + extension);

            long size = 0;
            if (File.Exists(t.SourcePath))
                size = new FileInfo(t.SourcePath).Length;

            items.Add(new UsbExportPlanItem(t.Hash, t.SourcePath, relative, size));
            total += size;
        }

        return new UsbExportPlan(items, total);
    }
}
