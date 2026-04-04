using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Models.Export;

namespace SLSKDONET.Services.Library;

public class PlaylistExportService
{
    private readonly ILogger<PlaylistExportService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PlaylistExportService(
        ILogger<PlaylistExportService> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public async Task ExportToRekordboxXmlAsync(string playlistName, IEnumerable<PlaylistTrack> tracks, string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to Rekordbox XML: {Path}", playlistName, targetPath);

            var trackList = tracks.ToList();

            // Pre-load all cue points for tracks in this export in one query
            var hashes = trackList.Select(t => t.TrackUniqueHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
            Dictionary<string, List<CuePointEntity>> cuesByHash = new(StringComparer.Ordinal);

            if (hashes.Count > 0)
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var cues = await db.CuePoints
                    .AsNoTracking()
                    .Where(c => hashes.Contains(c.TrackUniqueHash))
                    .OrderBy(c => c.TimestampInSeconds)
                    .ToListAsync();

                foreach (var cue in cues)
                {
                    if (!cuesByHash.TryGetValue(cue.TrackUniqueHash, out var bucket))
                    {
                        bucket = new List<CuePointEntity>();
                        cuesByHash[cue.TrackUniqueHash] = bucket;
                    }
                    bucket.Add(cue);
                }
            }

            var rbTracks = new List<RekordboxTrack>();
            int trackId = 1;

            foreach (var track in trackList)
            {
                if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
                    continue;

                var fileInfo = new FileInfo(track.ResolvedFilePath);
                var rbTrack = new RekordboxTrack
                {
                    TrackID = trackId++,
                    Name = track.Title ?? "Unknown Title",
                    Artist = track.Artist ?? "Unknown Artist",
                    Album = track.Album ?? "Unknown Album",
                    Genre = track.Genres ?? "",
                    Size = fileInfo.Length,
                    TotalTime = Math.Max(0, track.CanonicalDuration.GetValueOrDefault() / 1000),
                    DateAdded = track.AddedAt.ToString("yyyy-MM-dd"),
                    BitRate = track.Bitrate ?? 0,
                    AverageBpm = track.BPM ?? 0,
                    Tonality = track.MusicalKey ?? "",
                    Location = "file://localhost/" + track.ResolvedFilePath.Replace("\\", "/")
                };
                rbTracks.Add(rbTrack);
            }

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("DJ_PLAYLISTS",
                    new XAttribute("Version", "1.0.0"),
                    new XElement("PRODUCT",
                        new XAttribute("Name", "rekordbox"),
                        new XAttribute("Version", "6.0.0"),
                        new XAttribute("Company", "Pioneer DJ")),
                    new XElement("COLLECTION",
                        new XAttribute("Entries", rbTracks.Count),
                        rbTracks.Select((t, idx) =>
                        {
                            var srcTrack = trackList.ElementAtOrDefault(idx);
                            var hash = srcTrack?.TrackUniqueHash ?? string.Empty;
                            var cues = cuesByHash.TryGetValue(hash, out var list) ? list : new List<CuePointEntity>();
                            var bpm = t.AverageBpm;

                            return BuildTrackElement(t, cues, srcTrack, bpm);
                        })
                    ),
                    new XElement("PLAYLISTS",
                        new XElement("NODE",
                            new XAttribute("Type", "0"),
                            new XAttribute("Name", "ROOT"),
                            new XElement("NODE",
                                new XAttribute("Name", playlistName),
                                new XAttribute("Type", "1"),
                                new XAttribute("Entries", rbTracks.Count),
                                rbTracks.Select(t => new XElement("TRACK", new XAttribute("Key", t.TrackID)))
                            )
                        )
                    )
                )
            );

            await Task.Run(() => doc.Save(targetPath));
            _logger.LogInformation("Rekordbox XML export completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to Rekordbox XML");
            throw;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // XML node builders
    // ──────────────────────────────────────────────────────────────────────

    private static XElement BuildTrackElement(RekordboxTrack t, List<CuePointEntity> dbCues, PlaylistTrack? src, double bpm)
    {
        var trackElem = new XElement("TRACK",
            new XAttribute("TrackID", t.TrackID),
            new XAttribute("Name", t.Name),
            new XAttribute("Artist", t.Artist),
            new XAttribute("Album", t.Album),
            new XAttribute("Genre", t.Genre),
            new XAttribute("Kind", t.Kind),
            new XAttribute("Size", t.Size),
            new XAttribute("TotalTime", t.TotalTime),
            new XAttribute("DateAdded", t.DateAdded),
            new XAttribute("BitRate", t.BitRate),
            new XAttribute("SampleRate", t.SampleRate),
            new XAttribute("AverageBpm", t.AverageBpm.ToString("F2")),
            new XAttribute("Tonality", t.Tonality),
            new XAttribute("Location", t.Location)
        );

        // TEMPO node — one grid anchor at the beginning of the track
        if (bpm > 0)
        {
            trackElem.Add(new XElement("TEMPO",
                new XAttribute("Inizio", "0.000"),
                new XAttribute("Bpm", bpm.ToString("F2")),
                new XAttribute("Metro", "4/4"),
                new XAttribute("Battito", "1")
            ));
        }

        // Merge DB cue points + user cues from CuePointsJson
        var allCues = BuildCueList(dbCues, src?.CuePointsJson);

        int padNum = 0;
        foreach (var cue in allCues.Take(32)) // Rekordbox XML supports up to 8 hot cues + memory cues
        {
            var (r, g, b) = HexToRgb(cue.Color);
            int num = padNum < 8 ? padNum : -1; // 0-7 = hot cue pad; -1 = memory cue
            trackElem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", cue.Name),
                new XAttribute("Type", "0"),
                new XAttribute("Start", cue.Timestamp.ToString("F3")),
                new XAttribute("Num", num),
                new XAttribute("Red", r),
                new XAttribute("Green", g),
                new XAttribute("Blue", b)
            ));
            padNum++;
        }

