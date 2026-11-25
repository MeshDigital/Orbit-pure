using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library.Files;

/// <summary>One track to consider for reorganization: its hash, current path, and the metadata for the template.</summary>
public sealed record ReorganizeTrack(string Hash, string SourcePath, Track Metadata);

/// <summary>One row of the reorganization preview.</summary>
public sealed record ReorganizePreviewItem(string Hash, string SourcePath, string TargetPath, string Status);

/// <summary>The before/after preview: what will move, what's unchanged, and what's blocked.</summary>
public sealed record ReorganizePreview(IReadOnlyList<ReorganizePreviewItem> Items, int Movable, int Unchanged, int Blocked);

/// <summary>
/// LF5 (#347): computes the "Reorganise library files" before/after preview without moving anything.
/// For each track it resolves the template path, then runs the same validation as
/// <see cref="FileRelocationService.PlanAsync"/> so the preview matches exactly what an apply would do.
/// </summary>
public sealed class ReorganizePreviewService
{
    private readonly PathProviderService _pathProvider;
    private readonly FileRelocationService _relocation;

    public ReorganizePreviewService(PathProviderService pathProvider, FileRelocationService relocation)
    {
        _pathProvider = pathProvider;
        _relocation = relocation;
    }

    public async Task<ReorganizePreview> PreviewAsync(IReadOnlyList<ReorganizeTrack> tracks, CancellationToken ct = default)
    {
        var items = new List<ReorganizePreviewItem>();
        var requests = new List<RelocationRequest>();
        var requestByHash = new Dictionary<string, RelocationRequest>(StringComparer.Ordinal);

        foreach (var t in tracks)
        {
            var target = _pathProvider.GetTrackPath(t.Metadata);
            if (string.Equals(target, t.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ReorganizePreviewItem(t.Hash, t.SourcePath, target, "Unchanged"));
                continue;
            }

            var request = new RelocationRequest(t.Hash, t.SourcePath, target);
            requests.Add(request);
            requestByHash[t.Hash] = request;
        }

        var plan = await _relocation.PlanAsync(requests, ct).ConfigureAwait(false);

        foreach (var move in plan.Moves)
            items.Add(new ReorganizePreviewItem(move.Hash, move.SourcePath, move.TargetPath, "Move"));

        foreach (var problem in plan.Problems)
        {
            var request = requestByHash.TryGetValue(problem.Hash, out var r) ? r : null;
            items.Add(new ReorganizePreviewItem(
                problem.Hash,
                request?.SourcePath ?? problem.Hash,
                request?.TargetPath ?? string.Empty,
                problem.Reason));
        }

        return new ReorganizePreview(
            items,
            Movable: plan.Moves.Count,
            Unchanged: items.Count(i => i.Status == "Unchanged"),
            Blocked: plan.Problems.Count);
    }
}
