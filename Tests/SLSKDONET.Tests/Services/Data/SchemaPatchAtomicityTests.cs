using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services.Data;

/// <summary>#317: schema patches are atomic — a failing patch leaves the schema unchanged.</summary>
public sealed class SchemaPatchAtomicityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!,
        "atomic-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SqliteConnection _connection;

    public SchemaPatchAtomicityTests()
    {
        _connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        _connection.Open();
        Exec("CREATE TABLE \"audio_features\" (\"Id\" INTEGER PRIMARY KEY);");
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { File.Delete(_path); }
        catch (IOException) { /* intentional: best-effort cleanup in the isolated test folder */ }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool ColumnExists(string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('audio_features') WHERE name = '{column}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private bool TableExists(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public async Task AFailingPatch_RollsBackEveryEarlierPatch()
    {
        var applied = await SchemaMigratorService.RunAtomicallyAsync(_connection, () =>
        {
            Exec("ALTER TABLE \"audio_features\" ADD COLUMN \"First\" TEXT NULL;");
            Exec("CREATE TABLE \"NewTable\" (\"Id\" INTEGER PRIMARY KEY);");
            Exec("ALTER TABLE \"no_such_table\" ADD COLUMN \"Broken\" TEXT NULL;"); // a typo'd patch
            return Task.CompletedTask;
        }, NullLogger.Instance);

        Assert.False(applied);
        Assert.False(ColumnExists("First"));
        Assert.False(TableExists("NewTable"));

        // The connection is usable again (no transaction left open) and a good batch commits.
        Assert.True(await SchemaMigratorService.RunAtomicallyAsync(_connection, () =>
        {
            Exec("ALTER TABLE \"audio_features\" ADD COLUMN \"First\" TEXT NULL;");
            return Task.CompletedTask;
        }, NullLogger.Instance));
        Assert.True(ColumnExists("First"));
    }
}
