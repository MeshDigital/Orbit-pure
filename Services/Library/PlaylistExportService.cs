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

    /// <param name="pathMap">
    /// Optional override map: local source path → destination path.
    /// When supplied (USB/folder export) the XML Location attribute uses the
    /// destination path instead of the original local path.
    /// </param>
    public async Task ExportToRekordboxXmlAsync(
        string playlistName,
        IEnumerable<PlaylistTrack> tracks,
        string targetPath,
        IReadOnlyDictionary<string, string>? pathMap = null)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to Rekordbox XML: {Path}", playlistName, targetPath);

            // Deduplicate by ResolvedFilePath so the same physical file never appears twice in the XML.
            // A playlist can have multiple PlaylistTrack rows pointing to the same file when a track was
            // re-imported or synced more than once. Keep only the first occurrence per path.
            var trackList = tracks
                .GroupBy(
                    t => string.IsNullOrEmpty(t.ResolvedFilePath)
                        ? t.TrackUniqueHash ?? t.Id.ToString()   // fall back to hash for unresolved tracks
                        : t.ResolvedFilePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

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

            // Build parallel lists so cue look-up stays correct even when some tracks
            // are skipped (file not found). Using a separate source list avoids the
            // index-mismatch bug that occurs when iterating rbTracks by index into trackList.
            var rbTracks = new List<RekordboxTrack>();
            var rbSources = new List<PlaylistTrack>();
            int trackId = 1;

            foreach (var track in trackList)
            {
                // Use dest path when a pathMap is provided (USB/folder export), otherwise use local path
                var effectivePath = pathMap != null && pathMap.TryGetValue(track.ResolvedFilePath ?? "", out var mapped)
                    ? mapped
                    : track.ResolvedFilePath;

                if (string.IsNullOrEmpty(effectivePath) || !File.Exists(effectivePath))
                    continue;

                var fileInfo = new FileInfo(effectivePath);
                // Map energy (0-1 Spotify scale or ManualEnergy 1-10) → Rekordbox 0-255 Rating
                int energyStars = 0;
                if (track.ManualEnergy.HasValue)
                    energyStars = Math.Clamp((int)Math.Round(track.ManualEnergy.Value / 2.0), 1, 5);
                else if (track.Energy.HasValue && track.Energy.Value > 0)
                    energyStars = Math.Clamp((int)Math.Ceiling(track.Energy.Value * 5), 1, 5);
                int rbRating = energyStars > 0 ? energyStars * 51 : 0;

                // Comments: embed Camelot key + energy label for MIK-compatible tagging
                string camelotKey = track.MusicalKey ?? "";
                string energyLabel = energyStars > 0 ? $" Energy:{track.ManualEnergy ?? (int)Math.Round(track.Energy.GetValueOrDefault() * 10)}" : "";
                string comments = string.IsNullOrEmpty(camelotKey) ? energyLabel.TrimStart()
                    : $"{camelotKey}{energyLabel}";

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
                    Location = "file://localhost/" + effectivePath.Replace("\\", "/"),
                    Rating = rbRating,
                    Comments = comments,
                };
                rbTracks.Add(rbTrack);
                rbSources.Add(track);
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
                            var srcTrack = rbSources[idx];
                            var hash = srcTrack.TrackUniqueHash ?? string.Empty;
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
            new XAttribute("Location", t.Location),
            new XAttribute("Rating", t.Rating),
            new XAttribute("Comments", t.Comments)
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

        // Separate loops from point cues — loops get Type=4 with End attribute, point cues get Type=0
        var pointCues = allCues.Where(c => !c.IsLoop).ToList();
        var loopCues  = allCues.Where(c => c.IsLoop).ToList();

        // Assign Rekordbox pad numbers for point cues: use SlotIndex when set (0-7), else -1 (memory cue)
        int nextFreePad = 0;
        foreach (var cue in pointCues.Take(32))
        {
            var (r, g, b) = HexToRgb(cue.Color);
            int num;
            if (cue.SlotIndex >= 0 && cue.SlotIndex <= 7)
                num = cue.SlotIndex;
            else
                num = nextFreePad < 8 ? nextFreePad++ : -1;
            trackElem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", cue.Name),
                new XAttribute("Type", "0"),
                new XAttribute("Start", cue.Timestamp.ToString("F3")),
                new XAttribute("Num", num),
                new XAttribute("Red", r),
                new XAttribute("Green", g),
                new XAttribute("Blue", b)
            ));
        }

        // Loop cues: Type=4, Num=-1, Start + End attributes (Rekordbox format)
        foreach (var loop in loopCues)
        {
            var (r, g, b) = HexToRgb(loop.Color);
            trackElem.Add(new XElement("POSITION_MARK",
                new XAttribute("Name", loop.Name),
                new XAttribute("Type", "4"),
                new XAttribute("Start", loop.Timestamp.ToString("F3")),
                new XAttribute("End", loop.LoopEndSeconds.ToString("F3")),
                new XAttribute("Num", "-1"),
                new XAttribute("Red", r),
                new XAttribute("Green", g),
                new XAttribute("Blue", b)
            ));
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
                Timestamp      = c.TimestampInSeconds,
                Name           = c.Label,
                Color          = c.Color,
                Source         = CueSource.Auto,
                IsLoop         = c.IsLoop,
                LoopEndSeconds = c.LoopEndSeconds,
                SlotIndex      = c.SlotIndex
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
