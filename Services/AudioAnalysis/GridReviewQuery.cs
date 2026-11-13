using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// The "Grid: needs review" set (issue #221): tracks whose beat grid is unverified, not user-owned and
/// below <see cref="GridWriteRules.ReviewThreshold"/> confidence. Resolved to a hash set so the Library's
/// DB-backed track lists (real playlists, All tracks) can apply it through their existing hash filter —
/// the in-memory row predicate only ever ran for in-memory smart playlists, so the chip used to do
/// nothing in a normal playlist (2026-11-11).
/// </summary>
public static class GridReviewQuery
{
    public static async Task<HashSet<string>> GetHashesNeedingReviewAsync(CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var hashes = await db.AudioFeatures.AsNoTracking()
            .Where(f => !f.GridVerified
                        && f.GridSource != GridWriteRules.SourceUser
                        && f.GridConfidence != null
                        && f.GridConfidence < GridWriteRules.ReviewThreshold)
            .Select(f => f.TrackUniqueHash)
            .ToListAsync(ct).ConfigureAwait(false);
        return hashes.ToHashSet();
    }

    /// <summary>The row-level rule, shared with the in-memory predicate so both paths agree.</summary>
    public static bool NeedsReview(bool gridVerified, float? gridConfidence) =>
        !gridVerified && gridConfidence is { } c && c < GridWriteRules.ReviewThreshold;
}
