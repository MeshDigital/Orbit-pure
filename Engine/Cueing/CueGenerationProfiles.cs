using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Cueing;

/// <summary>Which generation flow a cue profile asks for.</summary>
public enum CueGenerationStrategy
{
    Auto,
    Phrase,
    Dsp,
    Heuristic,
}

/// <summary>
/// A named cue-generation flow. Profiles are deliberately small and additive: they select which
/// existing path runs (and whether the phrase-path DSP rescue may override a phrase drop), so the
/// same analysis signals can be compared side by side without a second analysis pipeline.
/// </summary>
public sealed record CueGenerationProfile(
    string Id,
    string Name,
    string Description,
    CueGenerationStrategy Strategy,
    bool UseDspRescue = true)
{
    public override string ToString() => Name;
}

/// <summary>The built-in experimental profiles shown in Cue Lab. Default is the current balanced flow.</summary>
public static class CueGenerationProfiles
{
    public static readonly CueGenerationProfile Balanced = new(
        "balanced", "Balanced", "Current flow: phrase when trustworthy, otherwise DSP, then heuristic.", CueGenerationStrategy.Auto);

    public static readonly CueGenerationProfile PhraseFirst = new(
        "phrase", "Phrase first", "Use the phrase map whenever it has at least two drops; skip DSP rescue.", CueGenerationStrategy.Phrase, UseDspRescue: false);

    public static readonly CueGenerationProfile PhraseWithRescue = new(
        "phrase-rescue", "Phrase + DSP rescue", "Phrase map, but allow the old early-DSP rescue.", CueGenerationStrategy.Phrase, UseDspRescue: true);

    public static readonly CueGenerationProfile DspOnly = new(
        "dsp", "DSP only", "Ignore phrase segments; use sub-bass/flux/energy candidates only.", CueGenerationStrategy.Dsp);

    public static readonly CueGenerationProfile HeuristicOnly = new(
        "heuristic", "Heuristic only", "Ignore phrase and DSP paths; use transient clustering.", CueGenerationStrategy.Heuristic);

    public static readonly IReadOnlyList<CueGenerationProfile> All = new[]
    {
        Balanced, PhraseFirst, PhraseWithRescue, DspOnly, HeuristicOnly,
    };

    public static CueGenerationProfile Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Balanced;
}
