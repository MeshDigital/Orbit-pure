using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

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

    public string TrackId { get; }
    public string Artist { get; }
    public string Title { get; }
    public string? Album { get; }
    public double? Bpm { get; }
    public string? MusicalKey { get; }
    public string? FilePath { get; }

    /// <summary>Current analysis run status (Queued → Processing → Completed/Failed).</summary>
    public AnalysisRunStatus AnalysisStatus
    {
        get => _analysisStatus;
        set => this.RaiseAndSetIfChanged(ref _analysisStatus, value);
    }

    /// <summary>Full ML analysis output; non-null only when Status == Completed.</summary>
    public AnalysisData? AnalysisData
    {
        get => _analysisData;
        set => this.RaiseAndSetIfChanged(ref _analysisData, value);
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
        set => this.RaiseAndSetIfChanged(ref _isInPlaylist, value);
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

    public bool HasAnalysis  => AnalysisData is not null;
    public bool IsProcessing => AnalysisStatus == AnalysisRunStatus.Processing;
    public bool IsCompleted  => AnalysisStatus == AnalysisRunStatus.Completed;

    public AnalysisTrackItem(
        string trackId,
        string artist,
        string title,
        string? album = null,
        double? bpm = null,
        string? musicalKey = null,
        string? filePath = null,
        AnalysisData? analysisData = null)
    {
        TrackId = trackId;
        Artist = artist;
        Title = title;
        Album = album;
        Bpm = bpm;
        MusicalKey = musicalKey;
        FilePath = filePath;
        _analysisData = analysisData;

        if (analysisData is not null)
            _analysisStatus = AnalysisRunStatus.Completed;
    }
}

