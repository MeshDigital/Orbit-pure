using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Copilot;

/// <summary>Unmapped row returned by the recursive Bridge Finder CTE.</summary>
public sealed class BridgePathRow
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string Visited { get; set; } = string.Empty;
    public double Score { get; set; }
}

/// <summary>
/// Slice K7: recursive graph search over verified <see cref="SLSKDONET.Data.Entities.TransitionKnowledgeEntity"/>
/// edges (SQLite <c>WITH RECURSIVE</c>), constrained by BPM and harmonic (Camelot) compatibility.
/// Returns score-weighted bridge paths from a source to a target track.
/// </summary>
public static class BridgeFinder
{
    private const string BridgeSql = """
        WITH RECURSIVE path(source, target, depth, visited, score) AS (
            SELECT SourceTrackId, TargetTrackId, 1,
                   SourceTrackId || '>' || TargetTrackId,
                   CompatibilityScore
            FROM TransitionKnowledge
            WHERE SourceTrackId = @source AND Verified = 1
              AND BpmScore >= @minBpmScore AND HarmonicScore >= @minHarmonicScore
            UNION ALL
            SELECT p.source, e.TargetTrackId, p.depth + 1,
                   p.visited || '>' || e.TargetTrackId,
                   p.score + e.CompatibilityScore
            FROM path p
            JOIN TransitionKnowledge e ON e.SourceTrackId = p.target
            WHERE p.depth < @maxDepth
              AND instr(p.visited, e.TargetTrackId) = 0
              AND e.Verified = 1
              AND e.BpmScore >= @minBpmScore AND e.HarmonicScore >= @minHarmonicScore
        )
        SELECT source, target, depth, visited, score
        FROM path
        WHERE target = @target
        ORDER BY score DESC
        LIMIT @maxResults;
        """;

    public static async Task<IReadOnlyList<BridgePath>> FindBridgesAsync(
        AppDbContext db,
        string sourceHash,
        string targetHash,
        CopilotConstraints constraints,
        CancellationToken ct = default)
    {
        var rows = await db.Database
            .SqlQueryRaw<BridgePathRow>(
                BridgeSql,
                new SqliteParameter("@source", sourceHash),
                new SqliteParameter("@target", targetHash),
                new SqliteParameter("@maxDepth", constraints.MaxDepth),
                new SqliteParameter("@minBpmScore", constraints.MinBpmScore),
                new SqliteParameter("@minHarmonicScore", constraints.MinHarmonicScore),
                new SqliteParameter("@maxResults", constraints.MaxResults))
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new BridgePath(
                r.Visited.Split('>', StringSplitOptions.RemoveEmptyEntries),
                r.Depth,
                r.Score))
            .ToList();
    }
}
