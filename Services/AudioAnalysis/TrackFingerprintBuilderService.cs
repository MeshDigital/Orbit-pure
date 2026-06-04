using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Pure A10.1 builder that maps existing analysis artifacts to a normalized track fingerprint.
/// </summary>
public sealed class TrackFingerprintBuilderService
{
    private readonly HarmonicAnalysisService _harmonicAnalysisService;

    public TrackFingerprintBuilderService(HarmonicAnalysisService harmonicAnalysisService)
    {
        _harmonicAnalysisService = harmonicAnalysisService;
    }

    public TrackFingerprint Build(
        string trackUniqueHash,
        AudioFeaturesEntity features,
        EssentiaOutput? essentiaOutput,
        IReadOnlyList<TrackPhraseEntity>? phrases,
        DateTime? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        var normalizedPhrases = (phrases ?? Array.Empty<TrackPhraseEntity>())
            .Where(p => p.EndTimeSeconds > p.StartTimeSeconds)
            .OrderBy(p => p.OrderIndex)
            .ToList();

        var durationSeconds = (float)Math.Max(0.001d, features.TrackDuration);
        var globalEnergy = Clamp01(features.Energy);

        var introEnergy = ResolveRoleEnergy(normalizedPhrases, PhraseType.Intro, globalEnergy);
        var buildEnergy = ResolveRoleEnergy(normalizedPhrases, PhraseType.Build, globalEnergy);
        var dropEnergy = ResolveRoleEnergy(normalizedPhrases, PhraseType.Drop, globalEnergy);
        var breakdownEnergy = ResolveRoleEnergy(normalizedPhrases, PhraseType.Breakdown, globalEnergy);
        var outroEnergy = ResolveRoleEnergy(normalizedPhrases, PhraseType.Outro, globalEnergy);

        var introRatio = ResolveRoleLengthRatio(normalizedPhrases, PhraseType.Intro, durationSeconds);
        var buildRatio = ResolveRoleLengthRatio(normalizedPhrases, PhraseType.Build, durationSeconds);
        var dropRatio = ResolveRoleLengthRatio(normalizedPhrases, PhraseType.Drop, durationSeconds);
        var breakdownRatio = ResolveRoleLengthRatio(normalizedPhrases, PhraseType.Breakdown, durationSeconds);
        var outroRatio = ResolveRoleLengthRatio(normalizedPhrases, PhraseType.Outro, durationSeconds);

        HarmonicVector? harmonic = null;
        try
        {
            harmonic = _harmonicAnalysisService.BuildHarmonicVector(essentiaOutput, features);
        }
        catch
        {
            harmonic = null;
        }

        var histogramSignature = ResolveBeatHistogramSignature(essentiaOutput?.Rhythm?.BpmHistogram, features.BpmConfidence);
        var tempoNormalized = Clamp01((features.Bpm - 70f) / 130f);
        var percussiveDensity = Clamp01(features.OnsetRate / 12f);
        var swingGroove = Clamp01(features.DynamicComplexity / 10f);

        var centroidProfile = Clamp01(features.SpectralCentroid / 8000f);
        var complexity = Clamp01(features.SpectralComplexity);
        var brightnessWarmth = Clamp01((centroidProfile * 0.65f) + (Clamp01(features.LoudnessLUFS / -6f) * 0.35f));

        var phraseDensityPerMinute = normalizedPhrases.Count / Math.Max(1f, durationSeconds / 60f);
        var phraseDensity = Clamp01(phraseDensityPerMinute / 16f);
        var buildSlope = Clamp01((buildEnergy - introEnergy + 1f) * 0.5f);

        var fingerprint = new TrackFingerprint
        {
            TrackUniqueHash = trackUniqueHash,
            SchemaVersion = TrackFingerprint.CurrentSchemaVersion,
            GeneratedAtUtc = generatedAtUtc ?? DateTime.UtcNow,
            BuilderVersion = "A10.2",
            Harmonic = harmonic,
            Energy = new EnergyVector
            {
                GlobalEnergy = globalEnergy,
                IntroEnergy = introEnergy,
                BuildEnergy = buildEnergy,
                DropEnergy = dropEnergy,
                BreakdownEnergy = breakdownEnergy,
                OutroEnergy = outroEnergy,
                DropIntensity = Clamp01(Math.Max(0f, dropEnergy - globalEnergy)),
                BreakdownDepth = Clamp01(Math.Max(0f, globalEnergy - breakdownEnergy)),
                Confidence = ResolvePhraseConfidence(normalizedPhrases, fallback: Clamp01(features.DropConfidence)),
            },
            Rhythm = new RhythmVector
            {
                TempoNormalized = tempoNormalized,
                SwingGrooveScore = swingGroove,
                BeatHistogramSignature = histogramSignature,
                PercussiveDensity = percussiveDensity,
                Confidence = Clamp01((Clamp01(features.BpmConfidence) + histogramSignature) * 0.5f),
            },
            Timbre = new TimbreVector
            {
                MfccTextureProxy = complexity,
                SpectralCentroidProfile = centroidProfile,
                BrightnessWarmthBalance = brightnessWarmth,
                SpectralComplexity = complexity,
                Confidence = Clamp01((complexity + Clamp01(features.LoudnessLUFS / -10f)) * 0.5f),
            },
            Structure = new StructureVector
            {
                PhraseMapDensity = phraseDensity,
                IntroLengthRatio = introRatio,
                BuildLengthRatio = buildRatio,
                DropLengthRatio = dropRatio,
                BreakdownLengthRatio = breakdownRatio,
                OutroLengthRatio = outroRatio,
                BuildUpSlope = buildSlope,
                Confidence = ResolvePhraseConfidence(normalizedPhrases, fallback: 0.4f),
            },
            Mood = new MoodVector
            {
                Danceability = Clamp01(features.Danceability),
                Aggressiveness = Clamp01(features.Intensity > 0 ? features.Intensity : features.Energy),
                Acousticness = Clamp01(1f - Clamp01(features.SpectralComplexity)),
                TonalVsPercussiveBalance = Clamp01(features.TonalProbability),
                Confidence = Clamp01(features.MoodConfidence > 0 ? features.MoodConfidence : 0.4f),
            },
        };

        return fingerprint;
    }

