using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Library;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Represents a single track entry in the analysis queue or library list,
/// carrying both display metadata and the result of ML analysis.
/// </summary>
public class AnalysisTrackItem : ReactiveObject
{
    private AnalysisRunStatus _analysisStatus = AnalysisRunStatus.Queued;
    private AnalysisData? _analysisData;
    private int _progressPercent;
    private string _currentStep = string.Empty;
    private bool _isInQueue;
    private byte[] _waveformLow;
    private byte[] _waveformMid;
    private byte[] _waveformHigh;
    private int _cueCount;
    private IReadOnlyList<double> _energyCurvePoints;
    private IReadOnlyList<double> _segmentedEnergyPoints;
    private double? _dropTimeSeconds;
    private double? _dropConfidence;
    private double? _qualityConfidence;
    private double? _bpmStability;
    private double? _vocalDensity;
    private double? _loudnessLufs;
    private string? _camelotKey;
    private string? _chordProgression;
    private string _vocalTypeLabel;
    private IReadOnlyList<PhraseTimelineBlock> _phraseTimelineBlocks;
    private double _durationSeconds;

    public string TrackId { get; private set; }
    public string Artist { get; private set; }
    public string Title { get; private set; }
    public string? Album { get; private set; }
    public double? Bpm { get; private set; }
    public string? MusicalKey { get; private set; }
    public string? FilePath { get; private set; }

