using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Events;
using SLSKDONET.Models.Flow;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services;
using SLSKDONET.Services.Playlist;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Services.Telemetry;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Powers the Flow Builder mode — lets users assemble a DJ set as an ordered sequence
/// of tracks with AI-computed transition scores between adjacent pairs.
///
/// Workflow:
///   1. User selects a playlist from <see cref="Playlists"/>.
///   2. <see cref="LoadSelectedPlaylistCommand"/> loads tracks into <see cref="Tracks"/>.
///   3. <see cref="SuggestNextCommand"/> appends the best next track via
///      <see cref="PlaylistOptimizer"/> greedy nearest-neighbour.
///   4. User reorders cards with MoveLeft/MoveRight or removes cards with Remove.
///   5. Transition bridges are recalculated after every structural change.
/// </summary>
public sealed class FlowBuilderViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ILibraryService     _library;
    private readonly PlaylistOptimizer   _optimizer;
    private readonly PlaylistIntelligenceService _playlistIntelligence;
    private readonly TrackSimilarityService _trackSimilarityService;
    private readonly TransitionStyleClassifier _transitionStyleClassifier;
    private readonly SLSKDONET.Services.Similarity.SectionVectorService? _sectionVectors;
    private readonly AppConfig           _appConfig;
    private readonly ConfigManager       _configManager;
    private readonly IDialogService _dialogService;
    private readonly FlowBuilderSuggestionTelemetryService _telemetryService;
    private string? _transitionCacheKey;
    private IReadOnlyDictionary<(string FromHash, string ToHash), PlaylistRecommendation>? _transitionCache;
    private string? _activeInspectorTransitionFromHash;
    private string? _activeInspectorTransitionToHash;
    private SuggestedFlowStyleImpact _currentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;

    // ── Playlist selector ─────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    private PlaylistJob? _selectedPlaylist;
    public PlaylistJob? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPlaylist, value);
            _appConfig.FlowBuilderSelectedPlaylistId = value?.Id.ToString();
            _ = _configManager.SaveAsync(_appConfig);
        }
    }

    // ── Set timeline ──────────────────────────────────────────────────────────

    public ObservableCollection<FlowTrackCardViewModel> Tracks { get; } = new();

    // ── UI state ──────────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(IsNotLoading));
        }
    }

    public bool IsNotLoading => !_isLoading;

    public bool HasTracks    => Tracks.Count > 0;
    public bool HasNoTracks  => Tracks.Count == 0;

    public IReadOnlyList<string> TransitionStyleFilters { get; } =
    [
        "All styles",
        "Smooth Blend",
        "Energy Lift",
        "Drop Swap",
        "Breakdown Reset",
        "Tension Bridge",
        "Risky Clash",
    ];

    private string _selectedTransitionStyleFilter = "All styles";
    public string SelectedTransitionStyleFilter
    {
        get => _selectedTransitionStyleFilter;
        set
        {
            if (string.Equals(_selectedTransitionStyleFilter, value, StringComparison.Ordinal))
                return;

            this.RaiseAndSetIfChanged(ref _selectedTransitionStyleFilter, value);
            ApplyTransitionStyleFilter();
            this.RaisePropertyChanged(nameof(TransitionFilterSummary));
        }
    }

    public string TransitionFilterSummary
    {
        get
        {
            var total = Tracks.Count(track => track.Bridge != null);
            var visible = Tracks.Count(track => track.Bridge != null && track.HasBridge);
            if (total == 0)
                return "No transitions to filter";

            return $"{visible}/{total} transitions visible";
        }
    }

    private PlaylistReorderResult? _suggestedFlow;
    private string _suggestedFlowStyleSummary = string.Empty;
    public PlaylistReorderResult? SuggestedFlow
    {
        get => _suggestedFlow;
        private set
        {
            this.RaiseAndSetIfChanged(ref _suggestedFlow, value);
            this.RaisePropertyChanged(nameof(HasSuggestedFlow));
            this.RaisePropertyChanged(nameof(HasSuggestedFlowImpactPreview));
            this.RaisePropertyChanged(nameof(SuggestedFlowPreview));
            this.RaisePropertyChanged(nameof(SuggestedFlowScoreDisplay));
            this.RaisePropertyChanged(nameof(SuggestedFlowStyleSummary));
            this.RaisePropertyChanged(nameof(SuggestedFlowReasonSummary));
        }
    }

    public bool HasSuggestedFlow => SuggestedFlow is { OrderedTrackHashes.Count: > 0 };

    public string SuggestedFlowPreview
    {
        get
        {
            if (!HasSuggestedFlow)
                return string.Empty;

            var cardLookup = Tracks.ToDictionary(track => track.TrackHash, StringComparer.Ordinal);
            return string.Join("  ->  ", SuggestedFlow!.OrderedTrackHashes
                .Take(5)
                .Select(hash => cardLookup.TryGetValue(hash, out var card)
                    ? $"{card.Artist} - {card.Title}"
                    : hash));
        }
    }

    public string SuggestedFlowScoreDisplay => HasSuggestedFlow
        ? $"Avg flow {(SuggestedFlow!.AverageTransitionScore * 100):F0}%"
        : string.Empty;

    public string SuggestedFlowStyleSummary => HasSuggestedFlow
        ? _suggestedFlowStyleSummary
        : string.Empty;

    public SuggestedFlowStyleImpact CurrentSuggestedFlowImpact
    {
        get => _currentSuggestedFlowImpact;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentSuggestedFlowImpact, value);
            this.RaisePropertyChanged(nameof(HasSuggestedFlowImpactPreview));
        }
    }

    public bool HasSuggestedFlowImpactPreview => HasSuggestedFlow
        && !string.IsNullOrWhiteSpace(CurrentSuggestedFlowImpact.SummaryText)
        && _appConfig.IsFlowBuilderPreviewEnabledForThisInstall(GetFlowBuilderPreviewInstallKey());

    public string SuggestedFlowReasonSummary
    {
        get
        {
            if (!HasSuggestedFlow)
                return string.Empty;

            var reasons = SuggestedFlow!.TransitionRecommendations
                .SelectMany(recommendation => recommendation.ReasonTags)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            return reasons.Count == 0
                ? "A10 found a cleaner transition path across the staged set."
                : string.Join("  ·  ", reasons);
        }
    }

    private string _statusText = "Select a playlist and click Load to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> LoadSelectedPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SuggestNextCommand          { get; }
    public ReactiveCommand<Unit, Unit> SuggestFlowCommand          { get; }
    public ReactiveCommand<Unit, Unit> ApplySuggestedFlowCommand   { get; }
    public ReactiveCommand<Unit, Unit> DismissSuggestedFlowCommand { get; }
    public ReactiveCommand<Unit, Unit> ViewSuggestedFlowImpactCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadPlaylistsCommand        { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand                { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public FlowBuilderViewModel(
        ILibraryService library,
        PlaylistOptimizer optimizer,
        PlaylistIntelligenceService playlistIntelligence,
        TrackSimilarityService trackSimilarityService,
        TransitionStyleClassifier transitionStyleClassifier,
        AppConfig appConfig,
        ConfigManager configManager,
        IDialogService dialogService,
        FlowBuilderSuggestionTelemetryService telemetryService,
        SLSKDONET.Services.Similarity.SectionVectorService? sectionVectors = null)
    {
        _library   = library;
        _optimizer = optimizer;
        _playlistIntelligence = playlistIntelligence;
        _trackSimilarityService = trackSimilarityService;
        _transitionStyleClassifier = transitionStyleClassifier;
        _appConfig = appConfig;
        _configManager = configManager;
        _dialogService = dialogService;
        _telemetryService = telemetryService;
        _sectionVectors = sectionVectors;

        LoadPlaylistsCommand = ReactiveCommand.CreateFromTask(LoadPlaylistsAsync);
        LoadSelectedPlaylistCommand = ReactiveCommand.CreateFromTask(
            LoadSelectedPlaylistAsync,
            this.WhenAnyValue(x => x.SelectedPlaylist, x => x.IsLoading,
                (pl, loading) => pl != null && !loading));

        SuggestNextCommand = ReactiveCommand.CreateFromTask(
            SuggestNextAsync,
            this.WhenAnyValue(x => x.IsLoading, loading => !loading));

        SuggestFlowCommand = ReactiveCommand.CreateFromTask(
            SuggestFlowAsync,
            this.WhenAnyValue(x => x.IsLoading, x => x.HasTracks,
                (loading, hasTracks) => !loading && hasTracks));

        ApplySuggestedFlowCommand = ReactiveCommand.CreateFromTask(
            ApplySuggestedFlowAsync,
            this.WhenAnyValue(x => x.IsLoading, x => x.HasSuggestedFlow,
                (loading, hasSuggestedFlow) => !loading && hasSuggestedFlow));

        DismissSuggestedFlowCommand = ReactiveCommand.Create(
            DismissSuggestedFlow,
            this.WhenAnyValue(x => x.HasSuggestedFlow));

        ViewSuggestedFlowImpactCommand = ReactiveCommand.CreateFromTask(
            ViewSuggestedFlowImpactAsync,
            this.WhenAnyValue(x => x.HasSuggestedFlowImpactPreview));

        ClearCommand = ReactiveCommand.Create(
            () =>
            {
                Tracks.Clear();
                InvalidateFlowCaches(clearSuggestedFlow: true);
                RaiseTrackCollectionChanged();
            },
            this.WhenAnyValue(x => x.HasTracks));

        LoadPlaylistsCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Unable to load playlists: {ex.Message}";
            })
            .DisposeWith(_disposables);

        LoadSelectedPlaylistCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Unable to load selected playlist: {ex.Message}";
            })
            .DisposeWith(_disposables);

        SuggestNextCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Suggestion failed: {ex.Message}";
            })
            .DisposeWith(_disposables);

        SuggestFlowCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Suggested flow failed: {ex.Message}";
            })
            .DisposeWith(_disposables);

        ApplySuggestedFlowCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Apply suggested flow failed: {ex.Message}";
            })
            .DisposeWith(_disposables);

        // Notify HasTracks/HasNoTracks when the collection changes
        Tracks.CollectionChanged += (_, _) =>
        {
            InvalidateFlowCaches(clearSuggestedFlow: true);
            RaiseTrackCollectionChanged();
        };

        _disposables.Add(ReactiveUI.MessageBus.Current.Listen<InsertBridgeTrackBetweenEvent>()
            .Subscribe(evt => Dispatcher.UIThread.Post(async () => await InsertBridgeTrackBetweenAsync(evt))));

        _ = LoadPlaylistsAsync();
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task LoadPlaylistsAsync()
    {
        IsLoading = true;
        StatusText = "Loading playlists...";
        try
        {
            var jobs = await _library.LoadAllPlaylistJobsAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Playlists.Clear();
                foreach (var j in jobs)
                {
                    Playlists.Add(j);
                }

                if (Playlists.Count > 0)
                {
                    PlaylistJob? selected = null;
                    if (Guid.TryParse(_appConfig.FlowBuilderSelectedPlaylistId, out var persistedId))
                    {
                        selected = Playlists.FirstOrDefault(p => p.Id == persistedId);
                    }

                    if (selected != null)
                    {
                        SelectedPlaylist = selected;
                    }
                    else if (SelectedPlaylist == null || !Playlists.Any(p => p.Id == SelectedPlaylist.Id))
                    {
                        SelectedPlaylist = Playlists[0];
                    }

                    StatusText = $"Loaded {Playlists.Count} playlists.";

                    if (_appConfig.FlowBuilderRestoreContentOnStartup &&
                        selected != null &&
                        Tracks.Count == 0 &&
                        !IsLoading)
                    {
                        _ = LoadSelectedPlaylistAsync();
                    }
                }
                else
                {
                    SelectedPlaylist = null;
                    StatusText = "No playlists found yet.";
                }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Unable to load playlists: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadSelectedPlaylistAsync()
    {
        if (SelectedPlaylist == null) return;

        IsLoading  = true;
        StatusText = $"Loading \"{SelectedPlaylist.SourceTitle}\"\u2026";
        try
        {
            var tracks = await _library.GetPagedPlaylistTracksAsync(
                SelectedPlaylist.Id, skip: 0, take: 500);

            var eligibleTracks = tracks
                .Where(SLSKDONET.ViewModels.Workstation.WorkstationDeckViewModel.IsTrackReadyForWorkstation)
                .ToList();
            var hiddenCount = Math.Max(0, tracks.Count - eligibleTracks.Count);
            var hiddenBreakdown = BuildHiddenEligibilityBreakdown(tracks, eligibleTracks);

            if (eligibleTracks.Count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Tracks.Clear();
                    StatusText = "No ready tracks yet. Download or import tracks, then run Analyze Playlist to enable workstation flow.";
                });
                return;
            }

            // Optimise the order: AI-powered greedy sort by Camelot + BPM + energy
            var hashes = eligibleTracks.Select(t => t.TrackUniqueHash ?? "").Where(h => h.Length > 0).ToList();
            PlaylistOptimizationResult? result = null;
            try
            {
                result = await _optimizer.OptimizeAsync(hashes);
            }
            catch
            {
                // Fall back to original order if optimizer fails (e.g. no audio features yet)
            }

            // Re-order tracks to match optimized hash order (unanalysed tracks appended at end)
            var trackByHash = eligibleTracks.ToDictionary(t => t.TrackUniqueHash ?? "", t => t);
            var orderedTracks = result != null
                ? result.OrderedHashes
                    .Where(h => trackByHash.ContainsKey(h))
                    .Select(h => trackByHash[h])
                    .Concat(eligibleTracks.Where(t => !result.OrderedHashes.Contains(t.TrackUniqueHash ?? "")))
                    .ToList()
                : eligibleTracks;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Clear();
                foreach (var t in orderedTracks)
                    Tracks.Add(BuildCard(t));
                if (result?.UnanalyzedTrackCount > 0)
                {
                    StatusText = hiddenCount > 0
                        ? $"Loaded {Tracks.Count} workstation-ready tracks ({hiddenCount} hidden: {hiddenBreakdown}; {result.UnanalyzedTrackCount} unanalysed appended)"
                        : $"Loaded {Tracks.Count} tracks ({result.UnanalyzedTrackCount} unanalysed, appended at end)";
                }
                else
                {
                    StatusText = hiddenCount > 0
                        ? $"Loaded {Tracks.Count} workstation-ready tracks • {hiddenCount} hidden ({hiddenBreakdown})"
                        : $"Loaded {Tracks.Count} tracks — transitions optimised";
                }

                    InvalidateFlowCaches(clearSuggestedFlow: true);
            });

            await RefreshBridgesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string BuildHiddenEligibilityBreakdown(
        IReadOnlyCollection<PlaylistTrack> allTracks,
        IReadOnlyCollection<PlaylistTrack> eligibleTracks)
    {
        var hiddenTracks = allTracks.Where(track => !eligibleTracks.Contains(track)).ToList();
        if (hiddenTracks.Count == 0)
        {
            return "none";
        }

        var missingDownload = 0;
        var missingFile = 0;
        var missingHash = 0;
        var missingWaveform = 0;
        var missingCues = 0;
        var other = 0;

        foreach (var track in hiddenTracks)
        {
            switch (SLSKDONET.ViewModels.Workstation.WorkstationDeckViewModel.GetTrackEligibilityIssue(track))
            {
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.NotDownloaded:
                    missingDownload++;
                    break;
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingFile:
                    missingFile++;
                    break;
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingHash:
                    missingHash++;
                    break;
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingWaveform:
                    missingWaveform++;
                    break;
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingCues:
                    missingCues++;
                    break;
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingAnalysis:
                case SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.NoTrack:
                    other++;
                    break;
            }
        }

        var segments = new List<string>();
        if (missingDownload > 0) segments.Add($"{missingDownload} not downloaded");
        if (missingFile > 0) segments.Add($"{missingFile} missing file");
        if (missingHash > 0) segments.Add($"{missingHash} missing hash");
        if (missingWaveform > 0) segments.Add($"{missingWaveform} missing waveform");
        if (missingCues > 0) segments.Add($"{missingCues} missing cues");
        if (other > 0) segments.Add($"{other} other");

        return segments.Count == 0 ? "none" : string.Join(", ", segments);
    }

    private async Task SuggestNextAsync()
    {
        if (SelectedPlaylist == null) return;

        IsLoading  = true;
        StatusText = "Finding the next best track…";
        try
        {
            // Load all available tracks in the playlist
            var all = await _library.GetPagedPlaylistTracksAsync(
                SelectedPlaylist.Id, skip: 0, take: 1000);

            var eligibleAll = all
                .Where(SLSKDONET.ViewModels.Workstation.WorkstationDeckViewModel.IsTrackReadyForWorkstation)
                .ToList();

            // Exclude already-queued hashes
            var queued = Tracks.Select(t => t.TrackHash).ToHashSet(StringComparer.Ordinal);
            var candidates = eligibleAll.Where(t => !queued.Contains(t.TrackUniqueHash ?? "")).ToList();

            if (candidates.Count == 0)
            {
                StatusText = "No more tracks to suggest from this playlist.";
                return;
            }

            string? startHash = Tracks.LastOrDefault()?.TrackHash;
            string? nextHash;
            PlaylistRecommendation? recommendation = null;

            if (!string.IsNullOrWhiteSpace(startHash))
            {
                var recommendations = await _playlistIntelligence.SuggestNextAsync(
                    startHash,
                    candidates.Select(c => c.TrackUniqueHash ?? string.Empty),
                    topK: 1);
                recommendation = recommendations.FirstOrDefault();
                nextHash = recommendation?.TrackHash;
            }
            else
            {
                var result = await _optimizer.OptimizeAsync(
                    candidates.Select(c => c.TrackUniqueHash ?? ""),
                    new PlaylistOptimizerOptions());
                nextHash = result.OrderedHashes.FirstOrDefault();
            }

            var nextTrack = nextHash != null
                ? candidates.FirstOrDefault(t => t.TrackUniqueHash == nextHash)
                : candidates.First();

            if (nextTrack == null)
            {
                StatusText = "Suggestion unavailable.";
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Add(BuildCard(nextTrack));
                StatusText = recommendation != null && recommendation.ReasonTags.Count > 0
                    ? $"Added: {nextTrack.Artist} — {nextTrack.Title} • {recommendation.ReasonTags[0]}"
                    : $"Added: {nextTrack.Artist} — {nextTrack.Title}";

                InvalidateFlowCaches(clearSuggestedFlow: true);
            });

            await RefreshBridgesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SuggestFlowAsync()
    {
        if (Tracks.Count < 3)
        {
            StatusText = "Stage at least 3 tracks before asking A10 for a flow proposal.";
            return;
        }

        IsLoading = true;
        StatusText = "A10 is proposing a cleaner flow...";
        try
        {
            var currentHashes = Tracks.Select(track => track.TrackHash)
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .ToList();

            var proposal = await _playlistIntelligence.ReorderAsync(
                currentHashes,
                anchorTrackHash: currentHashes.FirstOrDefault());

            if (proposal.OrderedTrackHashes.Count == 0)
            {
                StatusText = "A10 could not derive a reorder proposal for the current set.";
                SuggestedFlow = null;
                CurrentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;
                return;
            }

            if (proposal.OrderedTrackHashes.SequenceEqual(currentHashes, StringComparer.Ordinal))
            {
                StatusText = "Current flow already aligns with A10's recommended order.";
                SuggestedFlow = null;
                _suggestedFlowStyleSummary = string.Empty;
                CurrentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            CurrentSuggestedFlowImpact = await BuildSuggestedFlowStyleImpactAsync(
                currentHashes,
                proposal.OrderedTrackHashes,
                proposal.AverageTransitionScore).ConfigureAwait(false);
            stopwatch.Stop();
            CurrentSuggestedFlowImpact = CurrentSuggestedFlowImpact with { RefreshElapsed = stopwatch.Elapsed };
            _suggestedFlowStyleSummary = CurrentSuggestedFlowImpact.SummaryText;
            SuggestedFlow = proposal;
            StatusText = $"A10 proposed a flow with {SuggestedFlowScoreDisplay.ToLowerInvariant()}. Review or apply it.";
            await LogSuggestedFlowTelemetryAsync("suggested_flow_shown").ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ApplySuggestedFlowAsync()
    {
        if (!HasSuggestedFlow)
            return;

        IsLoading = true;
        StatusText = "Applying A10 suggested flow...";
        try
        {
            var cardLookup = Tracks.ToDictionary(track => track.TrackHash, StringComparer.Ordinal);
            var reordered = SuggestedFlow!.OrderedTrackHashes
                .Where(cardLookup.ContainsKey)
                .Select(hash => cardLookup[hash])
                .ToList();

            var remaining = Tracks.Where(track => !SuggestedFlow.OrderedTrackHashes.Contains(track.TrackHash, StringComparer.Ordinal));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Clear();
                foreach (var card in reordered.Concat(remaining))
                    Tracks.Add(card);
            });

            var appliedScore = SuggestedFlow.AverageTransitionScore;
            await LogSuggestedFlowTelemetryAsync("suggested_flow_applied").ConfigureAwait(false);
            SuggestedFlow = null;
            _suggestedFlowStyleSummary = string.Empty;
            CurrentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;
            await RefreshBridgesAsync();
            StatusText = $"Applied A10 suggested flow • avg flow {(appliedScore * 100):F0}%";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DismissSuggestedFlow()
    {
        _ = LogSuggestedFlowTelemetryAsync("suggested_flow_dismissed");
        SuggestedFlow = null;
        _suggestedFlowStyleSummary = string.Empty;
        CurrentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;
        StatusText = "Dismissed A10 flow proposal.";
    }

    private async Task ViewSuggestedFlowImpactAsync()
    {
        if (!HasSuggestedFlowImpactPreview)
            return;

        await LogSuggestedFlowTelemetryAsync("suggested_flow_summary_click").ConfigureAwait(false);
        await _dialogService.ShowSuggestedFlowImpactAsync(new SuggestedFlowImpactViewModel(CurrentSuggestedFlowImpact)).ConfigureAwait(false);
    }

    // ── Card factory ──────────────────────────────────────────────────────────

    private FlowTrackCardViewModel BuildCard(PlaylistTrack track)
    {
        var card = new FlowTrackCardViewModel(
            track,
            onMoveLeft:  () => MoveCard(track, -1),
            onMoveRight: () => MoveCard(track, +1),
            onRemove:    () => RemoveCard(track),
            onFindBridgeToNext: currentHash => FindBridgeToNextTrack(currentHash),
            onSelectTransitionInspector: currentHash => OpenTransitionInspector(currentHash));
        return card;
    }

    private void OpenTransitionInspector(string currentTrackHash)
    {
        var currentIndex = Tracks
            .Select((card, idx) => new { card, idx })
            .FirstOrDefault(x => string.Equals(x.card.TrackHash, currentTrackHash, StringComparison.Ordinal))?.idx ?? -1;

        if (currentIndex < 0 || currentIndex >= Tracks.Count - 1)
        {
            StatusText = "No adjacent transition available to inspect.";
            return;
        }

        var currentCard = Tracks[currentIndex];
        var nextCard = Tracks[currentIndex + 1];
        var inspectorVm = new PlaylistTrackViewModel(currentCard.Model);
        inspectorVm.ClearInspectorA10PairwiseContext();

        _activeInspectorTransitionFromHash = currentCard.TrackHash;
        _activeInspectorTransitionToHash = nextCard.TrackHash;

        ReactiveUI.MessageBus.Current.SendMessage(OpenInspectorEvent.Create(inspectorVm, "FlowBuilder.TransitionInspector"));
        _ = TryAttachTransitionInspectorPairwiseContextAsync(inspectorVm, currentCard, nextCard);
    }

    private async Task TryAttachTransitionInspectorPairwiseContextAsync(
        PlaylistTrackViewModel inspectorVm,
        FlowTrackCardViewModel currentCard,
        FlowTrackCardViewModel nextCard)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentCard.TrackHash) || string.IsNullOrWhiteSpace(nextCard.TrackHash))
                return;

            var snapshot = await _trackSimilarityService.BuildSnapshotAsync(
                currentCard.TrackHash,
                nextCard.TrackHash,
                TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

            if (snapshot is null)
                return;

            if (!string.Equals(_activeInspectorTransitionFromHash, currentCard.TrackHash, StringComparison.Ordinal) ||
                !string.Equals(_activeInspectorTransitionToHash, nextCard.TrackHash, StringComparison.Ordinal))
                return;

            var stillAdjacent = Tracks
                .Select((card, idx) => new { card, idx })
                .Where(x => string.Equals(x.card.TrackHash, currentCard.TrackHash, StringComparison.Ordinal))
                .Any(x => x.idx + 1 < Tracks.Count &&
                          string.Equals(Tracks[x.idx + 1].TrackHash, nextCard.TrackHash, StringComparison.Ordinal));
            if (!stillAdjacent)
                return;

            var contextLabel = $"Transition to: {nextCard.Artist} - {nextCard.Title}";
            var reasonTags = string.Join(" • ", snapshot.Result.ReasonTags.Take(2));
            var transitionStyle = _transitionStyleClassifier.Classify(
                snapshot.Left,
                snapshot.Right,
                snapshot.Result,
                snapshot.LeftSections,
                snapshot.RightSections);

            await Dispatcher.UIThread.InvokeAsync(() =>
                inspectorVm.SetInspectorA10PairwiseContext(
                    contextLabel,
                    snapshot.Result.FinalSimilarity,
                    snapshot.Result.VectorScores.Harmonic,
                    snapshot.Result.VectorScores.Rhythm,
                    snapshot.Result.SegmentScores.Drop,
                    reasonTags,
                    transitionStyle.Label,
                    transitionStyle.Reason));
        }
        catch
        {
            // Fail quietly to keep flow transition inspector opening resilient.
        }
    }

    private void MoveCard(PlaylistTrack track, int delta)
    {
        var card = Tracks.FirstOrDefault(c => c.TrackHash == (track.TrackUniqueHash ?? ""));
        if (card == null) return;

        int idx     = Tracks.IndexOf(card);
        int newIdx  = Math.Clamp(idx + delta, 0, Tracks.Count - 1);
        if (newIdx == idx) return;

        Tracks.RemoveAt(idx);
        Tracks.Insert(newIdx, card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        _ = RefreshBridgesAsync();
    }

    private void RemoveCard(PlaylistTrack track)
    {
        var card = Tracks.FirstOrDefault(c => c.TrackHash == (track.TrackUniqueHash ?? ""));
        if (card == null) return;
        Tracks.Remove(card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        _ = RefreshBridgesAsync();
    }

    private void FindBridgeToNextTrack(string currentTrackHash)
    {
        var currentIndex = Tracks
            .Select((card, idx) => new { card, idx })
            .FirstOrDefault(x => x.card.TrackHash == currentTrackHash)?.idx ?? -1;

        if (currentIndex < 0 || currentIndex >= Tracks.Count - 1)
        {
            StatusText = "No next track available.";
            return;
        }

        var currentCard = Tracks[currentIndex];
        var nextCard = Tracks[currentIndex + 1];

        ReactiveUI.MessageBus.Current.SendMessage(
            new FindBridgeBetweenTracksEvent(
                currentTrackHash,
                nextCard.TrackHash,
                $"{currentCard.Artist} - {currentCard.Title}",
                $"{nextCard.Artist} - {nextCard.Title}"));
    }

    private async Task InsertBridgeTrackBetweenAsync(InsertBridgeTrackBetweenEvent evt)
    {
        if (evt.BridgeTrack == null || string.IsNullOrWhiteSpace(evt.BridgeTrack.TrackUniqueHash))
            return;

        var currentHashes = Tracks.Select(t => t.TrackHash).ToList();
        var fromIndexBeforeInsert = currentHashes
            .Select((hash, idx) => new { hash, idx })
            .FirstOrDefault(x => string.Equals(x.hash, evt.FromTrackHash, StringComparison.Ordinal))?.idx ?? -1;
        var toIndexBeforeInsert = currentHashes
            .Select((hash, idx) => new { hash, idx })
            .FirstOrDefault(x => string.Equals(x.hash, evt.ToTrackHash, StringComparison.Ordinal))?.idx ?? -1;
        var insertIndex = DetermineBridgeInsertIndex(
            currentHashes,
            evt.FromTrackHash,
            evt.ToTrackHash,
            evt.BridgeTrack.TrackUniqueHash);

        if (insertIndex == -1)
        {
            StatusText = "Bridge insertion skipped: target pair is not in current flow.";
            return;
        }

        if (insertIndex == -2)
        {
            StatusText = "Bridge track is already in the current flow.";
            return;
        }

        var card = BuildCard(evt.BridgeTrack);
        Tracks.Insert(insertIndex, card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        await RefreshBridgesAsync();

        var placementHint = BuildPlacementHint(fromIndexBeforeInsert, toIndexBeforeInsert, insertIndex);
        StatusText = $"Inserted bridge: {evt.BridgeTrack.Artist} — {evt.BridgeTrack.Title} ({placementHint})";
    }

    private static string BuildPlacementHint(int fromIndex, int toIndex, int insertIndex)
    {
        if (fromIndex >= 0 && toIndex == fromIndex + 1 && insertIndex == toIndex)
            return "between selected pair";

        if (fromIndex >= 0 && toIndex > fromIndex)
            return "before target track";

        if (fromIndex >= 0)
            return "after source track";

        if (toIndex >= 0)
            return "at target position";

        return "at computed position";
    }

    /// <summary>
    /// Determines where a bridge track should be inserted.
    /// Returns:
    ///   >= 0: insertion index
    ///   -1 : neither from/to track exists in current flow
    ///   -2 : bridge track already exists in current flow
    /// </summary>
    public static int DetermineBridgeInsertIndex(
        IReadOnlyList<string> currentTrackHashes,
        string fromTrackHash,
        string toTrackHash,
        string bridgeTrackHash)
    {
        if (currentTrackHashes.Any(h => string.Equals(h, bridgeTrackHash, StringComparison.Ordinal)))
            return -2;

        var fromIndex = currentTrackHashes
            .Select((hash, idx) => new { hash, idx })
            .FirstOrDefault(x => string.Equals(x.hash, fromTrackHash, StringComparison.Ordinal))?.idx ?? -1;

        var toIndex = currentTrackHashes
            .Select((hash, idx) => new { hash, idx })
            .FirstOrDefault(x => string.Equals(x.hash, toTrackHash, StringComparison.Ordinal))?.idx ?? -1;

        if (fromIndex < 0 && toIndex < 0)
            return -1;

        if (fromIndex >= 0 && toIndex > fromIndex)
            return toIndex;

        if (fromIndex >= 0)
            return fromIndex + 1;

        return Math.Max(0, toIndex);
    }

    // ── Bridge computation ────────────────────────────────────────────────────

    private async Task RefreshBridgesAsync()
    {
        var orderedHashes = Tracks.Select(track => track.TrackHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToList();

        if (_sectionVectors != null)
        {
            await _sectionVectors.PreloadAsync(orderedHashes);
        }

        var cacheKey = BuildTransitionCacheKey(orderedHashes);
        IReadOnlyDictionary<(string FromHash, string ToHash), PlaylistRecommendation> transitionRecommendations;
        if (_transitionCache != null && string.Equals(_transitionCacheKey, cacheKey, StringComparison.Ordinal))
        {
            transitionRecommendations = _transitionCache;
        }
        else
        {
            transitionRecommendations = await _playlistIntelligence.ScorePathTransitionsAsync(orderedHashes);
            _transitionCacheKey = cacheKey;
            _transitionCache = transitionRecommendations;
        }

        for (int i = 0; i < Tracks.Count; i++)
        {
            var next = i < Tracks.Count - 1 ? Tracks[i + 1] : null;
            if (next == null)
            {
                Tracks[i].SetBridgeTo(null);
                continue;
            }

            double? sectionBlend = _sectionVectors != null
                ? _sectionVectors.TransitionScoreCached(Tracks[i].TrackHash, next.TrackHash)
                : null;
            double? doubleDropBlend = _sectionVectors != null
                ? _sectionVectors.DropSimilarityCached(Tracks[i].TrackHash, next.TrackHash)
                : null;

            var currentHash = Tracks[i].TrackHash;
            transitionRecommendations.TryGetValue((currentHash, next.TrackHash), out var recommendation);
            TransitionStyleResult? transitionStyle = null;
            var snapshot = await _trackSimilarityService.BuildSnapshotAsync(
                currentHash,
                next.TrackHash,
                TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);
            if (snapshot is not null)
            {
                transitionStyle = _transitionStyleClassifier.Classify(
                    snapshot.Left,
                    snapshot.Right,
                    snapshot.Result,
                    snapshot.LeftSections,
                    snapshot.RightSections);
            }

            Tracks[i].SetBridgeTo(next, sectionBlend, doubleDropBlend, recommendation, transitionStyle);
        }

        ApplyTransitionStyleFilter();
        this.RaisePropertyChanged(nameof(TransitionFilterSummary));
    }

    private void RaiseTrackCollectionChanged()
    {
        this.RaisePropertyChanged(nameof(HasTracks));
        this.RaisePropertyChanged(nameof(HasNoTracks));
        this.RaisePropertyChanged(nameof(TransitionFilterSummary));
    }

    private void ApplyTransitionStyleFilter()
    {
        foreach (var track in Tracks)
        {
            var bridgeStyle = track.Bridge?.TransitionStyle;
            track.IsBridgeVisibleByFilter = BridgeMatchesTransitionStyleFilter(bridgeStyle, SelectedTransitionStyleFilter);
        }
    }

    public static bool BridgeMatchesTransitionStyleFilter(TransitionStyle? style, string? selectedFilter)
    {
        if (string.IsNullOrWhiteSpace(selectedFilter) ||
            string.Equals(selectedFilter, "All styles", StringComparison.Ordinal) ||
            style is null)
            return true;

        return selectedFilter switch
        {
            "Smooth Blend" => style == TransitionStyle.SmoothBlend,
            "Energy Lift" => style == TransitionStyle.EnergyLift,
            "Drop Swap" => style == TransitionStyle.DropSwap,
            "Breakdown Reset" => style == TransitionStyle.BreakdownReset,
            "Tension Bridge" => style == TransitionStyle.TensionBridge,
            "Risky Clash" => style == TransitionStyle.RiskyClash,
            _ => true,
        };
    }

    private void InvalidateFlowCaches(bool clearSuggestedFlow)
    {
        _transitionCacheKey = null;
        _transitionCache = null;
        if (clearSuggestedFlow)
        {
            SuggestedFlow = null;
            _suggestedFlowStyleSummary = string.Empty;
            CurrentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;
        }
    }

    private async Task<SuggestedFlowStyleImpact> BuildSuggestedFlowStyleImpactAsync(
        IReadOnlyList<string> currentHashes,
        IReadOnlyList<string> proposedHashes,
        double averageTransitionScore)
    {
        var currentTransitions = await BuildTransitionStylesAsync(currentHashes).ConfigureAwait(false);
        var proposedTransitions = await BuildTransitionStylesAsync(proposedHashes).ConfigureAwait(false);
        return ComputeSuggestedFlowStyleImpact(currentTransitions, proposedTransitions, averageTransitionScore);
    }

    private async Task<IReadOnlyList<TransitionStyleEvaluation>> BuildTransitionStylesAsync(IReadOnlyList<string> orderedTrackHashes)
    {
        var evaluations = new List<TransitionStyleEvaluation>();

        for (var index = 0; index < orderedTrackHashes.Count - 1; index++)
        {
            var fromHash = orderedTrackHashes[index];
            var toHash = orderedTrackHashes[index + 1];
            if (string.IsNullOrWhiteSpace(fromHash) || string.IsNullOrWhiteSpace(toHash))
                continue;

            var snapshot = await _trackSimilarityService.BuildSnapshotAsync(
                fromHash,
                toHash,
                TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);
            if (snapshot is null)
                continue;

            var style = _transitionStyleClassifier.Classify(
                snapshot.Left,
                snapshot.Right,
                snapshot.Result,
                snapshot.LeftSections,
                snapshot.RightSections);
            evaluations.Add(new TransitionStyleEvaluation(
                index,
                fromHash,
                toHash,
                style.Style,
                style.Label,
                style.Reason));
        }

        return evaluations;
    }

    public static SuggestedFlowStyleImpact ComputeSuggestedFlowStyleImpact(
        IReadOnlyList<TransitionStyleEvaluation> currentTransitions,
        IReadOnlyList<TransitionStyleEvaluation> proposedTransitions,
        double? averageTransitionScore = null,
        TimeSpan? refreshElapsed = null)
    {
        var currentCounts = CountTransitionStyles(currentTransitions);
        var proposedCounts = CountTransitionStyles(proposedTransitions);
        var deltaCounts = GetOrderedTransitionStyles().ToDictionary(
            style => style,
            style => (proposedCounts.TryGetValue(style, out var proposed) ? proposed : 0) -
                     (currentCounts.TryGetValue(style, out var current) ? current : 0));

        var currentLookup = currentTransitions.ToDictionary(
            transition => (transition.FromTrackHash, transition.ToTrackHash),
            transition => transition);

        var affectedTransitions = proposedTransitions
            .Where(transition => !currentLookup.TryGetValue((transition.FromTrackHash, transition.ToTrackHash), out var current)
                || current.Style != transition.Style)
            .Select(transition => new SuggestedFlowAffectedTransition(
                transition.EdgeIndex,
                transition.FromTrackHash,
                transition.ToTrackHash,
                currentLookup.TryGetValue((transition.FromTrackHash, transition.ToTrackHash), out var current)
                    ? current.Style
                    : null,
                transition.Style,
                transition.StyleLabel,
                transition.Reason))
            .Take(6)
            .ToList();

        return new SuggestedFlowStyleImpact(
            currentCounts,
            proposedCounts,
            deltaCounts,
            affectedTransitions,
            SummarizeTransitionStyleDelta(currentCounts, proposedCounts),
            averageTransitionScore,
            refreshElapsed);
    }

    public static string SummarizeTransitionStyleDelta(
        IReadOnlyDictionary<TransitionStyle, int> currentCounts,
        IReadOnlyDictionary<TransitionStyle, int> proposedCounts)
    {
        var parts = new List<string>();
        foreach (var style in GetOrderedTransitionStyles())
        {
            var current = currentCounts.TryGetValue(style, out var currentValue) ? currentValue : 0;
            var proposed = proposedCounts.TryGetValue(style, out var proposedValue) ? proposedValue : 0;
            var delta = proposed - current;
            if (delta == 0)
                continue;

            parts.Add($"{FormatSignedDelta(delta)} {ToSummaryLabel(style, Math.Abs(delta))}");
            if (parts.Count == 3)
                break;
        }

        return parts.Count == 0
            ? "A10 keeps the same transition-style mix while improving overall flow."
            : string.Join("  ·  ", parts);
    }

    private static string FormatSignedDelta(int delta)
        => delta > 0 ? $"+{delta}" : delta.ToString();

    private static string ToSummaryLabel(TransitionStyle style, int magnitude)
    {
        var label = style switch
        {
            TransitionStyle.SmoothBlend => "smooth blend",
            TransitionStyle.EnergyLift => "energy lift",
            TransitionStyle.DropSwap => "drop swap",
            TransitionStyle.BreakdownReset => "breakdown reset",
            TransitionStyle.TensionBridge => "tension bridge",
            TransitionStyle.RiskyClash => "risky clash",
            _ => "transition style",
        };

        if (magnitude == 1)
            return label;

        return style switch
        {
            TransitionStyle.RiskyClash => "risky clashes",
            _ => label + "s",
        };
    }

    public static IReadOnlyList<TransitionStyle> GetOrderedTransitionStyles()
        =>
        [
            TransitionStyle.SmoothBlend,
            TransitionStyle.RiskyClash,
            TransitionStyle.EnergyLift,
            TransitionStyle.DropSwap,
            TransitionStyle.BreakdownReset,
            TransitionStyle.TensionBridge,
        ];

    public static string GetTransitionStyleDisplayName(TransitionStyle style)
        => style switch
        {
            TransitionStyle.SmoothBlend => "Smooth Blend",
            TransitionStyle.EnergyLift => "Energy Lift",
            TransitionStyle.DropSwap => "Drop Swap",
            TransitionStyle.BreakdownReset => "Breakdown Reset",
            TransitionStyle.TensionBridge => "Tension Bridge",
            TransitionStyle.RiskyClash => "Risky Clash",
            _ => style.ToString(),
        };

    private static Dictionary<TransitionStyle, int> CountTransitionStyles(IReadOnlyList<TransitionStyleEvaluation> transitions)
    {
        var counts = GetOrderedTransitionStyles().ToDictionary(style => style, _ => 0);
        foreach (var transition in transitions)
            counts[transition.Style]++;

        return counts;
    }

    private string GetFlowBuilderPreviewInstallKey()
        => !string.IsNullOrWhiteSpace(_appConfig.Username)
            ? _appConfig.Username!
            : Environment.MachineName;

    private async Task LogSuggestedFlowTelemetryAsync(string action)
    {
        if (!_appConfig.EnableFlowBuilderSuggestedFlowTelemetry ||
            !HasSuggestedFlow ||
            string.IsNullOrWhiteSpace(CurrentSuggestedFlowImpact.SummaryText))
            return;

        await _telemetryService.LogSuggestedFlowAsync(
            action,
            SelectedPlaylist?.Id,
            Tracks.Count,
            CurrentSuggestedFlowImpact,
            SuggestedFlow?.AverageTransitionScore ?? 0).ConfigureAwait(false);
    }

    public sealed record TransitionStyleEvaluation(
        int EdgeIndex,
        string FromTrackHash,
        string ToTrackHash,
        TransitionStyle Style,
        string StyleLabel,
        string Reason);

    private static string BuildTransitionCacheKey(IReadOnlyList<string> orderedHashes)
        => string.Join("|", orderedHashes);

    public void Dispose() => _disposables.Dispose();
}
