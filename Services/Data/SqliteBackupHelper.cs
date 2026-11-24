using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SLSKDONET.Services;

/// <summary>
/// Shared SQLite backup helper with transient BUSY/LOCKED retry (#387).
/// </summary>
public static class SqliteBackupHelper
{
    private const int Attempts = 6;

    /// <summary>
    /// Copies <paramref name="src"/> into <paramref name="dst"/> via <c>BackupDatabase</c>, retrying
    /// briefly (linear backoff) when the source is transiently locked by a concurrent writer.
    /// </summary>
    public static async Task BackupWithRetryAsync(SqliteConnection src, SqliteConnection dst, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                src.BackupDatabase(dst);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < Attempts) // BUSY, LOCKED
            {
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