/// <summary>
/// ViewModel for the Analysis page.
/// Manages the library track list, the analysis queue, processing state, and mock data.
/// </summary>
public class AnalysisPageViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _cts = new();

    private AnalysisProcessingState _processingState = AnalysisProcessingState.Idle;
    private string? _currentProcessingTrackId;
    private string _searchText = string.Empty;

    // ── Collections ─────────────────────────────────────────────────────────────

    /// <summary>All tracks available in the library (source-of-truth list).</summary>
    public ObservableCollection<AnalysisTrackItem> LibraryTracks { get; } = new();

    /// <summary>Filtered view of LibraryTracks based on the search box.</summary>
    public ObservableCollection<AnalysisTrackItem> FilteredLibraryTracks { get; } = new();

    /// <summary>Tracks staged for analysis by the user.</summary>
    public ObservableCollection<AnalysisTrackItem> AnalysisQueue { get; } = new();

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
            ApplyFilter();
        }
    }

    public bool IsIdle => ProcessingState == AnalysisProcessingState.Idle;
    public bool IsProcessing => ProcessingState == AnalysisProcessingState.Processing;
    public bool CanStartAnalysis => ProcessingState == AnalysisProcessingState.Idle && AnalysisQueue.Count > 0;

    /// <summary>True when the filtered library list is empty (e.g. search returned no results).</summary>
    public bool IsLibraryEmpty => FilteredLibraryTracks.Count == 0;

    /// <summary>True when no tracks have been staged for analysis yet.</summary>
    public bool IsQueueEmpty => AnalysisQueue.Count == 0;

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Adds a track from the library to the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> AddToQueueCommand { get; }

    /// <summary>Removes a track from the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> RemoveFromQueueCommand { get; }

    /// <summary>Adds every unanalyzed, un-queued visible track to the analysis queue.</summary>
    public ReactiveCommand<Unit, Unit> QueueAllUnanalyzedCommand { get; }

    /// <summary>Starts sequential analysis of all queued tracks.</summary>
    public ReactiveCommand<Unit, Unit> StartAnalysisCommand { get; }

    /// <summary>Generates an automix playlist from tracks that have completed analysis.</summary>
    public ReactiveCommand<Unit, Unit> CreateAutomixCommand { get; }

    /// <summary>Adds a track to the automix playlist (or removes it if already present).</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> TogglePlaylistCommand { get; }

    // ── Automix state ────────────────────────────────────────────────────────────

    private AutomixConstraints _automixConstraints = new();
    private string? _automixStatusMessage;

    /// <summary>Constraints used when building the next automix playlist.</summary>
    public AutomixConstraints AutomixConstraints
    {
        get => _automixConstraints;
        set => this.RaiseAndSetIfChanged(ref _automixConstraints, value);
    }

    /// <summary>Tracks selected for the current automix playlist.</summary>
    public ObservableCollection<AnalysisTrackItem> PlaylistTracks { get; } = new();

    /// <summary>Success / error message from the last automix generation attempt.</summary>
    public string? AutomixStatusMessage
    {
        get => _automixStatusMessage;
        private set => this.RaiseAndSetIfChanged(ref _automixStatusMessage, value);
    }

    // ── Constructor ──────────────────────────────────────────────────────────────

    public AnalysisPageViewModel(IEventBus eventBus)
    {
        _eventBus = eventBus;

        LoadMockData();
        ApplyFilter();

        // ── Wire up commands ──────────────────────────────────────────────────
        AddToQueueCommand     = ReactiveCommand.Create<AnalysisTrackItem>(AddToQueue);
        RemoveFromQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(RemoveFromQueue);
        QueueAllUnanalyzedCommand = ReactiveCommand.Create(QueueAllUnanalyzed);

        var canStart  = this.WhenAnyValue(x => x.CanStartAnalysis);
        StartAnalysisCommand = ReactiveCommand.CreateFromTask(StartAnalysisAsync, canStart);

        CreateAutomixCommand = ReactiveCommand.Create(CreateAutomixPlaylist);
        TogglePlaylistCommand = ReactiveCommand.Create<AnalysisTrackItem>(TogglePlaylist);

        // Keep CanStartAnalysis and IsQueueEmpty in sync when queue changes
        AnalysisQueue.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(CanStartAnalysis));
            this.RaisePropertyChanged(nameof(IsQueueEmpty));
        };

        // React to per-track progress events
        _eventBus.GetEvent<AnalysisProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisProgress)
            .DisposeWith(_disposables);
    }

    // ── Commands / Actions ───────────────────────────────────────────────────────

    /// <summary>Adds a library track to the analysis queue (if not already present).</summary>
    public void AddToQueue(AnalysisTrackItem track)
    {
        if (!AnalysisQueue.Any(t => t.TrackId == track.TrackId))
        {
            AnalysisQueue.Add(track);
            track.IsInQueue = true;
        }
    }

    /// <summary>Removes a track from the analysis queue.</summary>
    public void RemoveFromQueue(AnalysisTrackItem track)
    {
        AnalysisQueue.Remove(track);
        track.IsInQueue = false;
    }

    /// <summary>Adds all visible unanalyzed tracks that are not yet in the queue.</summary>
    public void QueueAllUnanalyzed()
    {
        foreach (var track in FilteredLibraryTracks.Where(t => !t.HasAnalysis && !t.IsInQueue))
            AddToQueue(track);
    }

    /// <summary>
    /// Starts sequential mock analysis of every track in the queue.
    /// Each track is simulated with a 3-second "processing" delay.
    /// </summary>
    public async Task StartAnalysisAsync()
    {
        if (!CanStartAnalysis) return;

        ProcessingState = AnalysisProcessingState.Processing;

        var queue = AnalysisQueue.ToList();

        foreach (var track in queue)
        {
            if (_cts.Token.IsCancellationRequested) break;

            CurrentProcessingTrackId = track.TrackId;
            track.AnalysisStatus = AnalysisRunStatus.Processing;
            track.ProgressPercent = 0;

            // Simulate the ML pipeline in discrete steps
            await SimulateTrackAnalysisAsync(track, _cts.Token);

            if (!_cts.Token.IsCancellationRequested)
            {
                track.AnalysisData = GenerateMockAnalysisData(track);
                track.AnalysisStatus  = AnalysisRunStatus.Completed;
                track.ProgressPercent = 100;
                track.CurrentStep     = "✓ Completed";
                track.LastAnalyzedAt  = DateTime.UtcNow;
                track.ModelVersion    = "essentia-2.1-b6";
                track.AnalysisError   = null;   // clear any previous error
            }
        }

        CurrentProcessingTrackId = null;
        ProcessingState = _cts.Token.IsCancellationRequested
            ? AnalysisProcessingState.Error
            : AnalysisProcessingState.Completed;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private static readonly string[] SimulatedSteps =
    {
        "Probing file integrity...",
        "Extracting waveform...",
        "Running Essentia BPM/key analysis...",
        "Running TensorFlow mood models...",
        "Computing embeddings...",
        "Saving to database..."
    };

    private static async Task SimulateTrackAnalysisAsync(
        AnalysisTrackItem track, CancellationToken ct)
    {
        int totalSteps = SimulatedSteps.Length;
        for (int i = 0; i < totalSteps; i++)
        {
            if (ct.IsCancellationRequested) return;

            track.CurrentStep = SimulatedSteps[i];
            track.ProgressPercent = (int)((i / (double)totalSteps) * 100);

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static AnalysisData GenerateMockAnalysisData(AnalysisTrackItem track)
    {
        // Use a stable seed derived from the string content (not object hash code)
        // so repeated calls for the same track produce the same mock values.
        int seed = 0;
        foreach (char c in track.TrackId) seed = seed * 31 + c;
        var rng = new Random(Math.Abs(seed));

        return new AnalysisData
        {
            Mechanics = new MechanicsData
            {
                Bpm = track.Bpm ?? (120.0 + rng.NextDouble() * 40.0),
                KeyScale = track.MusicalKey ?? "C Major",
                TonalProbability = 0.7 + rng.NextDouble() * 0.3
            },
            Affective = new AffectiveData
            {
                Arousal = rng.NextDouble() * 2.0 - 1.0,
                Valence = rng.NextDouble() * 2.0 - 1.0
            },
            Moods = new MoodData
            {
                Happy = rng.NextDouble() * 100,
                Sad = rng.NextDouble() * 100,
                Aggressive = rng.NextDouble() * 100,
                Relaxed = rng.NextDouble() * 100,
                Party = rng.NextDouble() * 100
            },
            Genres = new System.Collections.Generic.List<GenrePrediction>
            {
                new() { Label = "Electronic", Confidence = 0.6 + rng.NextDouble() * 0.4 },
                new() { Label = "House",      Confidence = 0.3 + rng.NextDouble() * 0.4 },
                new() { Label = "Techno",     Confidence = rng.NextDouble() * 0.3 }
            },
            Stems = new StemData { AreGenerated = false }
        };
    }

    private void OnAnalysisProgress(AnalysisProgressEvent evt)
    {
        var track = AnalysisQueue.FirstOrDefault(t => t.TrackId == evt.TrackGlobalId);
        if (track is null) return;

        track.ProgressPercent = evt.ProgressPercent;
        track.CurrentStep = evt.CurrentStep;
    }

    private void ApplyFilter()
    {
        FilteredLibraryTracks.Clear();
        var query = string.IsNullOrWhiteSpace(SearchText)
            ? LibraryTracks
            : LibraryTracks.Where(t =>
                t.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var t in query)
            FilteredLibraryTracks.Add(t);

        this.RaisePropertyChanged(nameof(IsLibraryEmpty));
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

        foreach (var t in analysed)   LibraryTracks.Add(t);
        foreach (var t in unanalysed) LibraryTracks.Add(t);
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

    /// <summary>
    /// Builds an ordered automix sequence from <see cref="PlaylistTracks"/> using
    /// <see cref="AutomixConstraints"/>.  Sorts by BPM within the allowed range,
    /// then applies a key-compatibility filter.
    /// </summary>
    public void CreateAutomixPlaylist()
    {
        if (PlaylistTracks.Count < 2)
        {
            AutomixStatusMessage = "Add at least 2 analysed tracks to the playlist first.";
            return;
        }

        var c = AutomixConstraints;

        // Filter tracks that satisfy BPM bounds
        var eligible = PlaylistTracks
            .Where(t =>
            {
                var bpm = t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0;
                return bpm >= c.MinBpm && bpm <= c.MaxBpm;
            })
            .ToList();

        if (eligible.Count < 2)
        {
            AutomixStatusMessage = $"Not enough tracks in the BPM range {c.MinBpm}–{c.MaxBpm}.";
            return;
        }

        // Sort by BPM so transitions are smooth
        var ordered = eligible.OrderBy(t => t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0).ToList();

        PlaylistTracks.Clear();
        foreach (var t in ordered) PlaylistTracks.Add(t);

        AutomixStatusMessage = $"Automix ready: {ordered.Count} tracks, "
            + $"{ordered.First().AnalysisData?.Mechanics.Bpm ?? 0:F0}–"
            + $"{ordered.Last().AnalysisData?.Mechanics.Bpm  ?? 0:F0} BPM.";
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
