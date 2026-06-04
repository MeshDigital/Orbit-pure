using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One row in the Similar Tracks panel — a candidate track with its similarity score.
/// </summary>
public sealed class SimilarTrackRowViewModel : ReactiveObject, IDisposable
{
    public string TrackHash { get; }
    private bool _disposed;

    private string _title          = string.Empty;
    private string _artist         = string.Empty;
    private string _key            = string.Empty;
    private float  _bpm;
    private double _score;
    private float  _overallScore;
    private float  _harmonyScore;
    private float  _beatScore;
    private float  _dropSonicScore;
    private float  _doubleDropScore;
    private string _harmonyLabel   = string.Empty;
    private string _beatLabel      = string.Empty;
    private string _dropLabel      = string.Empty;
    private bool   _isPotentialDoubleDrop;
    private bool   _isBridgeBetweenCandidate;
    private bool   _isInsertConfirmArmed;
    private CancellationTokenSource? _insertConfirmTimeoutCts;
    private float  _bridgeFromScore;
    private float  _bridgeToScore;
    private string _bridgeFromHash = string.Empty;
    private string _bridgeToHash = string.Empty;
    private string _transitionStyleLabel = string.Empty;
    private string _transitionStyleReason = string.Empty;
    private bool   _addToProjectOnBridgeInsert = true;
    private readonly IEventBus? _eventBus;
    private PlaylistTrack? _projectTrack;

    public string Title  { get => _title;  private set => this.RaiseAndSetIfChanged(ref _title,  value); }
    public string Artist { get => _artist; private set => this.RaiseAndSetIfChanged(ref _artist, value); }
    public string Key    { get => _key;    private set => this.RaiseAndSetIfChanged(ref _key,    value); }
    public float  Bpm    { get => _bpm;    private set => this.RaiseAndSetIfChanged(ref _bpm,    value); }

    /// <summary>Raw embedding cosine similarity to the seed track (0–1).</summary>
    public double Score { get => _score; set => this.RaiseAndSetIfChanged(ref _score, value); }

