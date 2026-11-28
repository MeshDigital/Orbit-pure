using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Vault;

/// <summary>
/// Vault phase 1 (epic #632): back the whole library up to a drive the user owns, as plain folders
/// they can open in a file browser. One backup = a consistent snapshot of the library database
/// (SQLite online backup, never a file copy of the live database) plus every music file the library
/// points at, laid out as <c>Music/&lt;root&gt;/&lt;path inside that Library Source&gt;</c>. Incremental:
/// a file whose copy in the Vault already has the same size, audio hash and last-write time is not
/// copied again (the time catches tag edits, which change neither the audio nor, often, the size).
/// Verified: every copy's SHA-256 is computed while copying and stored in the index; each backup
/// re-checks a sample, and "Verify vault" re-reads them all to catch copies that rot on the drive.
/// Restores resolve each root to a folder on THIS computer (remappable: another drive letter, a Mac)
/// and re-point the library at it. Nothing in the Vault is deleted: a removed track keeps its copy.
/// </summary>
public sealed class VaultService
{
    public const string VaultFolderName = "ORBIT Vault";
    public const string ManifestFile = "vault.json";
    public const string IndexFile = "music-index.json";
    public const string SnapshotFile = "library.db";
    public const string PreviousSnapshotFile = "library.previous.db";
    private const string PartSuffix = ".orbit-vault-part";
    private const int SaveIndexEvery = 200;
    private const int VerifySampleSize = 25;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly VaultLocalState _local;
    private readonly ILogger<VaultService> _logger;
    private readonly Func<string> _libraryDbPath;
    private readonly SLSKDONET.Services.Library.Files.FileRelocationService? _relocation;

    public VaultService(IDbContextFactory<AppDbContext> dbFactory, VaultLocalState local, ILogger<VaultService> logger,
        SLSKDONET.Services.Library.Files.FileRelocationService? relocation = null)
        : this(dbFactory, local, logger, () => OrbitPaths.LibraryDbPath, relocation) { }

    internal VaultService(IDbContextFactory<AppDbContext> dbFactory, VaultLocalState local, ILogger<VaultService> logger, Func<string> libraryDbPath,
        SLSKDONET.Services.Library.Files.FileRelocationService? relocation = null)
    {
        _dbFactory = dbFactory;
        _local = local;
        _logger = logger;
        _libraryDbPath = libraryDbPath;
        _relocation = relocation;
    }

    /// <summary>
    /// The Vault folder for a folder the user picked: the folder itself when it already holds a
    /// Vault, else an "ORBIT Vault" folder inside it.
    /// </summary>
    public static string ResolveVaultRoot(string picked)
    {
        var full = Path.GetFullPath(picked);
        return File.Exists(Path.Combine(full, ManifestFile)) ? full : Path.Combine(full, VaultFolderName);
    }

    public static bool IsVault(string folder) => File.Exists(Path.Combine(folder, ManifestFile));

    public async Task<VaultBackupResult> BackupAsync(string vaultRoot, IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(vaultRoot);
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false) ?? new VaultManifest();
        var device = _local.Device;

        // 1. Database snapshot first: the music copy can take hours, the snapshot is the part that
        //    holds cues, grids, tags and playlists, so it is never left out by a cancelled run.
        progress?.Report(new VaultProgress("Saving the library database", 0, 1, null));
        await SnapshotDatabaseAsync(vaultRoot, ct).ConfigureAwait(false);

        // 2. Music.
        var (tracks, folders) = await LoadLibraryAsync(ct).ConfigureAwait(false);
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        var byKey = index.ToDictionary(e => Key(e.RootId, e.RelativePath), StringComparer.OrdinalIgnoreCase);

        // Refuse up front rather than fill the drive halfway (the snapshot above is small and kept).
        long toCopy = 0;
        foreach (var t in tracks)
        {
            if (!File.Exists(t.FilePath)) continue;
            var (r, rel) = AssignRoot(manifest, device.Id, folders, t.FilePath);
            var info = new FileInfo(t.FilePath);
            if (!IsUpToDate(byKey.GetValueOrDefault(Key(r.Id, rel)), info.Length, t.ContentHash, info.LastWriteTimeUtc))
                toCopy += info.Length;
        }
        if (FreeBytes(vaultRoot) is long free && toCopy > free)
        {
            await SaveProgressAsync(vaultRoot, manifest, byKey.Values, ct).ConfigureAwait(false);
            throw new IOException($"The backup drive is too small: this backup needs {SLSKDONET.Utils.NumFormat.Size(toCopy)} more, "
                + $"{SLSKDONET.Utils.NumFormat.Size(free)} is free. The library database was backed up; the music wasn't.");
        }

