using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.InteropServices;

using SLSKDONET.Models;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Stores musical intelligence data extracted via Essentia (Tier 2 Analysis).
/// Includes BPM, key detection, drop detection, auto-generated cue points, and sonic characteristics.
/// </summary>
[Table("audio_features")]
public class AudioFeaturesEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key link to the LibraryEntry via TrackUniqueHash.
    /// </summary>
    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;

    public AudioFeaturesEntity()
    {
        // Sprint 5C Hardening: Set default neutral values for the 1-9 scale
        Arousal = 5.0f;
        Valence = 5.0f;
        Energy = 0.5f;
        Danceability = 0.5f;
        InstrumentalProbability = 0.5f;
    }

    public double TrackDuration { get; set; } // Duration in seconds from analysis

    // ============================================
    // Core Musical Features (BPM & Key)
    // ============================================
    
    /// <summary>
    /// Beats Per Minute detected by Essentia.
    /// </summary>
    public float Bpm { get; set; }
    
    /// <summary>
    /// BPM detection confidence (0.0 - 1.0).
    /// Values below 0.8 indicate uncertain detection.
    /// </summary>
    public float BpmConfidence { get; set; }
    
    /// <summary>
    /// Musical key in standard notation (e.g., "C#", "Am").
    /// </summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>
    /// Scale type: "major" or "minor".
    /// </summary>
    public string Scale { get; set; } = string.Empty;
    
    /// <summary>
    /// Key detection confidence (0.0 - 1.0).
    /// </summary>
    public float KeyConfidence { get; set; }
    
    /// <summary>
    /// Camelot key notation for harmonic mixing (e.g., "8A", "11B").
    /// Calculated from Key + Scale during analysis.
    /// </summary>
    public string CamelotKey { get; set; } = string.Empty;

    // ============================================
    // Sonic Characteristics
    // ============================================
    
    /// <summary>
    /// Energy level (0.0 - 1.0). Higher = more intense/aggressive.
    /// </summary>
    public float Energy { get; set; } = 0.5f;

    /// <summary>
    /// Mixed In Key compatible energy score (1-10 scale).
    /// </summary>
    public int EnergyScore { get; set; }
    
    /// <summary>
    /// Segmented energy levels (1-10) for each of the 8 structural points.
    /// Stored as a JSON array of integers.
    /// </summary>
    public string SegmentedEnergyJson { get; set; } = "[]";
    
    /// <summary>
    /// Danceability score (0.0 - 1.0). Higher = more suitable for dancing.
    /// </summary>
    public float Danceability { get; set; } = 0.5f;
    
    // Note: Valence moved to EDM Specialist Models section (arousal_valence model)

    /// <summary>
    /// Intensity score (0.0 - 1.0). Composite of onset rate + spectral complexity.
    /// Higher = more sonically complex/intense.
    /// </summary>
    public float Intensity { get; set; }

    /// <summary>
    /// Spectral centroid (Hz). Indicates "brightness" of the sound.
    /// Higher values = brighter, more treble-heavy.
    /// </summary>
    public float SpectralCentroid { get; set; }
    
    /// <summary>
    /// Spectral complexity (0.0 - 1.0). Measures harmonic richness.
    /// </summary>
    public float SpectralComplexity { get; set; }
    
    /// <summary>
    /// Onset rate (events per second). Higher = more rhythmic activity.
    /// </summary>
    public float OnsetRate { get; set; }
    
    /// <summary>
    /// Dynamic complexity. Measures volume variation throughout the track.
    /// </summary>
    public float DynamicComplexity { get; set; }
    
    /// <summary>
    /// Integrated loudness in LUFS (EBU R128 standard).
    /// Typical range: -14 to -8 LUFS for modern masters.
    /// </summary>
    public float LoudnessLUFS { get; set; }

    // ============================================
    // Drop Detection & DJ Cue Points
    // ============================================
    
    /// <summary>
    /// Timestamp (in seconds) of the detected "drop" (main energy peak).
    /// Calculated via intersection of loudness, spectral, and onset signals.
    /// Null if no clear drop detected.
    /// </summary>
    public float? DropTimeSeconds { get; set; }

    /// <summary>
    /// Confidence of the drop detection (0.0 - 1.0).
    /// </summary>
    public float DropConfidence { get; set; }
    
    /// <summary>
    /// Intro cue point (usually 0.0 - start of track).
    /// </summary>
    public float CueIntro { get; set; } = 0f;
    
    /// <summary>
    /// Build-up cue point. Calculated as: DropTime - (60/BPM * 16).
    /// Marks the start of the energy build before the drop.
    /// </summary>
    public float? CueBuild { get; set; }
    
    /// <summary>
    /// Drop cue point (same as DropTimeSeconds).
    /// Marks the exact moment of the main drop.
    /// </summary>
    public float? CueDrop { get; set; }
    
    /// <summary>
    /// Phrase start cue point. Calculated as: DropTime - (60/BPM * 32).
    /// Marks the beginning of the 32-bar phrase containing the drop.
    /// </summary>
    public float? CuePhraseStart { get; set; }

    // ============================================
    // Phase 13A: Forensic Librarian (Drift & Dynamics)
    // ============================================

    /// <summary>
    /// Measures BPM stability (0.0 - 1.0). 
    /// Low scores indicate tempo drift (Live drummer, Vinyl rip, or Transition track).
    /// Derived from Essentia's `bpm_histogram`.
    /// </summary>
    public float BpmStability { get; set; } = 1.0f;

    /// <summary>
    /// True if the track is flagged as "Over-compressed" or "Sausage Master".
    /// Triggered if DynamicComplexity < 2.0 and Loudness > -7 LUFS.
    /// </summary>
    public bool IsDynamicCompressed { get; set; }

    // ============================================
    // Phase 13C: AI Layer (Vibe & Vocals)
    // ============================================

    /// <summary>
    /// Probability that the track is Instrumental (no vocals).
    /// From 'voice_instrumental-msd-musicnn-1.pb'.
    /// </summary>
    public float InstrumentalProbability { get; set; } = 0.5f;

    /// <summary>
    /// "Vibe" classification (Happy, Aggressive, Relaxed, etc.)
    /// Derived from arousal_valence model mapping.
    /// </summary>
    public string MoodTag { get; set; } = string.Empty;

    /// <summary>
    /// Probability score for the primary MoodTag.
    /// </summary>
    public float MoodConfidence { get; set; }

    // ============================================
    // EDM Specialist Models (Phase 17)
    // ============================================

    /// <summary>
    /// Arousal (Energy/Intensity) from arousal_valence model.
    /// Range: 1-9 (1=calm, 9=energetic)
    /// </summary>
    public float Arousal { get; set; } = 5.0f;

    /// <summary>
    /// Valence (Emotion) from arousal_valence model.
    /// Range: 1-9 (1=negative/dark, 9=positive/uplifting)
    /// </summary>
    public float Valence { get; set; } = 5.0f;
    
    // Phase 21: AI Brain Upgrade
    public float? Sadness { get; set; }
    
    // Tier 3: Specialized Analysis
    public float? AvgPitch { get; set; }
    public float? PitchConfidence { get; set; }
    public string VggishEmbeddingJson { get; set; } = string.Empty;
    public string VisualizationVectorJson { get; set; } = string.Empty;
    /// <summary>
    /// Version of the structural analysis algorithm used.
    /// </summary>
    public int StructuralVersion { get; set; }
    /// <summary>
    /// Hash of the structural analysis data, used for change detection.
    /// </summary>
    public string? StructuralHash { get; set; }
    /// <summary>
    /// JSON array of detected anomalies or unusual characteristics in the audio.
    /// </summary>
    public string AnomaliesJson { get; set; } = "[]";
    
    // Raw byte storage for EF Core
    [Column("VectorEmbedding")]
    public byte[]? VectorEmbeddingBytes { get; set; } 

    // Friendly float[] access
    [NotMapped]
    public float[]? VectorEmbedding
    {
        get
        {
            if (VectorEmbeddingBytes == null) return null;
            var floatArray = new float[VectorEmbeddingBytes.Length / 4];
            Buffer.BlockCopy(VectorEmbeddingBytes, 0, floatArray, 0, VectorEmbeddingBytes.Length);
            return floatArray;
        }
        set
        {
            if (value == null)
            {
                VectorEmbeddingBytes = null;
                return;
            }
            var byteArray = new byte[value.Length * 4];
            Buffer.BlockCopy(value, 0, byteArray, 0, byteArray.Length);
            VectorEmbeddingBytes = byteArray;
        }
    }

    // ============================================
    // Phase 5: Deep DNA — 512-D Texture Embedding
    // ============================================

    /// <summary>
    /// Raw 512-D deep texture embedding from discogs-effnet model.
    /// Stored as a compact byte[] BLOB (512 floats × 4 bytes = 2048 bytes).
    /// </summary>
    [Column("DeepTextureEmbedding")]
    public byte[]? DeepTextureEmbeddingBytes { get; set; }

    /// <summary>
    /// Friendly float[] access for the 512-D deep texture vector.
    /// Uses MemoryMarshal.Cast for zero-allocation reinterpretation on read.
    /// </summary>
    [NotMapped]
    public float[]? DeepTextureEmbedding
    {
        get
        {
            if (DeepTextureEmbeddingBytes == null || DeepTextureEmbeddingBytes.Length < 4) return null;
            // MemoryMarshal: reinterprets bytes as floats without copying
            return MemoryMarshal.Cast<byte, float>(DeepTextureEmbeddingBytes.AsSpan()).ToArray();
        }
        set
        {
            if (value == null)
            {
                DeepTextureEmbeddingBytes = null;
                return;
            }
            // MemoryMarshal: reinterprets floats as bytes without copying
            DeepTextureEmbeddingBytes = MemoryMarshal.AsBytes(value.AsSpan()).ToArray();
        }
    }

    /// <summary>
    /// Electronic subgenre from 'genre_electronic-musicnn-msd-2.pb'.
    /// Values: "DnB", "House", "Techno", "Trance", "Ambient", "Unknown"
    /// </summary>
    public string ElectronicSubgenre { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score for ElectronicSubgenre (0.0 - 1.0).
    /// </summary>
    public float ElectronicSubgenreConfidence { get; set; }

    /// <summary>
    /// True if track is primarily rhythmic/percussive (no melody).
    /// From 'tonal_atonal-musicnn-msd-1.pb' (Atonal > 0.8).
    /// Used to flag "DJ Tools" or "Drum Loops".
    /// </summary>
    public bool IsDjTool { get; set; }

    /// <summary>
    /// Tonal probability (0.0 - 1.0). Higher = more melodic.
    /// </summary>
    public float TonalProbability { get; set; }

    // ============================================
    // Advanced Harmonic Mixing
    // ============================================

    /// <summary>
    /// Progression of chords (e.g., "Am | G | F | E").
    /// Simplifies harmonic mixing planning.
    /// </summary>
    public string ChordProgression { get; set; } = string.Empty;

    // ============================================
    // Identity & Metadata
    // ============================================
    
    /// <summary>
    /// Audio fingerprint (AcoustID/Chromaprint). Used for duplicate detection and metadata recovery.
    /// Implementation deferred to Tier 3.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;
    
    /// <summary>
    /// Version string of the analysis engine (e.g., "Essentia-2.1-beta5").
    /// Used to identify when re-analysis is needed after algorithm updates.
    /// </summary>
    public string AnalysisVersion { get; set; } = string.Empty;
    
    /// <summary>
    /// Timestamp when this analysis was performed.
    /// </summary>
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    // ============================================
    // Phase 15: Sonic Taxonomy (Style Lab)
    // ============================================

    /// <summary>
    /// The specific sub-genre detected by the Style Classifier (e.g., "Neurofunk").
    /// </summary>
    public string DetectedSubGenre { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score of the classification (0.0 - 1.0).
    /// </summary>
    public float SubGenreConfidence { get; set; }
    
    /// <summary>
    /// JSON dictionary of style probabilities for hybrid detection.
    /// e.g., {"Neurofunk": 0.6, "Jump Up": 0.4}
    /// </summary>
    public string GenreDistributionJson { get; set; } = "{}";

    // ============================================
    // Phase 15.5: ML.NET Brain
    // ============================================

    /// <summary>
    /// The raw 128-float vector from the embedding model, stored as a compressed JSON string.
    /// Used as input for the LightGBM classifier.
    /// </summary>
    public string AiEmbeddingJson { get; set; } = string.Empty;

    /// <summary>
    /// MusicBrainz Recording ID for deep credit/lineup matching.
    /// </summary>
    public string MusicBrainzId { get; set; } = string.Empty;

    /// <summary>
    /// The final "Vibe" or genre predicted by the ML model.
    /// </summary>
    public string PredictedVibe { get; set; } = string.Empty;

    /// <summary>
    /// The confidence score of the ML prediction (0.0 - 1.0).
    /// </summary>
    public float PredictionConfidence { get; set; }

    /// <summary>
    /// Cache of the embedding vector's magnitude (L2 norm).
    /// Used for O(1) retrieval during Cosine Similarity search.
    /// </summary>
    public float EmbeddingMagnitude { get; set; }

    // ============================================
    // Phase 10.5: Provenance & Reliability
    // ============================================

    /// <summary>
    /// Tiered confidence level for the overall curation of this track.
    /// Helps filter tracks that need human review.
    /// </summary>
    public CurationConfidence CurationConfidence { get; set; } = CurationConfidence.None;

    /// <summary>
    /// The primary source of the metadata (BPM, Key).
    /// </summary>
    public DataSource Source { get; set; } = DataSource.Unknown;

    /// <summary>
    /// JSON blob containing the audit trail of metadata changes.
    /// Tracks who changed what (BPM, Key) and when.
    /// </summary>
    public string ProvenanceJson { get; set; } = string.Empty;

    // ============================================
    // Phase 1 Foundations: Structural & curves
    // ============================================

    /// <summary>
    /// JSON serialized list of structural phrase segments (Intro, Verse, Build, etc.).
    /// Each segment has a label, timestamp, and duration.
    /// </summary>
    public string PhraseSegmentsJson { get; set; } = "[]";

    /// <summary>
    /// Time-series data for energy level throughout the track (JSON array of floats).
    /// </summary>
    public string EnergyCurveJson { get; set; } = "[]";

    /// <summary>
    /// Time-series data for vocal activity/density throughout the track (JSON array of floats).
    /// </summary>
    public string VocalDensityCurveJson { get; set; } = "[]";

    /// <summary>
    /// Detailed forensic reasoning for all major landmarks (Cues, Drop, Segments).
    /// Stores the "Why" behind the intelligence.
    /// </summary>
    public string AnalysisReasoningJson { get; set; } = "{}";

    // ============================================
    // Phase 3.5: Vocal Intelligence
    // ============================================

    /// <summary>
    /// Classification of vocal content (Instrumental, VocalChops, LeadVocal, etc.).
    /// Derived from VocalDensity during analysis and stored for fast retrieval.
    /// Defaults to Instrumental (migration-safe: 0 = no vocals assumed).
    /// </summary>
    public VocalType DetectedVocalType { get; set; } = VocalType.Instrumental;

    /// <summary>
    /// Phase 5.0: Vocal Density Ratio (0.0 – 1.0).
    /// Ratio of 3-second Essentia patches where voice_probability > 0.6 to total patches.
    ///   &lt; 0.05  → Instrumental
    ///   0.05–0.35 → VocalChops
    ///   ≥ 0.35  → LeadVocal
    /// Defaults to 0.0 (Instrumental) for backwards compatibility with unanalysed tracks.
    /// </summary>
    public float VocalDensity { get; set; } = 0.0f;

    /// <summary>
    /// Overall intensity of vocal presence (0.0 - 1.0).
    /// Legacy: use VocalDensity for density-based logic; VocalIntensity retains
    /// the older single-point measurement for backwards compat.
    /// </summary>
    public float VocalIntensity { get; set; }

    /// <summary>
    /// Timestamp of the first significant vocal activity.
    /// </summary>
    public float? VocalStartSeconds { get; set; }

    /// <summary>
    /// Timestamp of the last significant vocal activity.
    /// </summary>
    public float? VocalEndSeconds { get; set; }


    /// <summary>
    /// Phase 17: Persisted DJ Cue Points (JSON blob).
    /// </summary>
    public string? CuePointsJson { get; set; }
}


public enum CurationConfidence
{
    None = 0,
    Low = 1,    // Needs Review (High Variance / No Consensus)
    Medium = 2, // Suggestive (Single Source / Minor Variance)
    High = 3,   // Verified (Multi-Source Consensus / User Approved)
    Manual = 4  // User Manually Overridden/Locked
}

public enum DataSource
{
    Unknown = 0,
    Soulseek = 1,
    Spotify = 2,
    Essentia = 3,
    Manual = 4
}

