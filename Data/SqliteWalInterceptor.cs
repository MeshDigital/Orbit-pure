using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SLSKDONET.Data;

/// <summary>
/// EF Core connection interceptor that applies SQLite WAL mode and performance
/// PRAGMAs immediately after a connection is opened — before any EF Core
/// operation runs.  Resolves issue #45 (WAL was previously applied only during
/// schema migration, leaving a window where early reads used the slower journal
/// mode).
/// </summary>
public sealed class SqliteWalInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        await Task.CompletedTask;
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        if (connection is not SqliteConnection) return;
        if (connection.State != System.Data.ConnectionState.Open) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA cache_size=-10000;" +   // 10 MB page cache
            "PRAGMA wal_autocheckpoint=1000;";
        cmd.ExecuteNonQuery();
    }
}