    /// <summary>Current analysis run status (Queued → Processing → Completed/Failed).</summary>
    public AnalysisRunStatus AnalysisStatus
    {
        get => _analysisStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _analysisStatus, value);
            this.RaisePropertyChanged(nameof(StatusLabel));
            this.RaisePropertyChanged(nameof(IsProcessing));
            this.RaisePropertyChanged(nameof(IsCompleted));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsQueued));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysis));
            this.RaisePropertyChanged(nameof(HasSufficientAnalysis));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }
    }

    /// <summary>Full ML analysis output; non-null only when Status == Completed.</summary>
    public AnalysisData? AnalysisData
    {
        get => _analysisData;
        set
        {
            this.RaiseAndSetIfChanged(ref _analysisData, value);
            StemsReady = value?.Stems?.AreGenerated ?? false;
            this.RaisePropertyChanged(nameof(HasAnalysis));
            this.RaisePropertyChanged(nameof(HasConfidenceData));
            this.RaisePropertyChanged(nameof(BpmConfidence));
            this.RaisePropertyChanged(nameof(KeyConfidence));
            this.RaisePropertyChanged(nameof(PrimaryGenre));
            this.RaisePropertyChanged(nameof(CueCount));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysis));
            this.RaisePropertyChanged(nameof(HasSufficientAnalysis));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }
    }

    /// <summary>Progress percentage (0–100) while processing.</summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        set => this.RaiseAndSetIfChanged(ref _progressPercent, value);
    }

    /// <summary>Human-readable description of the current pipeline stage.</summary>
    public string CurrentStep
    {
        get => _currentStep;
        set => this.RaiseAndSetIfChanged(ref _currentStep, value);
    }

    /// <summary>True when this track has been added to the analysis queue.</summary>
    public bool IsInQueue
    {
        get => _isInQueue;
        set => this.RaiseAndSetIfChanged(ref _isInQueue, value);
    }

    // ── Issue 7.3 / #43 additions ─────────────────────────────────────────

    private bool   _stemsReady;
    private bool   _isInPlaylist;
    private string? _analysisError;
    private string? _stemError;
    private DateTime? _lastAnalyzedAt;
    private string? _modelVersion;

    /// <summary>True when stem separation output files are present and valid.</summary>
    public bool StemsReady
    {
        get => _stemsReady;
        set => this.RaiseAndSetIfChanged(ref _stemsReady, value);
    }

    /// <summary>True when this track has been added to the active automix playlist.</summary>
    public bool IsInPlaylist
    {
        get => _isInPlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _isInPlaylist, value);
            this.RaisePropertyChanged(nameof(PlaylistActionLabel));
        }
    }

    /// <summary>
    /// Non-null when audio analysis failed; contains a human-readable error summary.
    /// Displayed as an inline error badge on the track row.
    /// </summary>
    public string? AnalysisError
    {
        get => _analysisError;
        set
        {
            this.RaiseAndSetIfChanged(ref _analysisError, value);
            this.RaisePropertyChanged(nameof(HasAnalysisError));
            this.RaisePropertyChanged(nameof(IsLikelyCorruptFile));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysis));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }
    }

    /// <summary>Non-null when stem separation failed.</summary>
    public string? StemError
    {
        get => _stemError;
        set
        {
            this.RaiseAndSetIfChanged(ref _stemError, value);
            this.RaisePropertyChanged(nameof(HasStemError));
        }
    }

    /// <summary>
    /// UTC timestamp of the last successful analysis run.
    /// Used by the hover/tooltip to show "Last analyzed 2 days ago" etc.
    /// </summary>
    public DateTime? LastAnalyzedAt
    {
        get => _lastAnalyzedAt;
        set
        {
            this.RaiseAndSetIfChanged(ref _lastAnalyzedAt, value);
            this.RaisePropertyChanged(nameof(LastAnalyzedDisplay));
        }
    }

    /// <summary>
    /// Version tag of the ML model used for the latest analysis (e.g., "essentia-2.1-b6").
    /// Shown in the tooltip alongside the timestamp.
    /// </summary>
    public string? ModelVersion
    {
        get => _modelVersion;
        set => this.RaiseAndSetIfChanged(ref _modelVersion, value);
    }

    public bool HasAnalysisError => !string.IsNullOrWhiteSpace(AnalysisError);
    public bool HasStemError     => !string.IsNullOrWhiteSpace(StemError);

    /// <summary>
    /// True when the last analysis attempt failed because the audio file itself has decode
    /// errors serious enough to block analysis (see AudioCorruptionScannerService), rather than
    /// a transient/environmental failure. Re-analyzing will fail identically every time for these
    /// — the fix is re-acquiring the file, not retrying — so the UI should say so instead of
    /// implying a retry might help.
    /// </summary>
    public bool IsLikelyCorruptFile =>
        !string.IsNullOrWhiteSpace(AnalysisError) &&
        AnalysisError.Contains("Corrupt file", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable "Last analyzed" display string for tooltip use.</summary>
    public string LastAnalyzedDisplay
    {
        get
        {
            if (LastAnalyzedAt is null) return "Not yet analyzed";
            var ago = DateTime.UtcNow - LastAnalyzedAt.Value;
            if (ago.TotalMinutes < 2)  return "Just now";
            if (ago.TotalHours   < 1)  return $"{(int)ago.TotalMinutes} min ago";
            if (ago.TotalDays    < 1)  return $"{(int)ago.TotalHours} hr ago";
            if (ago.TotalDays    < 7)  return $"{(int)ago.TotalDays} day(s) ago";
            return LastAnalyzedAt.Value.ToString("yyyy-MM-dd");
        }
    }

    // ── Engine coverage ───────────────────────────────────────────────────
    // Null until a coverage pass has run for this row. When it is set, it — not the fabricated
    // AnalysisData — decides what the row claims about analysis.

    private TrackAnalysisCoverage? _coverage;

    /// <summary>What this track has and what it lacks, per engine. Null when not yet measured.</summary>
    public TrackAnalysisCoverage? Coverage
    {
        get => _coverage;
        set
        {
            this.RaiseAndSetIfChanged(ref _coverage, value);
            this.RaisePropertyChanged(nameof(HasCoverage));
            this.RaisePropertyChanged(nameof(IsFullyAnalysed));
            this.RaisePropertyChanged(nameof(HasMissingEngines));
            this.RaisePropertyChanged(nameof(MissingEnginesSummary));
            this.RaisePropertyChanged(nameof(HasAnalysis));
            this.RaisePropertyChanged(nameof(HasIncompleteAnalysis));
            this.RaisePropertyChanged(nameof(HasSufficientAnalysis));
            this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        }
    }

    public bool HasCoverage => _coverage is not null;

    /// <summary>True only when every engine in the coverage mask produced output.</summary>
    public bool IsFullyAnalysed => _coverage?.IsComplete ?? false;

    /// <summary>True when this track is measurable and at least one engine is missing.</summary>
    public bool HasMissingEngines => _coverage is { IsComplete: false };

    /// <summary>
    /// "Missing Key and Cues · Waveform outdated" for the row's warning line; empty when complete
    /// or unmeasured.
    /// </summary>
    public string MissingEnginesSummary => _coverage is { IsComplete: false } coverage
        ? coverage.MissingSummary
        : string.Empty;

    /// <summary>The engines a "run what's missing" action should queue for this row.</summary>
    public AnalysisEngine RunnableMissingEngines => _coverage?.RunnableMissing ?? AnalysisEngine.None;

    public bool HasAnalysis => _coverage?.HasAnyOutput ?? AnalysisData is not null;
    public bool HasIncompleteAnalysis => GetIncompleteAnalysisReasons().Count > 0;
    public bool HasSufficientAnalysis => !HasIncompleteAnalysis;
    public string IncompleteAnalysisSummary => BuildIncompleteAnalysisSummary(GetIncompleteAnalysisReasons());
    public bool IsProcessing => AnalysisStatus == AnalysisRunStatus.Processing;
    public bool IsCompleted => AnalysisStatus == AnalysisRunStatus.Completed;
    public bool IsFailed => AnalysisStatus == AnalysisRunStatus.Failed;
    public bool IsQueued => AnalysisStatus == AnalysisRunStatus.Queued;
    public bool HasConfidenceData => AnalysisData is not null;
    public double BpmConfidence => AnalysisData?.Mechanics.TonalProbability ?? 0;
    public double KeyConfidence => AnalysisData is null ? 0 : Math.Clamp((AnalysisData.Mechanics.TonalProbability * 0.85) + 0.1, 0, 1);
    public string BpmConfidenceTier => ToConfidenceTier(BpmConfidence);
    public string KeyConfidenceTier => ToConfidenceTier(KeyConfidence);
    public string ConfidenceSemanticsSummary => $"Tonal {BpmConfidenceTier} • Key {KeyConfidenceTier}";
    public int CueCount => HasAnalysis ? _cueCount : 0;

    // ── Measured values (0–1) straight from the library, null when the analyser never stored one.
    // The view shows these as bars; it never derives moods or confidences from defaults.
    public double? MeasuredBpmConfidence { get; }
    public double? MeasuredKeyConfidence { get; }
    public double? MeasuredEnergy { get; }
    public double? MeasuredValence { get; }
    public bool HasMeasuredBpmConfidence => MeasuredBpmConfidence.HasValue;
    public bool HasMeasuredKeyConfidence => MeasuredKeyConfidence.HasValue;
    public bool HasMeasuredEnergy => MeasuredEnergy.HasValue;
    public bool HasMeasuredValence => MeasuredValence.HasValue;
    public bool HasMeasuredProfile => HasMeasuredEnergy || HasMeasuredValence;
    public bool HasGenres => AnalysisData?.Genres is { Count: > 0 };

    /// <summary>
    /// Brings a stored score onto 0–1: values already in range pass through, 0–10 and 0–100
    /// scales are divided down, and a −1…1 valence is shifted up.
    /// </summary>
    private static double? Normalize01(double? value)
    {
        if (value is not { } v || double.IsNaN(v)) return null;
        if (v < 0) return Math.Clamp((v + 1.0) / 2.0, 0, 1);
        if (v <= 1.0) return v;
        if (v <= 10.0) return v / 10.0;
        return Math.Clamp(v / 100.0, 0, 1);
    }
    public byte[] WaveformLow => _waveformLow;
    public byte[] WaveformMid => _waveformMid;
    public byte[] WaveformHigh => _waveformHigh;
    public WaveformAnalysisData WaveformData => BuildWaveformAnalysisData();
    public bool HasWaveform => _waveformLow.Length > 0 || _waveformMid.Length > 0 || _waveformHigh.Length > 0;
    public IReadOnlyList<double> EnergyCurvePoints => _energyCurvePoints;
    public bool HasEnergyCurve => _energyCurvePoints.Count >= 2;
    public IReadOnlyList<double> SegmentedEnergyPoints => _segmentedEnergyPoints;
    public bool HasSegmentedEnergy => _segmentedEnergyPoints.Count >= 2;
    public IReadOnlyList<PhraseTimelineBlock> PhraseTimelineBlocks => _phraseTimelineBlocks;
    public bool HasPhraseTimeline => _phraseTimelineBlocks.Count > 0;
    public string DominantMoodLabel => ResolveDominantMoodLabel(AnalysisData?.Moods);
    public string VocalProfileSummary => BuildVocalProfileSummary(_vocalTypeLabel, _vocalDensity);
    public string HarmonicProfileSummary => BuildHarmonicProfileSummary(_camelotKey, AnalysisData?.Mechanics.KeyScale, _chordProgression);
    public string QualityProfileSummary => BuildQualityProfileSummary(_qualityConfidence, _loudnessLufs, _bpmStability);
    public string StructuralSummary => BuildStructuralSummary(_dropTimeSeconds, _dropConfidence);
    public string PrimaryGenre => AnalysisData?.Genres.OrderByDescending(g => g.Confidence).FirstOrDefault()?.Label ?? "Unclassified";
    public string GenreSummary => BuildGenreSummary(AnalysisData?.Genres);
    public string PlaylistActionLabel => IsInPlaylist ? "− Remove from Mix" : "＋ Add to Mix";
    public string StatusLabel => AnalysisStatus switch
    {
        AnalysisRunStatus.Processing => "Processing",
        AnalysisRunStatus.Completed => "Completed",
        AnalysisRunStatus.Failed => "Failed",
        _ => "Queued"
    };

    public AnalysisTrackItem(
        string trackId,
        string artist,
        string title,
        string? album = null,
        double? bpm = null,
        string? musicalKey = null,
        string? filePath = null,
        AnalysisData? analysisData = null,
        int cueCount = 0,
        byte[]? lowBand = null,
        byte[]? midBand = null,
        byte[]? highBand = null,
        DateTime? lastAnalyzedAt = null,
        string? modelVersion = null,
        IReadOnlyList<double>? energyCurvePoints = null,
        IReadOnlyList<double>? segmentedEnergyPoints = null,
        double? dropTimeSeconds = null,
        double? dropConfidence = null,
        double? qualityConfidence = null,
        double? bpmStability = null,
        double? vocalDensity = null,
        string? vocalTypeLabel = null,
        double? loudnessLufs = null,
        string? camelotKey = null,
        string? chordProgression = null,
        double? durationSeconds = null,
        double? measuredBpmConfidence = null,
        double? measuredKeyConfidence = null,
        double? measuredEnergy = null,
        double? measuredValence = null)
    {
        MeasuredBpmConfidence = Normalize01(measuredBpmConfidence);
        MeasuredKeyConfidence = Normalize01(measuredKeyConfidence);
        MeasuredEnergy = Normalize01(measuredEnergy);
        MeasuredValence = Normalize01(measuredValence);
        TrackId = trackId;
        Artist = artist;
        Title = title;
        Album = album;
        Bpm = bpm;
        MusicalKey = musicalKey;
        FilePath = filePath;
        _analysisData = analysisData;
        // Only real data: no estimated cue counts, synthetic waveforms or invented timestamps.
        // A track with no stored cues shows 0 and an empty waveform well, not placeholder numbers.
        _cueCount = Math.Max(0, cueCount);
        _waveformLow = lowBand ?? Array.Empty<byte>();
        _waveformMid = midBand ?? Array.Empty<byte>();
        _waveformHigh = highBand ?? Array.Empty<byte>();
        _stemsReady = analysisData?.Stems?.AreGenerated ?? false;
        _lastAnalyzedAt = lastAnalyzedAt;
        _modelVersion = modelVersion;
        _energyCurvePoints = energyCurvePoints ?? Array.Empty<double>();
        _segmentedEnergyPoints = segmentedEnergyPoints ?? Array.Empty<double>();
        _dropTimeSeconds = dropTimeSeconds;
        _dropConfidence = dropConfidence;
        _qualityConfidence = qualityConfidence;
        _bpmStability = bpmStability;
        _vocalDensity = vocalDensity;
        _vocalTypeLabel = string.IsNullOrWhiteSpace(vocalTypeLabel) ? "Unknown" : vocalTypeLabel;
        _loudnessLufs = loudnessLufs;
        _camelotKey = camelotKey;
        _chordProgression = chordProgression;
        _phraseTimelineBlocks = BuildPhraseTimeline(_segmentedEnergyPoints, _dropTimeSeconds);
        _durationSeconds = durationSeconds ?? 0;

        if (analysisData is not null)
            _analysisStatus = AnalysisRunStatus.Completed;
    }

    public void UpdateFrom(AnalysisTrackItem source)
    {
        ArgumentNullException.ThrowIfNull(source);

        TrackId = source.TrackId;
        Artist = source.Artist;
        Title = source.Title;
        Album = source.Album;
        Bpm = source.Bpm;
        MusicalKey = source.MusicalKey;
        FilePath = source.FilePath;

        _analysisStatus = source._analysisStatus;
        _analysisData = source._analysisData;
        _progressPercent = source._progressPercent;
        _currentStep = source._currentStep;
        _isInQueue = source._isInQueue;

        _waveformLow = source._waveformLow.ToArray();
        _waveformMid = source._waveformMid.ToArray();
        _waveformHigh = source._waveformHigh.ToArray();
        _cueCount = source._cueCount;
        _energyCurvePoints = source._energyCurvePoints.ToArray();
        _segmentedEnergyPoints = source._segmentedEnergyPoints.ToArray();
        _dropTimeSeconds = source._dropTimeSeconds;
        _dropConfidence = source._dropConfidence;
        _qualityConfidence = source._qualityConfidence;
        _bpmStability = source._bpmStability;
        _vocalDensity = source._vocalDensity;
        _loudnessLufs = source._loudnessLufs;
        _camelotKey = source._camelotKey;
        _chordProgression = source._chordProgression;
        _vocalTypeLabel = source._vocalTypeLabel;
        _phraseTimelineBlocks = source._phraseTimelineBlocks.ToArray();
        _durationSeconds = source._durationSeconds;

        _stemsReady = source._stemsReady;
        _isInPlaylist = source._isInPlaylist;
        _analysisError = source._analysisError;
        _stemError = source._stemError;
        _lastAnalyzedAt = source._lastAnalyzedAt;
        _modelVersion = source._modelVersion;

        this.RaisePropertyChanged(string.Empty);
    }

    private IReadOnlyList<string> GetIncompleteAnalysisReasons()
    {
        var reasons = new List<string>();

        if (AnalysisStatus == AnalysisRunStatus.Failed)
        {
            reasons.Add(IsLikelyCorruptFile
                ? "file may be corrupt — try re-downloading rather than re-analyzing"
                : HasAnalysisError ? $"analysis failed: {Truncate(AnalysisError!, 70)}" : "analysis failed");
        }

        if (AnalysisData is null)
        {
            reasons.Add("analysis missing");
        }

        var effectiveBpm = AnalysisData?.Mechanics.Bpm ?? Bpm ?? 0;
        if (effectiveBpm <= 0)
        {
            reasons.Add("BPM missing");
        }

        var effectiveKey = string.IsNullOrWhiteSpace(MusicalKey)
            ? AnalysisData?.Mechanics.KeyScale
            : MusicalKey;
        if (string.IsNullOrWhiteSpace(effectiveKey))
        {
            reasons.Add("key missing");
        }

        if (CueCount <= 0)
        {
            reasons.Add("cues missing");
        }

        if (!HasWaveform)
        {
            reasons.Add("waveform missing");
        }

        return reasons;
    }

    private static string BuildIncompleteAnalysisSummary(IReadOnlyList<string> reasons)
    {
        return reasons.Count == 0 ? "Complete" : string.Join(" • ", reasons);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";

    private WaveformAnalysisData BuildWaveformAnalysisData()
    {
        var peakData = BuildPeakData(_waveformLow, _waveformMid, _waveformHigh);
        var rmsData = BuildRmsData(_waveformLow, _waveformMid, _waveformHigh);

        return new WaveformAnalysisData
        {
            PeakData = peakData,
            RmsData = rmsData,
            LowData = _waveformLow,
            MidData = _waveformMid,
            HighData = _waveformHigh,
            DurationSeconds = Math.Max(1, _durationSeconds),
            PointsPerSecond = peakData.Length > 0 && _durationSeconds > 0
                ? Math.Max(1, (int)Math.Round(peakData.Length / _durationSeconds))
                : 100
        };
    }

    private static byte[] BuildPeakData(byte[] lowData, byte[] midData, byte[] highData)
    {
        var length = Math.Min(lowData.Length, Math.Min(midData.Length, highData.Length));
        if (length <= 0)
            return Array.Empty<byte>();

        var peakData = new byte[length];
        for (var i = 0; i < length; i++)
        {
            peakData[i] = Math.Max(lowData[i], Math.Max(midData[i], highData[i]));
        }

        return peakData;
    }

    private static byte[] BuildRmsData(byte[] lowData, byte[] midData, byte[] highData)
    {
        var length = Math.Min(lowData.Length, Math.Min(midData.Length, highData.Length));
        if (length <= 0)
            return Array.Empty<byte>();

        var rmsData = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var low = lowData[i];
            var mid = midData[i];
            var high = highData[i];
            var rms = Math.Sqrt((low * low + mid * mid + high * high) / 3.0);
            rmsData[i] = (byte)Math.Clamp(rms, 0.0, 255.0);
        }

        return rmsData;
    }

    private static string ToConfidenceTier(double value)
    {
        if (value >= 0.8) return "High";
        if (value >= 0.6) return "Medium";
        return "Low";
    }

    private static string ResolveDominantMoodLabel(MoodData? moods)
    {
        if (moods == null) return "Mood unknown";

        var ranked = new[]
        {
            ("Happy", moods.Happy),
            ("Sad", moods.Sad),
            ("Aggressive", moods.Aggressive),
            ("Relaxed", moods.Relaxed),
            ("Party", moods.Party),
        };

        var top = ranked.OrderByDescending(item => item.Item2).First();
        return $"{top.Item1} {top.Item2:F0}%";
    }

    private static string BuildVocalProfileSummary(string vocalTypeLabel, double? vocalDensity)
    {
        if (!vocalDensity.HasValue)
        {
            return $"Vocal profile: {vocalTypeLabel}";
        }

        return $"Vocal profile: {vocalTypeLabel} ({vocalDensity.Value * 100:F0}% density)";
    }

    private static string BuildHarmonicProfileSummary(string? camelotKey, string? keyScale, string? chordProgression)
    {
        var key = string.IsNullOrWhiteSpace(keyScale) ? "Unknown key" : keyScale;
        var camelot = string.IsNullOrWhiteSpace(camelotKey) ? "—" : camelotKey;
        var chords = string.IsNullOrWhiteSpace(chordProgression) ? "" : $" • {chordProgression}";
        return $"Harmonic: {key} ({camelot}){chords}";
    }

    private static string BuildQualityProfileSummary(double? qualityConfidence, double? loudnessLufs, double? bpmStability)
    {
        var confidenceText = qualityConfidence.HasValue ? $"Q {qualityConfidence.Value * 100:F0}%" : "Q —";
        var loudnessText = loudnessLufs.HasValue ? NumFormat.Ui($"LUFS {loudnessLufs.Value:F1}") : "LUFS —";
        var stabilityText = bpmStability.HasValue ? $"Stability {bpmStability.Value * 100:F0}%" : "Stability —";
        return $"{confidenceText} • {loudnessText} • {stabilityText}";
    }

    private static string BuildStructuralSummary(double? dropTimeSeconds, double? dropConfidence)
    {
        if (!dropTimeSeconds.HasValue)
        {
            return "Structure: drop marker unavailable";
        }

        var ts = TimeSpan.FromSeconds(Math.Max(0, dropTimeSeconds.Value));
        var confidence = dropConfidence.HasValue ? $" ({dropConfidence.Value * 100:F0}% conf)" : string.Empty;
        return $"Structure: drop near {ts.Minutes}:{ts.Seconds:00}{confidence}";
    }

    private static string BuildGenreSummary(IReadOnlyCollection<GenrePrediction>? genres)
    {
        if (genres == null || genres.Count == 0)
        {
            return "Genre: Unclassified";
        }

        var top = genres.OrderByDescending(g => g.Confidence).Take(3).ToList();
        return "Genre: " + string.Join(" / ", top.Select(g => $"{g.Label} {g.Confidence * 100:F0}%"));
    }

    private static IReadOnlyList<PhraseTimelineBlock> BuildPhraseTimeline(
        IReadOnlyList<double> segmentedEnergy,
        double? dropTimeSeconds)
    {
        if (segmentedEnergy.Count < 2)
        {
            return Array.Empty<PhraseTimelineBlock>();
        }

        var blocks = new List<PhraseTimelineBlock>(segmentedEnergy.Count);
        var count = segmentedEnergy.Count;
        var dropSegmentIndex = dropTimeSeconds.HasValue
            ? segmentedEnergy
                .Select((value, index) => (value, index))
                .OrderByDescending(pair => pair.value)
                .First().index
            : -1;

        for (var i = 0; i < count; i++)
        {
            var energy = Math.Clamp(segmentedEnergy[i], 0.0, 1.0);
            var start = i / (double)count;
            var end = (i + 1) / (double)count;

            string label;
            if (i == 0)
            {
                label = "Intro";
            }
            else if (i == count - 1)
            {
                label = "Outro";
            }
            else if (energy >= 0.78)
            {
                label = "Drop";
            }
            else if (energy <= 0.36)
            {
                label = "Break";
            }
            else if (energy >= 0.62)
            {
                label = "Build";
            }
            else
            {
                label = "Verse";
            }

            if (i == dropSegmentIndex)
            {
                label = "Drop";
            }

            blocks.Add(new PhraseTimelineBlock(
                label,
                start,
                end,
                (end - start) * 100.0,
                Math.Max(26.0, (end - start) * 280.0),
                energy,
                $"{label} • energy {(energy * 100):F0}%"));
        }

        return blocks;
    }

}