    /// <summary>Weighted overall compatibility (0–1) from <see cref="TrackMatchScore"/>.</summary>
    public float OverallScore    { get => _overallScore;    set => this.RaiseAndSetIfChanged(ref _overallScore,    value); }
    public float HarmonyScore    { get => _harmonyScore;    set => this.RaiseAndSetIfChanged(ref _harmonyScore,    value); }
    public float BeatScore       { get => _beatScore;       set => this.RaiseAndSetIfChanged(ref _beatScore,       value); }
    public float DropSonicScore  { get => _dropSonicScore;  set => this.RaiseAndSetIfChanged(ref _dropSonicScore,  value); }
    public float DoubleDropScore { get => _doubleDropScore; set => this.RaiseAndSetIfChanged(ref _doubleDropScore, value); }
    public string HarmonyLabel   { get => _harmonyLabel;    set => this.RaiseAndSetIfChanged(ref _harmonyLabel,    value); }
    public string BeatLabel      { get => _beatLabel;       set => this.RaiseAndSetIfChanged(ref _beatLabel,       value); }
    public string DropLabel      { get => _dropLabel;       set => this.RaiseAndSetIfChanged(ref _dropLabel,       value); }
    public bool IsBridgeBetweenCandidate
    {
        get => _isBridgeBetweenCandidate;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBridgeBetweenCandidate, value);
            this.RaisePropertyChanged(nameof(AddActionLabel));
            this.RaisePropertyChanged(nameof(AddActionToolTip));
        }
    }
    public bool IsInsertConfirmArmed
    {
        get => _isInsertConfirmArmed;
        set
        {
            this.RaiseAndSetIfChanged(ref _isInsertConfirmArmed, value);
            this.RaisePropertyChanged(nameof(AddActionLabel));
            this.RaisePropertyChanged(nameof(AddActionToolTip));
        }
    }
    public float BridgeFromScore { get => _bridgeFromScore; set => this.RaiseAndSetIfChanged(ref _bridgeFromScore, value); }
    public float BridgeToScore { get => _bridgeToScore; set => this.RaiseAndSetIfChanged(ref _bridgeToScore, value); }
    public string BridgeFromHash { get => _bridgeFromHash; set => this.RaiseAndSetIfChanged(ref _bridgeFromHash, value); }
    public string BridgeToHash { get => _bridgeToHash; set => this.RaiseAndSetIfChanged(ref _bridgeToHash, value); }
    public string TransitionStyleLabel { get => _transitionStyleLabel; set => this.RaiseAndSetIfChanged(ref _transitionStyleLabel, value); }
    public string TransitionStyleReason { get => _transitionStyleReason; set => this.RaiseAndSetIfChanged(ref _transitionStyleReason, value); }
    public bool AddToProjectOnBridgeInsert { get => _addToProjectOnBridgeInsert; set => this.RaiseAndSetIfChanged(ref _addToProjectOnBridgeInsert, value); }
    public string AddActionLabel => IsBridgeBetweenCandidate
        ? (IsInsertConfirmArmed ? "✓" : "Insert")
        : "+";
    public string AddActionToolTip => IsBridgeBetweenCandidate
        ? (IsInsertConfirmArmed ? "Click again to confirm insertion between selected tracks" : "Insert this bridge candidate between selected tracks")
        : "Add to project/playlist";

    /// <summary>True when <see cref="DoubleDropScore"/> ≥ 0.75.</summary>
    public bool IsPotentialDoubleDrop
    {
        get => _isPotentialDoubleDrop;
        set => this.RaiseAndSetIfChanged(ref _isPotentialDoubleDrop, value);
    }

    public ICommand AddToPlaylistCommand { get; }

    public SimilarTrackRowViewModel(
        SimilarTrack result,
        DatabaseService db,
        ILogger logger,
        IEventBus? eventBus = null,
        TrackMatchScore? matchScore = null)
    {
        TrackHash = result.TrackHash;
        _score    = result.Score;
        _eventBus = eventBus;

        AddToPlaylistCommand = new RelayCommand(() =>
        {
            if (_eventBus == null) return;

            if (IsBridgeBetweenCandidate && !IsInsertConfirmArmed)
            {
                IsInsertConfirmArmed = true;
                ArmInsertConfirmationTimeout();
                return;
            }

            var track = ToPlaylistTrack();
            if (!IsBridgeBetweenCandidate || AddToProjectOnBridgeInsert)
                _eventBus.Publish(new AddToProjectRequestEvent(new[] { track }));

            if (IsBridgeBetweenCandidate
                && !string.IsNullOrWhiteSpace(BridgeFromHash)
                && !string.IsNullOrWhiteSpace(BridgeToHash))
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    new InsertBridgeTrackBetweenEvent(BridgeFromHash, BridgeToHash, track));
            }

            IsInsertConfirmArmed = false;
            CancelInsertConfirmationTimeout();
        });

        // Apply pre-computed match scores immediately if supplied.
        if (matchScore is not null) ApplyMatchScore(matchScore);

        // Async metadata lookup — fire-and-forget; notifies UI when features arrive.
        Task.Run(async () =>
        {
            try
            {
                using var context = new AppDbContext();

                var libraryEntry = await context.LibraryEntries
                    .AsNoTracking()
                    .Where(e => e.UniqueHash == TrackHash)
                    .Select(e => new { e.Artist, e.Title, e.Album, e.FilePath })
                    .FirstOrDefaultAsync();

                var playlistTrack = await context.PlaylistTracks
                    .AsNoTracking()
                    .Where(t => t.TrackUniqueHash == TrackHash)
                    .OrderByDescending(t => t.AddedAt)
                    .Select(t => new { t.Artist, t.Title, t.Album, t.ResolvedFilePath, t.Format })
                    .FirstOrDefaultAsync();

                var features = await db.GetAudioFeaturesByHashAsync(TrackHash);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    var shortHash = TrackHash.Length > 8 ? TrackHash.Substring(0, 8) : TrackHash;
                    Title = libraryEntry?.Title ?? playlistTrack?.Title ?? $"Track {shortHash}";
                    Artist = libraryEntry?.Artist ?? playlistTrack?.Artist ?? "Unknown Artist";
                    Key = features?.CamelotKey ?? features?.Key ?? string.Empty;
                    Bpm = features?.Bpm ?? 0f;

                    _projectTrack = new PlaylistTrack
                    {
                        TrackUniqueHash = TrackHash,
                        Artist = Artist,
                        Title = Title,
                        Album = libraryEntry?.Album ?? playlistTrack?.Album ?? string.Empty,
                        ResolvedFilePath = libraryEntry?.FilePath ?? playlistTrack?.ResolvedFilePath ?? string.Empty,
                        Format = playlistTrack?.Format ?? System.IO.Path.GetExtension(libraryEntry?.FilePath ?? string.Empty).TrimStart('.'),
                        BPM = Bpm,
                        MusicalKey = Key,
                        Status = TrackStatus.Downloaded
                    };
                });
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[SimilarTracksVM] Metadata fetch failed for {Hash}", TrackHash);
            }
        });
    }

    internal PlaylistTrack ToPlaylistTrack() =>
        _projectTrack ?? new PlaylistTrack
        {
            TrackUniqueHash = TrackHash,
            Artist = Artist,
            Title = Title,
            BPM = Bpm,
            MusicalKey = Key,
            Status = TrackStatus.Downloaded
        };

    internal void ApplyMatchScore(TrackMatchScore s)
    {
        OverallScore          = s.OverallScore;
        HarmonyScore          = s.HarmonyScore;
        BeatScore             = s.BeatScore;
        DropSonicScore        = s.DropSonicScore;
        DoubleDropScore       = s.DoubleDropScore;
        HarmonyLabel          = s.HarmonyLabel;
        BeatLabel             = s.BeatLabel;
        DropLabel             = s.DropLabel;
        IsPotentialDoubleDrop = s.IsPotentialDoubleDrop;
    }

    public void Dispose()
    {
        _disposed = true;
        CancelInsertConfirmationTimeout();
    }

    private void ArmInsertConfirmationTimeout()
    {
        CancelInsertConfirmationTimeout();

        _insertConfirmTimeoutCts = new CancellationTokenSource();
        var token = _insertConfirmTimeoutCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
                if (token.IsCancellationRequested || _disposed)
                    return;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                        IsInsertConfirmArmed = false;
                });
            }
            catch (TaskCanceledException)
            {
                // expected when user confirms quickly or row is disposed
            }
        }, token);
    }

    private void CancelInsertConfirmationTimeout()
    {
        if (_insertConfirmTimeoutCts is null)
            return;

        try { _insertConfirmTimeoutCts.Cancel(); }
        catch { /* ignore cancellation race */ }

        _insertConfirmTimeoutCts.Dispose();
        _insertConfirmTimeoutCts = null;
    }
}

