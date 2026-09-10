using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// Locates every local Rekordbox analysis cache (USBANLZ) root, shared by every service that reads
/// real ANLZ files from disk (<see cref="RekordboxPssiService"/> today; any future cue/waveform
/// reader tomorrow) so the "where does Rekordbox keep this" logic exists exactly once.
///
/// There are two distinct kinds of root, and real saved cue points (PCOB/PCO2) only ever showed up
/// in the second one against this project's actual test library — the first kind had 0/1,391 real
/// tracks with any saved cue, despite 1,328 having full phrase analysis:
///   1. Rekordbox's own local prep/browsing cache under %AppData%\Pioneer\rekordbox\... — analysis
///      Rekordbox runs for its own use while you browse/prep a track. Phrase/structure data lives
///      here, but hand-placed hot/memory cues apparently do not get mirrored back to it.
///   2. A device export cache at "&lt;drive&gt;\PIONEER\USBANLZ" — written when you export a
///      track/playlist to a USB drive or SD card from Rekordbox's Export view. This is where real
///      saved cue points actually land.
/// </summary>
public static class RekordboxAnlzLocator
{
    // Process-wide cache: re-probing every drive on every call across every caller would be pure
    // waste, and these roots can't move while ORBIT is running (a drive appearing/disappearing
    // mid-session is the one case this deliberately doesn't handle — restart ORBIT after plugging
    // in the export device, same as the local-cache-only behavior already required).
    private static IReadOnlyList<string>? _cachedRoots;

    /// <summary>
    /// Returns every USBANLZ root found: Rekordbox's local prep cache (if present) plus a
    /// "PIONEER\USBANLZ" folder on any drive currently attached (removable or fixed — an SD card
    /// and a permanently-connected external drive both export the same way). Empty list if none
    /// found, never null.
    /// </summary>
    public static IReadOnlyList<string> FindRoots()
    {
        if (_cachedRoots != null) return _cachedRoots;

        var roots = new List<string>();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localCandidates = new[]
        {
            Path.Combine(appData, "Pioneer", "rekordbox", "share", "PIONEER", "USBANLZ"), // Rekordbox 6+
            Path.Combine(appData, "Pioneer", "rekordbox", "PIONEER", "USBANLZ"),          // Rekordbox 5
        };
        foreach (var candidate in localCandidates)
        {
            if (Directory.Exists(candidate))
                roots.Add(candidate);
        }

        // Device export caches: cheap to probe (one Directory.Exists per drive), so this runs
        // unconditionally rather than requiring the user to configure a path. DriveInfo.GetDrives()
        // can throw or return drives that aren't actually ready (a card reader with no card, a
        // disconnected network share) — each drive is probed independently so one bad drive can't
        // hide the rest.
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var candidate = Path.Combine(drive.RootDirectory.FullName, "PIONEER", "USBANLZ");
                    if (Directory.Exists(candidate))
                        roots.Add(candidate);
                }
                catch
                {
                    // One inaccessible drive must not abort discovery of the rest.
                }
            }
        }
        catch
        {
            // DriveInfo.GetDrives() itself failing leaves roots as whatever the local-cache probe
            // above already found — still a valid, usable result.
        }

        _cachedRoots = roots;
        return roots;
    }
}
