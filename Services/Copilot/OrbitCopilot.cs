using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Copilot;

/// <summary>
/// Slice K7: the copilot orchestrator. Thin delegation layer — hydrates EF Core entities from the
/// database and hands them to the pure static engines (<see cref="BridgeFinder"/>,
/// <see cref="SetBuilder"/>, <see cref="ClashDetector"/>, <see cref="AlternativeRouteFinder"/>).
/// Registered as a scoped service so a single context lifetime covers a traversal/sweep.
/// </summary>
public sealed class OrbitCopilot : IOrbitCopilot
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public OrbitCopilot(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<BridgePath>> FindBridgesAsync(
        string sourceHash,
        string targetHash,
        CopilotConstraints? constraints = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await BridgeFinder
            .FindBridgesAsync(db, sourceHash, targetHash, constraints ?? new CopilotConstraints(), ct)
            .ConfigureAwait(false);
    }

    public async Task<SetBuildResult> BuildSetAsync(SetBuildRequest request, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var features = await db.AudioFeatures
            .Where(f => f.Bpm > 0 && f.TrackDuration > 0)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var candidates = features
            .Select(f => new SpineCandidate(f.TrackUniqueHash, f.Bpm, f.Energy * 100.0, f.TrackDuration))
            .ToList();
        var hashes = candidates.Select(c => c.TrackUniqueHash).Distinct().ToList();

        var edges = await db.TransitionKnowledge
            .Where(e => hashes.Contains(e.SourceTrackId) && hashes.Contains(e.TargetTrackId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var confidence = edges.ToDictionary(e => (e.SourceTrackId, e.TargetTrackId), e => e.ConfidenceScore);

        var energyCurve = ParseEnergyCurve(request.EventProfile?.TargetEnergyCurveJson);

        var spine = SetBuilder.BuildSpine(candidates, confidence, request.TargetMinutes, request.MaxBpmVariance, energyCurve);
        var duration = spine.Sum(h => candidates.First(c => c.TrackUniqueHash == h).DurationSeconds) / 60.0;

        return new SetBuildResult(spine, duration);
    }

    public async Task<IReadOnlyList<AlternativeRoute>> FindAlternativeRoutesAsync(Guid setProjectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId, ct)
            .ConfigureAwait(false);
        if (set is null)
            return Array.Empty<AlternativeRoute>();

        var spine = set.Tracks.OrderBy(t => t.Position).Select(t => t.TrackUniqueHash).ToList();

        var results = new List<AlternativeRoute>();
        for (var i = 0; i + 2 < spine.Count; i++)
        {
            var paths = await AlternativeRouteFinder
                .FindAlternativeRoutesAsync(db, spine, i, i + 2, constraints: null, ct)
                .ConfigureAwait(false);

            foreach (var path in paths)
                results.Add(new AlternativeRoute(path.TrackHashes[0], path.TrackHashes[^1], path.TotalScore));
        }

        return results;
    }

    public async Task<IReadOnlyList<ClashReport>> DetectClashesAsync(Guid setProjectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId, ct)
            .ConfigureAwait(false);
        if (set is null)
            return Array.Empty<ClashReport>();

        var ordered = set.Tracks.OrderBy(t => t.Position).ToList();
        var hashes = ordered.Select(t => t.TrackUniqueHash).ToList();

        var features = await db.AudioFeatures
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var featuresByHash = features.ToDictionary(f => f.TrackUniqueHash, StringComparer.Ordinal);

        var cues = await db.CuePoints
            .Where(c => hashes.Contains(c.TrackUniqueHash))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var spine = new List<TrackClashData>();
        foreach (var slot in ordered)
        {
            featuresByHash.TryGetValue(slot.TrackUniqueHash, out var f);
            var trackCues = cues.Where(c => c.TrackUniqueHash == slot.TrackUniqueHash).ToList();
            var duration = f?.TrackDuration > 0 ? f.TrackDuration : 300.0;

            var mixOut = ResolveCue(trackCues, CuePointType.MixOut, CuePointType.OutroStart) ?? duration;
            var mixIn = ResolveCue(trackCues, CuePointType.MixIn, CuePointType.Intro) ?? 0.0;
            var camelot = ClashDetector.CamelotNumberFromKey(f?.CamelotKey);

            var vocalSegments = (f?.VocalDensity ?? 0f) > 0.5f
                ? new[] { new VocalSegment(0.0, duration) }
                : Array.Empty<VocalSegment>();

            spine.Add(new TrackClashData(slot.TrackUniqueHash, mixOut, mixIn, BarsBeforeMix: 16, camelot, vocalSegments));
        }

        var bpm = featuresByHash.Values.FirstOrDefault(f => f.Bpm > 0)?.Bpm ?? 120.0;
        return ClashDetector.DetectClashes(spine, bpm);
    }

    private static double? ResolveCue(IReadOnlyList<CuePointEntity> cues, params CuePointType[] types)
    {
        foreach (var type in types)
        {
            var cue = cues.FirstOrDefault(c => c.Type == type);
            if (cue is not null)
                return cue.TimestampInSeconds;
        }
        return null;
    }

    private static IReadOnlyList<double>? ParseEnergyCurve(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<double>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
