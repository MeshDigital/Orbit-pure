using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services;

/// <summary>
/// Creates and reads .orbsession bundle files — portable ZIP archives that contain
/// workstation session state + rich per-track metadata so a collaborator can open
/// the session on a different machine (with matching audio files).
///
/// Bundle layout:
///   manifest.json  — version, creation date, track count
///   session.json   — WorkstationSession (decks, mode, timeline viewport)
///   tracks.json    — List of OrbSessionTrack (analysis snapshot per track)
/// </summary>
public class OrbSessionBundleService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private const string ManifestEntry = "manifest.json";
    private const string SessionEntry  = "session.json";
    private const string TracksEntry   = "tracks.json";

    private readonly ILogger<OrbSessionBundleService> _logger;
    private readonly IDbContextFactory<AppDbContext>   _dbFactory;

    public OrbSessionBundleService(
        ILogger<OrbSessionBundleService> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger    = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Exports the current session + playlist tracks to a .orbsession bundle at <paramref name="targetPath"/>.
    /// </summary>
    public async Task ExportAsync(
        WorkstationSession session,
        IReadOnlyList<PlaylistTrack> tracks,
        string targetPath)
    {
        _logger.LogInformation("Exporting .orbsession bundle to {Path}", targetPath);

        var hashes = tracks
            .Select(t => t.TrackUniqueHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct()
            .ToList();

        // Load cue points for all tracks in one query
        Dictionary<string, List<(double Ts, string Label, string Color)>> cuesByHash = new(StringComparer.Ordinal);
        if (hashes.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var cues = await db.CuePoints
                .AsNoTracking()
                .Where(c => hashes.Contains(c.TrackUniqueHash))
                .OrderBy(c => c.TimestampInSeconds)
                .ToListAsync();

            foreach (var c in cues)
            {
                if (!cuesByHash.TryGetValue(c.TrackUniqueHash, out var list))
                    cuesByHash[c.TrackUniqueHash] = list = [];
                list.Add((c.TimestampInSeconds, c.Label, c.Color));
            }
        }

        var orbTracks = tracks.Select(t =>
        {
            var hash = t.TrackUniqueHash ?? "";
            var cues = cuesByHash.TryGetValue(hash, out var cl) ? cl : [];
            return new OrbSessionTrack
            {
                FilePath        = t.ResolvedFilePath,
                TrackUniqueHash = hash,
                Title           = t.Title,
                Artist          = t.Artist,
                Album           = t.Album,
                Genre           = t.Genres,
                BPM             = t.BPM,
                MusicalKey      = t.MusicalKey,
                Energy          = t.Energy,
                ManualEnergy    = t.ManualEnergy,
                DurationMs      = t.CanonicalDuration,
                Bitrate         = t.Bitrate,
                CuePoints       = cues.Select(c => new OrbCuePoint
                {
                    TimestampSeconds = c.Ts,
                    Label = c.Label,
                    Color = c.Color,
                }).ToList(),
            };
        }).ToList();

        var manifest = new OrbSessionManifest
        {
            CreatedUtc = DateTime.UtcNow,
            TrackCount = orbTracks.Count,
        };

        var tmp = targetPath + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);
                WriteJson(zip, ManifestEntry, manifest);
                WriteJson(zip, SessionEntry,  session);
                WriteJson(zip, TracksEntry,   orbTracks);
            });
            File.Move(tmp, targetPath, overwrite: true);
            _logger.LogInformation(".orbsession exported: {Track} tracks", orbTracks.Count);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Reads a .orbsession bundle and returns the embedded session and tracks.
    /// Returns null if the file is corrupt or incompatible.
    /// </summary>
    public async Task<(WorkstationSession Session, List<OrbSessionTrack> Tracks, OrbSessionManifest Manifest)?> ImportAsync(string path)
    {
        try
        {
            return await Task.Run<(WorkstationSession, List<OrbSessionTrack>, OrbSessionManifest)?>(
            () =>
            {
                using var zip = ZipFile.OpenRead(path);
                var manifest = ReadJson<OrbSessionManifest>(zip, ManifestEntry);
                var session  = ReadJson<WorkstationSession>(zip, SessionEntry);
                var tracks   = ReadJson<List<OrbSessionTrack>>(zip, TracksEntry);
                if (manifest == null || session == null || tracks == null) return null;
                return (session, tracks, manifest);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import .orbsession bundle from {Path}", path);
            return null;
        }
    }

    private static void WriteJson<T>(ZipArchive zip, string entryName, T value)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(value, _json));
    }

    private static T? ReadJson<T>(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null) return default;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), _json);
    }
}
