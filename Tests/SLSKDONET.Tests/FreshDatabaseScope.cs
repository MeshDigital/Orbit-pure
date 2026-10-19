using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SLSKDONET.Data;

namespace SLSKDONET.Tests;

/// <summary>
/// Points <c>ORBIT_DB_PATH</c> at a brand-new, empty database file for the lifetime of the scope,
/// then restores the suite's shared test database. A fresh file is what a new install starts
/// from, unlike the shared test DB, which <see cref="TestDatabaseSetup"/> builds with
/// <c>EnsureCreated()</c> straight from the EF model — that hides every gap between the model and
/// the schema the real startup path (<c>SchemaMigratorService</c>) builds.
/// <para>
/// Swapping a process-wide environment variable is only safe for tests in
/// <see cref="NonParallelCollection"/>.
/// </para>
/// </summary>
internal sealed class FreshDatabaseScope : IDisposable
{
    private readonly string? _previousDbPath;

    public string Directory { get; }
    public string DbPath { get; }

    public FreshDatabaseScope()
    {
        _previousDbPath = Environment.GetEnvironmentVariable(OrbitPaths.DbPathEnvironmentVariable);
        Directory = Path.Combine(Path.GetTempPath(), "orbit-tests", "fresh-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "library.db");
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(OrbitPaths.DbPathEnvironmentVariable, DbPath);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OrbitPaths.DbPathEnvironmentVariable, _previousDbPath);
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException ex)
        {
            // A lingering handle (WAL checkpoint, background task) — the folder is under %TEMP%.
            Console.WriteLine($"FreshDatabaseScope: could not delete {Directory}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"FreshDatabaseScope: could not delete {Directory}: {ex.Message}");
        }
    }
}
