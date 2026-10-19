using System;
using System.IO;

namespace SLSKDONET.Data;

/// <summary>
/// The one place ORBIT's per-user data locations are decided.
/// <para>
/// <c>ORBIT_DB_PATH</c> overrides the library database alone — the test project sets that to a
/// throwaway database, because tests that opened <c>new AppDbContext()</c> used to read and write
/// the user's real library (2026-09-29: two test temp folders were left behind as enabled Library
/// Sources and shared on Soulseek).
/// </para>
/// <para>
/// <c>ORBIT_PROFILE_DIR</c> (or the <c>--profile &lt;dir&gt;</c> / <c>--setup-preview</c> command-line
/// flags, which set it) moves the whole user profile: config.ini, library.db, stored Soulseek and
/// Spotify credentials, workstation session, column/keyboard layouts and the other per-user state
/// files. That is how a running install can open a second, completely fresh ORBIT (for example to
/// preview the first-run setup wizard) without touching the real config or library. Shared,
/// content-addressed caches (artwork, stems, playback conversions) stay where they are.
/// </para>
/// </summary>
public static class OrbitPaths
{
    public const string DbPathEnvironmentVariable = "ORBIT_DB_PATH";
    public const string ProfileDirectoryEnvironmentVariable = "ORBIT_PROFILE_DIR";

    /// <summary>The profile override directory, or null when ORBIT uses the normal AppData folders.</summary>
    public static string? ProfileDirectory =>
        Environment.GetEnvironmentVariable(ProfileDirectoryEnvironmentVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : null;

    /// <summary>True when ORBIT runs on a <see cref="ProfileDirectory"/> instead of the real AppData folders.</summary>
    public static bool IsProfileOverridden => ProfileDirectory != null;

    /// <summary>
    /// Stand-in for <c>%AppData%</c> (Roaming). Callers keep combining their own sub-folder
    /// ("ORBIT", "ORBIT-Pure", "Antigravity") so the layout under a profile matches the real one.
    /// </summary>
    public static string RoamingRoot =>
        ProfileDirectory is { } profile
            ? Path.Combine(profile, "Roaming")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Stand-in for <c>%LocalAppData%</c> (credential and token stores).</summary>
    public static string LocalRoot =>
        ProfileDirectory is { } profile
            ? Path.Combine(profile, "Local")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%AppData%\ORBIT</c> (or the profile's equivalent).</summary>
    public static string AppDataDirectory => Path.Combine(RoamingRoot, "ORBIT");

    public static string LibraryDbPath =>
        Environment.GetEnvironmentVariable(DbPathEnvironmentVariable) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(AppDataDirectory, "library.db");
}
