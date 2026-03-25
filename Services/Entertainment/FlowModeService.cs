using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Implements ORBIT's Flow Mode engine — scores and selects upcoming tracks
/// for a seamless, DJ-like continuous playback experience.
/// </summary>
public sealed class FlowModeService : IFlowModeService, IDisposable
{
    private readonly ILogger<FlowModeService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private FlowModeState _state = new();
    private bool _disposed;

    public FlowModeState State => _state;

    public event EventHandler<FlowModeState>? StateChanged;

    public FlowModeService(
        ILogger<FlowModeService> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public void Activate(VibePreset preset = VibePreset.SilkySmooth, double energyNudge = 0.0)
    {
        _state = _state with
        {
            IsActive = true,
            ActivePreset = preset,
            EnergyNudge = energyNudge
        };
        _logger.LogInformation("Flow Mode activated (preset: {Preset}, nudge: {Nudge:+0.0;-0.0;0})",
            preset, energyNudge);
        RaiseStateChanged();
    }

    public void Deactivate()
    {
        if (!_state.IsActive) return;
        _state = _state with { IsActive = false };
        _logger.LogInformation("Flow Mode deactivated.");
        RaiseStateChanged();
    }

    public void Toggle()
    {
        if (_state.IsActive) Deactivate();
        else Activate(_state.ActivePreset, _state.EnergyNudge);
    }

    public void SetEnergyNudge(double nudge)
    {
        nudge = Math.Clamp(nudge, -1.0, 1.0);
        _state = _state with { EnergyNudge = nudge };
        RaiseStateChanged();
    }

    public void SetPreset(VibePreset preset)
    {
        _state = _state with { ActivePreset = preset };
        RaiseStateChanged();
    }

    public async Task RebuildFlowPathAsync(
        string currentTrackHash,
        IEnumerable<string> candidateHashes,
        CancellationToken cancellationToken = default)
    {
        if (!_state.IsActive) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // Fetch audio features for the current track
            var currentFeatures = await db.AudioFeatures
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == currentTrackHash, cancellationToken);

            if (currentFeatures is null)
            {
                _logger.LogDebug("Flow Mode: no audio features for current track '{Hash}' — skipping rebuild.",
                    currentTrackHash);
                return;
            }

            var candidateList = candidateHashes.ToList();
            if (candidateList.Count == 0) return;

            // Fetch features for candidates (batch, up to 200)
            var candidates = await db.AudioFeatures
                .AsNoTracking()
                .Where(f => candidateList.Contains(f.TrackUniqueHash) && f.TrackUniqueHash != currentTrackHash)
                .ToListAsync(cancellationToken);

            var weights = _state.ActivePreset.ToSettings();
            double targetEnergy = Math.Clamp(currentFeatures.Energy + _state.EnergyNudge * 0.3, 0.0, 1.0);

            // Score each candidate
            var scored = candidates
                .Select(c => (features: c, score: ScoreCandidate(currentFeatures, c, weights, targetEnergy)))
                .OrderByDescending(x => x.score)
                .Take(5)
                .ToList();

            // Resolve titles from library entries
            var topHashes = scored.Select(x => x.features.TrackUniqueHash).ToList();
            var libraryMap = await db.LibraryEntries
                .AsNoTracking()
                .Where(e => topHashes.Contains(e.UniqueHash))
                .ToDictionaryAsync(e => e.UniqueHash, e => $"{e.Artist} — {e.Title}", cancellationToken);

            var titles = scored
                .Select(x => libraryMap.TryGetValue(x.features.TrackUniqueHash, out var t) ? t : x.features.TrackUniqueHash)
                .ToList();

            var energies = scored.Select(x => (double)x.features.Energy).ToList();

            // Determine crossfade duration from BPM proximity
            var nextFeatures = scored.FirstOrDefault().features;
            double crossfade = nextFeatures is not null
                ? ComputeCrossfadeDuration(currentFeatures.Bpm, nextFeatures.Bpm)
                : 4.0;

            string reason = scored.Count > 0
                ? BuildReason(currentFeatures, scored[0].features, weights)
                : "No suitable candidates found.";

            _state = _state with
            {
                FlowPathTitles = titles,
                FlowPathEnergies = energies,
                CrossfadeDurationSeconds = crossfade,
                NextTrackReason = reason
            };

            _logger.LogDebug("Flow Mode: rebuilt path with {Count} candidates. Next: {Title}",
                scored.Count, titles.FirstOrDefault() ?? "—");

            RaiseStateChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow Mode: error rebuilding flow path.");
        }
    }

    // ── Scoring ─────────────────────────────────────────────────────────────

    private static double ScoreCandidate(
        AudioFeaturesEntity current,
        AudioFeaturesEntity candidate,
        FlowWeightSettings weights,
        double targetEnergy)
    {
        double score = 0;

        // Energy proximity (weighted)
        double energyDiff = Math.Abs(candidate.Energy - targetEnergy);
        score += weights.EnergyWeight * (1.0 - Math.Min(energyDiff / 0.5, 1.0));

        // BPM proximity (weighted)
        double bpmRatio = current.Bpm > 0 && candidate.Bpm > 0
            ? Math.Abs(current.Bpm - candidate.Bpm) / Math.Max(current.Bpm, candidate.Bpm)
            : 0.5;
        score += weights.BpmWeight * (1.0 - Math.Min(bpmRatio, 1.0));

        // Harmonic compatibility (simplified: same key = high score)
        bool sameKey = string.Equals(current.Key, candidate.Key, StringComparison.OrdinalIgnoreCase);
        score += weights.HarmonicWeight * (sameKey ? 1.0 : 0.3);

        return score;
    }

    private static double ComputeCrossfadeDuration(float bpmA, float bpmB)
    {
        if (bpmA <= 0 || bpmB <= 0) return 4.0;
        double bpmDiff = Math.Abs(bpmA - bpmB);
        // Large BPM diff → longer fade to mask the mismatch
        return Math.Clamp(4.0 + (bpmDiff / 10.0), 2.0, 12.0);
    }

    private static string BuildReason(
        AudioFeaturesEntity current,
        AudioFeaturesEntity next,
        FlowWeightSettings weights)
    {
        var parts = new List<string>();

        if (weights.HarmonicWeight >= 0.7 &&
            string.Equals(current.Key, next.Key, StringComparison.OrdinalIgnoreCase))
            parts.Add("same key");

        if (weights.EnergyWeight >= 0.6)
        {
            double diff = Math.Abs(current.Energy - next.Energy);
            parts.Add(diff < 0.1 ? "matching energy" : "energy transition");
        }

        if (weights.BpmWeight >= 0.5 && current.Bpm > 0 && next.Bpm > 0)
        {
            double bpmDiff = Math.Abs(current.Bpm - next.Bpm);
            parts.Add(bpmDiff < 5 ? $"near-identical BPM ({next.Bpm:F0})" : $"BPM drift ({next.Bpm:F0})");
        }

        return parts.Count > 0
            ? "Selected for: " + string.Join(", ", parts)
            : "Best available match.";
    }

    private void RaiseStateChanged() =>
        StateChanged?.Invoke(this, _state);

    public void Dispose()
    {
        _disposed = true;
    }
}
