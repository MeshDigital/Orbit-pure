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

    public bool HasAnalysis => AnalysisData is not null;
    public bool IsProcessing => AnalysisStatus == AnalysisRunStatus.Processing;
    public bool IsCompleted => AnalysisStatus == AnalysisRunStatus.Completed;

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

    // ── Commands ─────────────────────────────────────────────────────────────

    /// <summary>Adds a track from the library to the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> AddToQueueCommand { get; }

    /// <summary>Removes a track from the analysis queue.</summary>
    public ReactiveCommand<AnalysisTrackItem, Unit> RemoveFromQueueCommand { get; }

    /// <summary>Starts sequential analysis of all queued tracks.</summary>
    public ReactiveCommand<Unit, Unit> StartAnalysisCommand { get; }

    // ── Constructor ──────────────────────────────────────────────────────────────

    public AnalysisPageViewModel(IEventBus eventBus)
    {
        _eventBus = eventBus;

        LoadMockData();
        ApplyFilter();

        // ── Wire up commands ──────────────────────────────────────────────────
        AddToQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(AddToQueue);
        RemoveFromQueueCommand = ReactiveCommand.Create<AnalysisTrackItem>(RemoveFromQueue);

        var canStart = this.WhenAnyValue(x => x.CanStartAnalysis);
        StartAnalysisCommand = ReactiveCommand.CreateFromTask(StartAnalysisAsync, canStart);

        // Keep CanStartAnalysis in sync when queue changes
        AnalysisQueue.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(CanStartAnalysis));

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
            AnalysisQueue.Add(track);
    }

    /// <summary>Removes a track from the analysis queue.</summary>
    public void RemoveFromQueue(AnalysisTrackItem track)
    {
        AnalysisQueue.Remove(track);
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
                track.AnalysisStatus = AnalysisRunStatus.Completed;
                track.ProgressPercent = 100;
                track.CurrentStep = "✓ Completed";
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
        var rng = new Random(track.TrackId.GetHashCode());

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
}
