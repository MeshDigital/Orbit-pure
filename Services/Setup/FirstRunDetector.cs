using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services.Setup;

/// <summary>What ORBIT knows about this install at startup, gathered before the main window shows.</summary>
public sealed record FirstRunSignals(
    bool SetupCompleted,
    string? Username,
    bool HasStoredCredentials,
    string? DownloadDirectory,
    string? SharedFolderPath,
    int TrackCount,
    int LibraryFolderCount);

public enum FirstRunDecision
{
    /// <summary>[Setup] Completed is already set: nothing to do.</summary>
    AlreadyCompleted,

    /// <summary>The install was used before the wizard existed: mark setup completed silently, never show it.</summary>
    ExistingInstall,

    /// <summary>A genuinely fresh install: show the wizard.</summary>
    ShowWizard,
}

public sealed record FirstRunResult(FirstRunDecision Decision, IReadOnlyList<string> Evidence)
{
    public bool ShouldShowWizard => Decision == FirstRunDecision.ShowWizard;
    public bool ShouldMarkCompleted => Decision == FirstRunDecision.ExistingInstall;
}

/// <summary>
/// Decides whether the first-run setup wizard appears. Pure: no I/O, so every rule is unit-tested.
/// An install that has ever been used (a Soulseek name or stored password, a download or shared
/// folder the user chose, tracks or library folders in the database) is an existing install: it
/// is marked completed and never sees the wizard unasked.
/// </summary>
public static class FirstRunDetector
{
    /// <summary>Bump when the wizard gains steps an existing user should be offered (not used to re-show it yet).</summary>
    public const int CurrentSetupVersion = 1;

    /// <param name="fallbackDownloadDirectory">
    /// The folder ORBIT fills in when config.ini has none (<see cref="Configuration.AppConfig.FallbackDownloadDirectory"/>).
    /// It is not a sign the user chose anything.
    /// </param>
    public static FirstRunResult Evaluate(FirstRunSignals signals, string? fallbackDownloadDirectory)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.SetupCompleted)
            return new FirstRunResult(FirstRunDecision.AlreadyCompleted, new[] { "[Setup] Completed is set" });

        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(signals.Username)) evidence.Add("Soulseek username in config");
        if (signals.HasStoredCredentials) evidence.Add("stored Soulseek credentials");
        if (IsChosenFolder(signals.DownloadDirectory, fallbackDownloadDirectory)) evidence.Add("download folder set");
        if (!string.IsNullOrWhiteSpace(signals.SharedFolderPath)) evidence.Add("shared folder set");
        if (signals.TrackCount > 0) evidence.Add($"{signals.TrackCount} tracks in the library");
        if (signals.LibraryFolderCount > 0) evidence.Add($"{signals.LibraryFolderCount} library folders");

        return evidence.Count > 0
            ? new FirstRunResult(FirstRunDecision.ExistingInstall, evidence)
            : new FirstRunResult(FirstRunDecision.ShowWizard, new[] { "no account, folders or tracks yet" });
    }

    private static bool IsChosenFolder(string? path, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (string.IsNullOrWhiteSpace(fallback)) return true;
        return !SamePath(path, fallback);
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim(); // not a valid path: compare it as typed
        }
    }
}