        return trackElem;
    }

    /// <summary>
    /// Merges DB-stored <see cref="CuePointEntity"/> rows with user-placed
    /// <see cref="OrbitCue"/> objects serialised in <paramref name="cuePointsJson"/>.
    /// DB cues come first (auto-generated structural cues); user cues follow.
    /// Deduplicates by timestamp within a 50 ms window.
    /// </summary>
    private static List<OrbitCue> BuildCueList(List<CuePointEntity> dbCues, string? cuePointsJson)
    {
        var result = new List<OrbitCue>();

        // 1. Auto-generated structural cues from DB
        foreach (var c in dbCues.OrderBy(c => c.TimestampInSeconds))
        {
            result.Add(new OrbitCue
            {
                Timestamp = c.TimestampInSeconds,
                Name = c.Label,
                Color = c.Color,
                Source = CueSource.Auto
            });
        }

        // 2. User-placed cues from JSON field
        if (!string.IsNullOrWhiteSpace(cuePointsJson))
        {
            try
            {
                var userCues = JsonSerializer.Deserialize<List<OrbitCue>>(cuePointsJson);
                if (userCues != null)
                {
                    foreach (var uc in userCues.OrderBy(u => u.Timestamp))
                    {
                        // Dedup: skip if a cue within 50 ms already exists
                        bool duplicate = result.Any(e => Math.Abs(e.Timestamp - uc.Timestamp) < 0.05);
                        if (!duplicate) result.Add(uc);
                    }
                }
            }
            catch
            {
                // Malformed JSON — ignore user cues for this track
            }
        }

        return result.OrderBy(c => c.Timestamp).ToList();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Color helper
    // ──────────────────────────────────────────────────────────────────────

    private static (int R, int G, int B) HexToRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (255, 255, 255);
        try
        {
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex[2..4], 16);
            int b = Convert.ToInt32(hex[4..6], 16);
            return (r, g, b);
        }
        catch
        {
            return (255, 255, 255);
        }
    }


    /// <summary>
    /// Phase 12: Enhanced CSV Export with Forensic Metrics
    /// </summary>
    public async Task ExportToCsvWithForensicsAsync(string playlistName, IEnumerable<LibraryEntryEntity> entries, string targetPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to CSV with forensics: {Path}", playlistName, targetPath);

            var csvLines = new List<string>
            {
                // Phase 12: Enhanced CSV headers with forensic data
                "Title,Artist,Album,Genre,BPM,Key,Bitrate,Duration,FilePath,AddedAt,IsTranscoded"
            };

            foreach (var entry in entries)
            {
                // Escape commas and quotes in CSV fields
                string EscapeCsvField(string? field) =>
                    field?.Replace("\"", "\"\"").Replace(",", ";") ?? "";

                var line = string.Join(",", new[]
                {
                    $"\"{EscapeCsvField(entry.Title)}\"",
                    $"\"{EscapeCsvField(entry.Artist)}\"",
                    $"\"{EscapeCsvField(entry.Album)}\"",
                    $"\"{EscapeCsvField(entry.Genres)}\"",
                    entry.BPM?.ToString() ?? "",
                    $"\"{EscapeCsvField(entry.MusicalKey)}\"",
                    entry.Bitrate.ToString(),
                    entry.DurationSeconds?.ToString() ?? "",
                    $"\"{EscapeCsvField(entry.FilePath)}\"",
                    entry.AddedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    entry.IsTranscoded?.ToString() ?? "false"
                });

                csvLines.Add(line);
            }

            await File.WriteAllLinesAsync(targetPath, csvLines);
            _logger.LogInformation("CSV export with forensics completed successfully. Exported {Count} tracks.", entries.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to CSV with forensics");
            throw;
        }
    }
}
