using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library.Rekordbox;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>What to do with a track that already has cues in Rekordbox.</summary>
public enum RekordboxWriteMode
{
    /// <summary>Leave it alone; only tracks without any Rekordbox cues are written (the default).</summary>
    FillEmpty,
    /// <summary>Delete Rekordbox's cues on that track and write ORBIT's instead.</summary>
    Replace,
}

/// <summary>One djmdCue row as ORBIT plans it. Kind 0 = memory cue; hot cues A–H = 1,2,3,5,6,7,8,9.
/// OutMsec is -1 for a point cue, the loop end for a loop.</summary>
public sealed record RekordboxPlannedCue(int Kind, long InMsec, long OutMsec, int ColorTableIndex, string? Comment);

/// <summary>One file and the cues ORBIT would give it in Rekordbox.</summary>
public sealed record RekordboxPlannedTrack(string Path, string Label, IReadOnlyList<RekordboxPlannedCue> Cues);

/// <summary>Optional playlist to create or top up with missing tracks in Rekordbox's library.</summary>
public sealed record RekordboxPlannedPlaylist(string Name, IReadOnlyList<string> TrackPaths);

/// <summary>One ORBIT-made copy of Rekordbox's library, taken before a write.</summary>
public sealed record RekordboxBackup(string Folder, DateTime CreatedLocal, string? SourcePath, long Bytes);

public sealed record RekordboxWriteReport(
    bool Success,
    string Message,
    bool DryRun,
    int TracksWritten,
    int CuesWritten,
    int CuesRemoved,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> SkippedHasCues,
    IReadOnlyList<string> NotInRekordbox,
    IReadOnlyList<string> Invalid,
    string? BackupFolder,
    int Verified,
    IReadOnlyList<string> VerifyMismatches,
    string? PlaylistName = null,
    bool PlaylistCreated = false,
    int PlaylistTracksAdded = 0,
    string? PlaylistId = null,
    string? PlaylistParentId = null,
    long PlaylistUpdatedAtMs = 0,
    bool MasterPlaylistsXmlUpdated = false)
{
    public static RekordboxWriteReport Fail(string message, bool dryRun, string? backupFolder = null) =>
        new(false, message, dryRun, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), backupFolder, 0, Array.Empty<string>());
}

/// <summary>
/// Opt-in (Settings → Advanced → Rekordbox library write, off by default): writes ORBIT's cues
/// straight into Rekordbox's own library database (master.db), so they show up in Rekordbox without
/// an XML import. Pioneer doesn't support third-party writes, so every write is guarded:
///   1. only when the user turned it on and Rekordbox (and its agent) are closed;
///   2. a full copy of master.db (+ WAL/SHM) goes to %AppData%\ORBIT\Backups\Rekordbox first — no
///      backup, no write; <see cref="RestoreAsync"/> puts one back;
///   3. the bridge refuses unknown database versions, writes in one transaction, reads the result
///      back before committing, and runs SQLite's quick_check afterwards;
///   4. ORBIT then re-reads Rekordbox's cues and compares every written track with the plan.
/// Tracks that already have cues in Rekordbox are skipped unless the user picks Replace. Only files
/// already in the Rekordbox collection can be written — adding tracks is left to Rekordbox.
/// Cues use exactly the XML export's layout (<see cref="RekordboxCueLayout"/>).
/// </summary>
public sealed class RekordboxDbWriteService
{
    private const int BackupsToKeep = 10;
    private static readonly string[] BlockingProcesses = { "rekordbox", "rekordboxAgent" };

    private readonly ILogger<RekordboxDbWriteService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RekordboxLibraryService _library;
    private readonly AppConfig _config;

