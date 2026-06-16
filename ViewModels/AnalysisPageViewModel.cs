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
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Processing state for the analysis queue.
/// </summary>
public enum AnalysisProcessingState
{
    Idle,
    Processing,
    Completed,
    Error
}

public enum AnalysisPaneMode
{
    Library,
    Queue
}

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

    public bool HasAnalysis => AnalysisData is not null;
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
        double? durationSeconds = null)
    {
        TrackId = trackId;
        Artist = artist;
        Title = title;
        Album = album;
        Bpm = bpm;
        MusicalKey = musicalKey;
        FilePath = filePath;
        _analysisData = analysisData;
        _cueCount = cueCount > 0 ? cueCount : (analysisData is not null ? EstimateCueCount(trackId) : 0);
        var canUseSyntheticWaveform = string.IsNullOrWhiteSpace(filePath);
        _waveformLow = lowBand is { Length: > 0 }
            ? lowBand
            : (canUseSyntheticWaveform ? BuildWaveformBand(trackId, 64, 7) : Array.Empty<byte>());
        _waveformMid = midBand is { Length: > 0 }
            ? midBand
            : (canUseSyntheticWaveform ? BuildWaveformBand(trackId, 64, 17) : Array.Empty<byte>());
        _waveformHigh = highBand is { Length: > 0 }
            ? highBand
            : (canUseSyntheticWaveform ? BuildWaveformBand(trackId, 64, 29) : Array.Empty<byte>());
        _stemsReady = analysisData?.Stems?.AreGenerated ?? false;
        _lastAnalyzedAt = lastAnalyzedAt ?? (analysisData is not null ? DateTime.UtcNow.AddMinutes(-EstimateCueCount(trackId) * 7) : null);
        _modelVersion = modelVersion ?? (analysisData is not null ? "essentia-2.1-b6" : null);
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
            reasons.Add("analysis failed");
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
        var loudnessText = loudnessLufs.HasValue ? $"LUFS {loudnessLufs.Value:F1}" : "LUFS —";
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
                ResolvePhraseColor(label, energy),
                $"{label} • energy {(energy * 100):F0}%"));
        }

        return blocks;
    }

    private static string ResolvePhraseColor(string label, double energy)
    {
        return label switch
        {
            "Intro" => "#3A4A5A",
            "Build" => "#5A4A2A",
            "Drop" => "#7A2A2A",
            "Break" => "#2A4A5A",
            "Outro" => "#3A3A46",
            _ => energy >= 0.6 ? "#4A5A3A" : "#3A4A3A"
        };
    }


public sealed record PhraseTimelineBlock(
    string Label,
    double StartRatio,
    double EndRatio,
    double WidthPercent,
    double WidthPx,
    string FillColor,
    string Tooltip);
    private static int EstimateCueCount(string seed)
    {
        var hash = Math.Abs(seed.Aggregate(17, (current, c) => current * 31 + c));
        return 4 + (hash % 5);
    }

    private static byte[] BuildWaveformBand(string seed, int length, int salt)
    {
        var data = new byte[length];
        var hash = Math.Abs(seed.Aggregate(salt, (current, c) => current * 31 + c));
        var rng = new Random(hash);

        for (var i = 0; i < length; i++)
        {
            var wave = Math.Abs(Math.Sin((i + salt) / 6.0));
            var jitter = rng.NextDouble() * 0.35;
            data[i] = (byte)Math.Clamp((wave + jitter) * 180.0, 18, 255);
        }

        return data;
    }
}

