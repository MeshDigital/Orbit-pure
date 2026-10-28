using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using TagLib;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Integrations;

/// <summary>
/// Result of a DJ-metadata import from an external library format.
/// </summary>
public sealed class DjMetadataResult
{
    public string FilePath { get; init; } = string.Empty;
    public string Title    { get; init; } = string.Empty;
    public string Artist   { get; init; } = string.Empty;
    public string Album    { get; init; } = string.Empty;
    /// <summary>BPM read from the external metadata (0 = not set).</summary>
    public double Bpm      { get; init; }
    /// <summary>Musical key in the source format (e.g. "Cm", "8A", "Am").</summary>
    public string Key      { get; init; } = string.Empty;
    /// <summary>Cue points imported from the external library.</summary>
    public IReadOnlyList<ImportedCue> Cues { get; init; } = Array.Empty<ImportedCue>();
}

/// <summary>A single cue point from an external DJ library.</summary>
public sealed class ImportedCue
{
    public int    Index            { get; init; }
    public double TimestampSeconds { get; init; }
    public string Name             { get; init; } = string.Empty;
    public string Color            { get; init; } = "#FFFFFF";
}

/// <summary>
/// Imports Serato DJ metadata embedded in audio file ID3/MP4 tags.
/// Serato stores cues in the custom tag <c>GEOB:Serato Markers2</c> (ID3)
/// or <c>----:com.serato.dj:markers2</c> (MP4).
/// Implements Issue 6.3 / #40 (Serato side).
/// </summary>
public sealed class SeratoMetadataImporter
{
    private readonly ILogger<SeratoMetadataImporter> _logger;

    public SeratoMetadataImporter(ILogger<SeratoMetadataImporter> logger)
    {
        _logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads Serato metadata from an audio file and returns a populated
    /// <see cref="DjMetadataResult"/>.  Returns <c>null</c> if the file
    /// cannot be opened or contains no Serato tags.
    /// </summary>
    public DjMetadataResult? Import(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
        {
            _logger.LogWarning("Serato import: file not found – {Path}", filePath);
            return null;
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;

            double bpm = tag.BeatsPerMinute;
            string key = tag.InitialKey ?? string.Empty;

            var cues = ParseSeratoCues(tagFile);

            _logger.LogDebug(
                "Serato import: {File} → BPM={Bpm}, Key={Key}, Cues={N}",
                Path.GetFileName(filePath), bpm, key, cues.Count);

            return new DjMetadataResult
            {
                FilePath = filePath,
                Title    = tag.Title   ?? Path.GetFileNameWithoutExtension(filePath),
                Artist   = tag.FirstPerformer ?? string.Empty,
                Album    = tag.Album    ?? string.Empty,
                Bpm      = bpm,
                Key      = key,
                Cues     = cues,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serato import failed for {Path}", filePath);
            return null;
        }
    }

    // ── Serato cue parsing ───────────────────────────────────────────────

    private List<ImportedCue> ParseSeratoCues(TagLib.File tagFile)
    {
        var cues = new List<ImportedCue>();
        try
        {
            // Serato Markers2: ID3 GEOB frame (MP3/AIFF/WAV) or SERATO_MARKERS_V2 Vorbis comment (FLAC/Ogg).
            List<Serato.SeratoEntry>? entries = null;
            if (tagFile.GetTag(TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3
                && Serato.SeratoCueExportService.FindGeob(id3) is { } geob)
                entries = Serato.SeratoMarkers2.ParseId3Body(geob.Data.Data);
            else if (tagFile.GetTag(TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph
                && xiph.GetFirstField(Serato.SeratoMarkers2.VorbisField) is { Length: > 0 } value)
                entries = Serato.SeratoMarkers2.ParseVorbisValue(value);

            foreach (var cue in (entries ?? new()).Select(Serato.SeratoMarkers2.ReadCue).Where(c => c != null))
            {
                cues.Add(new ImportedCue
                {
                    Index            = cue!.Index,
                    TimestampSeconds = cue.PositionMs / 1000.0,
                    Name             = string.IsNullOrEmpty(cue.Name) ? $"Cue {cue.Index + 1}" : cue.Name,
                    Color            = $"#{cue.R:X2}{cue.G:X2}{cue.B:X2}",
                });
            }
        }
        catch (Exception ex)
        {
            // Was a bare catch that silently swallowed this — a user importing a track with real
            // Serato hot cues could end up with zero cues transferred and no indication why.
            _logger.LogWarning(ex, "Failed to parse Serato Markers2 cue data — import will proceed with no cues");
        }
        return cues.OrderBy(c => c.Index).ToList();
    }
}
