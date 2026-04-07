using System;
using System.Collections.Generic;
using System.IO;
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

    private static List<ImportedCue> ParseSeratoCues(TagLib.File tagFile)
    {
        var cues = new List<ImportedCue>();

        try
        {
            // Try to read Serato Markers2 GEOB frame (ID3)
            if (tagFile.Tag is TagLib.Id3v2.Tag id3Tag)
            {
                foreach (var frame in id3Tag.GetFrames())
                {
                    if (frame is TagLib.Id3v2.AttachmentFrame af &&
                        af.Description.Contains("Serato Markers2", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseSeratoMarkersPayload(af.Data.Data, cues);
                        break;
                    }
                }
            }
        }
        catch
        {
            // Serato markers are optional; silently ignore parse errors
        }

        return cues;
    }

    /// <summary>
    /// Minimal Serato Markers2 binary parser.
    /// The payload is a base64-encoded binary blob; this reads the header
    /// and extracts hot-cue entries (type 0x00).
    /// </summary>
    private static void ParseSeratoMarkersPayload(byte[] data, List<ImportedCue> cues)
    {
        if (data is null || data.Length < 10) return;

        // Serato Markers2 starts with "application/octet-stream\0\0" header
        // followed by base64 entries. For robustness, scan for CUE markers.
        // Entry format (after base64 decode): type(1) | size(4 BE) | payload
        //   type 0x00 = hot cue: index(1) | pos_ms(4 BE) | colour(3)

        // Locate the base64 content after the header
        int start = 0;
        for (int i = 0; i < data.Length - 1; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0) { start = i + 2; break; }
        }
        if (start == 0 || start >= data.Length) return;

        // Base64-decode the remainder
        string b64 = Encoding.ASCII.GetString(data, start,
            Math.Min(data.Length - start, 4096)).TrimEnd('\0');
        byte[] decoded;
        try   { decoded = Convert.FromBase64String(b64); }
        catch { return; }

        int pos = 0;
        while (pos + 6 < decoded.Length)
        {
            byte type = decoded[pos];
            if (pos + 5 > decoded.Length) break;
            int size = (decoded[pos + 1] << 24) | (decoded[pos + 2] << 16)
                     | (decoded[pos + 3] <<  8) |  decoded[pos + 4];
            pos += 5;

            if (size < 0 || pos + size > decoded.Length) break;

            if (type == 0x00 && size >= 8) // HOT CUE
            {
                int  index    = decoded[pos];
                long ms       = (decoded[pos + 1] << 24) | (decoded[pos + 2] << 16)
                              | (decoded[pos + 3] <<  8) |  decoded[pos + 4];
                // byte 5: enabled flag; bytes 6-8: RGB colour
                string colour = size >= 9
                    ? $"#{decoded[pos + 6]:X2}{decoded[pos + 7]:X2}{decoded[pos + 8]:X2}"
                    : "#FFFFFF";

                cues.Add(new ImportedCue
                {
                    Index            = index,
                    TimestampSeconds = ms / 1000.0,
                    Name             = $"Cue {index + 1}",
                    Color            = colour,
                });
            }

            pos += size;
        }
    }
}
