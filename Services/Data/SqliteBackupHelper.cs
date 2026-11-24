using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SLSKDONET.Services;

/// <summary>
/// Shared SQLite online-backup with a transient BUSY/LOCKED retry (#387). A writer on another
/// connection (analysis, downloads, a test in parallel) makes the online backup fail briefly with
/// SQLITE_BUSY / SQLITE_LOCKED; retry with a non-blocking backoff instead of aborting.
/// </summary>
public static class SqliteBackupHelper
{
    public static async Task BackupWithRetryAsync(string source, string target, int attempts = 6, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var src = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                using var dst = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = target, Pooling = false }.ToString());
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
                SqliteConnection.ClearPool(dst); // release the backup file handle
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < attempts) // BUSY, LOCKED
            {
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
