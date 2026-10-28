using System.Collections.Generic;
using SLSKDONET.Utils;

namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// One track Flow may pick next, flattened from the queue row it belongs to.
///
/// Flow deliberately plans over data the player already holds (the queue), not by re-querying the
/// database: the plan has to be instant so it can be rebuilt the moment a track starts, before the
/// next deck is preloaded.
/// </summary>
public sealed record FlowCandidate
{
    public required string Hash { get; init; }

    public string Artist { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Analysed tempo, or 0 when unknown.</summary>
    public double Bpm { get; init; }

    /// <summary>Camelot key (e.g. "8A"), or empty when unknown. Never the em-dash placeholder.</summary>
    public string CamelotKey { get; init; } = string.Empty;

    /// <summary>Energy 0–1, or a negative value when the track has no energy reading yet.</summary>
    public double Energy { get; init; } = -1.0;

    /// <summary>Mood label from analysis, or empty.</summary>
    public string Mood { get; init; } = string.Empty;

    /// <summary>Primary genre, or empty.</summary>
    public string Genre { get; init; } = string.Empty;

    public bool HasTempo => Bpm > 0;

    public bool HasKey => !string.IsNullOrWhiteSpace(CamelotKey);

    public bool HasEnergy => Energy >= 0.0;

    /// <summary>True when Flow has at least one axis to judge this track on.</summary>
    public bool HasAnyFeature => HasTempo || HasKey || HasEnergy;

    /// <summary>"Artist — Title", falling back to whichever half exists.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist) ? Title
        : string.IsNullOrWhiteSpace(Title) ? Artist
        : $"{Artist} — {Title}";
}

/// <summary>Why the current <see cref="FlowModeState.FlowPath"/> is empty, or that it is usable.</summary>
public enum FlowPlanStatus
{
    /// <summary>Flow is off; no path is shown.</summary>
    Off = 0,

    /// <summary>A path is ready.</summary>
    Ready = 1,

    /// <summary>Nothing is playing, so there is nothing to flow from.</summary>
    NoTrack = 2,

    /// <summary>The playing track has no BPM/key/energy yet.</summary>
    NoAnalysis = 3,

    /// <summary>The queue holds no other playable track.</summary>
    NoCandidates = 4,

    /// <summary>The queue has other tracks, but none of them are analysed.</summary>
    NoCandidateData = 5,

    /// <summary>Planning threw; the message carries the reason.</summary>
    Faulted = 6,
}

/// <summary>One computed hop in a Flow path, with the sub-scores that produced the pick.</summary>
public sealed record FlowStep
{
    /// <summary>1-based position in the path.</summary>
    public int Rank { get; init; }

    public required string Hash { get; init; }

    public string Artist { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Composite 0–1 fit for this hop.</summary>
    public double Score { get; init; }

    public double Bpm { get; init; }

    public string CamelotKey { get; init; } = string.Empty;

    /// <summary>Energy 0–1, or negative when unknown.</summary>
    public double Energy { get; init; }

    public double KeyScore { get; init; }

    public double TempoScore { get; init; }

    public double EnergyScore { get; init; }

    public double VibeScore { get; init; }

    /// <summary>"Same key", "Compatible (+1)", "Different key (4 steps)" …</summary>
    public string KeyLabel { get; init; } = string.Empty;

    /// <summary>"Same BPM", "±1.4 BPM", "2× (double time)" …</summary>
    public string TempoLabel { get; init; } = string.Empty;

    /// <summary>"Energy +0.04", "Holds energy", "No energy data" …</summary>
    public string EnergyLabel { get; init; } = string.Empty;

    /// <summary>"Mood match (Sad)", "Mood shift (Sad → Dark)", "Mood unknown" …</summary>
    public string VibeLabel { get; init; } = string.Empty;

    /// <summary>Crossfade length this hop will use, in seconds.</summary>
    public double CrossfadeSeconds { get; init; } = 4.0;

    /// <summary>Tempo/key of the track this hop follows — what "Same key" is relative to.</summary>
    public double PreviousBpm { get; init; }

    public string PreviousCamelotKey { get; init; } = string.Empty;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist) ? Title
        : string.IsNullOrWhiteSpace(Title) ? Artist
        : $"{Artist} — {Title}";

    /// <summary>"87%" — invariant, mono in the UI.</summary>
    public string ScoreText => NumFormat.Percent(Score);

    /// <summary>"114.0 BPM · 8A · Energy 0.78" — the next track's musical facts at a glance.</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(3);
            if (HasTempo) parts.Add(NumFormat.Ui($"{Bpm:0.0} BPM"));
            if (!string.IsNullOrWhiteSpace(CamelotKey)) parts.Add(CamelotKey);
            if (HasEnergy) parts.Add(NumFormat.Ui($"Energy {Energy:0.00}"));
            return parts.Count == 0 ? "Not analysed" : string.Join(" · ", parts);
        }
    }

    /// <summary>"174.0 BPM" or "—".</summary>
    public string BpmText => HasTempo ? NumFormat.Bpm(Bpm) : "—";

    public bool HasTempo => Bpm > 0;

    public bool HasEnergy => Energy >= 0.0;

    /// <summary>"8A → 8A" with the current track's key supplied by the caller via <see cref="KeyLabel"/>.</summary>
    public string KeyText => string.IsNullOrWhiteSpace(CamelotKey) ? "—" : CamelotKey;

    // One-line rows for the Now Playing card: the label and its value live in a single TextBlock so
    // they can never drift apart (or get clipped) at narrow widths, and each row shows the actual
    // numbers ("114.5 → 172.0 BPM"), not just the verdict.
    public string KeyRowText => NumFormat.Ui($"Key · {KeyPair} · {KeyLabel}");

    public string TempoRowText => NumFormat.Ui($"Tempo · {TempoPair} · {TempoLabel}");

    public string EnergyRowText => $"Energy · {EnergyLabel}";

    public string VibeRowText => $"Mood · {VibeLabel}";

    private string KeyPair =>
        string.IsNullOrWhiteSpace(PreviousCamelotKey) || string.IsNullOrWhiteSpace(CamelotKey)
            ? "—"
            : NumFormat.Ui($"{PreviousCamelotKey} → {CamelotKey}");

    private string TempoPair =>
        PreviousBpm <= 0 || Bpm <= 0 ? "—" : NumFormat.Ui($"{PreviousBpm:0.0} → {Bpm:0.0} BPM");

    public string CrossfadeText => NumFormat.Ui($"{CrossfadeSeconds:0.0} s");

    /// <summary>Comma-joined reasons, e.g. "same key, ±0.4 BPM, holds energy".</summary>
    public string ReasonSummary { get; init; } = string.Empty;
}

/// <summary>Output of <see cref="Services.Entertainment.FlowPathPlanner"/>.</summary>
public sealed record FlowPlanResult
{
    public FlowPlanStatus Status { get; init; } = FlowPlanStatus.Off;

    public IReadOnlyList<FlowStep> Steps { get; init; } = [];

    /// <summary>Human-readable explanation for a non-ready status (or an empty string when ready).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Playable tracks that were scored.</summary>
    public int EvaluatedCandidates { get; init; }

    /// <summary>Of <see cref="EvaluatedCandidates"/>, how many carry usable analysis.</summary>
    public int AnalysedCandidates { get; init; }

    public static FlowPlanResult Empty(FlowPlanStatus status, string message, int evaluated = 0, int analysed = 0) =>
        new() { Status = status, Message = message, EvaluatedCandidates = evaluated, AnalysedCandidates = analysed };
}