public sealed record PhraseTimelineBlock(
    string Label,
    double StartRatio,
    double EndRatio,
    double WidthPercent,
    double WidthPx,
    double Energy,
    string Tooltip)
{
    /// <summary>The drop segment is the one block the view highlights; the rest stay neutral.</summary>
    public bool IsDrop => Label == "Drop";
}

/// <summary>
/// Configurable constraints for the automix playlist generation flow (issue #43).
/// </summary>
public class AutomixConstraints : ReactiveObject
{
    private double _minBpm  = 100;
    private double _maxBpm  = 160;
    private int    _maxTracks = 20;
    private bool   _matchKey = true;
    private int    _maxEnergyJump = 3;
    private string _energyCurve = "Wave";
    private double _harmonicWeight = 3.0;
    private double _tempoWeight    = 1.0;
    private double _energyWeight   = 0.5;

    /// <summary>Minimum BPM allowed in the generated playlist.</summary>
    public double MinBpm
    {
        get => _minBpm;
        set => this.RaiseAndSetIfChanged(ref _minBpm, value);
    }

    /// <summary>Maximum BPM allowed in the generated playlist.</summary>
    public double MaxBpm
    {
        get => _maxBpm;
        set => this.RaiseAndSetIfChanged(ref _maxBpm, value);
    }