/// <summary>
/// Task 2.4 — ViewModel for the "Similar Tracks" inspector panel.
///
/// Usage:
///   1. Set <see cref="SeedTrackHash"/> to trigger an automatic search.
///   2. Bind <see cref="Results"/> to a ListView/ItemsControl.
///   3. <see cref="IsBusy"/> drives a loading spinner.
///
/// Debouncing: when <see cref="SeedTrackHash"/> changes rapidly (e.g. arrow-key
/// navigation), the actual query waits for <see cref="DebounceMs"/> of quiet time.
/// </summary>
public sealed class SimilarTracksViewModel : ReactiveObject, IDisposable
{
    public const int DefaultTopN    = 12;
    public const int DebounceMs     = 300;

    private readonly SimilarityIndex       _index;
    private readonly DatabaseService       _db;
    private readonly IEventBus?            _eventBus;
    private readonly SectionVectorService? _sectionVectors;
    private readonly PlaylistIntelligenceService? _playlistIntelligence;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private readonly TransitionStyleClassifier? _transitionStyleClassifier;
    private readonly ILogger               _logger;
    private readonly CompositeDisposable   _disposables = new();

    // ── Seed ──────────────────────────────────────────────────────────────

    private string? _seedTrackHash;

    /// <summary>
    /// Hash of the currently selected / inspected track.
    /// Setting this property triggers a debounced similarity query.
    /// </summary>
    public string? SeedTrackHash
    {
        get => _seedTrackHash;
        set => this.RaiseAndSetIfChanged(ref _seedTrackHash, value);
    }

