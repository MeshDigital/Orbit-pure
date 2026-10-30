using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Copilot;

/// <summary>
/// Slice K7: the ORBIT copilot — active, algorithmic curation over the knowledge graph.
/// Bridge finding is implemented first (recursive SQL); the remaining methods are the contract for
/// the Set Builder, Alternative Route finder and Clash Detector.
/// </summary>
public interface IOrbitCopilot
{
    /// <summary>
    /// Finds verified, score-weighted knowledge-edge paths connecting <paramref name="sourceHash"/> to
    /// <paramref name="targetHash"/> within BPM/Camelot and depth constraints.
    /// </summary>
    Task<IReadOnlyList<BridgePath>> FindBridgesAsync(
        string sourceHash,
        string targetHash,
        CopilotConstraints? constraints = null,
        CancellationToken ct = default);

    /// <summary>Auto-builds a primary set spine for a target duration + event energy curve.</summary>
    Task<SetBuildResult> BuildSetAsync(SetBuildRequest request, CancellationToken ct = default);

    /// <summary>Finds peak-time branch alternatives for a set's slots via outgoing <c>SetStoryEdge</c> records.</summary>
    Task<IReadOnlyList<AlternativeRoute>> FindAlternativeRoutesAsync(Guid setProjectId, CancellationToken ct = default);

    /// <summary>Sweeps a set timeline for vocal and harmonic collisions.</summary>
    Task<IReadOnlyList<ClashReport>> DetectClashesAsync(Guid setProjectId, CancellationToken ct = default);
}