    /// <summary>Maximum number of tracks to include in the generated playlist.</summary>
    public int MaxTracks
    {
        get => _maxTracks;
        set => this.RaiseAndSetIfChanged(ref _maxTracks, value);
    }

    /// <summary>When true, only include harmonically compatible key transitions.</summary>
    public bool MatchKey
    {
        get => _matchKey;
        set => this.RaiseAndSetIfChanged(ref _matchKey, value);
    }

    /// <summary>
    /// Maximum energy jump (1–10 scale) allowed between consecutive tracks.
    /// Pairs exceeding this value receive a large penalty in the optimizer.
    /// </summary>
    public int MaxEnergyJump
    {
        get => _maxEnergyJump;
        set => this.RaiseAndSetIfChanged(ref _maxEnergyJump, Math.Clamp(value, 1, 9));
    }

    /// <summary>"None" | "Rising" | "Wave" | "Peak" — post-pass energy shaping.</summary>
    public string EnergyCurve
    {
        get => _energyCurve;
        set => this.RaiseAndSetIfChanged(ref _energyCurve, value);
    }

    /// <summary>Multiplier for Camelot key distance in the optimizer edge cost.</summary>
    public double HarmonicWeight
    {
        get => _harmonicWeight;
        set => this.RaiseAndSetIfChanged(ref _harmonicWeight, Math.Clamp(value, 0.1, 10.0));
    }

