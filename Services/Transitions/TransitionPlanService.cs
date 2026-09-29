using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;

namespace SLSKDONET.Services.Transitions;

/// <summary>
/// Loads what <see cref="TransitionPlanner"/> needs from the library (analysis + cues) and plans
/// a transition for a track pair. Used by real playback (PlayerViewModel's Auto transitions) and
/// the Mix editor, so both agree on where and how a pair mixes.
/// </summary>
public sealed class TransitionPlanService
{
    private readonly DatabaseService _database;
    private readonly ICuePointService _cues;

    public TransitionPlanService(DatabaseService database, ICuePointService cues)
    {
        _database = database;
        _cues = cues;
    }

    /// <summary>Structure of a track, or null when it hasn't been analysed.</summary>
    public async Task<TrackStructure?> LoadStructureAsync(string trackHash)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return null;
        var features = await _database.GetAudioFeaturesByHashAsync(trackHash).ConfigureAwait(false);
        if (features == null || features.Bpm <= 0 || features.TrackDuration <= 0) return null;
        var cues = await _cues.GetByTrackIdAsync(trackHash).ConfigureAwait(false);
        return BuildStructure(features, cues);
    }

    /// <summary>Plans (outgoing → incoming); null when either track lacks analysis.</summary>
    public async Task<(TransitionPlan Plan, TrackStructure Outgoing, TrackStructure Incoming)?> PlanAsync(
        string outgoingHash, string incomingHash, double compatibility)
    {
        var outgoing = await LoadStructureAsync(outgoingHash).ConfigureAwait(false);
        var incoming = await LoadStructureAsync(incomingHash).ConfigureAwait(false);
        if (outgoing == null || incoming == null) return null;
        return (TransitionPlanner.Plan(outgoing, incoming, compatibility), outgoing, incoming);
    }

    /// <summary>Public for tests.</summary>
    public static TrackStructure BuildStructure(AudioFeaturesEntity f, IReadOnlyList<CuePointEntity> cues)
    {
        // Countdown/approach markers are Build-type, so Drop-type cues are the drops themselves.
        var ordered = cues.Where(c => !c.IsLoop).OrderBy(c => c.TimestampInSeconds).ToList();
        var drops = ordered.Where(c => c.Type == CuePointType.Drop).Select(c => c.TimestampInSeconds).ToList();
        return new TrackStructure
        {
            DurationSeconds = f.TrackDuration,
            Bpm = f.Bpm,
            FirstDownbeat = Math.Max(0, f.DownbeatOffsetSeconds),
            IntroCue = ordered.FirstOrDefault(c => c.Type == CuePointType.Intro)?.TimestampInSeconds,
            Drop1 = drops.Count > 0 ? drops[0] : null,
            Drop2 = drops.Count > 1 ? drops[1] : null,
            OutroCue = ordered.LastOrDefault(c => c.Type == CuePointType.Outro)?.TimestampInSeconds,
            LastSound = LastSoundSeconds(f.EnergyCurveJson, f.TrackDuration),
            VocalStart = f.VocalStartSeconds,
            VocalEnd = f.VocalEndSeconds,
            Energy = Math.Clamp(f.Energy, 0, 1),
        };
    }

    /// <summary>
    /// Last second the track is audible, from the stored 1-per-second energy curve (normalised to
    /// its loudest second): the end of the last second above 5 % of peak. Mixxx does the same with
    /// a -60 dBFS threshold to find "outro end".
    /// </summary>
    public static double? LastSoundSeconds(string? energyCurveJson, double duration)
    {
        if (string.IsNullOrWhiteSpace(energyCurveJson) || energyCurveJson == "[]") return null;
        try
        {
            var curve = JsonSerializer.Deserialize<float[]>(energyCurveJson);
            if (curve is not { Length: > 4 }) return null;
            float peak = curve.Max();
            if (peak <= 0) return null;
            for (int i = curve.Length - 1; i >= 0; i--)
                if (curve[i] > peak * 0.05f)
                    return Math.Min(duration, i + 1.0);
        }
        catch (JsonException) { }
        return null;
    }
}
