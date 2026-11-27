using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Exports a standalone Traktor Pro NML playlist file with cues, loops, beatgrid, BPM, and key (DX1 #556).
/// Safety rule: NEVER writes into the user's live <c>collection.nml</c>; always writes a standalone
/// NML playlist file that the user imports in Traktor Pro.
/// </summary>
public static class TraktorNmlExporter
{
    public const string NmlVersion = "19";

    public static XDocument BuildDocument(string playlistName, IReadOnlyList<ExportTrackProjection> tracks)
    {
        var validTracks = tracks.Where(t => !string.IsNullOrWhiteSpace(t.FilePath)).ToList();
        var collectionEntries = new List<XElement>(validTracks.Count);
        var playlistEntries = new List<XElement>(validTracks.Count);

        foreach (var t in validTracks)
        {
            var (volume, dir, file) = SplitTraktorLocation(t.FilePath);
            var primaryKey = $"{volume}{dir}{file}";

            var entry = new XElement("ENTRY",
                new XAttribute("TITLE", t.Title),
                new XAttribute("ARTIST", t.Artist),
                new XElement("LOCATION",
                    new XAttribute("DIR", dir),
                    new XAttribute("FILE", file),
                    new XAttribute("VOLUME", volume),
                    new XAttribute("VOLUMEID", string.Empty)));

            if (!string.IsNullOrWhiteSpace(t.Album))
                entry.Add(new XElement("ALBUM", new XAttribute("TITLE", t.Album)));

            var comment = ExportFieldMapping.FormatComment(t, ExportTargetKind.Traktor);
            var info = new XElement("INFO");
            if (!string.IsNullOrWhiteSpace(t.Genre)) info.Add(new XAttribute("GENRE", t.Genre));
            if (!string.IsNullOrWhiteSpace(t.Label)) info.Add(new XAttribute("LABEL", t.Label));
            if (!string.IsNullOrWhiteSpace(comment)) info.Add(new XAttribute("COMMENT", comment));
            if (t.DurationSeconds > 0) info.Add(new XAttribute("PLAYTIME", ((int)Math.Round(t.DurationSeconds)).ToString(CultureInfo.InvariantCulture)));
            if (t.Bitrate > 0) info.Add(new XAttribute("BITRATE", (t.Bitrate * 1000).ToString(CultureInfo.InvariantCulture)));
            if (t.Rating > 0) info.Add(new XAttribute("RANKING", Math.Clamp(t.Rating * 51, 0, 255).ToString(CultureInfo.InvariantCulture)));
            if (info.HasAttributes)
                entry.Add(info);

            if (t.Bpm > 0)
            {
                entry.Add(new XElement("TEMPO",
                    new XAttribute("BPM", t.Bpm.ToString("F6", CultureInfo.InvariantCulture)),
                    new XAttribute("BPM_QUALITY", "100.000000")));
            }

            if (KeyNotation.ToTraktorInt(t.CanonicalKey) is int traktorKey)
            {
                entry.Add(new XElement("MUSICAL_KEY",
                    new XAttribute("VALUE", traktorKey.ToString(CultureInfo.InvariantCulture))));
            }

            // Beatgrid marker (TYPE="4", HOTCUE="-1")
            if (t.BeatgridAnchorSeconds.HasValue && t.Bpm > 0)
            {
                entry.Add(new XElement("CUE_V2",
                    new XAttribute("NAME", "AutoGrid"),
                    new XAttribute("DISPL_ORDER", "0"),
                    new XAttribute("TYPE", "4"),
                    new XAttribute("START", (t.BeatgridAnchorSeconds.Value * 1000.0).ToString("F6", CultureInfo.InvariantCulture)),
                    new XAttribute("LEN", "0.000000"),
                    new XAttribute("REPEATS", "-1"),
                    new XAttribute("HOTCUE", "-1")));
            }

            // Hot cues (TYPE="0") and loops (TYPE="5"), capped at 8 slots
            var maxSlots = ExportFieldMapping.MaxHotCueSlots(ExportTargetKind.Traktor);
            foreach (var cue in t.Cues.Where(c => c.IsHotCue && c.Slot >= 0 && c.Slot < maxSlots))
            {
                var startMs = cue.StartSeconds * 1000.0;
                var lenMs = cue.IsLoop && cue.LoopEndSeconds.HasValue
                    ? Math.Max(0, (cue.LoopEndSeconds.Value - cue.StartSeconds) * 1000.0)
                    : 0.0;

                entry.Add(new XElement("CUE_V2",
                    new XAttribute("NAME", cue.Name),
                    new XAttribute("DISPL_ORDER", "0"),
                    new XAttribute("TYPE", cue.IsLoop ? "5" : "0"),
                    new XAttribute("START", startMs.ToString("F6", CultureInfo.InvariantCulture)),
                    new XAttribute("LEN", lenMs.ToString("F6", CultureInfo.InvariantCulture)),
                    new XAttribute("REPEATS", "-1"),
                    new XAttribute("HOTCUE", cue.Slot.ToString(CultureInfo.InvariantCulture))));
            }

            collectionEntries.Add(entry);
            playlistEntries.Add(new XElement("ENTRY",
                new XElement("PRIMARYKEY",
                    new XAttribute("TYPE", "TRACK"),
                    new XAttribute("KEY", primaryKey))));
        }

        var safeName = string.IsNullOrWhiteSpace(playlistName) ? "ORBIT Playlist" : playlistName.Trim();

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement("NML",
                new XAttribute("VERSION", NmlVersion),
                new XElement("HEAD",
                    new XAttribute("COMPANY", "ORBIT"),
                    new XAttribute("PROGRAM", "ORBIT DJ")),
                new XElement("COLLECTION",
                    new XAttribute("ENTRIES", collectionEntries.Count.ToString(CultureInfo.InvariantCulture)),
                    collectionEntries),
                new XElement("PLAYLISTS",
                    new XElement("NODE",
                        new XAttribute("TYPE", "FOLDER"),
                        new XAttribute("NAME", "$ROOT"),
                        new XElement("SUBNODES",
                            new XAttribute("COUNT", "1"),
                            new XElement("NODE",
                                new XAttribute("TYPE", "PLAYLIST"),
                                new XAttribute("NAME", safeName),
                                new XElement("PLAYLIST",
                                    new XAttribute("ENTRIES", playlistEntries.Count.ToString(CultureInfo.InvariantCulture)),
                                    new XAttribute("TYPE", "LIST"),
                                    new XAttribute("UUID", Guid.NewGuid().ToString("N")),
                                    playlistEntries)))))));
    }

    public static void WriteToFile(string outputPath, string playlistName, IReadOnlyList<ExportTrackProjection> tracks)
    {
        var doc = BuildDocument(playlistName, tracks);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer);
    }

    /// <summary>
    /// Converts a Windows or POSIX absolute path into Traktor's <c>(VOLUME, DIR, FILE)</c> triple
    /// using <c>/:</c> path separators (compatible with <see cref="Integrations.TraktorMetadataImporter"/>).
    /// </summary>
    public static (string Volume, string Dir, string File) SplitTraktorLocation(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        var file = Path.GetFileName(filePath);

        string volume = string.Empty;
        string rest = normalized;

        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            volume = normalized[..2]; // e.g. "C:"
            rest = normalized[2..];
        }

        var lastSlash = rest.LastIndexOf('/');
        var dirPart = lastSlash >= 0 ? rest[..lastSlash] : string.Empty;
        var segments = dirPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var traktorDir = segments.Length == 0
            ? "/:"
            : "/:" + string.Join("/:", segments) + "/:";

        return (volume, traktorDir, file);
    }
}
