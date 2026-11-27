using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SLSKDONET.Services.Export;

public sealed record VirtualDjMergeResult(
    bool Success,
    int UpdatedSongs,
    int AddedSongs,
    string? BackupPath,
    string? Error = null);

/// <summary>
/// Exports VirtualDJ <c>.vdjfolder</c> playlists and performs guarded merges into VirtualDJ's
/// <c>database.xml</c> (DX2 #557).
/// Safety rules:
/// - Refuses while VirtualDJ is running.
/// - Creates a timestamped backup copy next to <c>database.xml</c> before writing.
/// - Preserves unknown elements and attributes byte-for-byte.
/// - Stores <c>Scan Bpm</c> as seconds-per-beat (<c>60.0 / BPM</c>), per VirtualDJ's format.
/// </summary>
public static class VirtualDjExporter
{
    public static bool IsVirtualDjRunning()
    {
        return Process.GetProcessesByName("virtualdj").Length > 0
            || Process.GetProcessesByName("VirtualDJ").Length > 0;
    }

    public static XDocument BuildVdjFolderDocument(IReadOnlyList<ExportTrackProjection> tracks)
    {
        var songs = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => new XElement("song", new XAttribute("path", t.FilePath)));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("VirtualFolder", songs));
    }

    public static void WriteVdjFolder(string outputPath, IReadOnlyList<ExportTrackProjection> tracks)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var doc = BuildVdjFolderDocument(tracks);
        using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer);
    }

    /// <summary>
    /// Converts BPM to VirtualDJ's <c>Scan Bpm</c> attribute (seconds per beat = <c>60 / BPM</c>).
    /// </summary>
    public static double BpmToSecondsPerBeat(double bpm) => bpm > 0 ? 60.0 / bpm : 0.0;

    /// <summary>
    /// Guarded merge of ORBIT cues, loops, beatgrid, BPM, and key into a VirtualDJ <c>database.xml</c>.
    /// </summary>
    public static VirtualDjMergeResult MergeDatabaseXml(
        string databaseXmlPath,
        IReadOnlyList<ExportTrackProjection> tracks,
        Func<bool>? isRunningCheck = null)
    {
        var running = (isRunningCheck ?? IsVirtualDjRunning)();
        if (running)
        {
            return new VirtualDjMergeResult(
                Success: false,
                UpdatedSongs: 0,
                AddedSongs: 0,
                BackupPath: null,
                Error: "VirtualDJ is currently running. Close VirtualDJ before exporting to database.xml.");
        }

        if (!File.Exists(databaseXmlPath))
        {
            return new VirtualDjMergeResult(
                Success: false,
                UpdatedSongs: 0,
                AddedSongs: 0,
                BackupPath: null,
                Error: $"VirtualDJ database.xml not found at: {databaseXmlPath}");
        }

        try
        {
            var backupPath = $"{databaseXmlPath}.orbit-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(databaseXmlPath, backupPath, overwrite: true);

            var doc = XDocument.Load(databaseXmlPath, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root == null)
            {
                return new VirtualDjMergeResult(false, 0, 0, backupPath, "database.xml has no root element.");
            }

            var existingByPath = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var song in root.Elements("Song"))
            {
                var fp = song.Attribute("FilePath")?.Value;
                if (!string.IsNullOrWhiteSpace(fp))
                    existingByPath[fp] = song;
            }

            var updated = 0;
            var added = 0;

            foreach (var track in tracks.Where(t => !string.IsNullOrWhiteSpace(t.FilePath)))
            {
                if (!existingByPath.TryGetValue(track.FilePath, out var songEl))
                {
                    songEl = new XElement("Song", new XAttribute("FilePath", track.FilePath));
                    root.Add(songEl);
                    existingByPath[track.FilePath] = songEl;
                    added++;
                }
                else
                {
                    updated++;
                }

                UpdateScanElement(songEl, track);
                UpdatePoiElements(songEl, track);
            }

            using var writer = new StreamWriter(databaseXmlPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            doc.Save(writer, SaveOptions.DisableFormatting);

            return new VirtualDjMergeResult(true, updated, added, backupPath);
        }
        catch (Exception ex)
        {
            return new VirtualDjMergeResult(false, 0, 0, null, ex.Message);
        }
    }

    private static void UpdateScanElement(XElement songEl, ExportTrackProjection track)
    {
        var scan = songEl.Element("Scan");
        if (scan == null)
        {
            scan = new XElement("Scan");
            songEl.Add(scan);
        }

        if (track.Bpm > 0)
        {
            var spb = BpmToSecondsPerBeat(track.Bpm);
            scan.SetAttributeValue("Bpm", spb.ToString("F6", CultureInfo.InvariantCulture));
        }

        var key = ExportFieldMapping.FormatKey(track, ExportTargetKind.VirtualDj);
        if (!string.IsNullOrWhiteSpace(key))
        {
            scan.SetAttributeValue("Key", key);
        }
    }

    private static void UpdatePoiElements(XElement songEl, ExportTrackProjection track)
    {
        // Remove only cue/loop/beatgrid Poi nodes we are replacing; preserve any other Poi types (e.g. automix, remix).
        var replaceableTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cue", "loop", "beatgrid" };
        var toRemove = songEl.Elements("Poi")
            .Where(p => replaceableTypes.Contains(p.Attribute("Type")?.Value ?? string.Empty))
            .ToList();
        foreach (var el in toRemove)
            el.Remove();

        if (track.BeatgridAnchorSeconds.HasValue && track.Bpm > 0)
        {
            songEl.Add(new XElement("Poi",
                new XAttribute("Pos", track.BeatgridAnchorSeconds.Value.ToString("F6", CultureInfo.InvariantCulture)),
                new XAttribute("Type", "beatgrid"),
                new XAttribute("Bpm", BpmToSecondsPerBeat(track.Bpm).ToString("F6", CultureInfo.InvariantCulture))));
        }

        var maxSlots = ExportFieldMapping.MaxHotCueSlots(ExportTargetKind.VirtualDj);
        foreach (var cue in track.Cues.Where(c => c.IsHotCue && c.Slot >= 0 && c.Slot < maxSlots))
        {
            var poi = new XElement("Poi",
                new XAttribute("Pos", cue.StartSeconds.ToString("F6", CultureInfo.InvariantCulture)),
                new XAttribute("Type", cue.IsLoop ? "loop" : "cue"),
                new XAttribute("Num", (cue.Slot + 1).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Name", cue.Name));

            if (cue.IsLoop && cue.LoopEndSeconds.HasValue)
            {
                var sizeSec = Math.Max(0, cue.LoopEndSeconds.Value - cue.StartSeconds);
                poi.Add(new XAttribute("Size", sizeSec.ToString("F6", CultureInfo.InvariantCulture)));
            }

            songEl.Add(poi);
        }
    }
}
