using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF6 (#348): replaces a lower-quality library file with a better one in place. The new file moves
/// into the old file's folder under the old base name with the new extension, the old file goes to
/// <see cref="OrbitTrash"/>, and the three path columns are repointed at the new path.
/// </summary>
public sealed class QualityUpgradeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<QualityUpgradeService> _logger;

    public QualityUpgradeService(IDbContextFactory<AppDbContext> dbFactory, ILogger<QualityUpgradeService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<string> ReplaceAsync(
        string hash,
        string newFile,
        string oldFile,
        string downloadDir,
        bool allowProtectedOverride = false,
        double offsetSeconds = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newFile) || !File.Exists(newFile))
            throw new FileNotFoundException("Replacement audio file not found.", newFile);
        if (string.IsNullOrWhiteSpace(oldFile) || !File.Exists(oldFile))
            throw new FileNotFoundException("Original audio file not found.", oldFile);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false);

        // LF4 path protection guard: never auto-replace locked or linked-folder files unless explicitly overridden.
        if (!allowProtectedOverride && entry is not null)
        {
            if (entry.PathLockedAt is not null)
                throw new InvalidOperationException($"Track {hash} path is locked (exported/protected); unlock before upgrading.");
            if (string.Equals(entry.FileOrigin, FileOriginDeriver.LinkedFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Track {hash} lives in a linked folder; ORBIT does not replace linked files automatically.");
        }

        var target = Path.Combine(
            Path.GetDirectoryName(oldFile)!,
            Path.GetFileNameWithoutExtension(oldFile) + Path.GetExtension(newFile));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Trash the old file FIRST so same-extension upgrades (e.g. 128kbps .mp3 -> 320kbps .mp3)
        // don't collide at `target`. Roll back both filesystem operations if anything fails.
        var trashedOld = OrbitTrash.TrashFile(oldFile, downloadDir);
        var movedNew = false;
        try
        {
            File.Move(newFile, target);
            movedNew = true;

            if (entry != null)
            {
                entry.FilePath = target;
                entry.FilePathUpdatedAt = DateTime.UtcNow;
            }

            var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == hash).ToListAsync(ct).ConfigureAwait(false);
            foreach (var t in playlistTracks)
                t.ResolvedFilePath = target;

            var tracks = await db.Tracks.Where(t => t.GlobalId == hash).ToListAsync(ct).ConfigureAwait(false);
            foreach (var t in tracks)
                t.LocalFilePath = target;

            // LF6: shift cue/beatgrid timestamps by the measured decoder offset (encoder delay/padding),
            // so they stay on the beat after swapping one rendition for the other. Clamp at 0 so early
            // cues/beats never go negative when removing positive encoder delay.
            if (offsetSeconds != 0 && double.IsFinite(offsetSeconds))
            {
                var cues = await db.CuePoints.Where(c => c.TrackUniqueHash == hash).ToListAsync(ct).ConfigureAwait(false);
                foreach (var cue in cues)
                    cue.TimestampInSeconds = Math.Max(0.0, cue.TimestampInSeconds + offsetSeconds);

                var features = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == hash, ct).ConfigureAwait(false);
                if (features != null && !string.IsNullOrWhiteSpace(features.BeatGridJson) && features.BeatGridJson != "[]")
                {
                    try
                    {
                        var beats = System.Text.Json.JsonSerializer.Deserialize<List<double>>(features.BeatGridJson);
                        if (beats != null)
                            features.BeatGridJson = System.Text.Json.JsonSerializer.Serialize(
                                beats.Select(b => Math.Max(0.0, b + offsetSeconds)).ToList());
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogWarning(ex, "Skipping BeatGridJson offset shift for {Hash}: malformed JSON.", hash);
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quality upgrade failed for {Hash}; rolling back filesystem changes.", hash);
            if (movedNew && File.Exists(target) && !File.Exists(newFile))
            {
                try { File.Move(target, newFile); }
                catch (Exception rbEx) { _logger.LogWarning(rbEx, "Rollback: could not move {Target} back to {NewFile}", target, newFile); }
            }
            if (File.Exists(trashedOld) && !File.Exists(oldFile))
            {
                try { OrbitTrash.Restore(trashedOld); }
                catch (Exception rbEx) { _logger.LogWarning(rbEx, "Rollback: could not restore {Trashed} back to {OldFile}", trashedOld, oldFile); }
            }
            throw;
        }

        _logger.LogInformation("Upgraded {Hash}: {Old} -> {New}", hash, oldFile, target);
        return target;
    }
}