    private static float ResolveBeatHistogramSignature(float[]? histogram, float bpmConfidence)
    {
        if (histogram is null || histogram.Length == 0)
            return Clamp01(bpmConfidence);

        var sum = histogram.Sum(v => Math.Max(0f, v));
        if (sum <= 0)
            return Clamp01(bpmConfidence);

        var peak = histogram.Max(v => Math.Max(0f, v));
        return Clamp01(peak / sum);
    }

    private static float ResolveRoleEnergy(IReadOnlyList<TrackPhraseEntity> phrases, PhraseType type, float fallback)
    {
        var values = phrases
            .Where(p => p.Type == type)
            .Select(p => Clamp01(p.EnergyLevel))
            .ToList();

        if (values.Count == 0)
            return fallback;

        return Clamp01(values.Average());
    }

    private static float ResolveRoleLengthRatio(IReadOnlyList<TrackPhraseEntity> phrases, PhraseType type, float durationSeconds)
    {
        if (durationSeconds <= 0f)
            return 0f;

        var sum = phrases
            .Where(p => p.Type == type)
            .Sum(p => Math.Max(0f, p.EndTimeSeconds - p.StartTimeSeconds));

        return Clamp01(sum / durationSeconds);
    }

    private static float ResolvePhraseConfidence(IReadOnlyList<TrackPhraseEntity> phrases, float fallback)
    {
        if (phrases.Count == 0)
            return Clamp01(fallback);

        return Clamp01(phrases.Average(p => Clamp01(p.Confidence)));
    }
    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
}