using System;
using System.Collections.Generic;
using System.IO;

namespace SLSKDONET.Services.Setup;

/// <summary>What ORBIT knows about this install at startup, gathered before the main window shows.</summary>
public sealed record FirstRunSignals(
    bool SetupCompleted,
    int SetupVersion,
    string? Username,
    bool HasStoredCredentials,
    string? DownloadDirectory,
    string? SharedFolderPath,
    int TrackCount,
    int LibraryFolderCount);

public enum FirstRunDecision
{
    /// <summary>[Setup] Completed is set at (or past) the current wizard version: nothing to do.</summary>
    AlreadyCompleted,

    /// <summary>A genuinely fresh install: show the full first-run wizard.</summary>
    ShowWizard,

    /// <summary>
    /// An install that completed an older wizard version, or was used before the wizard existed:
    /// show the "what's new" re-onboarding instead of the full first-run flow.
    /// </summary>
    UpgradeWizard,
}

public sealed record FirstRunResult(FirstRunDecision Decision, IReadOnlyList<string> Evidence)
{
    public bool ShouldShowWizard => Decision is FirstRunDecision.ShowWizard or FirstRunDecision.UpgradeWizard;
    public bool IsUpgrade => Decision == FirstRunDecision.UpgradeWizard;
}

/// <summary>
/// Decides whether the setup wizard appears. Pure: no I/O, so every rule is unit-tested.
/// A fresh install sees the full wizard. An install that already completed the wizard at the
/// current version sees nothing. Anything older — a completed wizard from a previous version,
/// or an install used before the wizard existed — is offered the "what's new" re-onboarding.
/// </summary>
public static class FirstRunDetector
{
    /// <summary>
    /// Bump when the wizard gains steps or content an existing user should be offered. On startup,
    /// any install whose saved <see cref="Configuration.AppConfig.SetupVersion"/> is below this is re-onboarded.
    /// </summary>
    public const int CurrentSetupVersion = 4;

    /// <param name="fallbackDownloadDirectory">
    /// The folder ORBIT fills in when config.ini has none (<see cref="Configuration.AppConfig.FallbackDownloadDirectory"/>).
    /// It is not a sign the user chose anything.
    /// </param>
    public static FirstRunResult Evaluate(FirstRunSignals signals, string? fallbackDownloadDirectory)
    {
        ArgumentNullException.ThrowIfNull(signals);

        if (signals.SetupCompleted)
        {
            return signals.SetupVersion < CurrentSetupVersion
                ? new FirstRunResult(FirstRunDecision.UpgradeWizard,
                    new[] { $"[Setup] Completed at version {signals.SetupVersion}, current is {CurrentSetupVersion}" })
                : new FirstRunResult(FirstRunDecision.AlreadyCompleted, new[] { "[Setup] Completed is set" });
        }

        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(signals.Username)) evidence.Add("Soulseek username in config");
        if (signals.HasStoredCredentials) evidence.Add("stored Soulseek credentials");
        if (IsChosenFolder(signals.DownloadDirectory, fallbackDownloadDirectory)) evidence.Add("download folder set");
        if (!string.IsNullOrWhiteSpace(signals.SharedFolderPath)) evidence.Add("shared folder set");
        if (signals.TrackCount > 0) evidence.Add($"{signals.TrackCount} tracks in the library");
        if (signals.LibraryFolderCount > 0) evidence.Add($"{signals.LibraryFolderCount} library folders");

        return evidence.Count > 0
            ? new FirstRunResult(FirstRunDecision.UpgradeWizard, evidence)
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