    private string? _fromTrackHash;
    /// <summary>First track in a bridge search (the one ending, whose outro we need to match).</summary>
    public string? FromTrackHash
    {
        get => _fromTrackHash;
        set => this.RaiseAndSetIfChanged(ref _fromTrackHash, value);
    }

    private string? _toTrackHash;
    /// <summary>Second track in a bridge search (the one we're jumping to, whose intro we need to match).</summary>
    public string? ToTrackHash
    {
        get => _toTrackHash;
        set => this.RaiseAndSetIfChanged(ref _toTrackHash, value);
    }

    // ── Results ───────────────────────────────────────────────────────────

    public ObservableCollection<SimilarTrackRowViewModel> Results { get; } = new();
    public ObservableCollection<SimilarTrackRowViewModel> BridgeSuggestions { get; } = new();

    // ── State ─────────────────────────────────────────────────────────────

    private bool _isBridgeMode;
    public bool IsBridgeMode
    {
        get => _isBridgeMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBridgeMode, value);
            this.RaisePropertyChanged(nameof(IsSimilarMode));
            this.RaisePropertyChanged(nameof(IsAnyBridgeMode));
        }
    }

    private bool _isBridgeBetweenMode;
    public bool IsBridgeBetweenMode
    {
        get => _isBridgeBetweenMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBridgeBetweenMode, value);
            this.RaisePropertyChanged(nameof(IsSimilarMode));
            this.RaisePropertyChanged(nameof(IsAnyBridgeMode));
        }
    }

    public bool IsSimilarMode => !IsBridgeMode && !IsBridgeBetweenMode;
    public bool IsAnyBridgeMode => IsBridgeMode || IsBridgeBetweenMode;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _isBridgeInsertOnlyMode = true;
    public bool IsBridgeInsertOnlyMode
    {
        get => _isBridgeInsertOnlyMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBridgeInsertOnlyMode, value);
            this.RaisePropertyChanged(nameof(BridgeInsertModeLabel));
            this.RaisePropertyChanged(nameof(BridgeInsertModeToolTip));
        }
    }

    public string BridgeInsertModeLabel => IsBridgeInsertOnlyMode ? "Insert Only" : "Insert + Add";
    public string BridgeInsertModeToolTip => IsBridgeInsertOnlyMode
        ? "Bridge insert will only update Flow Builder"
        : "Bridge insert will update Flow Builder and add to project";

    // ── Count ─────────────────────────────────────────────────────────────

    private int _topN = DefaultTopN;

    /// <summary>Maximum number of results to display.</summary>
    public int TopN
    {
        get => _topN;
        set => this.RaiseAndSetIfChanged(ref _topN, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    /// <summary>Forces a refresh with the current seed hash, bypassing the debounce timer.</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAllCommand { get; }
    public ReactiveCommand<Unit, Unit> BridgeSuggestionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSimilarModeCommand { get; }
    public ReactiveCommand<Unit, Unit> FindBridgeBetweenCommand { get; }
    public ReactiveCommand<Unit, Unit> ReturnToBridgeListCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBridgeInsertModeCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public SimilarTracksViewModel(
        SimilarityIndex index,
        DatabaseService db,
        ILogger<SimilarTracksViewModel> logger,
        IEventBus? eventBus = null,
        SectionVectorService? sectionVectors = null,
        PlaylistIntelligenceService? playlistIntelligence = null,
        TrackSimilarityService? trackSimilarityService = null,
        TransitionStyleClassifier? transitionStyleClassifier = null)
    {
        _index          = index;
        _db             = db;
        _logger         = logger;
        _eventBus       = eventBus;
        _sectionVectors = sectionVectors;
        _playlistIntelligence = playlistIntelligence;
        _trackSimilarityService = trackSimilarityService;
        _transitionStyleClassifier = transitionStyleClassifier;

        StatusMessage = "Select a track in Library, Search, or Downloads to fill this sidebar.";

        RefreshCommand = ReactiveCommand.CreateFromTask(
            () => QueryAsync(_seedTrackHash, CancellationToken.None));
        AddAllCommand = ReactiveCommand.Create(AddAllResultsToProject);
        BridgeSuggestionsCommand = ReactiveCommand.CreateFromTask(RefreshBridgeSuggestionsAsync);
        ShowSimilarModeCommand = ReactiveCommand.Create(() =>
        {
            IsBridgeMode = false;
            StatusMessage = Results.Count == 0
                ? "No similar tracks found for the current selection."
                : null;
        });
        FindBridgeBetweenCommand = ReactiveCommand.CreateFromTask(FindBridgeBetweenAsync);
        ReturnToBridgeListCommand = ReactiveCommand.Create(() =>
        {
            IsBridgeBetweenMode = false;
            BridgeSuggestions.Clear();
            StatusMessage = null;
        });
        ToggleBridgeInsertModeCommand = ReactiveCommand.Create(() =>
        {
            IsBridgeInsertOnlyMode = !IsBridgeInsertOnlyMode;
            ApplyBridgeInsertModeToRows();
        });

        ReactiveUI.MessageBus.Current.Listen<OpenInspectorEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => PrimeFromInspectorContext(evt.ViewModel))
            .DisposeWith(_disposables);

        ReactiveUI.MessageBus.Current.Listen<FindBridgeBetweenTracksEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt => HandleFindBridgeBetweenRequest(evt))
            .DisposeWith(_disposables);

        // Debounced subscription on SeedTrackHash changes
        this.WhenAnyValue(x => x.SeedTrackHash)
            .Throttle(TimeSpan.FromMilliseconds(DebounceMs),
                      RxApp.TaskpoolScheduler)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .SelectMany(h => Observable.FromAsync(ct => QueryAsync(h, ct)))
            .Subscribe()
            .DisposeWith(_disposables);
    }

    // ── Query ─────────────────────────────────────────────────────────────

    private async Task QueryAsync(string? hash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Results.Clear();
                BridgeSuggestions.Clear();
                IsBridgeMode = false;
                StatusMessage = "Select a downloaded track to view similar and bridge suggestions.";
            });
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsBusy = true;
            IsBridgeMode = false;
            StatusMessage = null;
            Results.Clear();
            BridgeSuggestions.Clear();
        });

        try
        {
            var hits = await _index.GetSimilarTracksAsync(hash, TopN, ct);

            // Compute multi-dimensional match scores for every result.
            var matchScores = await ComputeMatchScoresAsync(hash, hits, ct);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var hit in hits)
                {
                    matchScores.TryGetValue(hit.TrackHash, out var ms);
                    Results.Add(new SimilarTrackRowViewModel(hit, _db, _logger, _eventBus, ms));
                }

                StatusMessage = hits.Count == 0
                    ? "No similar tracks found — analysis embeddings may not be available."
                    : null;
            });
        }
        catch (OperationCanceledException)
        {
            // Debounce superseded; ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SimilarTracksVM] Query failed for seed {Hash}", hash);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = "Error querying similar tracks.");
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private void HandleFindBridgeBetweenRequest(FindBridgeBetweenTracksEvent evt)
    {
        FromTrackHash = evt.FromTrackHash;
        ToTrackHash = evt.ToTrackHash;
        var fromTitle = evt.FromTrackTitle ?? evt.FromTrackHash[..Math.Min(8, evt.FromTrackHash.Length)];
        var toTitle = evt.ToTrackTitle ?? evt.ToTrackHash[..Math.Min(8, evt.ToTrackHash.Length)];
        StatusMessage = $"Finding bridges between \"{fromTitle}\" and \"{toTitle}\"...";
        FindBridgeBetweenCommand.Execute().Subscribe();
    }

    public void PrimeFromInspectorContext(object? viewModel)
    {
        switch (viewModel)
        {
            case PlaylistTrackViewModel playlistTrack:
                _ = playlistTrack.LoadAnalysisDataAsync();
                SeedTrackHash = playlistTrack.GlobalId;
                StatusMessage = $"Finding matches for {playlistTrack.ArtistName} — {playlistTrack.TrackTitle}";
                break;

            case Downloads.UnifiedTrackViewModel unifiedTrack when !string.IsNullOrWhiteSpace(unifiedTrack.Model.TrackUniqueHash):
                SeedTrackHash = unifiedTrack.Model.TrackUniqueHash;
                StatusMessage = $"Finding matches for {unifiedTrack.Model.Artist} — {unifiedTrack.Model.Title}";
                break;

            case PlaylistTrack model when !string.IsNullOrWhiteSpace(model.TrackUniqueHash):
                SeedTrackHash = model.TrackUniqueHash;
                StatusMessage = $"Finding matches for {model.Artist} — {model.Title}";
                break;

            case AnalyzedSearchResultViewModel searchResult:
                StatusMessage = $"Similar suggestions need a downloaded library track. Current selection: {searchResult.ArtistName} — {searchResult.TrackTitle}";
                break;

            case LibraryDoubleInspectorViewModel doubleInspector:
                PrimeFromLibraryContext(doubleInspector.Library, "Double Inspector");
                break;

            case PlaylistIntelligenceViewModel intelligence:
                PrimeFromLibraryContext(intelligence.Library, "Playlist Intelligence");
                break;
        }
    }

    private void PrimeFromLibraryContext(LibraryViewModel? library, string contextLabel)
    {
        var selectedTrack = library?.Tracks?.SelectedTracks?
            .OfType<PlaylistTrackViewModel>()
            .FirstOrDefault(track => !string.IsNullOrWhiteSpace(track.GlobalId));

        if (selectedTrack is not null)
        {
            _ = selectedTrack.LoadAnalysisDataAsync();
            SeedTrackHash = selectedTrack.GlobalId;
            StatusMessage = $"Finding matches for {selectedTrack.ArtistName} — {selectedTrack.TrackTitle}";
            return;
        }

        var fallbackTrack = library?.Tracks?.FilteredTracks?
            .OfType<PlaylistTrackViewModel>()
            .FirstOrDefault(track => !string.IsNullOrWhiteSpace(track.GlobalId))
            ?? library?.Tracks?.CurrentProjectTracks?
                .OfType<PlaylistTrackViewModel>()
                .FirstOrDefault(track => !string.IsNullOrWhiteSpace(track.GlobalId));

        if (fallbackTrack is not null)
        {
            SeedTrackHash = fallbackTrack.GlobalId;
            StatusMessage = $"Finding matches for {fallbackTrack.ArtistName} — {fallbackTrack.TrackTitle}";
            return;
        }

        SeedTrackHash = null;
        StatusMessage = $"{contextLabel} is open. Select a library track to generate similar suggestions.";
    }

    private void AddAllResultsToProject()
    {
        if (_eventBus == null || Results.Count == 0)
            return;

        var tracks = Results.Select(r => r.ToPlaylistTrack()).ToList();
        _eventBus.Publish(new AddToProjectRequestEvent(tracks));
        StatusMessage = $"Queued {tracks.Count} tracks for project add.";
    }

    private Task RefreshBridgeSuggestionsAsync()
    {
        IsBridgeMode = true;
        BridgeSuggestions.Clear();

        foreach (var row in Results
                     .OrderByDescending(r => r.DoubleDropScore > 0 ? ((r.DoubleDropScore * 0.6f) + (r.OverallScore * 0.4f)) : r.OverallScore)
                     .Take(8))
        {
            BridgeSuggestions.Add(row);
        }

        if (BridgeSuggestions.Count == 0)
        {
            StatusMessage = string.IsNullOrWhiteSpace(SeedTrackHash)
                ? "Select a track to generate bridge ideas."
                : "No bridge candidates are ready for this track yet.";
        }
        else
        {
            StatusMessage = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds bridge candidates between two tracks (from→to).
    /// Shows tracks that smoothly connect the first track's outro to the second track's intro.
    /// </summary>
    private async Task FindBridgeBetweenAsync()
    {
        if (string.IsNullOrWhiteSpace(FromTrackHash) || string.IsNullOrWhiteSpace(ToTrackHash) || _sectionVectors == null)
        {
            StatusMessage = "Two tracks must be selected to find bridges between them.";
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsBusy = true;
            IsBridgeBetweenMode = true;
            BridgeSuggestions.Clear();
            StatusMessage = "Finding bridge candidates…";
        });

        try
        {
            // Get all library tracks as candidates
            IReadOnlyList<string> candidateHashes;
            using (var db = new AppDbContext())
            {
                // Only consider tracks that are analyzed and present in local library with a file path.
                candidateHashes = await (
                    from af in db.AudioFeatures.AsNoTracking()
                    join le in db.LibraryEntries.AsNoTracking()
                        on af.TrackUniqueHash equals le.UniqueHash
                    where af.TrackUniqueHash != FromTrackHash
                          && af.TrackUniqueHash != ToTrackHash
                          && !string.IsNullOrWhiteSpace(le.FilePath)
                    select af.TrackUniqueHash)
                    .Distinct()
                    .AsNoTracking()
                    .ToListAsync();
            }

            var bridgeCandidates = _playlistIntelligence != null
                ? await _playlistIntelligence.InsertBetweenAsync(
                    FromTrackHash,
                    ToTrackHash,
                    candidateHashes,
                    topK: 12,
                    ct: CancellationToken.None)
                : null;

            var legacyBridgeCandidates = bridgeCandidates == null && _sectionVectors != null
                ? await _sectionVectors.FindBridgeCandidatesAsync(
                    FromTrackHash,
                    ToTrackHash,
                    candidateHashes,
                    CancellationToken.None)
                : null;

            var styledBridgeCandidates = new List<(PlaylistRecommendation Recommendation, TransitionStyleResult? TransitionStyle)>();
            if (bridgeCandidates != null)
            {
                foreach (var recommendation in bridgeCandidates)
                {
                    styledBridgeCandidates.Add((
                        recommendation,
                        await BuildTransitionStyleForBridgeCandidateAsync(FromTrackHash, recommendation.TrackHash).ConfigureAwait(false)));
                }
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                BridgeSuggestions.Clear();
                var count = 0;

                if (bridgeCandidates != null)
                {
                    foreach (var (recommendation, transitionStyle) in styledBridgeCandidates)
                    {
                        var result = new SimilarTrack(recommendation.TrackHash, recommendation.Score);

                        var row = new SimilarTrackRowViewModel(result, _db, _logger, _eventBus)
                        {
                            IsBridgeBetweenCandidate = true,
                            BridgeFromScore = (float)recommendation.SimilarityScore,
                            BridgeToScore = (float)recommendation.TransitionScore,
                            BridgeFromHash = FromTrackHash,
                            BridgeToHash = ToTrackHash,
                            AddToProjectOnBridgeInsert = !IsBridgeInsertOnlyMode,
                            OverallScore = (float)recommendation.Score,
                            HarmonyScore = (float)recommendation.HarmonicScore,
                            BeatScore = (float)recommendation.SimilarityScore,
                            DropSonicScore = (float)recommendation.EnergyFitScore,
                            HarmonyLabel = recommendation.ReasonTags.ElementAtOrDefault(0) ?? "A10 bridge fit",
                            BeatLabel = recommendation.ReasonTags.ElementAtOrDefault(1) ?? "Segment-aware transition",
                            DropLabel = recommendation.ReasonTags.ElementAtOrDefault(2) ?? string.Empty,
                            TransitionStyleLabel = transitionStyle?.Label ?? string.Empty,
                            TransitionStyleReason = transitionStyle?.Reason ?? string.Empty,
                        };
                        BridgeSuggestions.Add(row);
                        count++;
                    }
                }
                else if (legacyBridgeCandidates != null)
                {
                    foreach (var (trackHash, bridgeScore, aToXScore, xToBScore) in legacyBridgeCandidates.Take(12))
                    {
                        var result = new SimilarTrack(trackHash, bridgeScore);

                        var row = new SimilarTrackRowViewModel(result, _db, _logger, _eventBus)
                        {
                            IsBridgeBetweenCandidate = true,
                            BridgeFromScore = (float)aToXScore,
                            BridgeToScore = (float)xToBScore,
                            BridgeFromHash = FromTrackHash,
                            BridgeToHash = ToTrackHash,
                            AddToProjectOnBridgeInsert = !IsBridgeInsertOnlyMode,
                            OverallScore = (float)bridgeScore
                        };
                        BridgeSuggestions.Add(row);
                        count++;
                    }
                }

                StatusMessage = count == 0
                    ? "No bridge candidates found between these tracks."
                    : bridgeCandidates != null
                        ? $"Found {count} A10 bridge candidate(s)"
                        : $"Found {count} bridge candidate(s)";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SimilarTracksVM] FindBridgeBetweenAsync failed");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = "Error finding bridge candidates.");
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task<TransitionStyleResult?> BuildTransitionStyleForBridgeCandidateAsync(string fromTrackHash, string candidateTrackHash)
    {
        if (_trackSimilarityService is null || _transitionStyleClassifier is null)
            return null;

        var snapshot = await _trackSimilarityService.BuildSnapshotAsync(
            fromTrackHash,
            candidateTrackHash,
            TrackSimilarityProfile.BlendSafe,
            CancellationToken.None).ConfigureAwait(false);

        if (snapshot is null)
            return null;

        return _transitionStyleClassifier.Classify(
            snapshot.Left,
            snapshot.Right,
            snapshot.Result,
            snapshot.LeftSections,
            snapshot.RightSections);
    }

    // ── Match score computation ────────────────────────────────────────────

    private async Task<Dictionary<string, TrackMatchScore>> ComputeMatchScoresAsync(
        string seedHash,
        IReadOnlyList<SimilarTrack> hits,
        CancellationToken ct)
    {
        var result = new Dictionary<string, TrackMatchScore>(hits.Count);

        try
        {
            // Batch-load audio features for seed + all candidates in one query.
            var allHashes = new List<string>(hits.Count + 1) { seedHash };
            allHashes.AddRange(hits.Select(h => h.TrackHash));

            Dictionary<string, Data.Entities.AudioFeaturesEntity> featureMap;
            using (var db = new AppDbContext())
            {
                var rows = await db.AudioFeatures
                    .Where(f => allHashes.Contains(f.TrackUniqueHash))
                    .ToListAsync(ct);
                featureMap = rows.ToDictionary(r => r.TrackUniqueHash);
            }

            featureMap.TryGetValue(seedHash, out var seedFeatures);

            // Pre-warm section vector cache for all involved tracks.
            if (_sectionVectors != null)
                await _sectionVectors.PreloadAsync(allHashes, ct);

            Models.SectionFeatureVector? seedDrop = null;
            Models.SectionFeatureVector? seedOutro = null;
            if (_sectionVectors != null && seedFeatures != null)
            {
                seedDrop = await _sectionVectors.GetSectionAsync(
                    seedHash, Data.Entities.PhraseType.Drop, ct);
                seedOutro = await _sectionVectors.GetSectionAsync(
                    seedHash, Data.Entities.PhraseType.Outro, ct);
            }

            foreach (var hit in hits)
            {
                if (ct.IsCancellationRequested) break;

                featureMap.TryGetValue(hit.TrackHash, out var hitFeatures);

                Models.SectionFeatureVector? hitDrop = null;
                Models.SectionFeatureVector? hitIntro = null;
                if (_sectionVectors != null && hitFeatures != null)
                {
                    hitDrop = await _sectionVectors.GetSectionAsync(
                        hit.TrackHash, Data.Entities.PhraseType.Drop, ct);
                    hitIntro = await _sectionVectors.GetSectionAsync(
                        hit.TrackHash, Data.Entities.PhraseType.Intro, ct);
                }

                var score = TrackMatchScorer.Compute(
                    seedFeatures,
                    hitFeatures,
                    hit.Score,
                    seedDrop,
                    hitDrop,
                    seedOutro,
                    hitIntro);

                result[hit.TrackHash] = score;
            }
        }
        catch (OperationCanceledException) { /* debounce cancelled */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SimilarTracksVM] Match score computation failed");
        }

        return result;
    }

    public void Dispose() => _disposables.Dispose();

    private void ApplyBridgeInsertModeToRows()
    {
        foreach (var row in BridgeSuggestions.Where(r => r.IsBridgeBetweenCandidate))
            row.AddToProjectOnBridgeInsert = !IsBridgeInsertOnlyMode;
    }
}
