using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Library.Files;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.LibraryActions;

/// <summary>
/// LF4 (#346): locks or unlocks the selected tracks' library paths. Locks when any selected track is
/// unlocked, unlocks when all are locked — a locked track is skipped by FileRelocationService.
/// </summary>
public class TogglePathLockAction : ILibraryAction
{
    private readonly PathProtectionService _pathProtection;
    private readonly ILogger<TogglePathLockAction> _logger;

    public string Name => "Lock/Unlock path";
    public string IconGlyph => "🔒";
    public string Category => "File";

    public TogglePathLockAction(PathProtectionService pathProtection, ILogger<TogglePathLockAction> logger)
    {
        _pathProtection = pathProtection;
        _logger = logger;
    }

    public bool CanExecute(LibraryContext context) =>
        context.SelectedTracks.Any(t => !string.IsNullOrEmpty(t.Model.TrackUniqueHash));

    public async Task ExecuteAsync(LibraryContext context)
    {
        var hashes = context.SelectedTracks
            .Select(t => t.Model.TrackUniqueHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0) return;

        // Lock unless every selected track is already locked, in which case unlock.
        var anyUnlocked = false;
        foreach (var hash in hashes)
        {
            if (!await _pathProtection.IsPathLockedAsync(hash).ConfigureAwait(false))
            {
                anyUnlocked = true;
                break;
            }
        }

        var lockIt = anyUnlocked;
        foreach (var hash in hashes)
            await _pathProtection.SetPathLockedAsync(hash, lockIt).ConfigureAwait(false);

        _logger.LogInformation("{Action} {Count} track path(s)", lockIt ? "Locked" : "Unlocked", hashes.Count);
    }
}
