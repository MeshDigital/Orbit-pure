using System;
using System.IO;
using System.Runtime.CompilerServices;
using SLSKDONET.Data;

namespace SLSKDONET.Tests;

/// <summary>
/// Runs before any test: points ORBIT at a throwaway database for this test run. Tests (and the
/// services they exercise) that open <c>new AppDbContext()</c> used to hit the user's real
/// %APPDATA%\ORBIT\library.db — an interrupted run left test temp folders behind as enabled
/// Library Sources, which ORBIT then shared on Soulseek.
/// </summary>
internal static class TestDatabaseSetup
{
    private static string? _directory;

    [ModuleInitializer]
    internal static void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "orbit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable(OrbitPaths.DbPathEnvironmentVariable, Path.Combine(_directory, "library.db"));

        using (var context = new AppDbContext())
            context.Database.EnsureCreated();

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_directory!, recursive: true); }
            catch { /* temp dir; the OS cleans up eventually */ }
        };
    }
}
