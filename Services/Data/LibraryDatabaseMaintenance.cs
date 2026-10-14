using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SLSKDONET.Services;

/// <summary>
/// Library-database housekeeping.
///
/// <para><b>Why this exists (backlog E1/B6, 2026-10-14):</b> startup maintenance ran a full
/// <c>VACUUM</c> on every launch, from inside a UI-thread continuation. Microsoft.Data.Sqlite's
/// "async" commands run synchronously, so the whole VACUUM blocked the UI thread and held the
/// database lock (measured: a 10.4 s UI stall and a 6.6 s page fetch). Now VACUUM runs at most
/// once a week, gated by a marker file in the Backups folder, and the caller runs it off the UI
/// thread.</para>
///
/// <para><see cref="CreateBackup"/> uses SQLite's online backup API rather than a file copy: the
/// library runs in WAL mode, so copying <c>library.db</c> alone can miss committed pages still in
/// the <c>-wal</c> file. It backs the startup snapshot and the Settings "Back up now" button.</para>
///
/// Everything is derived from <see cref="SLSKDONET.Data.OrbitPaths.LibraryDbPath"/> unless a path
/// is passed in, so tests (which set ORBIT_DB_PATH) never touch the user's real library or its
/// real Backups folder.
/// </summary>
public static class LibraryDatabaseMaintenance
{
    public const string ManualBackupPrefix = "library.manual.";
    public const string BackupExtension = ".db";

    public static readonly TimeSpan VacuumInterval = TimeSpan.FromDays(7);

    private const string VacuumMarker = "vacuum.last";

    /// <summary>The Backups folder next to the given (or live) library database.</summary>
    public static string GetBackupDirectory(string? dbPath = null)
    {
        dbPath ??= SLSKDONET.Data.OrbitPaths.LibraryDbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".";
        return Path.Combine(dir, "Backups");
    }

    /// <summary>
    /// True when <paramref name="lastRunUtc"/> is unknown or at least <paramref name="interval"/>
    /// ago. A last-run time in the future (clock moved back) also counts as due, so a bad marker
    /// can't suppress maintenance forever.
    /// </summary>
    public static bool IsDue(DateTime? lastRunUtc, TimeSpan interval, DateTime nowUtc)
    {
        if (lastRunUtc is not { } last) return true;
        if (last > nowUtc) return true;
        return nowUtc - last >= interval;
    }

    public static DateTime? ReadMarker(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath)) return null;
            var text = File.ReadAllText(markerPath).Trim();
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }
        catch (IOException) { return null; } // unreadable marker → treat as never run
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void WriteMarker(string markerPath, DateTime nowUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, nowUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Takes a consistent snapshot of the library database with SQLite's online backup API and
    /// returns the backup path. Throws if the source database doesn't exist or the backup fails,
    /// so callers can tell the user the truth.
    /// </summary>
    public static string CreateBackup(string? dbPath = null, string? targetPath = null)
    {
        dbPath ??= SLSKDONET.Data.OrbitPaths.LibraryDbPath;
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("Library database not found.", dbPath);

        if (targetPath == null)
        {
            var dir = GetBackupDirectory(dbPath);
            Directory.CreateDirectory(dir);
            targetPath = Path.Combine(dir, $"{ManualBackupPrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{BackupExtension}");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        }

        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = targetPath, Pooling = false }.ToString()))
        {
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
        }

        return targetPath;
    }

    /// <summary>
    /// Runs the weekly VACUUM if it's due and returns whether it ran. Blocking (SQLite I/O) —
    /// call it from a background thread. Never throws; failures go to <paramref name="onError"/>.
    /// </summary>
    public static bool RunScheduledVacuum(string? dbPath = null, DateTime? nowUtc = null, Action<Exception>? onError = null)
    {
        try
        {
            dbPath ??= SLSKDONET.Data.OrbitPaths.LibraryDbPath;
            var now = nowUtc ?? DateTime.UtcNow;
            if (!File.Exists(dbPath)) return false;

            var marker = Path.Combine(GetBackupDirectory(dbPath), VacuumMarker);
            if (!IsDue(ReadMarker(marker), VacuumInterval, now)) return false;

            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                       { DataSource = dbPath, Pooling = false, DefaultTimeout = 30 }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM";
                cmd.CommandTimeout = 300;
                cmd.ExecuteNonQuery();
            }

            WriteMarker(marker, now);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }
}
