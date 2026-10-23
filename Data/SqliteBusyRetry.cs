using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Data;

/// <summary>
/// Retries a short database write that failed with SQLITE_BUSY / SQLITE_LOCKED.
/// <para>
/// Every connection already waits up to 10 s for a lock (<c>busy_timeout</c>, see
/// WalModeInterceptor), but SQLite returns BUSY immediately, without waiting, when a read
/// transaction tries to become a write transaction after another writer committed (WAL
/// "busy snapshot"). With a folder import, enrichment and analysis writing the same tables at
/// once that is a real case, and before this a single one cost a whole import batch (500 rows
/// reported "not saved"). Each attempt must start from a fresh DbContext, which the callers do.
/// </para>
/// </summary>
public static class SqliteBusyRetry
{
    public const int DefaultAttempts = 4;

    /// <summary>True when <paramref name="ex"/> (or what it wraps) is SQLITE_BUSY or SQLITE_LOCKED.</summary>
    public static bool IsBusy(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        return false;
    }

    public static async Task RunAsync(Func<Task> write, ILogger? logger, string what, CancellationToken ct = default, int attempts = DefaultAttempts)
    {
        await RunAsync(async () => { await write().ConfigureAwait(false); return true; }, logger, what, ct, attempts).ConfigureAwait(false);
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> write, ILogger? logger, string what, CancellationToken ct = default, int attempts = DefaultAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsBusy(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 50));
                logger?.LogWarning("Database busy during {What} (attempt {Attempt}/{Max}); retrying in {Delay} ms",
                    what, attempt, attempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}
