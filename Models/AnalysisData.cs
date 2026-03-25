using System.Collections.Generic;

namespace SLSKDONET.Models;

/// <summary>
/// Comprehensive output of the ML analysis pipeline for a single audio track.
/// Produced by Essentia/TensorFlow models and stored alongside the track.
/// </summary>
public class AnalysisData
{
    /// <summary>Core mechanical/rhythmic features extracted by Essentia.</summary>
    public MechanicsData Mechanics { get; set; } = new();

    /// <summary>Affective (emotional) dimensions from the valence/arousal models.</summary>
    public AffectiveData Affective { get; set; } = new();

    /// <summary>Per-mood probability scores (0–100) from the Essentia mood classifiers.</summary>
    public MoodData Moods { get; set; } = new();

    /// <summary>Top genre predictions with confidence scores.</summary>
    public List<GenrePrediction> Genres { get; set; } = new();

    /// <summary>Stem separation status and file locations.</summary>
    public StemData Stems { get; set; } = new();
}

/// <summary>
/// Mechanical/rhythmic audio features.
/// </summary>
public class MechanicsData
{
    /// <summary>Tempo in beats per minute.</summary>
    public double Bpm { get; set; }

    /// <summary>Detected musical key and scale, e.g. "C# Minor" or Camelot "8A".</summary>
    public string KeyScale { get; set; } = string.Empty;

    /// <summary>
    /// Probability that the track is tonal (0.0 = fully atonal, 1.0 = fully tonal).
    /// </summary>
    public double TonalProbability { get; set; }
}

/// <summary>
/// Affective (emotional) dimensions on a continuous scale.
/// </summary>
public class AffectiveData
{
    /// <summary>
    /// Arousal level: degree of energy/excitement.
    /// Range: -1.0 (very calm) to 1.0 (very energetic).
    /// </summary>
    public double Arousal { get; set; }

    /// <summary>
    /// Valence: emotional positivity/negativity.
    /// Range: -1.0 (very negative/sad) to 1.0 (very positive/happy).
    /// </summary>
    public double Valence { get; set; }
}

/// <summary>
/// Per-mood probability scores from the Essentia TensorFlow mood classifiers.
/// All values are in the range 0–100.
/// </summary>
public class MoodData
{
    public double Happy { get; set; }
    public double Sad { get; set; }
    public double Aggressive { get; set; }
    public double Relaxed { get; set; }
    public double Party { get; set; }
}

/// <summary>
/// A single genre prediction from the ML classifier.
/// </summary>
public class GenrePrediction
{
    /// <summary>Genre or sub-genre label (e.g. "House", "Techno", "Deep House").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Classifier confidence, 0.0–1.0.</summary>
    public double Confidence { get; set; }
}

/// <summary>
/// Stem separation status and (optionally) the paths to the separated audio files.
/// </summary>
public class StemData
{
    /// <summary>True when stem files have been generated for this track.</summary>
    public bool AreGenerated { get; set; }

    /// <summary>Path to the vocals stem file, if generated.</summary>
    public string? VocalsPath { get; set; }

    /// <summary>Path to the drums stem file, if generated.</summary>
    public string? DrumsPath { get; set; }

    /// <summary>Path to the bass stem file, if generated.</summary>
    public string? BassPath { get; set; }

    /// <summary>Path to the piano/keys stem file, if generated.</summary>
    public string? PianoPath { get; set; }

    /// <summary>Path to the "other" (everything remaining) stem file, if generated.</summary>
    public string? OtherPath { get; set; }
}
