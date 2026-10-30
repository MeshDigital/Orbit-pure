using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Copilot;

/// <summary>
/// Slice K7c: wraps the Bridge Finder to build parallel peak-time pathways. Forces the search to
/// branch from a specific spine slot and target a subsequent anchor track, reusing the verified
/// <c>TransitionKnowledge</c> recursive CTE for DRYness.
/// </summary>
public static class AlternativeRouteFinder
{
    /// <summary>
    /// Finds alternative bridge paths from <paramref name="branchSlotIndex"/> to
    /// <paramref name="anchorSlotIndex"/> in the spine. Returns empty when the indices are invalid.
    /// </summary>
    public static async Task<IReadOnlyList<BridgePath>> FindAlternativeRoutesAsync(
        AppDbContext db,
        IReadOnlyList<string> spineHashes,
        int branchSlotIndex,
        int anchorSlotIndex,
        CopilotConstraints? constraints = null,
        CancellationToken ct = default)
    {
        if (branchSlotIndex < 0 || anchorSlotIndex >= spineHashes.Count || branchSlotIndex >= anchorSlotIndex)
            return Array.Empty<BridgePath>();

        var source = spineHashes[branchSlotIndex];
        var anchor = spineHashes[anchorSlotIndex];

        return await BridgeFinder.FindBridgesAsync(db, source, anchor, constraints ?? new CopilotConstraints(), ct)
            .ConfigureAwait(false);
    }
}
