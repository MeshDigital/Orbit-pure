using System;
using System.IO;

namespace SLSKDONET.Services.Vault;

/// <summary>The outcome of a staged database restore, applied at startup.</summary>
public sealed record VaultRestoreOutcome(bool Applied, string Message, string? SafetyBackupPath);

/// <summary>
/// Swaps in a Vault's database snapshot at startup, before anything opens the library (epic #632).
/// Scheduling writes a marker next to the live database naming the snapshot; the next start copies
/// the live database (and its -wal/-shm) to the Backups folder first, then copies the snapshot over
/// it and removes the old -wal/-shm so stale pages can't be replayed onto the restored file. The
/// marker is removed whatever happens, so a failing restore can't loop on every start.
/// </summary>
public static class VaultPendingRestore
{
    public const string MarkerSuffix = ".vault-restore";

    public static string MarkerPath(string dbPath) => dbPath + MarkerSuffix;

    /// <summary>What the restore applied at this start did (shown on the Backup page), or null.</summary>
    public static VaultRestoreOutcome? LastOutcome { get; private set; }

    public static void Schedule(string dbPath, string snapshotPath) =>
        File.WriteAllText(MarkerPath(dbPath), Path.GetFullPath(snapshotPath));

    public static bool IsScheduled(string dbPath) => File.Exists(MarkerPath(dbPath));

    public static void Cancel(string dbPath)
    {
        if (File.Exists(MarkerPath(dbPath))) File.Delete(MarkerPath(dbPath));
    }

    /// <summary>Applies a scheduled restore. Returns null when none was scheduled. Never throws.</summary>
    public static VaultRestoreOutcome? ApplyIfScheduled(string dbPath)
    {
        var outcome = ApplyCore(dbPath);
        if (outcome != null) LastOutcome = outcome;
        return outcome;
    }

    private static VaultRestoreOutcome? ApplyCore(string dbPath)
    {
        var marker = MarkerPath(dbPath);
        if (!File.Exists(marker)) return null;

        string snapshot;
        try
        {
            snapshot = File.ReadAllText(marker).Trim();
            File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VaultRestoreOutcome(false, $"The library restore couldn't start: {ex.Message}", null);
        }

        if (!File.Exists(snapshot))
            return new VaultRestoreOutcome(false, $"The library restore was skipped: the Vault snapshot isn't reachable ({snapshot}). Is the drive connected?", null);

        string? safety = null;
        var temp = dbPath + ".vault-incoming";
        try
        {
            // Nothing live changes until the snapshot is fully copied next to the database.
            File.Copy(snapshot, temp, overwrite: true);

            if (File.Exists(dbPath))
            {
                var backups = LibraryDatabaseMaintenance.GetBackupDirectory(dbPath);
                Directory.CreateDirectory(backups);
                safety = Path.Combine(backups, $"library-before-vault-restore-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db");
                File.Copy(dbPath, safety);
                foreach (var side in new[] { "-wal", "-shm" })
                    if (File.Exists(dbPath + side)) File.Copy(dbPath + side, safety + side);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new VaultRestoreOutcome(false, $"The library restore failed before anything changed: {ex.Message}", safety);
        }

        try
        {
            // An old -wal would be replayed onto the restored file: remove it with the swap.
            foreach (var side in new[] { "-wal", "-shm" })
                if (File.Exists(dbPath + side)) File.Delete(dbPath + side);
            File.Move(temp, dbPath, overwrite: true);
            return new VaultRestoreOutcome(true, "Your library was restored from the Vault. The previous library was saved first.", safety);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the previous library (with its -wal) back exactly as it was.
            if (safety != null)
            {
                try
                {
                    File.Copy(safety, dbPath, overwrite: true);
                    foreach (var side in new[] { "-wal", "-shm" })
                        if (File.Exists(safety + side)) File.Copy(safety + side, dbPath + side, overwrite: true);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    return new VaultRestoreOutcome(false,
                        $"The library restore failed ({ex.Message}) and putting the previous library back failed too ({rollback.Message}). Your previous library is saved in {safety}.", safety);
                }
            }
            TryDelete(temp);
            return new VaultRestoreOutcome(false, $"The library restore failed: {ex.Message}. Your library was left as it was.", safety);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Vault restore: couldn't remove {Path}", path);
        }
    }
}