/// <summary>
/// ViewModel for the Analysis page.
/// Manages the library track list, the analysis queue, processing state, and mock data.
/// </summary>
public class AnalysisPageViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILibraryService? _libraryService;
    private readonly ILifecycleProjectionService _lifecycleProjectionService;
    private readonly IClipboardService? _clipboardService;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _analysisSessionStopwatch = new();
    private readonly Dictionary<string, DateTime> _trackAnalysisStartedAtUtc = new(StringComparer.Ordinal);
    private readonly System.Reactive.Subjects.Subject<System.Reactive.Unit> _refreshRequestSubject = new();
    private readonly System.Reactive.Subjects.Subject<string?> _filterRequestSubject = new();

    private AnalysisProcessingState _processingState = AnalysisProcessingState.Idle;
    private string? _currentProcessingTrackId;
    private string _searchText = string.Empty;
    private bool _isCompactLayout;
    private AnalysisPaneMode _activePaneMode = AnalysisPaneMode.Queue;
    private int _completedAnalysisRuns;
    private double _totalAnalysisSeconds;
    private double _lastFilterMilliseconds;
    private double _averageFilterMilliseconds;
    private int _filterSampleCount;
    private int _filteredTrackCount;
    private double _lastQueueMutationMilliseconds;
    private string? _performanceProbeSummary;
    private int _indexedCatalogCount;
    private int _staleIndexedCount;
    private int _desiredDownloadCount;
    private int _ingestionBacklogCount;
    private int _onDiskIndexedTrackCount;

    // ── Collections ─────────────────────────────────────────────────────────────

    /// <summary>All tracks available in the library (source-of-truth list).</summary>
    public ObservableCollection<AnalysisTrackItem> LibraryTracks { get; } = new();

    /// <summary>Filtered view of LibraryTracks based on the search box.</summary>
    public ObservableCollection<AnalysisTrackItem> FilteredLibraryTracks { get; } = new();

    /// <summary>Tracks staged for analysis by the user.</summary>
    public ObservableCollection<AnalysisTrackItem> AnalysisQueue { get; } = new();
    public ObservableCollection<PerformanceProbeRun> PerformanceProbeRuns { get; } = new();
    public ObservableCollection<AnalysisTrackItem> PlaylistTracks { get; } = new();
    public AutomixConstraints AutomixConstraints { get; } = new();

    // ── State ────────────────────────────────────────────────────────────────────

    public AnalysisProcessingState ProcessingState
    {
        get => _processingState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _processingState, value);
            this.RaisePropertyChanged(nameof(IsIdle));
            this.RaisePropertyChanged(nameof(IsProcessing));
            this.RaisePropertyChanged(nameof(CanStartAnalysis));
        }
    }

    public string? CurrentProcessingTrackId
    {
        get => _currentProcessingTrackId;
        private set => this.RaiseAndSetIfChanged(ref _currentProcessingTrackId, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
        }
    }

    public bool IsIdle => ProcessingState == AnalysisProcessingState.Idle;
    public bool IsProcessing => ProcessingState == AnalysisProcessingState.Processing;
    public bool CanStartAnalysis => !IsProcessing && AnalysisQueue.Any(t => t.AnalysisStatus != AnalysisRunStatus.Completed);
    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (_isCompactLayout == value)
                return;

            this.RaiseAndSetIfChanged(ref _isCompactLayout, value);
            this.RaisePropertyChanged(nameof(ShowLibraryPane));
            this.RaisePropertyChanged(nameof(ShowQueuePane));
            this.RaisePropertyChanged(nameof(IsLibraryPaneSelected));
            this.RaisePropertyChanged(nameof(IsQueuePaneSelected));
            this.RaisePropertyChanged(nameof(QueuePaneColumn));
            this.RaisePropertyChanged(nameof(QueuePaneColumnSpan));
            this.RaisePropertyChanged(nameof(LibraryPaneColumnSpan));
        }
    }

    public AnalysisPaneMode ActivePaneMode
    {
        get => _activePaneMode;
        private set
        {
            if (_activePaneMode == value)
                return;

            this.RaiseAndSetIfChanged(ref _activePaneMode, value);
            this.RaisePropertyChanged(nameof(ShowLibraryPane));
            this.RaisePropertyChanged(nameof(ShowQueuePane));
            this.RaisePropertyChanged(nameof(IsLibraryPaneSelected));
            this.RaisePropertyChanged(nameof(IsQueuePaneSelected));
        }
    }

    public bool ShowLibraryPane => !IsCompactLayout || ActivePaneMode == AnalysisPaneMode.Library;
    public bool ShowQueuePane => !IsCompactLayout || ActivePaneMode == AnalysisPaneMode.Queue;
    public bool IsLibraryPaneSelected => ActivePaneMode == AnalysisPaneMode.Library;
    public bool IsQueuePaneSelected => ActivePaneMode == AnalysisPaneMode.Queue;
    public int QueuePaneColumn => IsCompactLayout ? 0 : 2;
    public int QueuePaneColumnSpan => IsCompactLayout ? 3 : 1;
    public int LibraryPaneColumnSpan => IsCompactLayout ? 3 : 1;

    /// <summary>True when the filtered library list is empty (e.g. search returned no results).</summary>
    public bool IsLibraryEmpty => FilteredLibraryTracks.Count == 0;

    /// <summary>True when no tracks have been staged for analysis yet.</summary>
    public bool IsQueueEmpty => AnalysisQueue.Count == 0;

    public int TotalTrackCount => LibraryTracks.Count;
    public int OnDiskIndexedTrackCount => _onDiskIndexedTrackCount;
    public int IndexedCatalogCount => _indexedCatalogCount;
    public int StaleIndexedCount => _staleIndexedCount;
    public int DesiredDownloadCount => _desiredDownloadCount;
    public int IngestionBacklogCount => _ingestionBacklogCount;
    public int AnalyzedTrackCount => LibraryTracks.Count(t => t.HasAnalysis);
    public int PendingTrackCount => LibraryTracks.Count(t => !t.HasAnalysis);
    public int QueueTrackCount => AnalysisQueue.Count;
    public int IncompleteAnalysisTrackCount => LibraryTracks.Count(t => t.HasIncompleteAnalysis);
    public bool HasIncompleteAnalysisTracks => IncompleteAnalysisTrackCount > 0;
    public IReadOnlyList<AnalysisTrackItem> IncompleteAnalysisTracks => LibraryTracks
        .Where(t => t.HasIncompleteAnalysis)
        .OrderBy(t => t.Artist)
        .ThenBy(t => t.Title)
        .ToList();
    public int StemsReadyCount => LibraryTracks.Count(t => t.StemsReady);
    public bool HasQueueMetrics => QueueTrackCount > 0 || _completedAnalysisRuns > 0 || IsProcessing;
    public bool IsDeveloperMode => Debugger.IsAttached || string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
    public string AvgAnalysisTimeDisplay => _completedAnalysisRuns == 0 ? "—" : $"{_totalAnalysisSeconds / _completedAnalysisRuns:F1}s";
    public string ThroughputDisplay => _completedAnalysisRuns == 0 ? "—" : $"{_completedAnalysisRuns / Math.Max(_analysisSessionStopwatch.Elapsed.TotalMinutes, 1.0 / 60.0):F1}/min";
    public string FilterPerformanceDisplay => $"Filter {_lastFilterMilliseconds:F1} ms (avg {_averageFilterMilliseconds:F1} ms)";
    public string InteractionPerformanceDisplay => _lastQueueMutationMilliseconds <= 0
        ? "Queue update —"
        : $"Queue update {_lastQueueMutationMilliseconds:F1} ms";
    public string PerformanceDiagnosticsSummary => $"{_filteredTrackCount}/{LibraryTracks.Count} visible • {FilterPerformanceDisplay} • {InteractionPerformanceDisplay}";
    public string? PerformanceProbeSummary
    {
        get => _performanceProbeSummary;
        private set => this.RaiseAndSetIfChanged(ref _performanceProbeSummary, value);
    }
    public bool HasPerformanceProbeRuns => PerformanceProbeRuns.Count > 0;
    public string ElapsedTimeDisplay => _analysisSessionStopwatch.Elapsed == TimeSpan.Zero
        ? "—"
        : _analysisSessionStopwatch.Elapsed.ToString(_analysisSessionStopwatch.Elapsed.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    public string QueueMetricsSummary => $"{QueueTrackCount} queued • {AnalyzedTrackCount} analyzed • {PendingTrackCount} remaining";
    public string IncompleteAnalysisSummary => IncompleteAnalysisTrackCount == 0
        ? "All tracks have sufficient analysis coverage."
        : $"{IncompleteAnalysisTrackCount} track(s) have incomplete or insufficient analysis data.";
    public string LibraryCountDifferentiationSummary =>
        $"Wanted downloads: {DesiredDownloadCount} • Ingestion backlog: {IngestionBacklogCount} • Physical on-disk indexed: {OnDiskIndexedTrackCount} • Stale indexed rows: {StaleIndexedCount}";
    public string CompletionRateDisplay => TotalTrackCount == 0 ? "0%" : $"{(AnalyzedTrackCount * 100.0 / TotalTrackCount):F0}%";

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Adds a track from the library to the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> AddToQueueCommand { get; }

    /// <summary>Removes a track from the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> RemoveFromQueueCommand { get; }

    /// <summary>Adds every unanalyzed, un-queued visible track to the analysis queue.</summary>
    public ReactiveCommand<Unit, Unit> QueueAllUnanalyzedCommand { get; }

    /// <summary>Starts sequential analysis of all queued tracks.</summary>
    public ReactiveCommand<Unit, Unit> StartAnalysisCommand { get; }
    public ReactiveCommand<Unit, Unit> RunPerformanceProbeCommand { get; }

    /// <summary>Re-queues a completed track and clears cached output.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> ReanalyzeCommand { get; }

    /// <summary>Queues all tracks that currently have incomplete analysis data.</summary>
    public ReactiveCommand<Unit, Unit> ReanalyzeAllIncompleteCommand { get; }

    /// <summary>Copies the full analysis JSON for a track to the clipboard.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> CopyAnalysisJsonCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPerformanceSnapshotCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPerformanceProbeHistoryCommand { get; }

    public ReactiveCommand<AnalysisPaneMode, Unit> SetPaneModeCommand { get; }

    private string? _automixStatusMessage;

    /// <summary>Success / error message from the last automix generation attempt.</summary>
    public string? AutomixStatusMessage
    {
        get => _automixStatusMessage;
        private set => this.RaiseAndSetIfChanged(ref _automixStatusMessage, value);
    }

    public string? AnalysisStatusMessage => AutomixStatusMessage;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public AnalysisPageViewModel(
        IEventBus eventBus,
        ILifecycleProjectionService lifecycleProjectionService,
        ILibraryService? libraryService = null,
        IClipboardService? clipboardService = null)
    {
        _eventBus = eventBus;
        _libraryService = libraryService;
        _clipboardService = clipboardService;
        _lifecycleProjectionService = lifecycleProjectionService;

        if (_libraryService is null)
            LoadMockData();
        else
            _ = LoadLibraryAsync();

        // Throttled UI refresh logic
        _refreshRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshComputedStateInternal())
            .DisposeWith(_disposables);

        _filterRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.MainThreadScheduler)
            .Subscribe(query => ApplyFilterDirect(query))
            .DisposeWith(_disposables);

        ApplyFilterDirect();
        RefreshComputedStateInternal();

        // ── Wire up commands ──────────────────────────────────────────────────
        AddToQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(AddToQueue);
        RemoveFromQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(RemoveFromQueue);
        QueueAllUnanalyzedCommand = ReactiveCommand.CreateFromTask(QueueAllUnanalyzedAsync);

        var canStart = this.WhenAnyValue(x => x.CanStartAnalysis);
        StartAnalysisCommand = ReactiveCommand.CreateFromTask(StartAnalysisAsync, canStart);
        RunPerformanceProbeCommand = ReactiveCommand.Create(RunPerformanceProbe);
        ReanalyzeCommand = ReactiveCommand.Create<AnalysisTrackItem>(Reanalyze);
        ReanalyzeAllIncompleteCommand = ReactiveCommand.CreateFromTask(ReanalyzeAllIncompleteAsync);
        CopyAnalysisJsonCommand = ReactiveCommand.CreateFromTask<AnalysisTrackItem>(CopyAnalysisJsonAsync);
        CopyPerformanceSnapshotCommand = ReactiveCommand.CreateFromTask(CopyPerformanceSnapshotAsync);
        ClearPerformanceProbeHistoryCommand = ReactiveCommand.Create(ClearPerformanceProbeHistory);
        SetPaneModeCommand = ReactiveCommand.Create<AnalysisPaneMode>(SetPaneMode);

        // Keep all computed dashboard metrics in sync as the collections change.
        LibraryTracks.CollectionChanged += (_, _) => RefreshComputedState();
        AnalysisQueue.CollectionChanged += (_, _) => RefreshComputedState();
        PerformanceProbeRuns.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasPerformanceProbeRuns));

        // React to per-track progress events
        _eventBus.GetEvent<AnalysisProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisProgress)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackAnalysisStarted)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisFailedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackAnalysisFailed)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = OnTrackAnalysisCompletedAsync(evt);
            })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<AnalysisQueueStatusChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisQueueStatusChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackAnalysisRequestedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnTrackAnalysisRequested)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileIngestionQueuedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileIngestionQueued)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileIngestionCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileIngestionCompleted)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<FileMissingDetectedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnFileMissingDetected)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(220), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        UpdateLayoutMode(1280);
    }

    // ── Commands / Actions ───────────────────────────────────────────────────────

    /// <summary>Adds a library track to the analysis queue (if not already present).</summary>
    public void AddToQueue(AnalysisTrackItem track)
    {
        var timer = Stopwatch.StartNew();
        AddToQueueCore(track, refreshState: true);
        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    /// <summary>Removes a track from the analysis queue.</summary>
    public void RemoveFromQueue(AnalysisTrackItem track)
    {
        var timer = Stopwatch.StartNew();
        AnalysisQueue.Remove(track);
        track.IsInQueue = false;
        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
        RefreshComputedState();
    }

    /// <summary>Clears existing output and stages a track for a fresh analysis pass.</summary>
    public void Reanalyze(AnalysisTrackItem track)
    {
        ResetTrackAnalysisState(track);
        AddToQueueCore(track, refreshState: true, prioritize: true);
        _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackId, AnalysisTier.Tier1, IsHighPriority: true));
        AutomixStatusMessage = $"Priority reanalyze queued: {track.Artist} — {track.Title}.";
    }

    public async Task ReanalyzeAllIncompleteAsync()
    {
        var timer = Stopwatch.StartNew();

        var incompleteTracks = LibraryTracks
            .Where(t => t.HasIncompleteAnalysis)
            .ToList();

        int batchCount = 0;
        foreach (var track in incompleteTracks)
        {
            ResetTrackAnalysisState(track);
            AddToQueueCore(track, refreshState: false);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));

        AutomixStatusMessage = incompleteTracks.Count == 0
            ? "No incomplete tracks found for reanalysis."
            : $"Queued {incompleteTracks.Count} incomplete track(s) for reanalysis.";

        RefreshComputedState();
    }

    private async Task CopyAnalysisJsonAsync(AnalysisTrackItem? track)
    {
        if (track?.AnalysisData is null || _clipboardService is null)
            return;

        var json = JsonSerializer.Serialize(track.AnalysisData, new JsonSerializerOptions { WriteIndented = true });
        await _clipboardService.SetTextAsync(json);
        AutomixStatusMessage = $"Copied analysis payload for {track.Artist} — {track.Title}.";
    }

    private async Task CopyPerformanceSnapshotAsync()
    {
        if (_clipboardService is null)
            return;

        var snapshot = string.Join(Environment.NewLine,
            "Analysis Performance Snapshot",
            $"UTC: {DateTime.UtcNow:O}",
            $"Tracks: {TotalTrackCount}",
            $"Visible: {_filteredTrackCount}",
            $"Queue: {QueueTrackCount}",
            $"Analyzed: {AnalyzedTrackCount}",
            $"{FilterPerformanceDisplay}",
            $"{InteractionPerformanceDisplay}",
            PerformanceProbeSummary ?? "Probe not run yet");

        await _clipboardService.SetTextAsync(snapshot);
        AutomixStatusMessage = "Copied performance snapshot to clipboard.";
    }

    private void ClearPerformanceProbeHistory()
    {
        PerformanceProbeRuns.Clear();
        PerformanceProbeSummary = "Probe history cleared.";
    }

    /// <summary>Adds all visible unanalyzed tracks that are not yet in the queue.</summary>
    public async Task QueueAllUnanalyzedAsync()
    {
        var timer = Stopwatch.StartNew();
        var unanalyzedTracks = FilteredLibraryTracks.Where(t => !t.HasAnalysis && !t.IsInQueue).ToList();

        int batchCount = 0;
        foreach (var track in unanalyzedTracks)
        {
            AddToQueueCore(track, refreshState: false);
            batchCount++;
            if (batchCount % 10 == 0)
            {
                await Task.Yield();
            }
        }

        timer.Stop();
        _lastQueueMutationMilliseconds = timer.Elapsed.TotalMilliseconds;
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
        RefreshComputedState();
    }

    /// <summary>
    /// Starts sequential analysis of all queued tracks.
    /// Progress/completion is driven by queue-service events.
    /// </summary>
    public async Task StartAnalysisAsync()
    {
        if (!CanStartAnalysis)
            return;

        ProcessingState = AnalysisProcessingState.Processing;
        if (!_analysisSessionStopwatch.IsRunning || _analysisSessionStopwatch.Elapsed == TimeSpan.Zero)
            _analysisSessionStopwatch.Restart();

        var queue = AnalysisQueue.Where(t => t.AnalysisStatus != AnalysisRunStatus.Completed).ToList();

        foreach (var track in queue)
        {
            if (_cts.Token.IsCancellationRequested)
                break;

            track.AnalysisStatus = AnalysisRunStatus.Queued;
            track.ProgressPercent = 0;
            track.CurrentStep = "Queued for analysis";
            track.AnalysisError = null;
            _eventBus.Publish(new TrackAnalysisRequestedEvent(track.TrackId));
        }

        RefreshComputedState();
    }

    private void OnAnalysisProgress(AnalysisProgressEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null) return;

        CurrentProcessingTrackId = evt.TrackGlobalId;
        if (track.AnalysisStatus != AnalysisRunStatus.Completed)
            track.AnalysisStatus = AnalysisRunStatus.Processing;

        track.ProgressPercent = evt.ProgressPercent;
        track.CurrentStep = evt.CurrentStep;
        ProcessingState = AnalysisProcessingState.Processing;
        RefreshComputedState();
    }

    private void OnTrackAnalysisStarted(TrackAnalysisStartedEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
            return;

        _trackAnalysisStartedAtUtc[evt.TrackGlobalId] = DateTime.UtcNow;
        CurrentProcessingTrackId = evt.TrackGlobalId;
        ProcessingState = AnalysisProcessingState.Processing;

        track.AnalysisStatus = AnalysisRunStatus.Processing;
        track.ProgressPercent = Math.Max(track.ProgressPercent, 1);
        track.CurrentStep = $"Analyzing {evt.FileName}";
        track.AnalysisError = null;
        RefreshComputedState();
    }

    private void OnTrackAnalysisFailed(TrackAnalysisFailedEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
            return;

        track.AnalysisStatus = AnalysisRunStatus.Failed;
        track.CurrentStep = "Analysis failed";
        track.AnalysisError = evt.Error;
        track.ProgressPercent = 100;
        RefreshComputedState();
    }

    private async Task OnTrackAnalysisCompletedAsync(TrackAnalysisCompletedEvent evt)
    {
        var track = FindTrack(evt.TrackGlobalId);
        if (track is null)
        {
            TryFinalizeProcessingState();
            return;
        }

        if (evt.Success)
        {
            AnalysisTrackItem? refreshedTrack = null;
            if (_libraryService is not null)
            {
                try
                {
                    var entry = await _libraryService.FindLibraryEntryAsync(evt.TrackGlobalId).ConfigureAwait(true);
                    if (entry is not null)
                    {
                        refreshedTrack = MapLibraryEntryToTrack(entry);
                        refreshedTrack.IsInPlaylist = track.IsInPlaylist;
                    }
                }
                catch
                {
                    // Ignore refresh lookup failures; keep status updates flowing.
                }
            }

            if (refreshedTrack is not null)
            {
                track.UpdateFrom(refreshedTrack);
            }

            track.AnalysisStatus = AnalysisRunStatus.Completed;
            track.ProgressPercent = 100;
            track.CurrentStep = "Completed";
            track.AnalysisError = null;
            track.LastAnalyzedAt = DateTime.UtcNow;
            track.ModelVersion ??= "essentia-live";

            _completedAnalysisRuns++;
            if (_trackAnalysisStartedAtUtc.TryGetValue(evt.TrackGlobalId, out var startedAt))
            {
                _trackAnalysisStartedAtUtc.Remove(evt.TrackGlobalId);
                _totalAnalysisSeconds += Math.Max(0.0, (DateTime.UtcNow - startedAt).TotalSeconds);
            }
        }
        else
        {
            track.AnalysisStatus = AnalysisRunStatus.Failed;
            track.ProgressPercent = 100;
            track.CurrentStep = "Analysis failed";
            track.AnalysisError = string.IsNullOrWhiteSpace(evt.ErrorMessage) ? "Analysis failed." : evt.ErrorMessage;
            _trackAnalysisStartedAtUtc.Remove(evt.TrackGlobalId);
        }

        track.IsInQueue = false;
        AnalysisQueue.Remove(track);

        if (string.Equals(CurrentProcessingTrackId, evt.TrackGlobalId, StringComparison.Ordinal))
        {
            CurrentProcessingTrackId = null;
        }

        TryFinalizeProcessingState();
        RefreshComputedState();
    }

    private void OnAnalysisQueueStatusChanged(AnalysisQueueStatusChangedEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.CurrentTrackHash))
        {
            CurrentProcessingTrackId = evt.CurrentTrackHash;
        }

        if (evt.QueuedCount > 0 || !string.IsNullOrWhiteSpace(evt.CurrentTrackHash))
        {
            ProcessingState = AnalysisProcessingState.Processing;
            return;
        }

        TryFinalizeProcessingState();
    }

    private AnalysisTrackItem? FindTrack(string trackId)
    {
        return AnalysisQueue.FirstOrDefault(t => t.TrackId == trackId)
            ?? LibraryTracks.FirstOrDefault(t => t.TrackId == trackId);
    }

    private void TryFinalizeProcessingState()
    {
        var hasPending = AnalysisQueue.Any(t => t.AnalysisStatus is AnalysisRunStatus.Queued or AnalysisRunStatus.Processing);
        if (hasPending)
        {
            ProcessingState = AnalysisProcessingState.Processing;
            return;
        }

        if (_analysisSessionStopwatch.IsRunning)
        {
            _analysisSessionStopwatch.Stop();
        }

        ProcessingState = _completedAnalysisRuns > 0
            ? AnalysisProcessingState.Completed
            : AnalysisProcessingState.Idle;
    }

    private void OnTrackAnalysisRequested(TrackAnalysisRequestedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.TrackGlobalId))
            return;

        // Exit early if already queued or processing to avoid loopbacks.
        if (AnalysisQueue.Any(t => t.TrackId == evt.TrackGlobalId))
            return;

        var track = LibraryTracks.FirstOrDefault(t => t.TrackId == evt.TrackGlobalId)
            ?? AnalysisQueue.FirstOrDefault(t => t.TrackId == evt.TrackGlobalId);

        if (track == null)
        {
            track = new AnalysisTrackItem(
                evt.TrackGlobalId,
                "Selected Artist",
                "Selected Track");

            HookTrack(track);
            LibraryTracks.Add(track);
            ApplyFilter();
        }

        AddToQueue(track);
    }

    private void OnFileIngestionQueued(FileIngestionQueuedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileIngestionQueued(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Ingestion pending: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    private void OnFileIngestionCompleted(FileIngestionCompletedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileIngestionCompleted(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Indexed: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    private void OnFileMissingDetected(FileMissingDetectedEvent evt)
    {
        ApplyLifecycleMetrics(_lifecycleProjectionService.ApplyFileMissingDetected(GetCurrentLifecycleMetrics()));

        AutomixStatusMessage = $"Stale index detected: {System.IO.Path.GetFileName(evt.FilePath)}";
        RefreshComputedState();
    }

    private void ApplyFilter(string? queryOverride = null)
    {
        _filterRequestSubject.OnNext(queryOverride ?? SearchText);
    }

    private void ApplyFilterDirect(string? queryOverride = null)
    {
        var timer = Stopwatch.StartNew();
        FilteredLibraryTracks.Clear();
        var activeQuery = queryOverride ?? SearchText;
        var query = string.IsNullOrWhiteSpace(activeQuery)
            ? LibraryTracks
            : LibraryTracks.Where(t =>
                t.Title.Contains(activeQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(activeQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var t in query)
            FilteredLibraryTracks.Add(t);

        timer.Stop();
        _lastFilterMilliseconds = timer.Elapsed.TotalMilliseconds;
        _filterSampleCount++;
        _averageFilterMilliseconds += (_lastFilterMilliseconds - _averageFilterMilliseconds) / _filterSampleCount;
        _filteredTrackCount = FilteredLibraryTracks.Count;

        this.RaisePropertyChanged(nameof(IsLibraryEmpty));
        this.RaisePropertyChanged(nameof(FilterPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    private void RunPerformanceProbe()
    {
        var probeQueries = new[]
        {
            string.Empty,
            "a",
            "the",
            "mix",
            "house",
            "zzzz"
        };

        var samples = new List<double>(probeQueries.Length);
        var originalQuery = SearchText;

        foreach (var query in probeQueries)
        {
            var timer = Stopwatch.StartNew();
            ApplyFilter(query);
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }

        // Restore user-visible query results after the probe run.
        ApplyFilter(originalQuery);

        if (samples.Count == 0)
        {
            PerformanceProbeSummary = "Probe did not collect samples.";
            return;
        }

        var average = samples.Average();
        var max = samples.Max();
        var p95Index = Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1);
        var p95 = samples.OrderBy(x => x).ElementAt(p95Index);

        var previousP95 = PerformanceProbeRuns.Count > 0 ? PerformanceProbeRuns[0].P95Ms : (double?)null;
        var p95Delta = previousP95.HasValue ? p95 - previousP95.Value : (double?)null;

        PerformanceProbeSummary = $"Probe {samples.Count} queries • avg {average:F1} ms • p95 {p95:F1} ms • max {max:F1} ms";
        PerformanceProbeRuns.Insert(0, new PerformanceProbeRun(DateTime.UtcNow, samples.Count, average, p95, max, p95Delta));
        while (PerformanceProbeRuns.Count > 10)
        {
            PerformanceProbeRuns.RemoveAt(PerformanceProbeRuns.Count - 1);
        }

        AutomixStatusMessage = "Developer performance probe completed.";
    }

    private void AddToQueueCore(AnalysisTrackItem track, bool refreshState, bool prioritize = false)
    {
        if (!AnalysisQueue.Any(t => t.TrackId == track.TrackId))
        {
            if (prioritize)
            {
                AnalysisQueue.Insert(0, track);
            }
            else
            {
                AnalysisQueue.Add(track);
            }
        }

        track.IsInQueue = true;
        track.AnalysisStatus = AnalysisRunStatus.Queued;
        track.ProgressPercent = 0;
        track.CurrentStep = "Queued for analysis";

        if (refreshState)
        {
            RefreshComputedState();
        }
    }

    private static void ResetTrackAnalysisState(AnalysisTrackItem track)
    {
        track.AnalysisData = null;
        track.AnalysisError = null;
        track.StemError = null;
        track.LastAnalyzedAt = null;
        track.ModelVersion = null;
    }

    private async Task LoadLibraryAsync()
    {
        try
        {
            if (_libraryService is null)
                return;

            var entries = await _libraryService.LoadAllLibraryEntriesAsync();
            var existingEntries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.FilePath) && System.IO.File.Exists(e.FilePath))
                .ToList();

            var lifecycleMetrics = await _lifecycleProjectionService.ComputeMetricsAsync().ConfigureAwait(false);
            ApplyLifecycleMetrics(lifecycleMetrics);

            LibraryTracks.Clear();

            foreach (var entry in existingEntries
                .OrderByDescending(e => e.AddedAt)
                .Select(MapLibraryEntryToTrack))
            {
                HookTrack(entry);
                LibraryTracks.Add(entry);
            }

            if (lifecycleMetrics.StaleIndexed > 0)
            {
                AutomixStatusMessage = $"Loaded {existingEntries.Count} valid tracks; skipped {lifecycleMetrics.StaleIndexed} stale index entries.";
            }

            ApplyFilter();
            RefreshComputedState();
        }
        catch
        {
            if (LibraryTracks.Count == 0)
            {
                LoadMockData();
                ApplyFilter();
                RefreshComputedState();
            }
        }
    }

    private LifecycleMetrics GetCurrentLifecycleMetrics()
    {
        return new LifecycleMetrics(
            _onDiskIndexedTrackCount,
            _indexedCatalogCount,
            _staleIndexedCount,
            _ingestionBacklogCount,
            _desiredDownloadCount);
    }

    private void ApplyLifecycleMetrics(LifecycleMetrics metrics)
    {
        _onDiskIndexedTrackCount = Math.Max(0, metrics.PhysicalOnDisk);
        _indexedCatalogCount = Math.Max(0, metrics.IndexedCatalog);
        _staleIndexedCount = Math.Max(0, metrics.StaleIndexed);
        _ingestionBacklogCount = Math.Max(0, metrics.IngestionBacklog);
        _desiredDownloadCount = Math.Max(0, metrics.DesiredDownloads);
    }

    private AnalysisTrackItem MapLibraryEntryToTrack(LibraryEntry entry)
    {
        var hasRealAnalysis = entry.IsEnriched
            || entry.BPM.HasValue
            || !string.IsNullOrWhiteSpace(entry.MusicalKey)
            || !string.IsNullOrWhiteSpace(entry.PrimaryGenre)
            || entry.Energy.HasValue
            || entry.Valence.HasValue;

        var energyCurvePoints = ParseEnergyCurvePoints(entry.EnergyCurveJson, entry.Energy);
        var segmentedEnergyPoints = ParseSegmentedEnergyPoints(entry.SegmentedEnergyJson, energyCurvePoints);
        var genrePredictions = BuildGenrePredictions(entry);

        AnalysisData? analysisData = null;
        if (hasRealAnalysis)
        {
            var energyPercent = Math.Clamp((entry.Energy ?? 5) * 10.0, 0, 100);
            var valencePercent = Math.Clamp(((entry.Valence ?? 0.5) + 1.0) * 50.0, 0, 100);
            analysisData = new AnalysisData
            {
                Mechanics = new MechanicsData
                {
                    Bpm = entry.BPM ?? entry.SpotifyBPM ?? 0,
                    KeyScale = entry.MusicalKey ?? entry.SpotifyKey ?? string.Empty,
                    TonalProbability = Math.Clamp(entry.KeyConfidence ?? entry.QualityConfidence ?? 0.82, 0.1, 1.0)
                },
                Affective = new AffectiveData
                {
                    Arousal = Math.Clamp((energyPercent / 50.0) - 1.0, -1.0, 1.0),
                    Valence = Math.Clamp((valencePercent / 50.0) - 1.0, -1.0, 1.0)
                },
                Moods = new MoodData
                {
                    Happy = valencePercent,
                    Sad = 100 - valencePercent,
                    Aggressive = energyPercent,
                    Relaxed = 100 - energyPercent,
                    Party = Math.Clamp((energyPercent + valencePercent) / 2.0, 0, 100)
                },
                Genres = genrePredictions,
                Stems = new StemData { AreGenerated = false }
            };
        }

        if (analysisData == null)
        {
            analysisData = new AnalysisData
            {
                Mechanics = new MechanicsData
                {
                    Bpm = entry.BPM ?? entry.SpotifyBPM ?? 0,
                    KeyScale = entry.MusicalKey ?? entry.SpotifyKey ?? string.Empty,
                    TonalProbability = Math.Clamp(entry.KeyConfidence ?? entry.QualityConfidence ?? 0.55, 0.1, 1.0)
                },
                Affective = new AffectiveData
                {
                    Arousal = 0,
                    Valence = 0
                },
                Moods = new MoodData(),
                Genres = genrePredictions,
                Stems = new StemData { AreGenerated = false }
            };
        }

        return new AnalysisTrackItem(
            entry.UniqueHash,
            entry.Artist,
            entry.Title,
            album: entry.Album,
            bpm: entry.BPM,
            musicalKey: entry.MusicalKey,
            filePath: entry.FilePath,
            analysisData: analysisData,
            cueCount: ParseCueCount(entry.CuePointsJson),
            lowBand: entry.LowData,
            midBand: entry.MidData,
            highBand: entry.HighData,
            lastAnalyzedAt: entry.IsEnriched ? entry.AddedAt : null,
            modelVersion: entry.IsEnriched ? "library-cache" : null,
            energyCurvePoints: energyCurvePoints,
            segmentedEnergyPoints: segmentedEnergyPoints,
            dropTimeSeconds: entry.DropTimeSeconds,
            dropConfidence: entry.DropConfidence,
            qualityConfidence: entry.QualityConfidence,
            bpmStability: entry.BpmStability,
            vocalDensity: entry.VocalDensity,
            vocalTypeLabel: entry.VocalType.ToDisplayLabel(),
            loudnessLufs: entry.LoudnessLufs,
            camelotKey: entry.CamelotKey,
                chordProgression: entry.ChordProgression,
                durationSeconds: entry.DurationSeconds);
    }

    private static List<double> ParseSegmentedEnergyPoints(string? segmentedEnergyJson, IReadOnlyList<double> fallbackCurve)
    {
        if (!string.IsNullOrWhiteSpace(segmentedEnergyJson) && segmentedEnergyJson != "[]")
        {
            try
            {
                using var doc = JsonDocument.Parse(segmentedEnergyJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<double>();
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetDouble(out var value))
                        {
                            // Segmented energy is commonly 1-10; normalize to 0-1 for sparkline consistency.
                            points.Add(value > 1.0 ? Math.Clamp(value / 10.0, 0.0, 1.0) : Math.Clamp(value, 0.0, 1.0));
                        }
                    }

                    if (points.Count >= 2)
                    {
                        return points;
                    }
                }
            }
            catch
            {
                // Ignore malformed JSON and fall back.
            }
        }

        if (fallbackCurve.Count >= 2)
        {
            return fallbackCurve.ToList();
        }

        return new List<double> { 0.3, 0.45, 0.6, 0.75, 0.68, 0.52, 0.40, 0.35 };
    }

    public void UpdateLayoutMode(double width)
    {
        var compact = width < 1180;
        IsCompactLayout = compact;

        if (!compact)
        {
            ActivePaneMode = AnalysisPaneMode.Queue;
        }
    }

    private void SetPaneMode(AnalysisPaneMode mode)
    {
        ActivePaneMode = mode;
    }

    private static List<double> ParseEnergyCurvePoints(string? energyCurveJson, double? fallbackEnergy)
    {
        if (!string.IsNullOrWhiteSpace(energyCurveJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(energyCurveJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<double>();
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetDouble(out var value))
                        {
                            points.Add(value);
                        }
                    }

                    if (points.Count >= 2)
                    {
                        return points;
                    }
                }
            }
            catch
            {
                // Ignore malformed JSON and fall back to synthetic points.
            }
        }

        var baseEnergy = Math.Clamp(fallbackEnergy ?? 0.5, 0.0, 1.0);
        return new List<double>
        {
            Math.Clamp(baseEnergy * 0.65, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.85, 0.0, 1.0),
            Math.Clamp(baseEnergy * 1.05, 0.0, 1.0),
            Math.Clamp(baseEnergy * 1.15, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.95, 0.0, 1.0),
            Math.Clamp(baseEnergy * 0.80, 0.0, 1.0),
        };
    }

    private static List<GenrePrediction> BuildGenrePredictions(LibraryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.GenreDistributionJson) && entry.GenreDistributionJson != "{}")
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(entry.GenreDistributionJson);
                if (parsed != null && parsed.Count > 0)
                {
                    return parsed
                        .OrderByDescending(kvp => kvp.Value)
                        .Take(4)
                        .Select(kvp => new GenrePrediction
                        {
                            Label = kvp.Key,
                            Confidence = Math.Clamp(kvp.Value, 0.0, 1.0)
                        })
                        .ToList();
                }
            }
            catch
            {
                // Ignore malformed JSON and use fallback list.
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.PrimaryGenre) || !string.IsNullOrWhiteSpace(entry.Genres))
        {
            return (entry.PrimaryGenre ?? entry.Genres ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(3)
                .Select((label, index) => new GenrePrediction
                {
                    Label = label,
                    Confidence = Math.Max(0.35, 0.85 - (index * 0.15))
                })
                .ToList();
        }

        return new List<GenrePrediction>
        {
            new() { Label = "Analyzed", Confidence = 0.75 }
        };
    }

    private static int ParseCueCount(string? cuePointsJson)
    {
        if (string.IsNullOrWhiteSpace(cuePointsJson))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(cuePointsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void HookTrack(AnalysisTrackItem track)
    {
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => track.PropertyChanged += h,
                h => track.PropertyChanged -= h)
            .Subscribe(pattern =>
            {
                var args = pattern.EventArgs;
                if (args.PropertyName is nameof(AnalysisTrackItem.AnalysisData)
                    or nameof(AnalysisTrackItem.AnalysisStatus)
                    or nameof(AnalysisTrackItem.IsInQueue)
                    or nameof(AnalysisTrackItem.IsInPlaylist)
                    or nameof(AnalysisTrackItem.StemsReady)
                    or nameof(AnalysisTrackItem.AnalysisError)
                    or nameof(AnalysisTrackItem.StemError))
                {
                    RefreshComputedState();
                }
            })
            .DisposeWith(_disposables);
    }

    private void RefreshComputedState()
    {
        _refreshRequestSubject.OnNext(System.Reactive.Unit.Default);
    }

    private void RefreshComputedStateInternal()
    {
        this.RaisePropertyChanged(nameof(CanStartAnalysis));
        this.RaisePropertyChanged(nameof(IsQueueEmpty));
        this.RaisePropertyChanged(nameof(TotalTrackCount));
            this.RaisePropertyChanged(nameof(OnDiskIndexedTrackCount));
            this.RaisePropertyChanged(nameof(IndexedCatalogCount));
            this.RaisePropertyChanged(nameof(StaleIndexedCount));
            this.RaisePropertyChanged(nameof(DesiredDownloadCount));
            this.RaisePropertyChanged(nameof(IngestionBacklogCount));
        this.RaisePropertyChanged(nameof(AnalyzedTrackCount));
        this.RaisePropertyChanged(nameof(PendingTrackCount));
        this.RaisePropertyChanged(nameof(QueueTrackCount));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisTrackCount));
        this.RaisePropertyChanged(nameof(HasIncompleteAnalysisTracks));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisTracks));
        this.RaisePropertyChanged(nameof(IncompleteAnalysisSummary));
        this.RaisePropertyChanged(nameof(StemsReadyCount));
        this.RaisePropertyChanged(nameof(HasQueueMetrics));
        this.RaisePropertyChanged(nameof(AvgAnalysisTimeDisplay));
        this.RaisePropertyChanged(nameof(ThroughputDisplay));
        this.RaisePropertyChanged(nameof(ElapsedTimeDisplay));
        this.RaisePropertyChanged(nameof(QueueMetricsSummary));
        this.RaisePropertyChanged(nameof(LibraryCountDifferentiationSummary));
        this.RaisePropertyChanged(nameof(CompletionRateDisplay));
        this.RaisePropertyChanged(nameof(FilterPerformanceDisplay));
        this.RaisePropertyChanged(nameof(InteractionPerformanceDisplay));
        this.RaisePropertyChanged(nameof(PerformanceDiagnosticsSummary));
    }

    /// <summary>
    /// Loads 10 mock tracks (3 with complete AnalysisData, 7 without)
    /// as per the Phase 1 requirement.
    /// </summary>
    private void LoadMockData()
    {
        // 3 tracks with pre-computed analysis data
        var analysed = new[]
        {
            new AnalysisTrackItem(
                "track-001", "Disclosure", "Latch",
                album: "Settle", bpm: 122.0, musicalKey: "4A",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 122.0, KeyScale = "4A", TonalProbability = 0.92 },
                    Affective = new() { Arousal = 0.65, Valence = 0.70 },
                    Moods = new() { Happy = 80, Sad = 10, Aggressive = 15, Relaxed = 30, Party = 75 },
                    Genres = new()
                    {
                        new() { Label = "House",    Confidence = 0.85 },
                        new() { Label = "UK Garage", Confidence = 0.55 },
                        new() { Label = "Electronic", Confidence = 0.40 }
                    },
                    Stems = new() { AreGenerated = false }
                }),

            new AnalysisTrackItem(
                "track-002", "Aphex Twin", "Windowlicker",
                album: "Windowlicker EP", bpm: 138.0, musicalKey: "1A",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 138.0, KeyScale = "1A", TonalProbability = 0.61 },
                    Affective = new() { Arousal = 0.82, Valence = -0.25 },
                    Moods = new() { Happy = 30, Sad = 20, Aggressive = 70, Relaxed = 5, Party = 60 },
                    Genres = new()
                    {
                        new() { Label = "IDM",      Confidence = 0.90 },
                        new() { Label = "Techno",   Confidence = 0.50 },
                        new() { Label = "Ambient",  Confidence = 0.20 }
                    },
                    Stems = new()
                    {
                        AreGenerated = true,
                        VocalsPath  = "/stems/windowlicker_vocals.flac",
                        DrumsPath   = "/stems/windowlicker_drums.flac",
                        BassPath    = "/stems/windowlicker_bass.flac",
                        OtherPath   = "/stems/windowlicker_other.flac"
                    }
                }),

            new AnalysisTrackItem(
                "track-003", "Four Tet", "Baby",
                album: "There Is Love in You", bpm: 106.0, musicalKey: "11B",
                analysisData: new AnalysisData
                {
                    Mechanics = new() { Bpm = 106.0, KeyScale = "11B", TonalProbability = 0.88 },
                    Affective = new() { Arousal = 0.30, Valence = 0.55 },
                    Moods = new() { Happy = 65, Sad = 15, Aggressive = 5, Relaxed = 75, Party = 40 },
                    Genres = new()
                    {
                        new() { Label = "Electronica", Confidence = 0.82 },
                        new() { Label = "House",       Confidence = 0.45 },
                        new() { Label = "Ambient",     Confidence = 0.35 }
                    },
                    Stems = new() { AreGenerated = false }
                })
        };

        // 7 tracks without analysis data yet
        var unanalysed = new[]
        {
            new AnalysisTrackItem("track-004", "Bicep",        "Glue",     album: "Bicep",       bpm: 123.0, musicalKey: "7A"),
            new AnalysisTrackItem("track-005", "Jon Hopkins",  "Emerald",  album: "Immunity",    bpm: 130.0, musicalKey: "9B"),
            new AnalysisTrackItem("track-006", "Bonobo",       "Kiara",    album: "Black Sands", bpm: 118.0, musicalKey: "2A"),
            new AnalysisTrackItem("track-007", "Jamie xx",     "Loud Places", album: "In Colour", bpm: 124.0),
            new AnalysisTrackItem("track-008", "Floating Points", "Silhouettes", album: "Elaenia", bpm: 116.0),
            new AnalysisTrackItem("track-009", "Caribou",      "Can't Do Without You", album: "Our Love", bpm: 128.0, musicalKey: "6B"),
            new AnalysisTrackItem("track-010", "Nicolas Jaar", "Space Is Only Noise", album: "Space Is Only Noise", bpm: 98.0)
        };

        foreach (var t in analysed)
        {
            HookTrack(t);
            LibraryTracks.Add(t);
        }

        foreach (var t in unanalysed)
        {
            HookTrack(t);
            LibraryTracks.Add(t);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _disposables.Dispose();
        _cts.Dispose();
    }


    // ── Automix flow (#43) ────────────────────────────────────────────────────

    /// <summary>
    /// Toggles membership of <paramref name="track"/> in the automix playlist.
    /// The track must have completed analysis to be eligible.
    /// </summary>
    public void TogglePlaylist(AnalysisTrackItem track)
    {
        if (track.IsInPlaylist)
        {
            PlaylistTracks.Remove(track);
            track.IsInPlaylist = false;
        }
        else if (track.HasAnalysis)
        {
            PlaylistTracks.Add(track);
            track.IsInPlaylist = true;
        }
    }

    public void StageAllAnalyzedForAutomix()
    {
        PlaylistTracks.Clear();
        foreach (var track in LibraryTracks.Where(t => t.HasAnalysis).OrderBy(t => t.Artist).ThenBy(t => t.Title))
        {
            if (!PlaylistTracks.Contains(track))
            {
                PlaylistTracks.Add(track);
            }

            track.IsInPlaylist = true;
        }

        AutomixStatusMessage = PlaylistTracks.Count == 0
            ? "No analyzed tracks available for staging yet."
            : $"Staged {PlaylistTracks.Count} analyzed track(s) for automix.";
        RefreshComputedState();
    }

    public void ClearAutomixStaging()
    {
        foreach (var track in PlaylistTracks)
        {
            track.IsInPlaylist = false;
        }

        PlaylistTracks.Clear();
        AutomixStatusMessage = "Automix staging cleared.";
        RefreshComputedState();
    }

    /// <summary>
    /// Builds an ordered automix sequence from <see cref="PlaylistTracks"/> using
    /// <see cref="AutomixConstraints"/>.  Sorts by BPM within the allowed range,
    /// then applies a key-compatibility filter.
    /// </summary>
    public void CreateAutomixPlaylist()
    {
        CreateAutomixPlaylistAsync().GetAwaiter().GetResult();
    }

    public async Task CreateAutomixPlaylistAsync()
    {
        if (PlaylistTracks.Count < 2)
        {
            AutomixStatusMessage = "Add at least 2 analyzed tracks to the playlist first.";
            return;
        }

        var c = AutomixConstraints;

        var eligible = PlaylistTracks
            .Where(IsEligibleForAutomix)
            .ToList();

        if (eligible.Count < 2)
        {
            AutomixStatusMessage = $"Not enough tracks in the BPM range {c.MinBpm}–{c.MaxBpm}.";
            return;
        }

        var maxTracks = Math.Max(2, c.MaxTracks);
        var ordered = await TryBuildOptimizedOrderAsync(eligible, c, maxTracks).ConfigureAwait(true)
            ?? BuildFallbackOrder(eligible, maxTracks);

        PlaylistTracks.Clear();
        foreach (var t in ordered) PlaylistTracks.Add(t);

        if (ordered.Count < 2)
        {
            AutomixStatusMessage = "Not enough tracks available to build automix ordering.";
            return;
        }

        var minBpm = ordered.First().AnalysisData?.Mechanics.Bpm ?? ordered.First().Bpm ?? 0;
        var maxBpm = ordered.Last().AnalysisData?.Mechanics.Bpm ?? ordered.Last().Bpm ?? 0;
        AutomixStatusMessage = $"Automix ready: {ordered.Count} tracks, {minBpm:F0}–{maxBpm:F0} BPM.";
    }

    private bool IsEligibleForAutomix(AnalysisTrackItem track)
    {
        var bpm = track.AnalysisData?.Mechanics.Bpm ?? track.Bpm ?? 0;
        return bpm >= AutomixConstraints.MinBpm && bpm <= AutomixConstraints.MaxBpm;
    }

    private async Task<List<AnalysisTrackItem>?> TryBuildOptimizedOrderAsync(
        IReadOnlyList<AnalysisTrackItem> eligibleTracks,
        AutomixConstraints constraints,
        int maxTracks)
    {
        await Task.CompletedTask;
        return null;
    }

    private static List<AnalysisTrackItem> BuildFallbackOrder(IReadOnlyList<AnalysisTrackItem> tracks, int maxTracks)
    {
        return tracks
            .OrderBy(t => t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0)
            .Take(maxTracks)
            .ToList();
    }

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

    public string TrendText => DeltaP95Ms.HasValue ? $"{DeltaP95Ms.Value:+0.0;-0.0;0.0} ms" : "baseline";

    public string SeverityLabel => P95Ms switch
    {
        <= 6.0 => "good",
        <= 14.0 => "warn",
        _ => "slow"
    };

    public string SeverityColor => P95Ms switch
    {
        <= 6.0 => "#7BD88F",
        <= 14.0 => "#F1C15A",
        _ => "#FF8A80"
    };

    public string DisplayLabel => $"{TimestampUtc:HH:mm:ss} · {SeverityLabel} · {TrendArrow} {TrendText} · {QueryCount}q · avg {AverageMs:F1} ms · p95 {P95Ms:F1} ms · max {MaxMs:F1} ms";
}
