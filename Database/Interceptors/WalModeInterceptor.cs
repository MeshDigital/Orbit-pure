using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SLSKDONET.Database.Interceptors;

/// <summary>
/// EF Core connection interceptor that applies SQLite WAL mode and performance
/// PRAGMAs immediately after a connection is opened — before any EF Core
/// operation runs.
/// </summary>
public sealed class WalModeInterceptor : DbConnectionInterceptor
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
            "PRAGMA busy_timeout=10000;" +  // 10 seconds busy timeout
            "PRAGMA cache_size=-10000;" +   // 10 MB page cache
            "PRAGMA wal_autocheckpoint=1000;";
        cmd.ExecuteNonQuery();
    }
}