    /// <summary>Multiplier for BPM difference in the optimizer edge cost.</summary>
    public double TempoWeight
    {
        get => _tempoWeight;
        set => this.RaiseAndSetIfChanged(ref _tempoWeight, Math.Clamp(value, 0.1, 10.0));
    }

    /// <summary>Multiplier for energy score difference in the optimizer edge cost.</summary>
    public double EnergyWeight
    {
        get => _energyWeight;
        set => this.RaiseAndSetIfChanged(ref _energyWeight, Math.Clamp(value, 0.0, 10.0));
    }
}

public sealed record PerformanceProbeRun(
    DateTime TimestampUtc,
    int QueryCount,
    double AverageMs,
    double P95Ms,
    double MaxMs,
    double? DeltaP95Ms)
{
    public string TrendArrow => DeltaP95Ms switch
    {
        null => "•",
        > 0.35 => "↑",
        < -0.35 => "↓",
        _ => "→"
    };

    public string TrendText => DeltaP95Ms.HasValue ? NumFormat.Ui($"{DeltaP95Ms.Value:+0.0;-0.0;0.0} ms") : "baseline";

    public string SeverityLabel => P95Ms switch
    {
        <= 6.0 => "good",
        <= 14.0 => "warn",
        _ => "slow"
    };

    public bool IsGood => P95Ms <= 6.0;
    public bool IsWarn => P95Ms is > 6.0 and <= 14.0;
    public bool IsSlow => P95Ms > 14.0;

    public string DisplayLabel => NumFormat.Ui($"{TimestampUtc:HH:mm:ss} · {SeverityLabel} · {TrendArrow} {TrendText} · {QueryCount}q · avg {AverageMs:F1} ms · p95 {P95Ms:F1} ms · max {MaxMs:F1} ms");
}