        int copied = 0, unchanged = 0, missing = 0, failed = 0, done = 0;
        long bytes = 0;
        var copiedThisRun = new List<VaultFileEntry>();

        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new VaultProgress("Copying music", done, tracks.Count, t.FilePath));
            done++;
            if (!File.Exists(t.FilePath)) { missing++; continue; }

            var (root, relative) = AssignRoot(manifest, device.Id, folders, t.FilePath);
            var target = VaultCopyPath(vaultRoot, root, relative);
            var source = new FileInfo(t.FilePath);
            long size = source.Length;
            var modified = source.LastWriteTimeUtc;
            var key = Key(root.Id, relative);

            if (byKey.TryGetValue(key, out var known) && IsUpToDate(known, size, t.ContentHash, modified)
                && File.Exists(target) && new FileInfo(target).Length == size)
            {
                known.TrackHash = t.TrackHash;
                known.OriginalPath = t.FilePath;
                known.SourceModifiedUtc ??= modified; // entries from before the time was recorded
                unchanged++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var sha = await CopyHashedAsync(t.FilePath, target, size, ct).ConfigureAwait(false);
                var entry = new VaultFileEntry
                {
                    RootId = root.Id, RelativePath = relative, Size = size, ContentHash = t.ContentHash,
                    TrackHash = t.TrackHash, OriginalPath = t.FilePath, CopiedUtc = DateTime.UtcNow,
                    FileSha256 = sha, SourceModifiedUtc = modified,
                };
                byKey[key] = entry;
                copiedThisRun.Add(entry);
                copied++;
                bytes += size;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _logger.LogWarning(ex, "Vault: copying {Path} failed", t.FilePath);
            }

            if (copied > 0 && copied % SaveIndexEvery == 0)
                await SaveProgressAsync(vaultRoot, manifest, byKey.Values, ct).ConfigureAwait(false);
        }

        // 3. Re-check a sample against the checksums taken while copying: half from this run, half
        //    from older copies (those have left the OS cache, so this also catches a drive going bad).
        progress?.Report(new VaultProgress("Checking the copies", 0, 1, null));
        var thisRun = new HashSet<VaultFileEntry>(copiedThisRun);
        var older = byKey.Values.Where(e => !thisRun.Contains(e)).ToList();
        var fromThisRun = Sample(copiedThisRun, VerifySampleSize / 2 + 1);
        var sample = fromThisRun.Concat(Sample(older, VerifySampleSize - fromThisRun.Count)).ToList();
        int verified = 0, mismatches = 0;
        foreach (var e in sample)
        {
            ct.ThrowIfCancellationRequested();
            var root = manifest.Roots.FirstOrDefault(r => r.Id == e.RootId);
            if (root == null) continue;
            var copy = VaultCopyPath(vaultRoot, root, e.RelativePath);
            bool ok;
            if (e.FileSha256 != null)
                ok = File.Exists(copy) && string.Equals(await HashHexAsync(copy, ct).ConfigureAwait(false), e.FileSha256, StringComparison.OrdinalIgnoreCase);
            else if (File.Exists(e.OriginalPath))
                ok = File.Exists(copy) && await SameBytesAsync(e.OriginalPath, copy, ct).ConfigureAwait(false);
            else
                continue;
            verified++;
            if (!ok)
            {
                mismatches++;
                _logger.LogWarning("Vault: the copy of {Path} doesn't match its checksum", e.OriginalPath);
            }
        }
        var (snapshotCheck, snapshotTracks) = CheckSnapshot(Path.Combine(vaultRoot, SnapshotFile));

        // 4. Record the backup.
        var me = manifest.Devices.FirstOrDefault(d => d.Id == device.Id);
        if (me == null) manifest.Devices.Add(me = new VaultDevice { Id = device.Id });
        me.Name = device.Name;
        me.Platform = device.Platform;
        me.LastBackupUtc = DateTime.UtcNow;
        me.LastBackupTracks = tracks.Count;
        await SaveProgressAsync(vaultRoot, manifest, byKey.Values, ct).ConfigureAwait(false);
        _local.RememberVault(vaultRoot);

        var result = new VaultBackupResult(copied, unchanged, missing, failed, bytes, verified, mismatches, snapshotTracks, snapshotCheck);
        _logger.LogInformation("Vault backup to {Vault}: {Summary}", vaultRoot, result.Summary);
        return result;
    }

    /// <summary>
    /// What the next backup would copy (files new or changed since the last one) and the free space
    /// on the Vault's drive. Reads file sizes only; nothing is hashed or copied.
    /// </summary>
    public async Task<(int Files, long Bytes, long? FreeBytes)> EstimateAsync(string vaultRoot, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false) ?? new VaultManifest();
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        var byKey = index.ToDictionary(e => Key(e.RootId, e.RelativePath), StringComparer.OrdinalIgnoreCase);
        var (tracks, folders) = await LoadLibraryAsync(ct).ConfigureAwait(false);
        var deviceId = _local.Device.Id;
        int files = 0;
        long bytes = 0;
        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(t.FilePath)) continue;
            var info = new FileInfo(t.FilePath);
            var (root, relative) = AssignRoot(manifest, deviceId, folders, t.FilePath);
            if (IsUpToDate(byKey.GetValueOrDefault(Key(root.Id, relative)), info.Length, t.ContentHash, info.LastWriteTimeUtc))
                continue;
            files++;
            bytes += info.Length;
        }
        return (files, bytes, FreeBytes(vaultRoot));
    }

    internal static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<VaultStatus?> GetStatusAsync(string vaultRoot, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false);
        if (manifest == null) return null;
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        var snapshot = Path.Combine(vaultRoot, SnapshotFile);
        return new VaultStatus(
            vaultRoot, manifest.VaultId,
            manifest.Devices.FirstOrDefault(d => d.Id == _local.Device.Id)?.LastBackupUtc,
            index.Count, index.Sum(e => e.Size),
            File.Exists(snapshot) ? File.GetLastWriteTimeUtc(snapshot) : null,
            manifest.Devices, manifest.LastVerifiedUtc);
    }

    /// <summary>What a restore would change. Reads only.</summary>
    public async Task<VaultRestorePlan> PlanRestoreAsync(string vaultRoot, CancellationToken ct = default)
    {
        var snapshot = Path.Combine(vaultRoot, SnapshotFile);
        if (!File.Exists(snapshot)) throw new FileNotFoundException("This Vault has no library database yet. Back up first.", snapshot);
        var (sTracks, sPlaylists, sCues) = CountSnapshot(snapshot);

        int cTracks, cPlaylists, cCues;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            cTracks = await db.LibraryEntries.CountAsync(ct).ConfigureAwait(false);
            cPlaylists = await db.Projects.CountAsync(ct).ConfigureAwait(false);
            cCues = await db.CuePoints.CountAsync(ct).ConfigureAwait(false);
        }

        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false) ?? new VaultManifest();
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        var roots = PlanRoots(vaultRoot, manifest, index, _local.Device.Id, remap: null);
        int missingInVault = index.Count(e => manifest.Roots.FirstOrDefault(r => r.Id == e.RootId) is not { } root
                                              || !File.Exists(VaultCopyPath(vaultRoot, root, e.RelativePath)));
        return new VaultRestorePlan(sTracks, cTracks, sPlaylists, cPlaylists, sCues, cCues,
            roots.Sum(r => r.MissingHere), missingInVault,
            File.GetLastWriteTimeUtc(snapshot), LibraryDatabaseMaintenance.GetBackupDirectory(_libraryDbPath()), roots);
    }

    /// <summary>The Vault's music folders as seen from this computer, with any folders the user chose.</summary>
    public async Task<IReadOnlyList<VaultRootPlan>> PlanRootsAsync(string vaultRoot, IReadOnlyDictionary<string, string>? remap, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false) ?? new VaultManifest();
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        return PlanRoots(vaultRoot, manifest, index, _local.Device.Id, remap);
    }

    /// <summary>
    /// Each Vault root as seen from this computer: where it lives here (its recorded folder for this
    /// device, or a folder the user chose in <paramref name="remap"/>), whether that folder can be used,
    /// and how many of its files are missing there.
    /// </summary>
    internal static List<VaultRootPlan> PlanRoots(string vaultRoot, VaultManifest manifest, IReadOnlyList<VaultFileEntry> index,
        string deviceId, IReadOnlyDictionary<string, string>? remap)
    {
        var plans = new List<VaultRootPlan>();
        foreach (var root in manifest.Roots)
        {
            var entries = index.Where(e => e.RootId == root.Id).ToList();
            if (entries.Count == 0) continue;
            var (local, usable) = ResolveLocalRoot(root, deviceId, remap);
            int missing = usable
                ? entries.Count(e => File.Exists(VaultCopyPath(vaultRoot, root, e.RelativePath)) && !File.Exists(LocalFilePath(local!, e.RelativePath)))
                : entries.Count;
            var others = root.LocalPaths.Where(kv => kv.Key != deviceId).Select(kv => kv.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plans.Add(new VaultRootPlan(root.Id, root.Name, entries.Count, missing, local, usable, others));
        }
        return plans;
    }

    /// <summary>
    /// Where a root lives on this device: a folder chosen for it now, else the folder this device
    /// recorded for it. Usable when it's an absolute path for this OS whose drive or volume is there
    /// (a Windows "D:\Music" on a Mac, or a drive letter that's gone, is not).
    /// </summary>
    internal static (string? Path, bool Usable) ResolveLocalRoot(VaultRoot root, string deviceId, IReadOnlyDictionary<string, string>? remap)
    {
        string? path = remap != null && remap.TryGetValue(root.Id, out var chosen) && !string.IsNullOrWhiteSpace(chosen)
            ? chosen
            : root.LocalPaths.TryGetValue(deviceId, out var mine) ? mine : null;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return (path, false);
        var volume = System.IO.Path.GetPathRoot(path);
        return (path, !string.IsNullOrEmpty(volume) && Directory.Exists(volume));
    }

    /// <summary>
    /// Copies back every file the Vault has that is missing from its root's folder on this computer
    /// (see <see cref="ResolveLocalRoot"/>; <paramref name="remap"/> points roots at new folders, and is
    /// remembered for this device). Never overwrites a file that exists, and checks each restored copy
    /// against the checksum taken at backup. Then re-points library tracks that still point at a
    /// root's old folder (another drive letter, another machine's paths after a database restore).
    /// </summary>
    public async Task<VaultRestoreFilesResult> RestoreMissingFilesAsync(string vaultRoot, IReadOnlyDictionary<string, string>? remap = null,
        IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This folder isn't an ORBIT Vault.");
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        var deviceId = _local.Device.Id;
        int restored = 0, failed = 0, skippedRoots = 0, repointed = 0;

        foreach (var root in manifest.Roots)
        {
            var entries = index.Where(e => e.RootId == root.Id).ToList();
            if (entries.Count == 0) continue;
            var (local, usable) = ResolveLocalRoot(root, deviceId, remap);
            if (!usable)
            {
                skippedRoots++;
                _logger.LogInformation("Vault: {Root} has no usable folder on this computer ({Path}); skipped", root.Name, local ?? "none");
                continue;
            }

            root.LocalPaths.TryGetValue(deviceId, out var previousHere);
            root.LocalPaths[deviceId] = local!; // remember the choice: later backups from here use it too

            var todo = entries.Where(e => !File.Exists(LocalFilePath(local!, e.RelativePath))).ToList();
            int done = 0;
            foreach (var e in todo)
            {
                ct.ThrowIfCancellationRequested();
                var target = LocalFilePath(local!, e.RelativePath);
                progress?.Report(new VaultProgress("Restoring missing files", done++, todo.Count, target));
                var copy = VaultCopyPath(vaultRoot, root, e.RelativePath);
                if (!File.Exists(copy)) continue;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    var sha = await CopyHashedAsync(copy, target, e.Size, ct).ConfigureAwait(false);
                    if (e.FileSha256 != null && !string.Equals(sha, e.FileSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(target);
                        throw new IOException("the Vault's copy is damaged (it doesn't match its checksum)");
                    }
                    restored++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _logger.LogWarning(ex, "Vault: restoring {Path} failed", target);
                }
            }

            // Library rows may still point at where this root used to be: this device's old folder,
            // or another machine's (a database restored from it). Re-point the ones now found here.
            if (_relocation != null)
            {
                var oldFolders = root.LocalPaths.Where(kv => kv.Key != deviceId).Select(kv => kv.Value)
                    .Append(previousHere)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && !SamePath(p!, local!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var old in oldFolders)
                {
                    var preview = await _relocation.PlanRepointFolderAsync(old!, local!, ct: ct).ConfigureAwait(false);
                    if (preview.Plan.Moves.Count == 0) continue;
                    var outcome = await _relocation.ExecuteRepointAsync(preview.Plan, ct).ConfigureAwait(false);
                    repointed += outcome.Moved;
                    _logger.LogInformation("Vault: re-pointed {Count} library tracks from {Old} to {New}", outcome.Moved, old, local);
                }
            }
        }

        await WriteJsonAtomicAsync(System.IO.Path.Combine(vaultRoot, ManifestFile), manifest, ct).ConfigureAwait(false);
        var result = new VaultRestoreFilesResult(restored, failed, skippedRoots, repointed);
        _logger.LogInformation("Vault: restore from {Vault}: {Summary}", vaultRoot, result.Summary);
        return result;
    }

    /// <summary>
    /// Re-reads every copy in the Vault and checks it against the checksum taken while copying, to
    /// catch copies that went missing or rotted on the backup drive. Copies from before checksums
    /// existed are compared with their original instead and get a checksum when they match.
    /// </summary>
    public async Task<VaultVerifyResult> VerifyVaultAsync(string vaultRoot, IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This folder isn't an ORBIT Vault.");
        var index = await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false);
        int ok = 0, missing = 0, damaged = 0, added = 0, unknown = 0, done = 0;
        var problems = new List<string>();

        foreach (var e in index)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new VaultProgress("Verifying the Vault", done++, index.Count, e.RelativePath));
            var root = manifest.Roots.FirstOrDefault(r => r.Id == e.RootId);
            var copy = root == null ? null : VaultCopyPath(vaultRoot, root, e.RelativePath);
            if (copy == null || !File.Exists(copy))
            {
                missing++;
                if (problems.Count < 50) problems.Add($"Missing: {e.RelativePath}");
                continue;
            }

            var hash = await HashHexAsync(copy, ct).ConfigureAwait(false);
            if (e.FileSha256 != null)
            {
                if (string.Equals(hash, e.FileSha256, StringComparison.OrdinalIgnoreCase)) ok++;
                else
                {
                    damaged++;
                    if (problems.Count < 50) problems.Add($"Damaged: {e.RelativePath}");
                }
                continue;
            }

            // No checksum yet: the original is the reference, if it hasn't changed since the copy.
            var original = File.Exists(e.OriginalPath) ? new FileInfo(e.OriginalPath) : null;
            bool originalUnchanged = original != null && original.Length == e.Size
                && (e.SourceModifiedUtc is not DateTime m || Math.Abs((original.LastWriteTimeUtc - m).TotalSeconds) < 2);
            if (!originalUnchanged)
            {
                unknown++;
                continue;
            }
            if (string.Equals(hash, await HashHexAsync(original!.FullName, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                e.FileSha256 = hash;
                e.SourceModifiedUtc ??= original.LastWriteTimeUtc;
                added++;
                ok++;
            }
            else if (e.SourceModifiedUtc != null)
            {
                damaged++; // same file, same time, different bytes: the copy changed
                if (problems.Count < 50) problems.Add($"Damaged: {e.RelativePath}");
            }
            else
            {
                unknown++; // can't tell an in-place tag edit from a damaged copy
            }
        }

        manifest.LastVerifiedUtc = DateTime.UtcNow;
        await SaveProgressAsync(vaultRoot, manifest, index, ct).ConfigureAwait(false);
        var result = new VaultVerifyResult(index.Count, ok, missing, damaged, added, unknown, problems);
        _logger.LogInformation("Vault verify {Vault}: {Summary}", vaultRoot, result.Summary);
        return result;
    }

    /// <summary>
    /// Stages the Vault's database snapshot to replace the library database the next time ORBIT
    /// starts (the live database can't be swapped while it is open). The current database is
    /// backed up first at that moment; see <see cref="VaultPendingRestore"/>.
    /// </summary>
    public void ScheduleDatabaseRestore(string vaultRoot)
    {
        var snapshot = Path.Combine(vaultRoot, SnapshotFile);
        if (!File.Exists(snapshot)) throw new FileNotFoundException("This Vault has no library database yet.", snapshot);
        var (check, _) = CheckSnapshot(snapshot);
        if (check != "ok") throw new InvalidDataException($"The Vault's database snapshot failed its check ({check}); not restoring it.");
        VaultPendingRestore.Schedule(_libraryDbPath(), snapshot);
        _logger.LogWarning("Vault: database restore from {Snapshot} scheduled for the next start", snapshot);
    }

    // ── internals ─────────────────────────────────────────────────────────

    private sealed record LibraryTrack(string TrackHash, string FilePath, string? ContentHash);

    private async Task<(List<LibraryTrack> Tracks, List<string> Folders)> LoadLibraryAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tracks = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.AvailabilityState != SLSKDONET.Models.TrackAvailabilityState.Ghost && e.FilePath != "")
            .OrderBy(e => e.FilePath)
            .Select(e => new LibraryTrack(e.UniqueHash, e.FilePath, e.AudioContentHash))
            .ToListAsync(ct).ConfigureAwait(false);
        var folders = await db.LibraryFolders.AsNoTracking().Select(f => f.FolderPath).ToListAsync(ct).ConfigureAwait(false);
        // The same file listed by two entries is backed up once.
        var distinct = tracks.GroupBy(t => Path.GetFullPath(t.FilePath), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return (distinct, folders);
    }

    /// <summary>
    /// The root a file belongs to (the deepest Library Source containing it) and its path inside
    /// that root. Files outside every source go to an "Other" root keyed by their drive.
    /// </summary>
    internal static (VaultRoot Root, string Relative) AssignRoot(VaultManifest manifest, string deviceId, IReadOnlyList<string> folders, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var source = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Where(f => full.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        string rootPath, relative;
        if (source != null)
        {
            rootPath = source;
            relative = Path.GetRelativePath(source, full);
        }
        else
        {
            rootPath = Path.GetPathRoot(full) ?? string.Empty;
            relative = full.Substring(rootPath.Length);
        }
        relative = relative.Replace('\\', '/').TrimStart('/').Normalize(System.Text.NormalizationForm.FormC);

        var root = manifest.Roots.FirstOrDefault(r => r.LocalPaths.TryGetValue(deviceId, out var p)
            && string.Equals(p, rootPath, StringComparison.OrdinalIgnoreCase));
        if (root == null)
        {
            var name = source != null ? Path.GetFileName(source) : "Other " + rootPath.TrimEnd(':', '\\', '/');
            root = new VaultRoot { Name = name, Folder = UniqueFolder(manifest, Slug(name)) };
            root.LocalPaths[deviceId] = rootPath;
            manifest.Roots.Add(root);
        }
        return (root, relative);
    }

    private static string UniqueFolder(VaultManifest manifest, string slug)
    {
        var candidate = slug;
        for (int n = 2; manifest.Roots.Any(r => string.Equals(r.Folder, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{slug}-{n}";
        return candidate;
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var s = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return s.Length == 0 ? "music" : s;
    }

    private static string Key(string rootId, string relative) => rootId + "|" + relative;

    /// <summary>
    /// Whether the Vault's copy is still the file on disk: same size and audio hash, and the source
    /// hasn't been rewritten since (a tag edit). Entries from before the time was recorded count as
    /// current rather than re-copying the whole library once after an upgrade.
    /// </summary>
    internal static bool IsUpToDate(VaultFileEntry? known, long size, string? contentHash, DateTime sourceModifiedUtc) =>
        known != null
        && known.Size == size
        && string.Equals(known.ContentHash ?? "", contentHash ?? "", StringComparison.Ordinal)
        && (known.SourceModifiedUtc is not DateTime m || Math.Abs((sourceModifiedUtc - m).TotalSeconds) < 2);

    private static string VaultCopyPath(string vaultRoot, VaultRoot root, string relative) =>
        System.IO.Path.Combine(vaultRoot, "Music", root.Folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string LocalFilePath(string localRoot, string relative) =>
        System.IO.Path.Combine(localRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(a.Replace('\\', '/')),
                      System.IO.Path.TrimEndingDirectorySeparator(b.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);


    private async Task SnapshotDatabaseAsync(string vaultRoot, CancellationToken ct)
    {
        var target = Path.Combine(vaultRoot, SnapshotFile);
        var temp = target + PartSuffix;
        if (File.Exists(temp)) File.Delete(temp);
        ct.ThrowIfCancellationRequested();
        await LibraryDatabaseMaintenance.CreateBackupAsync(_libraryDbPath(), temp).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        var (check, _) = CheckSnapshot(temp);
        if (check != "ok")
        {
            File.Delete(temp);
            throw new InvalidDataException($"The database snapshot failed its check ({check}); the Vault's previous snapshot is kept.");
        }
        if (File.Exists(target)) File.Move(target, Path.Combine(vaultRoot, PreviousSnapshotFile), overwrite: true);
        File.Move(temp, target);
    }

    internal static (string Check, int Tracks) CheckSnapshot(string path)
    {
        if (!File.Exists(path)) return ("missing", 0);
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var check = Convert.ToString(cmd.ExecuteScalar()) ?? "no answer";
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='LibraryEntries';";
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return ("no library table", 0);
            cmd.CommandText = "SELECT count(*) FROM LibraryEntries;";
            return (check, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        catch (SqliteException ex)
        {
            return (ex.Message, 0);
        }
    }

    private static (int Tracks, int Playlists, int Cues) CountSnapshot(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        int Count(string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}';";
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return 0;
            cmd.CommandText = $"SELECT count(*) FROM \"{table}\";";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        return (Count("LibraryEntries"), Count("Projects"), Count("CuePoints"));
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> via a temporary file, hashing the
    /// bytes as they pass (no second read) and flushing to the drive before the rename. Returns the
    /// SHA-256 in hex.
    /// </summary>
    private async Task<string> CopyHashedAsync(string source, string target, long expectedSize, CancellationToken ct)
    {
        var part = target + PartSuffix;
        try
        {
            string hex;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                hex = Convert.ToHexString(sha.GetHashAndReset());
            }
            long copied = new FileInfo(part).Length;
            if (copied != expectedSize) throw new IOException($"The copy is incomplete ({copied} of {expectedSize} bytes).");
            File.Move(part, target, overwrite: true);
            return hex;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Vault: couldn't remove the partial copy {Path}", part);
            }
            throw;
        }
    }

    private static async Task<string> HashHexAsync(string path, CancellationToken ct) =>
        Convert.ToHexString(await HashAsync(path, ct).ConfigureAwait(false));

    private static async Task<bool> SameBytesAsync(string a, string b, CancellationToken ct)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        var ha = await HashAsync(a, ct).ConfigureAwait(false);
        var hb = await HashAsync(b, ct).ConfigureAwait(false);
        return ha.AsSpan().SequenceEqual(hb);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
    }

    private static List<VaultFileEntry> Sample(List<VaultFileEntry> entries, int n)
    {
        if (entries.Count <= n) return entries;
        return entries.OrderBy(_ => Random.Shared.Next()).Take(n).ToList();
    }

    internal static async Task<VaultManifest?> LoadManifestAsync(string vaultRoot, CancellationToken ct)
    {
        var path = Path.Combine(vaultRoot, ManifestFile);
        if (!File.Exists(path)) return null;
        await using var s = File.OpenRead(path);
        var m = await JsonSerializer.DeserializeAsync<VaultManifest>(s, Json, ct).ConfigureAwait(false);
        if (m != null && m.FormatVersion > VaultManifest.CurrentFormatVersion)
            throw new NotSupportedException("This Vault was written by a newer ORBIT. Update ORBIT to use it.");
        return m;
    }

    private static async Task<List<VaultFileEntry>> LoadIndexAsync(string vaultRoot, CancellationToken ct)
    {
        var path = Path.Combine(vaultRoot, IndexFile);
        if (!File.Exists(path)) return new List<VaultFileEntry>();
        await using var s = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<VaultFileEntry>>(s, Json, ct).ConfigureAwait(false) ?? new List<VaultFileEntry>();
    }

    private static async Task SaveProgressAsync(string vaultRoot, VaultManifest manifest, IEnumerable<VaultFileEntry> index, CancellationToken ct)
    {
        await WriteJsonAtomicAsync(Path.Combine(vaultRoot, IndexFile), index.OrderBy(e => e.RootId).ThenBy(e => e.RelativePath, StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
        await WriteJsonAtomicAsync(Path.Combine(vaultRoot, ManifestFile), manifest, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = path + PartSuffix;
        await using (var s = File.Create(temp))
            await JsonSerializer.SerializeAsync(s, value, Json, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
