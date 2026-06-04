using System;

namespace SLSKDONET.Models;

/// <summary>
/// A10.1 fingerprint schema used as the data backbone for similarity and optimizer slices.
/// </summary>
public sealed class TrackFingerprint
{
    public const int CurrentSchemaVersion = 2;

    public string TrackUniqueHash { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public string BuilderVersion { get; set; } = "A10.1";

    public HarmonicVector? Harmonic { get; set; }
    public EnergyVector Energy { get; set; } = new();
    public RhythmVector Rhythm { get; set; } = new();
    public TimbreVector Timbre { get; set; } = new();
    public StructureVector Structure { get; set; } = new();
    public MoodVector Mood { get; set; } = new();
}

public sealed class HarmonicVector
{
    public string? PrimaryKey { get; set; }
    public float PrimaryConfidence { get; set; }

    public string[] SecondaryKeys { get; set; } = Array.Empty<string>();
    public float[] SecondaryConfidences { get; set; } = Array.Empty<float>();

    public float PrimaryKeyPositionNormalized { get; set; }
    public float ModulationScore { get; set; }
    public float StabilityScore { get; set; }
}

public sealed class EnergyVector
{
    public float GlobalEnergy { get; set; }

    public float IntroEnergy { get; set; }
    public float BuildEnergy { get; set; }
    public float DropEnergy { get; set; }
    public float BreakdownEnergy { get; set; }
    public float OutroEnergy { get; set; }

    public float DropIntensity { get; set; }
    public float BreakdownDepth { get; set; }
    public float Confidence { get; set; }
}

public sealed class RhythmVector
{
    public float TempoNormalized { get; set; }
    public float SwingGrooveScore { get; set; }
    public float BeatHistogramSignature { get; set; }
    public float PercussiveDensity { get; set; }
    public float Confidence { get; set; }
}

public sealed class TimbreVector
{
    public float MfccTextureProxy { get; set; }
    public float SpectralCentroidProfile { get; set; }
    public float BrightnessWarmthBalance { get; set; }
    public float SpectralComplexity { get; set; }
    public float Confidence { get; set; }
}

public sealed class StructureVector
{
    public float PhraseMapDensity { get; set; }

    public float IntroLengthRatio { get; set; }
    public float BuildLengthRatio { get; set; }
    public float DropLengthRatio { get; set; }
    public float BreakdownLengthRatio { get; set; }
    public float OutroLengthRatio { get; set; }

    public float BuildUpSlope { get; set; }
    public float Confidence { get; set; }
}

public sealed class MoodVector
{
    public float Danceability { get; set; }
    public float Aggressiveness { get; set; }
    public float Acousticness { get; set; }
    public float TonalVsPercussiveBalance { get; set; }
    public float Confidence { get; set; }
}