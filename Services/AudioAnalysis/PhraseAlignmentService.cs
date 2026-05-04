using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Contract for phrase and transition alignment decisions.
/// Keeps the transition advisor and structural analysis stack testable.
/// </summary>
public interface IPhraseAlignmentService
{
    Task<IReadOnlyList<TrackPhraseEntity>> AlignPhrasesAsync(
        IReadOnlyList<RawBoundary> rawBoundaries,
        double bpm,
        string? genreHint,
        string? trackUniqueHash = null,
        CancellationToken cancellationToken = default);

    TransitionPoint? DetermineOptimalTransitionTime(
        LibraryEntryEntity? outgoingTrack,
        LibraryEntryEntity? incomingTrack,
        TransitionArchetype archetype);
}

/// <summary>
/// Raw phrase or energy boundary before it has been snapped to a genre-aware musical grid.
/// </summary>
public sealed class RawBoundary
{
    public double StartTimeSeconds { get; set; }
    public double TimestampSeconds
    {
        get => StartTimeSeconds;
        set => StartTimeSeconds = value;
    }

    public double? EndTimeSeconds { get; set; }
    public float EnergyLevel { get; set; }
    public float Confidence { get; set; } = 0.5f;
    public string Label { get; set; } = string.Empty;
    public PhraseType? SuggestedType { get; set; }
}

/// <summary>
/// Genre template used to convert loose energy boundaries into DJ-ready section anchors.
/// </summary>
public sealed record GenreStructurePreset(
    string Genre,
    int IntroBars,
    int Build1Bars,
    int Drop1Bars,
    int BreakBars,
    int Build2Bars,
    int Drop2Bars,
    int OutroBars,
    double ToleranceFactor = 0.20d)
{
    public IReadOnlyList<(PhraseType Type, string Label, int Bars)> GetTemplate()
    {
        return new[]
        {
            (PhraseType.Intro, "Intro", IntroBars),
            (PhraseType.Build, "Build 1", Build1Bars),
            (PhraseType.Drop, "Drop 1", Drop1Bars),
            (PhraseType.Breakdown, "Break", BreakBars),
            (PhraseType.Build, "Build 2", Build2Bars),
            (PhraseType.Drop, "Drop 2", Drop2Bars),
            (PhraseType.Outro, "Outro", OutroBars)
        };
    }
}

/// <summary>
/// A concrete transition suggestion with a timestamp and human-readable reason.
/// </summary>
public readonly record struct TransitionPoint(double Time, string Reason, float Confidence = 1.0f);

/// <summary>
/// Snaps raw structural boundaries to genre-aware bar templates.
/// This allows ORBIT to reason in DJ-meaningful sections such as intros, builds, drops and outros.
/// </summary>
public sealed class PhraseAlignmentService : IPhraseAlignmentService
{
    private readonly ILogger<PhraseAlignmentService> _logger;

    public PhraseAlignmentService(ILogger<PhraseAlignmentService>? logger = null)
    {
        _logger = logger ?? NullLogger<PhraseAlignmentService>.Instance;
    }

