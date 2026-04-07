using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Integrations;

/// <summary>
/// Imports DJ metadata from a Traktor Pro NML library file (.nml).
/// Implements Issue 6.3 / #40 (Traktor side).
///
/// NML structure:
/// <c>&lt;NML&gt; → &lt;COLLECTION&gt; → &lt;ENTRY&gt;</c>
/// Each ENTRY contains child elements: LOCATION, MUSICAL_KEY, TEMPO, CUE_V2.
/// </summary>
public sealed class TraktorMetadataImporter
{
    private readonly ILogger<TraktorMetadataImporter> _logger;

    public TraktorMetadataImporter(ILogger<TraktorMetadataImporter> logger)
    {
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a Traktor NML library file and returns one <see cref="DjMetadataResult"/>
    /// per COLLECTION ENTRY that contains a resolvable file path.
    /// </summary>
    public IReadOnlyList<DjMetadataResult> ImportLibrary(string nmlPath)
    {
        if (!File.Exists(nmlPath))
        {
            _logger.LogWarning("Traktor import: NML file not found – {Path}", nmlPath);
            return Array.Empty<DjMetadataResult>();
        }

        var results = new List<DjMetadataResult>();

        try
        {
            var doc = XDocument.Load(nmlPath, LoadOptions.None);
            var collection = doc.Root?.Element("COLLECTION");
            if (collection is null)
            {
                _logger.LogWarning("Traktor NML: no COLLECTION element found in {Path}", nmlPath);
                return results;
            }

            foreach (var entry in collection.Elements("ENTRY"))
            {
                try
                {
                    var result = ParseEntry(entry);
                    if (result is not null)
                        results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Traktor NML: error parsing ENTRY '{Title}'",
                        entry.Attribute("TITLE")?.Value);
                }
            }

            _logger.LogInformation(
                "Traktor import: {N} tracks loaded from {Path}", results.Count, nmlPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Traktor NML import failed for {Path}", nmlPath);
        }

        return results;
    }

    // ── NML entry parser ─────────────────────────────────────────────────

    private static DjMetadataResult? ParseEntry(XElement entry)
    {
        // TITLE / ARTIST from attributes
        string title  = entry.Attribute("TITLE")?.Value  ?? string.Empty;
        string artist = entry.Attribute("ARTIST")?.Value ?? string.Empty;
        string album  = entry.Attribute("ALBUM")?.Value  ?? string.Empty;

        // Absolute file path from <LOCATION DIR="..." FILE="..." VOLUME="..."/>
        string filePath = ResolveFilePath(entry.Element("LOCATION"));
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        // BPM from <TEMPO BPM="123.00"/>
        double bpm = 0;
        string? bpmStr = entry.Element("TEMPO")?.Attribute("BPM")?.Value;
        if (bpmStr is not null)
            double.TryParse(bpmStr, NumberStyles.Float, CultureInfo.InvariantCulture, out bpm);

        // Musical key from <MUSICAL_KEY VALUE="0"/> (Traktor uses integer 0-23)
        // Maps to Open Key notation: 0=1m(Am), 1=8d(C), 2=2m(Em)… 
        string key = string.Empty;
        string? keyValStr = entry.Element("MUSICAL_KEY")?.Attribute("VALUE")?.Value;
        if (keyValStr is not null && int.TryParse(keyValStr, out int keyVal))
            key = TraktorKeyToOpenKey(keyVal);

        // Cue points from <CUE_V2 NAME="..." START="..." TYPE="0" HOTCUE="0" .../>
        var cues = ParseCues(entry);

        return new DjMetadataResult
        {
            FilePath = filePath,
            Title    = string.IsNullOrWhiteSpace(title)  ? Path.GetFileNameWithoutExtension(filePath) : title,
            Artist   = artist,
            Album    = album,
            Bpm      = bpm,
            Key      = key,
            Cues     = cues,
        };
    }

    private static string ResolveFilePath(XElement? location)
    {
        if (location is null) return string.Empty;

        string dir    = location.Attribute("DIR")?.Value  ?? string.Empty;
        string file   = location.Attribute("FILE")?.Value ?? string.Empty;
        string volume = location.Attribute("VOLUME")?.Value ?? string.Empty;

        // Traktor encodes path components with ':' separator on Windows e.g. "/:Music/:Subfolder/"
        dir = dir.Replace("/:", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(volume) && !dir.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
            dir = volume + dir;

        return Path.Combine(dir, file);
    }

    private static List<ImportedCue> ParseCues(XElement entry)
    {
        var cues = new List<ImportedCue>();

        foreach (var cueEl in entry.Elements("CUE_V2"))
        {
            // TYPE="0" = regular cue; HOTCUE="-1" = not a hot cue
            string? typeStr   = cueEl.Attribute("TYPE")?.Value;
            string? hotcueStr = cueEl.Attribute("HOTCUE")?.Value;
            string? startStr  = cueEl.Attribute("START")?.Value;
            string? name      = cueEl.Attribute("NAME")?.Value;

            if (!double.TryParse(startStr, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double startMs))
                continue;

            int.TryParse(hotcueStr, out int hotcueIdx);
            if (!int.TryParse(typeStr, out int cueType)) cueType = 0;

            // Only import regular cues (TYPE 0) and grid markers (TYPE 4 skipped)
            if (cueType != 0 && cueType != 1) continue;

            cues.Add(new ImportedCue
            {
                Index            = Math.Max(0, hotcueIdx),
                TimestampSeconds = startMs / 1000.0,
                Name             = name ?? $"Cue {cues.Count + 1}",
                Color            = HotcueIndexToColor(hotcueIdx),
            });
        }

        return cues;
    }

    // ── Key mapping ──────────────────────────────────────────────────────

    // Traktor Open Key integer → Open Key string (1m, 1d, …, 12m, 12d)
    private static readonly string[] TraktorKeys =
    {
        "1m",  // 0  Am
        "8d",  // 1  C
        "2m",  // 2  Em
        "9d",  // 3  G
        "3m",  // 4  Bm
        "10d", // 5  D
        "4m",  // 6  F#m
        "11d", // 7  A
        "5m",  // 8  C#m/Dbm
        "12d", // 9  E
        "6m",  // 10 G#m/Abm
        "7d",  // 11 B
        "7m",  // 12 D#m/Ebm
        "2d",  // 13 F#/Gb
        "8m",  // 14 A#m/Bbm
        "3d",  // 15 Db/C#
        "9m",  // 16 Fm
        "4d",  // 17 Ab/G#
        "10m", // 18 Cm
        "5d",  // 19 Eb/D#
        "11m", // 20 Gm
        "6d",  // 21 Bb/A#
        "12m", // 22 Dm
        "1d",  // 23 F
    };

    private static string TraktorKeyToOpenKey(int value)
    {
        if (value < 0 || value >= TraktorKeys.Length) return string.Empty;
        return TraktorKeys[value];
    }

    // ── Colour helpers ───────────────────────────────────────────────────

    private static readonly string[] HotcueColors =
    {
        "#E2272E", // 0 red
        "#F28C28", // 1 orange
        "#F4D03F", // 2 yellow
        "#27AE60", // 3 green
        "#2980B9", // 4 blue
        "#8E44AD", // 5 purple
        "#F1948A", // 6 pink
        "#AEB6BF", // 7 grey
    };

    private static string HotcueIndexToColor(int index)
    {
        if (index < 0 || index >= HotcueColors.Length) return "#FFFFFF";
        return HotcueColors[index];
    }
}
