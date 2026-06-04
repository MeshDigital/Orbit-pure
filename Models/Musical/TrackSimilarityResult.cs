using System.Collections.Generic;

namespace SLSKDONET.Models.Musical;

public enum TrackSimilarityProfile
{
    BlendSafe = 0,
    EnergyDrive = 1,
    GenreCohesion = 2,
}

public enum TransitionStyle
{
    SmoothBlend = 0,
    EnergyLift = 1,
    DropSwap = 2,
    BreakdownReset = 3,
    TensionBridge = 4,
    RiskyClash = 5,
}

public sealed class TransitionStyleResult
{
    public TransitionStyle Style { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class TrackSimilarityResult
{
    public TrackSimilarityProfile Profile { get; init; } = TrackSimilarityProfile.BlendSafe;
    public double WholeTrackSimilarity { get; init; }
    public double SegmentSimilarity { get; init; }
    public double FinalSimilarity { get; init; }
    public SimilarityVectorScores VectorScores { get; init; } = new();
    public SegmentSimilarityScores SegmentScores { get; init; } = new();
    public IReadOnlyList<string> ReasonTags { get; init; } = new List<string>();
}

public sealed class SimilarityVectorScores
{
    public double Harmonic { get; init; }
    public double Energy { get; init; }
    public double Rhythm { get; init; }
    public double Timbre { get; init; }
    public double Structure { get; init; }
    public double Mood { get; init; }
}

public sealed class SegmentSimilarityScores
{
    public double Intro { get; init; }
    public double Build { get; init; }
    public double Drop { get; init; }
    public double Breakdown { get; init; }
    public double Outro { get; init; }
}