    public static IReadOnlyDictionary<string, GenreStructurePreset> Presets { get; } =
        new Dictionary<string, GenreStructurePreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["EDM"] = new("EDM", 32, 16, 32, 16, 16, 32, 32, 0.20d),
            ["TechHouse"] = new("TechHouse", 32, 16, 64, 16, 16, 64, 32, 0.18d),
            ["MelodicTechno"] = new("MelodicTechno", 32, 32, 64, 32, 16, 64, 32, 0.18d),
            ["Pop"] = new("Pop", 8, 8, 16, 8, 8, 16, 8, 0.25d),
            ["DnB"] = new("DnB", 32, 16, 32, 16, 16, 32, 32, 0.20d),
            ["Trap"] = new("Trap", 16, 16, 32, 8, 16, 32, 16, 0.22d)
        };

    public Task<IReadOnlyList<TrackPhraseEntity>> AlignPhrasesAsync(
        IReadOnlyList<RawBoundary> rawBoundaries,
        double bpm,
        string? genreHint,
        string? trackUniqueHash = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (bpm <= 0)
        {
            _logger.LogWarning("Phrase alignment received invalid BPM {Bpm}; defaulting to 128.", bpm);
            bpm = 128.0d;
        }

        var preset = ResolvePreset(genreHint);
        var boundaryList = rawBoundaries?
            .Where(b => b is not null)
            .OrderBy(b => b.StartTimeSeconds)
            .ToList() ?? new List<RawBoundary>();

        var expectedSections = BuildExpectedSections(preset, bpm).ToList();
        var estimatedDuration = EstimateDurationSeconds(boundaryList, expectedSections);

        var aligned = new List<AlignedPhraseCandidate>();
        var usedExpectedIndexes = new HashSet<int>();

        foreach (var raw in boundaryList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bestMatch = FindBestExpectedMatch(raw, expectedSections, usedExpectedIndexes, preset);
            if (bestMatch is not null)
            {
                usedExpectedIndexes.Add(bestMatch.Value.Index);
                aligned.Add(new AlignedPhraseCandidate(
                    bestMatch.Value.Section.Type,
                    bestMatch.Value.Section.Label,
                    bestMatch.Value.Section.StartSeconds,
                    raw.EndTimeSeconds,
                    raw.EnergyLevel,
                    CalculateConfidence(raw, bestMatch.Value.Section, preset, labelMatched: true),
                    bestMatch.Value.Index));
            }
            else
            {
                var inferredType = InferPhraseType(raw, estimatedDuration);
                aligned.Add(new AlignedPhraseCandidate(
                    inferredType,
                    string.IsNullOrWhiteSpace(raw.Label) ? inferredType.ToString() : raw.Label,
                    raw.StartTimeSeconds,
                    raw.EndTimeSeconds,
                    raw.EnergyLevel,
                    Math.Clamp(raw.Confidence, 0.20f, 0.95f),
                    aligned.Count));
            }
        }

        if (aligned.Count == 0)
        {
            for (var i = 0; i < expectedSections.Count; i++)
            {
                var section = expectedSections[i];
                aligned.Add(new AlignedPhraseCandidate(
                    section.Type,
                    section.Label,
                    section.StartSeconds,
                    section.StartSeconds + section.DurationSeconds,
                    0.5f,
                    0.35f,
                    i));
            }
        }
        else
        {
            for (var i = 0; i < expectedSections.Count; i++)
            {
                if (usedExpectedIndexes.Contains(i))
                {
                    continue;
                }

                var section = expectedSections[i];
                var nearby = aligned.Any(a => Math.Abs(a.StartSeconds - section.StartSeconds) <= Math.Max(1.5d, section.DurationSeconds * 0.25d));
                if (!nearby)
                {
                    aligned.Add(new AlignedPhraseCandidate(
                        section.Type,
                        section.Label,
                        section.StartSeconds,
                        section.StartSeconds + section.DurationSeconds,
                        0.5f,
                        0.30f,
                        i));
                }
            }
        }

        var deduped = aligned
            .OrderBy(a => a.StartSeconds)
            .GroupBy(a => Math.Round(a.StartSeconds, 1))
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderBy(a => a.StartSeconds)
            .ToList();

        var phrases = new List<TrackPhraseEntity>(deduped.Count);
        for (var i = 0; i < deduped.Count; i++)
        {
            var current = deduped[i];
            var nextStart = i < deduped.Count - 1
                ? deduped[i + 1].StartSeconds
                : current.EndSeconds ?? Math.Min(estimatedDuration, current.StartSeconds + FindPreferredDuration(current.Type, preset, bpm));

            if (nextStart <= current.StartSeconds)
            {
                nextStart = current.StartSeconds + Math.Max(2.0d, FindPreferredDuration(current.Type, preset, bpm));
            }

            phrases.Add(new TrackPhraseEntity
            {
                TrackUniqueHash = trackUniqueHash ?? string.Empty,
                Type = current.Type,
                StartTimeSeconds = (float)Math.Max(0d, current.StartSeconds),
                EndTimeSeconds = (float)Math.Max(current.StartSeconds, nextStart),
                EnergyLevel = Math.Clamp(current.EnergyLevel, 0f, 1f),
                Confidence = Math.Clamp(current.Confidence, 0f, 1f),
                OrderIndex = i,
                Label = current.Label
            });
        }

        _logger.LogDebug(
            "Aligned {BoundaryCount} raw boundaries into {PhraseCount} phrases using preset {Preset} at BPM {Bpm}.",
            boundaryList.Count,
            phrases.Count,
            preset.Genre,
            bpm);

        return Task.FromResult<IReadOnlyList<TrackPhraseEntity>>(phrases);
    }

    public TransitionPoint? DetermineOptimalTransitionTime(
        LibraryEntryEntity? outgoingTrack,
        LibraryEntryEntity? incomingTrack,
        TransitionArchetype archetype)
    {
        if (outgoingTrack is null)
        {
            return null;
        }

        var outgoingSegments = ParseSegments(outgoingTrack.AudioFeatures?.PhraseSegmentsJson);
        var vocalSafeTime = Math.Max(0d, outgoingTrack.VocalEndSeconds ?? 0d);

        if (archetype == TransitionArchetype.BuildToDrop)
        {
            var buildSegment = outgoingSegments
                .FirstOrDefault(s => s.Label.Contains("build", StringComparison.OrdinalIgnoreCase) ||
                                     s.Label.Contains("riser", StringComparison.OrdinalIgnoreCase));

            var incomingDrop = incomingTrack?.AudioFeatures?.DropTimeSeconds;
            if (buildSegment is not null && incomingDrop.HasValue)
            {
                var buildEnd = buildSegment.Start + buildSegment.Duration;
                var targetStart = Math.Max(0d, buildEnd - incomingDrop.Value);
                return new TransitionPoint(
                    targetStart,
                    "Build-to-Drop alignment selected from outgoing build end into incoming drop.",
                    0.95f);
            }
        }

        var phraseBoundaries = outgoingSegments
            .SelectMany(segment => new[] { (double)segment.Start, segment.Start + segment.Duration })
            .Where(time => time >= 0d)
            .Distinct()
            .OrderBy(time => time)
            .ToList();

        if (phraseBoundaries.Count == 0)
        {
            var fallback = vocalSafeTime > 0d ? vocalSafeTime : 0d;
            return new TransitionPoint(fallback, "Fallback transition point chosen because no phrase boundary data was available.", 0.50f);
        }

        var chosenBoundary = phraseBoundaries.FirstOrDefault(time => time >= vocalSafeTime);
        if (chosenBoundary <= 0d && phraseBoundaries.Count > 0)
        {
            chosenBoundary = phraseBoundaries.Last();
        }

        var reason = vocalSafeTime > 0d
            ? "Transition aligned to a phrase boundary after vocals clear."
            : "Transition aligned to the nearest phrase boundary.";

        return new TransitionPoint(chosenBoundary, reason, vocalSafeTime > 0d ? 0.90f : 0.80f);
    }

    private static GenreStructurePreset ResolvePreset(string? genreHint)
    {
        if (string.IsNullOrWhiteSpace(genreHint))
        {
            return Presets["EDM"];
        }

        var normalized = genreHint.Trim().Replace("-", string.Empty).Replace(" ", string.Empty);

        if (normalized.Contains("techhouse", StringComparison.OrdinalIgnoreCase))
        {
            return Presets["TechHouse"];
        }

        if (normalized.Contains("melodic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("techn", StringComparison.OrdinalIgnoreCase))
        {
            return Presets["MelodicTechno"];
        }

        if (normalized.Contains("dnb", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("drumandbass", StringComparison.OrdinalIgnoreCase))
        {
            return Presets["DnB"];
        }

        if (normalized.Contains("trap", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("hiphop", StringComparison.OrdinalIgnoreCase))
        {
            return Presets["Trap"];
        }

        if (normalized.Contains("pop", StringComparison.OrdinalIgnoreCase))
        {
            return Presets["Pop"];
        }

        return Presets.TryGetValue(genreHint.Trim(), out var directPreset)
            ? directPreset
            : Presets["EDM"];
    }

    private static IEnumerable<ExpectedSection> BuildExpectedSections(GenreStructurePreset preset, double bpm)
    {
        var secondsPerBar = (4d * 60d) / bpm;
        var cumulativeBars = 0;

        foreach (var templatePart in preset.GetTemplate())
        {
            var startSeconds = cumulativeBars * secondsPerBar;
            var durationSeconds = templatePart.Bars * secondsPerBar;

            yield return new ExpectedSection(
                templatePart.Type,
                templatePart.Label,
                templatePart.Bars,
                startSeconds,
                durationSeconds);

            cumulativeBars += templatePart.Bars;
        }
    }

    private static (int Index, ExpectedSection Section)? FindBestExpectedMatch(
        RawBoundary raw,
        IReadOnlyList<ExpectedSection> expectedSections,
        HashSet<int> usedExpectedIndexes,
        GenreStructurePreset preset)
    {
        var ranked = expectedSections
            .Select((section, index) => (index, section, delta: Math.Abs(raw.StartTimeSeconds - section.StartSeconds)))
            .OrderBy(item => item.delta)
            .ToList();

        foreach (var candidate in ranked)
        {
            if (usedExpectedIndexes.Contains(candidate.index))
            {
                continue;
            }

            var toleranceSeconds = Math.Max(1.5d, candidate.section.DurationSeconds * preset.ToleranceFactor);
            var labelLooksCompatible = LabelLooksCompatible(raw, candidate.section.Type, candidate.section.Label);

            if (candidate.delta <= toleranceSeconds || labelLooksCompatible)
            {
                return (candidate.index, candidate.section);
            }
        }

        return null;
    }

    private static float CalculateConfidence(RawBoundary raw, ExpectedSection section, GenreStructurePreset preset, bool labelMatched)
    {
        var toleranceSeconds = Math.Max(1.5d, section.DurationSeconds * preset.ToleranceFactor);
        var delta = Math.Abs(raw.StartTimeSeconds - section.StartSeconds);
        var closeness = 1d - Math.Min(1d, delta / toleranceSeconds);

        var confidence = (raw.Confidence * 0.65d) + (closeness * 0.25d) + (labelMatched ? 0.10d : 0d);
        return (float)Math.Clamp(confidence, 0.20d, 0.99d);
    }

    private static bool LabelLooksCompatible(RawBoundary raw, PhraseType type, string expectedLabel)
    {
        if (raw.SuggestedType.HasValue && raw.SuggestedType.Value == type)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw.Label))
        {
            return false;
        }

        var label = raw.Label.Trim();
        if (label.Contains(expectedLabel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return type switch
        {
            PhraseType.Intro => label.Contains("intro", StringComparison.OrdinalIgnoreCase) || label.Contains("start", StringComparison.OrdinalIgnoreCase),
            PhraseType.Build => label.Contains("build", StringComparison.OrdinalIgnoreCase) || label.Contains("riser", StringComparison.OrdinalIgnoreCase),
            PhraseType.Drop => label.Contains("drop", StringComparison.OrdinalIgnoreCase) || label.Contains("chorus", StringComparison.OrdinalIgnoreCase),
            PhraseType.Breakdown => label.Contains("break", StringComparison.OrdinalIgnoreCase) || label.Contains("breakdown", StringComparison.OrdinalIgnoreCase),
            PhraseType.Outro => label.Contains("outro", StringComparison.OrdinalIgnoreCase) || label.Contains("end", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static PhraseType InferPhraseType(RawBoundary raw, double estimatedDuration)
    {
        if (raw.SuggestedType.HasValue)
        {
            return raw.SuggestedType.Value;
        }

        if (!string.IsNullOrWhiteSpace(raw.Label))
        {
            if (raw.Label.Contains("intro", StringComparison.OrdinalIgnoreCase)) return PhraseType.Intro;
            if (raw.Label.Contains("build", StringComparison.OrdinalIgnoreCase) || raw.Label.Contains("riser", StringComparison.OrdinalIgnoreCase)) return PhraseType.Build;
            if (raw.Label.Contains("drop", StringComparison.OrdinalIgnoreCase) || raw.Label.Contains("chorus", StringComparison.OrdinalIgnoreCase)) return PhraseType.Drop;
            if (raw.Label.Contains("break", StringComparison.OrdinalIgnoreCase)) return PhraseType.Breakdown;
            if (raw.Label.Contains("outro", StringComparison.OrdinalIgnoreCase)) return PhraseType.Outro;
        }

        var progress = estimatedDuration > 0d ? raw.StartTimeSeconds / estimatedDuration : 0d;
        if (progress <= 0.12d) return PhraseType.Intro;
        if (progress >= 0.82d) return PhraseType.Outro;
        if (raw.EnergyLevel >= 0.72f) return PhraseType.Drop;
        if (raw.EnergyLevel <= 0.32f) return PhraseType.Breakdown;
        return PhraseType.Build;
    }

    private static double EstimateDurationSeconds(IReadOnlyList<RawBoundary> boundaries, IReadOnlyList<ExpectedSection> expectedSections)
    {
        var rawEnd = boundaries
            .Select(b => b.EndTimeSeconds ?? b.StartTimeSeconds)
            .DefaultIfEmpty(0d)
            .Max();

        var templateEnd = expectedSections.Count > 0
            ? expectedSections.Max(s => s.StartSeconds + s.DurationSeconds)
            : 0d;

        return Math.Max(rawEnd, templateEnd);
    }

    private static double FindPreferredDuration(PhraseType type, GenreStructurePreset preset, double bpm)
    {
        var secondsPerBar = (4d * 60d) / bpm;
        var bars = preset.GetTemplate()
            .FirstOrDefault(t => t.Type == type).Bars;

        if (bars <= 0)
        {
            bars = 16;
        }

        return bars * secondsPerBar;
    }

    private static List<PhraseSegment> ParseSegments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<PhraseSegment>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new List<PhraseSegment>();
        }
        catch
        {
            return new List<PhraseSegment>();
        }
    }

    private sealed record ExpectedSection(
        PhraseType Type,
        string Label,
        int Bars,
        double StartSeconds,
        double DurationSeconds);

    private sealed record AlignedPhraseCandidate(
        PhraseType Type,
        string Label,
        double StartSeconds,
        double? EndSeconds,
        float EnergyLevel,
        float Confidence,
        int OrderIndex);
}