    public RekordboxDbWriteService(
        ILogger<RekordboxDbWriteService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        RekordboxLibraryService library,
        AppConfig config)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _library = library;
        _config = config;
    }

    /// <summary>Where the pre-write copies of master.db go. Tests point this at a temp folder.</summary>
    internal string BackupRoot { get; set; } = Path.Combine(OrbitPaths.AppDataDirectory, "Backups", "Rekordbox");

    /// <summary>Tests replace the running-process check.</summary>
    internal Func<IReadOnlyList<string>> RunningBlockers { get; set; } = FindRunningRekordbox;

    public bool IsEnabled => _config.EnableRekordboxDbWrite;

    public bool IsAvailable => _library.IsAvailable;

    public string? DatabasePath => _library.DatabasePath;

    // ─── Plan ────────────────────────────────────────────────────────────────

    /// <summary>
    /// ORBIT's cues for each file (one entry per file; files without a path or without cues are left
    /// out), laid out exactly as the XML export would.
    /// </summary>
    public async Task<IReadOnlyList<RekordboxPlannedTrack>> BuildPlanAsync(IEnumerable<PlaylistTrack> tracks, CancellationToken ct = default)
    {
        var unique = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.ResolvedFilePath))
            .GroupBy(t => t.ResolvedFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var hashes = unique.Select(t => t.TrackUniqueHash).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();

        var cuesByHash = new Dictionary<string, List<CuePointEntity>>(StringComparer.Ordinal);
        if (hashes.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var cues = await db.CuePoints.AsNoTracking()
                .Where(c => hashes.Contains(c.TrackUniqueHash))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var cue in cues)
            {
                if (!cuesByHash.TryGetValue(cue.TrackUniqueHash, out var bucket))
                    cuesByHash[cue.TrackUniqueHash] = bucket = new List<CuePointEntity>();
                bucket.Add(cue);
            }
        }

        var plan = new List<RekordboxPlannedTrack>();
        foreach (var track in unique)
        {
            string label = $"{track.Artist} - {track.Title}";
            var dbCues = cuesByHash.TryGetValue(track.TrackUniqueHash ?? "", out var list) ? list : new List<CuePointEntity>();
            var all = RekordboxCueLayout.MergeCueSources(dbCues, track.CuePointsJson, label, _logger);
            var planned = ToPlannedCues(RekordboxCueLayout.Build(all, track.BPM ?? 0, _config.RekordboxDuplicateHotCuesAsMemory));
            if (planned.Count > 0)
                plan.Add(new RekordboxPlannedTrack(track.ResolvedFilePath, label, planned));
        }
        return plan;
    }

    /// <summary>
    /// Rekordbox's encoding of the shared layout. A pad can hold one cue: a later mark on a pad that
    /// is already used becomes a memory cue. Colours map to Rekordbox's hot cue colour table; memory
    /// cues stay uncoloured, as Rekordbox shows them.
    /// </summary>
    internal static List<RekordboxPlannedCue> ToPlannedCues(IEnumerable<RekordboxMark> marks)
    {
        var usedPads = new HashSet<int>();
        var cues = new List<RekordboxPlannedCue>();
        foreach (var m in marks)
        {
            if (double.IsNaN(m.StartSeconds) || m.StartSeconds < 0) continue;
            long inMsec = (long)Math.Round(m.StartSeconds * 1000, MidpointRounding.AwayFromZero);
            long outMsec = -1;
            if (m.IsLoop)
            {
                outMsec = (long)Math.Round(m.EndSeconds * 1000, MidpointRounding.AwayFromZero);
                if (outMsec <= inMsec) continue; // an empty loop isn't a loop
            }
            int pad = m.Num is >= 0 and <= 7 && usedPads.Add(m.Num) ? m.Num : -1;
            int kind = pad >= 0 ? KindForPad(pad) : 0;
            int colour = kind > 0 ? RekordboxCueColors.IndexForHex(m.ColorHex) : 0;
            cues.Add(new RekordboxPlannedCue(kind, inMsec, outMsec, colour, string.IsNullOrWhiteSpace(m.Name) ? null : m.Name.Trim()));
        }
        return cues;
    }

    /// <summary>Hot cue pad 0–7 (A–H) → djmdCue.Kind (4 is never used by Rekordbox).</summary>
    internal static int KindForPad(int pad) => pad switch
    {
        0 => 1, 1 => 2, 2 => 3, 3 => 5, 4 => 6, 5 => 7, 6 => 8, 7 => 9,
        _ => 0,
    };

    // ─── Preview / write ─────────────────────────────────────────────────────

    /// <summary>What a write would do, without changing anything (the bridge opens the file read-only).</summary>
    public Task<RekordboxWriteReport> PreviewAsync(IReadOnlyList<RekordboxPlannedTrack> plan, RekordboxWriteMode mode, CancellationToken ct = default, RekordboxPlannedPlaylist? playlist = null) =>
        RunAsync(plan, mode, dryRun: true, backupFolder: null, ct, playlist);

    /// <summary>Backs Rekordbox's library up, writes the plan (and optional playlist sync), and verifies it by reading it back.</summary>
    public async Task<RekordboxWriteReport> WriteAsync(IReadOnlyList<RekordboxPlannedTrack> plan, RekordboxWriteMode mode, CancellationToken ct = default, RekordboxPlannedPlaylist? playlist = null)
    {
        if (!IsEnabled)
            return RekordboxWriteReport.Fail("Writing to Rekordbox's library is turned off. Turn it on in Settings → Advanced first.", dryRun: false);
        if (plan.Count == 0 && playlist == null)
            return RekordboxWriteReport.Fail("None of these tracks have ORBIT cues to write.", dryRun: false);

        var blockers = RunningBlockers();
        if (blockers.Count > 0)
            return RekordboxWriteReport.Fail($"Quit Rekordbox first ({string.Join(", ", blockers)} is running; also close the agent in the system tray). Nothing was written.", dryRun: false);

        var db = DatabasePath;
        if (db == null) return RekordboxWriteReport.Fail("No Rekordbox 6/7 library found on this computer.", dryRun: false);

        string backup;
        try
        {
            backup = CreateBackup(db);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rekordbox backup failed; nothing was written");
            return RekordboxWriteReport.Fail($"Couldn't back up Rekordbox's library, so nothing was written: {ex.Message}", dryRun: false);
        }

        var report = await RunAsync(plan, mode, dryRun: false, backup, ct, playlist).ConfigureAwait(false);
        if (!report.Success) return report;

        // Sync Rekordbox's companion masterPlaylists6.xml after master.db commits cleanly.
        string? xmlError = null;
        string xmlNote = "";
        bool xmlUpdated = false;
        if (!string.IsNullOrWhiteSpace(report.PlaylistName)
            && (report.PlaylistCreated || report.PlaylistTracksAdded > 0)
            && !string.IsNullOrWhiteSpace(report.PlaylistId))
        {
            string xmlPath = RekordboxMasterPlaylistsXml.GetXmlPathForDatabase(db);
            try
            {
                var xmlResult = RekordboxMasterPlaylistsXml.UpsertPlaylistNode(
                    xmlPath, report.PlaylistId, report.PlaylistParentId, report.PlaylistUpdatedAtMs, dryRun: false);
                if (xmlResult.FileExists && xmlResult.Changed)
                {
                    xmlUpdated = true;
                    xmlNote = xmlResult.CreatedNode
                        ? " Added playlist entry to masterPlaylists6.xml."
                        : " Updated masterPlaylists6.xml.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                       or InvalidOperationException or FormatException)
            {
                _logger.LogWarning(ex, "Rekordbox master.db was updated, but syncing masterPlaylists6.xml failed. Backup: {Backup}", backup);
                xmlError = $"master.db was updated, but masterPlaylists6.xml could not be synced ({ex.Message}). Restore the backup in {backup} (Settings → Advanced) if playlists look out of sync in Rekordbox.";
            }
        }

        // Independent check: read Rekordbox's cues back and compare every written track with the plan.
        var byPath = plan.ToDictionary(p => RekordboxLibraryService.NormalizePath(p.Path), StringComparer.Ordinal);
        var read = await _library.ReadCuesAsync(ct).ConfigureAwait(false);
        var mismatches = new List<string>();
        int verified = 0;
        if (!read.Success)
        {
            mismatches.Add($"Couldn't read Rekordbox's library back: {read.Message}");
        }
        else
        {
            var actual = read.Tracks.Where(t => t.Path != null)
                .GroupBy(t => RekordboxLibraryService.NormalizePath(t.Path!))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var path in report.WrittenPaths)
            {
                var key = RekordboxLibraryService.NormalizePath(path);
                if (!byPath.TryGetValue(key, out var planned)) continue;
                var expected = planned.Cues.Select(c => (c.Kind, c.InMsec, c.OutMsec)).OrderBy(x => x).ToList();
                var got = actual.TryGetValue(key, out var t)
                    ? t.Cues.Select(c => (c.Kind, c.InMsec, c.OutMsec)).OrderBy(x => x).ToList()
                    : new List<(int, long, long)>();
                if (expected.SequenceEqual(got)) verified++;
                else mismatches.Add(planned.Label);
            }
        }

        if (mismatches.Count > 0)
            _logger.LogError("Rekordbox write verification: {Bad} of {Total} tracks don't match the plan. Backup: {Backup}", mismatches.Count, report.WrittenPaths.Count, backup);
        else
            _logger.LogInformation("Rekordbox write verified: {Verified} tracks, {Cues} cues. Backup: {Backup}", verified, report.CuesWritten, backup);

        bool allOk = mismatches.Count == 0 && xmlError == null;
        string finalMessage = report.Message + xmlNote;
        if (mismatches.Count > 0)
            finalMessage += $" But {mismatches.Count} track(s) didn't read back as planned — restore the backup from Settings → Advanced if Rekordbox looks wrong.";
        if (xmlError != null)
            finalMessage += " Warning: " + xmlError;

        return report with
        {
            Success = allOk,
            Message = finalMessage,
            Verified = verified,
            VerifyMismatches = mismatches,
            MasterPlaylistsXmlUpdated = xmlUpdated,
        };
    }

    private async Task<RekordboxWriteReport> RunAsync(IReadOnlyList<RekordboxPlannedTrack> plan, RekordboxWriteMode mode, bool dryRun, string? backupFolder, CancellationToken ct, RekordboxPlannedPlaylist? playlist = null)
    {
        var db = DatabasePath;
        if (db == null) return RekordboxWriteReport.Fail("No Rekordbox 6/7 library found on this computer.", dryRun, backupFolder);
        if (!File.Exists(_library.BridgePath)) return RekordboxWriteReport.Fail("The Rekordbox bridge isn't installed with this build of ORBIT.", dryRun, backupFolder);

        string planFile = Path.Combine(Path.GetTempPath(), $"orbit-rekordbox-plan-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(planFile, SerializePlan(plan, mode, playlist), ct).ConfigureAwait(false);
            var args = new List<string> { "write-cues", "--db", db, "--plan", planFile };
            if (dryRun) args.Add("--dry-run");

            var (exit, stdout, stderr) = await _library.RunBridgeExclusiveAsync(args, ct).ConfigureAwait(false);
            string reason = string.IsNullOrWhiteSpace(stderr) ? "no details" : stderr.Trim().Split('\n')[0].Trim();
            if (exit != 0)
            {
                _logger.LogWarning("Rekordbox cue {Action} failed (exit {Exit}): {Reason}", dryRun ? "preview" : "write", exit, reason);
                string message = exit switch
                {
                    3 => "Couldn't unlock Rekordbox's library. This Rekordbox version may use a different key.",
                    5 => $"{reason} Nothing was written.",
                    6 => $"{reason} Nothing was written.",
                    _ => $"Rekordbox's library wasn't changed: {reason}",
                };
                return RekordboxWriteReport.Fail(message, dryRun, backupFolder);
            }
            return ParseReport(stdout, dryRun, backupFolder, db);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Rekordbox cue {Action} failed", dryRun ? "preview" : "write");
            return RekordboxWriteReport.Fail($"Rekordbox cue {(dryRun ? "preview" : "write")} failed: {ex.Message}", dryRun, backupFolder);
        }
        finally
        {
            try { File.Delete(planFile); }
            catch (IOException ex) { _logger.LogDebug(ex, "Couldn't delete the temporary cue plan {File}", planFile); }
        }
    }

    internal static string SerializePlan(IReadOnlyList<RekordboxPlannedTrack> plan, RekordboxWriteMode mode, RekordboxPlannedPlaylist? playlist = null) =>
        JsonSerializer.Serialize(new
        {
            mode = mode == RekordboxWriteMode.Replace ? "replace" : "fillEmpty",
            tracks = plan.Select(t => new
            {
                path = t.Path,
                cues = t.Cues.Select(c => new { kind = c.Kind, inMsec = c.InMsec, outMsec = c.OutMsec, colorTableIndex = c.ColorTableIndex, comment = c.Comment }),
            }),
            playlist = playlist == null ? null : new
            {
                name = playlist.Name,
                trackPaths = playlist.TrackPaths,
            },
        });

    internal static RekordboxWriteReport ParseReport(string json, bool dryRun, string? backupFolder, string? databasePath = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        static List<string> Strings(JsonElement e, string name) =>
            e.GetProperty(name).EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var written = r.GetProperty("written").EnumerateArray().Select(w => w.GetProperty("path").GetString() ?? "").ToList();
        int cues = r.GetProperty("cuesWritten").GetInt32();
        int removed = r.GetProperty("cuesRemoved").GetInt32();
        var skipped = Strings(r, "skippedHasCues");
        var missing = Strings(r, "notInRekordbox");
        var invalid = Strings(r, "invalid");
        string? playlistName = r.TryGetProperty("playlistName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
        string? playlistId = r.TryGetProperty("playlistId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null;
        string? playlistParentId = r.TryGetProperty("playlistParentId", out var ppid) && ppid.ValueKind == JsonValueKind.String ? ppid.GetString() : null;
        bool playlistCreated = r.TryGetProperty("playlistCreated", out var pc) && pc.ValueKind == JsonValueKind.True;
        int playlistAdded = r.TryGetProperty("playlistTracksAdded", out var pa) && pa.ValueKind == JsonValueKind.Number ? pa.GetInt32() : 0;
        long playlistUpdatedAtMs = r.TryGetProperty("playlistUpdatedAtMs", out var pts) && pts.ValueKind == JsonValueKind.Number
            ? pts.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string? quick = r.TryGetProperty("quickCheck", out var q) ? q.GetString() : null;
        if (!dryRun && quick != null && quick != "ok")
            return RekordboxWriteReport.Fail($"Rekordbox's library failed SQLite's integrity check after the write ({quick}). Restore the backup from Settings → Advanced.", dryRun, backupFolder);

        string verb = dryRun ? "Would write" : "Wrote";
        string playlistNote = "";
        if (!string.IsNullOrWhiteSpace(playlistName))
        {
            if (playlistCreated)
                playlistNote = dryRun
                    ? $"; would create playlist '{playlistName}' (+{playlistAdded} track(s))"
                    : $"; created playlist '{playlistName}' (+{playlistAdded} track(s))";
            else if (playlistAdded > 0)
                playlistNote = dryRun
                    ? $"; would add {playlistAdded} missing track(s) to playlist '{playlistName}'"
                    : $"; added {playlistAdded} missing track(s) to playlist '{playlistName}'";
            else
                playlistNote = $"; playlist '{playlistName}' is already up to date";

            if (dryRun && (playlistCreated || playlistAdded > 0) && !string.IsNullOrWhiteSpace(databasePath))
            {
                string xmlPath = RekordboxMasterPlaylistsXml.GetXmlPathForDatabase(databasePath);
                if (File.Exists(xmlPath))
                {
                    playlistNote += playlistCreated
                        ? " and add it to masterPlaylists6.xml"
                        : " and update masterPlaylists6.xml";
                }
            }
        }
        string message = $"{verb} {SLSKDONET.Utils.CountFormat.N(cues)} cue(s) to {written.Count} track(s)"
            + (removed > 0 ? $", replacing {SLSKDONET.Utils.CountFormat.N(removed)} Rekordbox cue(s)" : "")
            + playlistNote
            + (skipped.Count > 0 ? $"; {skipped.Count} track(s) skipped (they already have cues in Rekordbox)" : "")
            + (missing.Count > 0 ? $"; {missing.Count} track(s) aren't in your Rekordbox collection" : "")
            + (invalid.Count > 0 ? $"; {invalid.Count} track(s) had cues Rekordbox can't take" : "")
            + ".";
        return new RekordboxWriteReport(true, message, dryRun, written.Count, cues, removed, written, skipped, missing, invalid,
            backupFolder, 0, Array.Empty<string>(), playlistName, playlistCreated, playlistAdded,
            playlistId, playlistParentId, playlistUpdatedAtMs);
    }

    // ─── Backups ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies master.db and its WAL/SHM side files into a new timestamped folder and checks every
    /// copy's size. Throws (and leaves nothing half-made) when any copy fails. Keeps the newest ten.
    /// </summary>
    internal string CreateBackup(string databasePath, string tag = "")
    {
        string folder = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + tag);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = databasePath + suffix;
                if (!File.Exists(source)) continue;
                var target = Path.Combine(folder, Path.GetFileName(source));
                File.Copy(source, target, overwrite: false);
                if (new FileInfo(target).Length != new FileInfo(source).Length)
                    throw new IOException($"The backup copy of {Path.GetFileName(source)} is incomplete.");
            }
            string xmlSource = RekordboxMasterPlaylistsXml.GetXmlPathForDatabase(databasePath);
            if (File.Exists(xmlSource))
            {
                string xmlTarget = Path.Combine(folder, RekordboxMasterPlaylistsXml.FileName);
                File.Copy(xmlSource, xmlTarget, overwrite: false);
                if (new FileInfo(xmlTarget).Length != new FileInfo(xmlSource).Length)
                    throw new IOException($"The backup copy of {RekordboxMasterPlaylistsXml.FileName} is incomplete.");
            }
            File.WriteAllText(Path.Combine(folder, "source.txt"), databasePath);
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException cleanup) { _logger.LogWarning(cleanup, "Couldn't remove the incomplete backup {Folder}", folder); }
            throw;
        }
        _logger.LogInformation("Rekordbox library backed up to {Folder}", folder);
        PruneBackups();
        return folder;
    }

    public IReadOnlyList<RekordboxBackup> ListBackups()
    {
        if (!Directory.Exists(BackupRoot)) return Array.Empty<RekordboxBackup>();
        var list = new List<RekordboxBackup>();
        foreach (var dir in Directory.GetDirectories(BackupRoot))
        {
            var db = Path.Combine(dir, "master.db");
            if (!File.Exists(db)) continue;
            string? source = File.Exists(Path.Combine(dir, "source.txt")) ? File.ReadAllText(Path.Combine(dir, "source.txt")).Trim() : null;
            long bytes = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
            list.Add(new RekordboxBackup(dir, Directory.GetCreationTime(dir), source, bytes));
        }
        return list.OrderByDescending(b => Path.GetFileName(b.Folder), StringComparer.Ordinal).ToList();
    }

    private void PruneBackups()
    {
        foreach (var old in ListBackups().Skip(BackupsToKeep))
        {
            try { Directory.Delete(old.Folder, recursive: true); }
            catch (IOException ex) { _logger.LogWarning(ex, "Couldn't remove the old Rekordbox backup {Folder}", old.Folder); }
        }
    }

    /// <summary>
    /// Puts a backup back over Rekordbox's library. Rekordbox must be closed. The current state is
    /// backed up first (so a restore can be undone), and a WAL file the backup didn't have is removed
    /// so stale changes can't be replayed on top of the restored database.
    /// </summary>
    public Task<(bool Success, string Message)> RestoreAsync(RekordboxBackup backup)
    {
        var blockers = RunningBlockers();
        if (blockers.Count > 0)
            return Task.FromResult((false, $"Quit Rekordbox first ({string.Join(", ", blockers)} is running). Nothing was restored."));
        var target = backup.SourcePath ?? DatabasePath;
        if (target == null) return Task.FromResult((false, "Couldn't tell which Rekordbox library this backup belongs to."));
        if (DatabasePath != null && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult((false, $"This backup belongs to a different Rekordbox library ({target}). Nothing was restored."));

        try
        {
            string safety = File.Exists(target) ? CreateBackup(target, "-before-restore") : "(none)";
            File.Copy(Path.Combine(backup.Folder, "master.db"), target, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var saved = Path.Combine(backup.Folder, "master.db" + suffix);
                if (File.Exists(saved)) File.Copy(saved, target + suffix, overwrite: true);
                else if (File.Exists(target + suffix)) File.Delete(target + suffix);
            }
            string savedXml = Path.Combine(backup.Folder, RekordboxMasterPlaylistsXml.FileName);
            string targetXml = RekordboxMasterPlaylistsXml.GetXmlPathForDatabase(target);
            string xmlRestoreNote = "";
            if (File.Exists(savedXml))
            {
                File.Copy(savedXml, targetXml, overwrite: true);
            }
            else if (File.Exists(targetXml))
            {
                xmlRestoreNote = $" Note: this older backup didn't include {RekordboxMasterPlaylistsXml.FileName}, so that file wasn't changed.";
            }
            _logger.LogInformation("Rekordbox library restored from {Backup} (previous state saved to {Safety})", backup.Folder, safety);
            return Task.FromResult((true, $"Restored Rekordbox's library from {Path.GetFileName(backup.Folder)}. The state before the restore was saved too.{xmlRestoreNote}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Restoring Rekordbox backup {Backup} failed", backup.Folder);
            return Task.FromResult((false, $"Restore failed: {ex.Message}. Your backups are untouched in {BackupRoot}."));
        }
    }

    private static IReadOnlyList<string> FindRunningRekordbox()
    {
        var running = new List<string>();
        foreach (var name in BlockingProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) running.Add(name);
            foreach (var p in processes) p.Dispose();
        }
        return running;
    }
}
