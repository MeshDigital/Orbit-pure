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
/// a file whose copy in the Vault already has the same size and content hash is not copied again.
/// Verified: a sample of copies is compared byte for byte, and the snapshot is opened and checked.
/// Nothing in the Vault is deleted: a track removed from the library keeps its copy.
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

    public VaultService(IDbContextFactory<AppDbContext> dbFactory, VaultLocalState local, ILogger<VaultService> logger)
        : this(dbFactory, local, logger, () => OrbitPaths.LibraryDbPath) { }

    internal VaultService(IDbContextFactory<AppDbContext> dbFactory, VaultLocalState local, ILogger<VaultService> logger, Func<string> libraryDbPath)
    {
        _dbFactory = dbFactory;
        _local = local;
        _logger = logger;
        _libraryDbPath = libraryDbPath;
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
            var target = Path.Combine(vaultRoot, "Music", root.Folder, relative.Replace('/', Path.DirectorySeparatorChar));
            long size = new FileInfo(t.FilePath).Length;
            var key = Key(root.Id, relative);

            if (byKey.TryGetValue(key, out var known) && known.Size == size
                && string.Equals(known.ContentHash ?? "", t.ContentHash ?? "", StringComparison.Ordinal)
                && File.Exists(target) && new FileInfo(target).Length == size)
            {
                known.TrackHash = t.TrackHash;
                known.OriginalPath = t.FilePath;
                unchanged++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyVerifiedAsync(t.FilePath, target, size, ct).ConfigureAwait(false);
                var entry = new VaultFileEntry
                {
                    RootId = root.Id, RelativePath = relative, Size = size, ContentHash = t.ContentHash,
                    TrackHash = t.TrackHash, OriginalPath = t.FilePath, CopiedUtc = DateTime.UtcNow,
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

        // 3. Verify a sample of this run's copies (or of the Vault when nothing new was copied).
        progress?.Report(new VaultProgress("Checking the copies", 0, 1, null));
        var sample = Sample(copiedThisRun.Count > 0 ? copiedThisRun : byKey.Values.ToList(), VerifySampleSize);
        int verified = 0, mismatches = 0;
        foreach (var e in sample)
        {
            ct.ThrowIfCancellationRequested();
            var root = manifest.Roots.FirstOrDefault(r => r.Id == e.RootId);
            if (root == null || !File.Exists(e.OriginalPath)) continue;
            var copy = Path.Combine(vaultRoot, "Music", root.Folder, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            verified++;
            if (!File.Exists(copy) || !await SameBytesAsync(e.OriginalPath, copy, ct).ConfigureAwait(false))
            {
                mismatches++;
                _logger.LogWarning("Vault: the copy of {Path} differs from the original", e.OriginalPath);
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
            manifest.Devices);
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
        int missingLocally = 0, missingInVault = 0;
        foreach (var e in index)
        {
            if (!File.Exists(e.OriginalPath)) missingLocally++;
            var root = manifest.Roots.FirstOrDefault(r => r.Id == e.RootId);
            if (root == null || !File.Exists(Path.Combine(vaultRoot, "Music", root.Folder, e.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
                missingInVault++;
        }
        return new VaultRestorePlan(sTracks, cTracks, sPlaylists, cPlaylists, sCues, cCues, missingLocally, missingInVault,
            File.GetLastWriteTimeUtc(snapshot), LibraryDatabaseMaintenance.GetBackupDirectory(_libraryDbPath()));
    }

    /// <summary>
    /// Copies back every file the Vault has that is missing from its original place on this
    /// computer. Never overwrites a file that exists. Returns how many were restored.
    /// </summary>
    public async Task<int> RestoreMissingFilesAsync(string vaultRoot, IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(vaultRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This folder isn't an ORBIT Vault.");
        var index = (await LoadIndexAsync(vaultRoot, ct).ConfigureAwait(false)).Where(e => !File.Exists(e.OriginalPath)).ToList();
        int restored = 0, done = 0;
        foreach (var e in index)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new VaultProgress("Restoring missing files", done++, index.Count, e.OriginalPath));
            var root = manifest.Roots.FirstOrDefault(r => r.Id == e.RootId);
            if (root == null) continue;
            var copy = Path.Combine(vaultRoot, "Music", root.Folder, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(copy)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(e.OriginalPath)!);
                await CopyVerifiedAsync(copy, e.OriginalPath, e.Size, ct).ConfigureAwait(false);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Vault: restoring {Path} failed", e.OriginalPath);
            }
        }
        _logger.LogInformation("Vault: restored {Count} missing files from {Vault}", restored, vaultRoot);
        return restored;
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

    private async Task CopyVerifiedAsync(string source, string target, long expectedSize, CancellationToken ct)
    {
        var part = target + PartSuffix;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            long copied = new FileInfo(part).Length;
            if (copied != expectedSize) throw new IOException($"The copy is incomplete ({copied} of {expectedSize} bytes).");
            File.Move(part, target, overwrite: true);
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
