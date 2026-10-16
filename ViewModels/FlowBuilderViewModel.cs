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
/// Powers Flow Builder — a transition designer for a playlist/set.
///
/// Workflow:
///   1. Pick a playlist (<see cref="PickedPlaylist"/>): its downloaded tracks load into
///      <see cref="Tracks"/> in the playlist's own order, so saved transitions line up with playback.
///   2. Every adjacent pair shows a compatibility score; selecting a pair opens it in the
///      transition editor (<see cref="MixTransitionVm"/>): mix-out/mix-in cues, length, blend
///      preset, preview, save. Saved transitions are what auto-mix playback uses for that pair.
///   3. Reorder (drag, move up/down), remove, insert bridge tracks, or let A10 propose an order;
///      <see cref="SaveOrderToPlaylistCommand"/> writes the order back to the playlist.
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
    private readonly IEventBus _eventBus;
    private readonly FlowBuilderSuggestionTelemetryService _telemetryService;
    private readonly MixTransitionViewModel _mixTransitionVm;
    private readonly SLSKDONET.Services.Audio.ILibraryPreviewPlayer? _libraryPreviewPlayer;
    private readonly SLSKDONET.Services.Repositories.ITransitionRepository? _transitionRepository;
    private FlowTrackCardViewModel? _activePreviewTrackCard;
    private string? _transitionCacheKey;
    private IReadOnlyDictionary<(string FromHash, string ToHash), PlaylistRecommendation>? _transitionCache;
    private IReadOnlyList<Guid> _persistedOrder = Array.Empty<Guid>();
    private SuggestedFlowStyleImpact _currentSuggestedFlowImpact = SuggestedFlowStyleImpact.Empty;

    // ── Playlist selector ─────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    public bool HasEnoughPlaylistsToCombine => Playlists.Count >= 2;

    private PlaylistJob? _selectedPlaylist;
    public PlaylistJob? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPlaylist, value);
            this.RaisePropertyChanged(nameof(PickedPlaylist));
            this.RaisePropertyChanged(nameof(HeaderSubtitle));
            _appConfig.FlowBuilderSelectedPlaylistId = value?.Id.ToString();
            _ = _configManager.SaveAsync(_appConfig);
        }
    }

    /// <summary>The header playlist picker. Picking a playlist loads it (no separate Load click);
    /// <see cref="SelectedPlaylist"/> stays the programmatic API (MainViewModel sets it and then
    /// runs <see cref="LoadSelectedPlaylistCommand"/> itself).</summary>
    public PlaylistJob? PickedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedPlaylist)) return;
            SelectedPlaylist = value;
            this.RaisePropertyChanged();
            LoadSelectedPlaylistCommand.Execute().Subscribe(_ => { }, ex => StatusText = $"Unable to load playlist: {ex.Message}");
        }
    }

    public bool HasNoPlaylists => Playlists.Count == 0 && !IsLoading;

    // ── Set timeline ──────────────────────────────────────────────────────────

    public ObservableCollection<FlowTrackCardViewModel> Tracks { get; } = new();

    // ── Transition editor (bottom slide-out) ────────────────────────────────────

    /// <summary>The full-option Mix transition editor, docked into Flow Builder — same
    /// ViewModel/persistence the compact CONTEXT-sidepanel "Mix" tab uses (see
    /// SidebarViewModel.MixTransitionVm), just given a roomier host here.</summary>
    public MixTransitionViewModel MixTransitionVm => _mixTransitionVm;

    private bool _isTransitionEditorOpen;
    public bool IsTransitionEditorOpen
    {
        get => _isTransitionEditorOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTransitionEditorOpen, value);
            this.RaisePropertyChanged(nameof(ShowTrackList));
            this.RaisePropertyChanged(nameof(ShowPickTransitionHint));
        }
    }

    private FlowBridgeInfo? _activeBridge;
    /// <summary>Compatibility of the pair open in the editor (score bar + sub-scores in its header).</summary>
    public FlowBridgeInfo? ActiveBridge { get => _activeBridge; private set => this.RaiseAndSetIfChanged(ref _activeBridge, value); }

    private string _activePairTitle = string.Empty;
    /// <summary>"A title → B title" for the editor header.</summary>
    public string ActivePairTitle { get => _activePairTitle; private set => this.RaiseAndSetIfChanged(ref _activePairTitle, value); }

    // ── UI state ──────────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(IsNotLoading));
            this.RaisePropertyChanged(nameof(HasNoPlaylists));
        }
    }

    public bool IsNotLoading => !_isLoading;

    public bool HasTracks    => Tracks.Count > 0;
    public bool HasNoTracks  => Tracks.Count == 0;

    /// <summary>The set list is always shown next to the editor now.</summary>
    public bool ShowTrackList => HasTracks;

    /// <summary>Exactly one track: nothing to transition between yet.</summary>
    public bool HasSingleTrack => Tracks.Count == 1;

    /// <summary>Two or more tracks but no pair selected: the editor shows "select a transition".</summary>
    public bool ShowPickTransitionHint => Tracks.Count >= 2 && !IsTransitionEditorOpen;

    public int TransitionCount => Math.Max(0, Tracks.Count - 1);
    public int SavedTransitionCount => Tracks.Take(Math.Max(0, Tracks.Count - 1)).Count(t => t.HasSavedTransition);

    /// <summary>The on-screen order differs from the playlist's stored order.</summary>
    public bool IsOrderDirty => !Tracks.Select(t => t.Model.Id).SequenceEqual(_persistedOrder);

    /// <summary>"Warm-up · 12 tracks · 3 of 11 transitions saved".</summary>
    public string HeaderSubtitle
    {
        get
        {
            if (Tracks.Count == 0)
                return SelectedPlaylist == null ? "Design the transitions of a set, pair by pair." : SelectedPlaylist.SourceTitle;
            var parts = new List<string>();
            if (SelectedPlaylist != null) parts.Add(SelectedPlaylist.SourceTitle);
            parts.Add($"{Tracks.Count} tracks");
            if (TransitionCount > 0) parts.Add($"{SavedTransitionCount} of {TransitionCount} transitions saved");
            if (IsOrderDirty) parts.Add("order not saved");
            return string.Join(" · ", parts);
        }
    }

    // ── Transition navigation (editor header ◀ ▶ and the set mini strip) ─────────────────

    private int _activeTransitionIndex = -1;
    /// <summary>Index of the OUTGOING card of the pair open in the transition editor, or -1.</summary>
    public int ActiveTransitionIndex
    {
        get => _activeTransitionIndex;
        private set
        {
            this.RaiseAndSetIfChanged(ref _activeTransitionIndex, value);
            RaiseTransitionNavigationChanged();
        }
    }

    public bool HasPreviousTransition => _activeTransitionIndex > 0;
    public bool HasNextTransition => _activeTransitionIndex >= 0 && _activeTransitionIndex < Tracks.Count - 2;

    /// <summary>"Transition 3 of 11" — where the open pair sits in the set.</summary>
    public string ActiveTransitionPositionText =>
        _activeTransitionIndex >= 0 && Tracks.Count > 1
            ? $"Transition {_activeTransitionIndex + 1} of {Tracks.Count - 1}"
            : string.Empty;

    private void RaiseTransitionNavigationChanged()
    {
        this.RaisePropertyChanged(nameof(HasPreviousTransition));
        this.RaisePropertyChanged(nameof(HasNextTransition));
        this.RaisePropertyChanged(nameof(ActiveTransitionPositionText));
        for (int i = 0; i < Tracks.Count; i++)
            Tracks[i].IsActiveTransition = IsTransitionEditorOpen && i == _activeTransitionIndex;
    }

    public ReactiveCommand<Unit, Unit> PreviousTransitionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NextTransitionCommand { get; private set; } = null!;

    public IReadOnlyList<EnergyCurveOption> EnergyCurveOptions { get; } =
    [
        new("None",   EnergyCurvePattern.None,   "Pure harmonic/BPM flow — no energy shaping"),
        new("Rising", EnergyCurvePattern.Rising,  "Build from low to high energy"),
        new("Wave",   EnergyCurvePattern.Wave,    "Low → peak in middle → low again"),
        new("Peak",   EnergyCurvePattern.Peak,    "Steady → spike at 2/3 → steady"),
    ];

    private EnergyCurveOption _selectedEnergyCurveOption;
    public EnergyCurveOption SelectedEnergyCurveOption
    {
        get => _selectedEnergyCurveOption;
        set => this.RaiseAndSetIfChanged(ref _selectedEnergyCurveOption, value);
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

    // Previously also gated behind a silent per-install rollout flag
    // (AppConfig.FlowBuilderSuggestedFlowPreviewRolloutPercent) — the full impact computation
    // ran unconditionally on every "Suggest Flow" click regardless of the flag, so it only ever
    // wasted the already-computed work for ~90% of installs with zero in-app indication that
    // it was a rollout rather than a bug. This is a single-user desktop app, not a service
    // running a real experiment, so removed the gate entirely.
    public bool HasSuggestedFlowImpactPreview => HasSuggestedFlow
        && !string.IsNullOrWhiteSpace(CurrentSuggestedFlowImpact.SummaryText);

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
    public ReactiveCommand<Unit, Unit> CombinePlaylistsCommand     { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand                { get; }
    public ReactiveCommand<Unit, Unit> SaveOrderToPlaylistCommand  { get; }
    public ReactiveCommand<Unit, Unit> CloseTransitionEditorCommand { get; }
    /// <summary>"None" / "Rising" / "Wave" / "Peak" — Suggest flow order with that energy arc.</summary>
    public ReactiveCommand<string, Unit> SuggestFlowWithCurveCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenFirstTransitionCommand { get; }
    public ReactiveCommand<Unit, Unit> FindBridgeForActiveTransitionCommand { get; }

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
        IEventBus eventBus,
        FlowBuilderSuggestionTelemetryService telemetryService,
        MixTransitionViewModel mixTransitionVm,
        SLSKDONET.Services.Similarity.SectionVectorService? sectionVectors = null,
        SLSKDONET.Services.Audio.ILibraryPreviewPlayer? libraryPreviewPlayer = null,
        SLSKDONET.Services.Repositories.ITransitionRepository? transitionRepository = null)
    {
        _mixTransitionVm = mixTransitionVm;
        _mixTransitionVm.Closed += OnMixEditorClosed;
        _mixTransitionVm.SavedStateChanged += OnMixSavedStateChanged;
        _disposables.Add(System.Reactive.Disposables.Disposable.Create(() =>
        {
            _mixTransitionVm.Closed -= OnMixEditorClosed;
            _mixTransitionVm.SavedStateChanged -= OnMixSavedStateChanged;
        }));
        _libraryPreviewPlayer = libraryPreviewPlayer;
        _transitionRepository = transitionRepository;
        if (_libraryPreviewPlayer != null)
        {
            _libraryPreviewPlayer.PreviewStopped += OnTrackPreviewStopped;
            _disposables.Add(System.Reactive.Disposables.Disposable.Create(
                () => _libraryPreviewPlayer.PreviewStopped -= OnTrackPreviewStopped));
        }
        _library   = library;
        _optimizer = optimizer;
        _playlistIntelligence = playlistIntelligence;
        _trackSimilarityService = trackSimilarityService;
        _transitionStyleClassifier = transitionStyleClassifier;
        _appConfig = appConfig;
        _configManager = configManager;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _telemetryService = telemetryService;
        _sectionVectors = sectionVectors;
        _selectedEnergyCurveOption = EnergyCurveOptions[0];

        LoadPlaylistsCommand = ReactiveCommand.CreateFromTask(
            LoadPlaylistsAsync,
            this.WhenAnyValue(x => x.IsLoading, loading => !loading));
        CombinePlaylistsCommand = ReactiveCommand.CreateFromTask(
            () => CombinePlaylistsAsync(),
            this.WhenAnyValue(x => x.IsLoading, x => x.HasEnoughPlaylistsToCombine,
                (loading, canCombine) => !loading && canCombine));

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

        ClearCommand = ReactiveCommand.CreateFromTask(
            ClearAsync,
            this.WhenAnyValue(x => x.HasTracks));

        SaveOrderToPlaylistCommand = ReactiveCommand.CreateFromTask(
            SaveOrderToPlaylistAsync,
            this.WhenAnyValue(x => x.SelectedPlaylist, x => x.IsLoading, x => x.HasTracks,
                (pl, loading, hasTracks) => pl != null && !loading && hasTracks));

        SaveOrderToPlaylistCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Save order failed: {ex.Message}";
            })
            .DisposeWith(_disposables);

        LoadPlaylistsCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Unable to load playlists: {ex.Message}";
            })
            .DisposeWith(_disposables);

        CombinePlaylistsCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Unable to combine playlists: {ex.Message}";
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

        Playlists.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasEnoughPlaylistsToCombine));
            this.RaisePropertyChanged(nameof(HasNoPlaylists));
        };

        _disposables.Add(ReactiveUI.MessageBus.Current.Listen<InsertBridgeTrackBetweenEvent>()
            .Subscribe(evt => Dispatcher.UIThread.Post(async () => await InsertBridgeTrackBetweenAsync(evt))));

        // The transition editor's BPM save edits its OWN freshly-constructed PlaylistTrackViewModel
        // (see MixTransitionViewModel.LoadPairAsync), a separate instance from whatever card here
        // wraps the same track — this is what keeps the on-screen card's BPM (and the bridge's BPM
        // delta, which reads it) in sync without a full playlist reload. The event only carries the
        // hash, not the new value, so re-fetch it — generic enough to also pick up a BPM edit made
        // from anywhere else that publishes this same event, not just this one new code path.
        _disposables.Add(_eventBus.GetEvent<TrackMetadataUpdatedEvent>()
            .Subscribe(evt => Dispatcher.UIThread.Post(async () => await OnTrackMetadataUpdatedAsync(evt.TrackGlobalId))));

        CloseTransitionEditorCommand = ReactiveCommand.CreateFromTask(CloseTransitionEditorAsync);
        PreviousTransitionCommand = ReactiveCommand.Create(
            () => { if (HasPreviousTransition) OpenTransition(Tracks[_activeTransitionIndex - 1]); },
            this.WhenAnyValue(x => x.HasPreviousTransition));
        NextTransitionCommand = ReactiveCommand.Create(
            () => { if (HasNextTransition) OpenTransition(Tracks[_activeTransitionIndex + 1]); },
            this.WhenAnyValue(x => x.HasNextTransition));
        OpenFirstTransitionCommand = ReactiveCommand.Create(
            () => { if (Tracks.Count >= 2) OpenTransition(Tracks[0]); },
            this.WhenAnyValue(x => x.HasTracks));
        FindBridgeForActiveTransitionCommand = ReactiveCommand.Create(
            () => { if (_activeTransitionIndex >= 0 && _activeTransitionIndex < Tracks.Count - 1) FindBridgeToNextTrack(Tracks[_activeTransitionIndex]); },
            this.WhenAnyValue(x => x.IsTransitionEditorOpen));
        SuggestFlowWithCurveCommand = ReactiveCommand.CreateFromTask<string>(async pattern =>
        {
            SelectedEnergyCurveOption = EnergyCurveOptions.FirstOrDefault(o => o.Label == pattern) ?? EnergyCurveOptions[0];
            await SuggestFlowAsync();
        }, this.WhenAnyValue(x => x.IsLoading, x => x.HasTracks, (loading, hasTracks) => !loading && hasTracks));
        SuggestFlowWithCurveCommand.ThrownExceptions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ex =>
            {
                IsLoading = false;
                StatusText = $"Suggested flow failed: {ex.Message}";
            })
            .DisposeWith(_disposables);
        // Keep the active pair, positions and the header summary right when cards are added,
        // removed or reordered. A reorder breaks the open pair, so the editor follows the
        // outgoing track if it still has a next track, else closes.
        Tracks.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Tracks.Count; i++) Tracks[i].Position = i + 1;
            var followed = IsTransitionEditorOpen ? FindPairIndex(_activeOutgoingCard, _activeIncomingCard) : -1;
            if (followed < 0) { IsTransitionEditorOpen = false; ActiveTransitionIndex = -1; }
            else if (followed != _activeTransitionIndex) ActiveTransitionIndex = followed;
            else RaiseTransitionNavigationChanged();
        };

        _ = LoadPlaylistsAsync();
    }

    private FlowTrackCardViewModel? _activeOutgoingCard, _activeIncomingCard;

    /// <summary>Index of <paramref name="outgoing"/> when it is still directly followed by
    /// <paramref name="incoming"/>, else -1.</summary>
    private int FindPairIndex(FlowTrackCardViewModel? outgoing, FlowTrackCardViewModel? incoming)
    {
        if (outgoing == null || incoming == null) return -1;
        var idx = Tracks.IndexOf(outgoing);
        return idx >= 0 && idx < Tracks.Count - 1 && ReferenceEquals(Tracks[idx + 1], incoming) ? idx : -1;
    }

    private void OnMixEditorClosed(object? sender, EventArgs e)
    {
        IsTransitionEditorOpen = false;
        ActiveTransitionIndex = -1;
    }

    private void OnMixSavedStateChanged(object? sender, EventArgs e)
    {
        if (_activeTransitionIndex >= 0 && _activeTransitionIndex < Tracks.Count)
            Tracks[_activeTransitionIndex].HasSavedTransition = _mixTransitionVm.HasSavedTransition;
        RaiseHeaderChanged();
    }

    private void RaiseHeaderChanged()
    {
        this.RaisePropertyChanged(nameof(SavedTransitionCount));
        this.RaisePropertyChanged(nameof(TransitionCount));
        this.RaisePropertyChanged(nameof(IsOrderDirty));
        this.RaisePropertyChanged(nameof(HeaderSubtitle));
    }

    /// <summary>Marks each pair whose transition is saved, from the playlist's stored transitions.</summary>
    private async Task RefreshSavedTransitionsAsync()
    {
        if (_transitionRepository == null || SelectedPlaylist == null) return;
        HashSet<(Guid, Guid)> saved;
        try
        {
            saved = (await _transitionRepository.GetTransitionsForPlaylistAsync(SelectedPlaylist.Id))
                .Select(t => (t.OutgoingPlaylistTrackId, t.IncomingPlaylistTrackId))
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[FlowBuilder] Couldn't read saved transitions for {Playlist}", SelectedPlaylist.Id);
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            for (int i = 0; i < Tracks.Count; i++)
                Tracks[i].HasSavedTransition = i < Tracks.Count - 1 && saved.Contains((Tracks[i].Model.Id, Tracks[i + 1].Model.Id));
            RaiseHeaderChanged();
        });
    }

    /// <summary>Why the pair can't be saved yet, or null. Saved transitions are keyed by the
    /// playlist's own rows, so both tracks must already be in the selected playlist.</summary>
    internal static string? GetSaveBlockedReason(Guid? playlistId, PlaylistTrack outgoing, PlaylistTrack incoming)
    {
        if (playlistId is not Guid id || id == Guid.Empty) return "Pick a playlist to save transitions.";
        var foreign = new[] { outgoing, incoming }.FirstOrDefault(t => t.PlaylistId != id);
        return foreign == null ? null : $"Save the order first: \"{foreign.Title}\" isn't in this playlist yet.";
    }

    private async Task OnTrackMetadataUpdatedAsync(string trackHash)
    {
        var index = -1;
        for (var i = 0; i < Tracks.Count; i++)
        {
            if (string.Equals(Tracks[i].TrackHash, trackHash, StringComparison.Ordinal)) { index = i; break; }
        }
        if (index < 0) return;

        var entry = await _library.FindLibraryEntryAsync(trackHash);
        if (entry == null) return;

        Tracks[index].UpdateBpm(entry.BPM);
        // BPM feeds the bridge's delta text on both sides — the bridge FROM this card (if any)
        // and the bridge INTO it from the previous card (if any) both need recomputing. The
        // no-optional-args overload is a cheap, fully synchronous recalculation (BpmDisplay/
        // KeyDisplay/EnergyCurvePoints only) — it just won't have the async A10/style extras the
        // full RefreshBridgesAsync pass fetches, until that next runs.
        if (index + 1 < Tracks.Count) Tracks[index].SetBridgeTo(Tracks[index + 1]);
        if (index > 0) Tracks[index - 1].SetBridgeTo(Tracks[index]);
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

            // Playlist order, not an optimizer re-sort: transitions are saved per adjacent pair and
            // playback follows the playlist order, so what you design here must be that order.
            // Reordering is explicit (drag, Suggest flow order) and saved with Save order.
            var playable = tracks.Where(IsPlayable).ToList();
            var hiddenCount = tracks.Count - playable.Count;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTransitionEditorOpen = false;
                ActiveTransitionIndex = -1;
                Tracks.Clear();
                foreach (var t in playable)
                    Tracks.Add(BuildCard(t));
                _persistedOrder = Tracks.Select(t => t.Model.Id).ToList();

                var needsAnalysis = Tracks.Count(t => t.NeedsAnalysis);
                var notes = new List<string>();
                if (hiddenCount > 0) notes.Add($"{hiddenCount} not downloaded (hidden)");
                if (needsAnalysis > 0) notes.Add($"{needsAnalysis} need analysis for cues");
                StatusText = Tracks.Count == 0
                    ? "No downloaded tracks in this playlist yet."
                    : notes.Count > 0 ? $"Loaded {Tracks.Count} tracks · {string.Join(" · ", notes)}" : $"Loaded {Tracks.Count} tracks";

                InvalidateFlowCaches(clearSuggestedFlow: true);
                RaiseHeaderChanged();
            });

            await RefreshBridgesAsync();
            await RefreshSavedTransitionsAsync();
            if (Tracks.Count >= 2 && !IsTransitionEditorOpen)
                await Dispatcher.UIThread.InvokeAsync(() => OpenTransition(Tracks[0]));
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

    /// <summary>Downloaded, on disk and hashed: can be previewed and mixed. Tracks that still need
    /// analysis are kept (and flagged), so the set matches the playlist pair for pair.</summary>
    internal static bool IsPlayable(PlaylistTrack track) =>
        SLSKDONET.ViewModels.Workstation.WorkstationDeckViewModel.GetTrackEligibilityIssue(track) is not
            (SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.NoTrack
             or SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.NotDownloaded
             or SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingFile
             or SLSKDONET.ViewModels.Workstation.WorkstationTrackEligibilityIssue.MissingHash);

    /// <summary>
    /// Combines 2+ playlists into a new persisted playlist: unions their tracks (optionally
    /// deduping by hash) and auto-orders the result with the same optimizer used by
    /// <see cref="LoadSelectedPlaylistAsync"/>.
    ///
    /// Deliberately uses PlaylistOptimizer, not PlaylistIntelligenceService.ReorderAsync (the
    /// engine Suggest Flow/Auto-Arrange use): ReorderAsync silently drops any track with no stored
    /// fingerprint from its output entirely, with no fallback bucket or count. PlaylistOptimizer
    /// explicitly buckets and appends unanalyzed tracks, which the status text below already
    /// depends on. Switching engines correctly is real parity work (see
    /// <see cref="ApplySuggestedFlowAsync"/> for the hash-recovery pattern it would need), not a
    /// drive-by change.
    /// </summary>
    private async Task CombinePlaylistsAsync(IReadOnlyList<PlaylistJob>? preSelected = null)
    {
        var dialogResult = await _dialogService.ShowCombinePlaylistsDialogAsync(Playlists, preSelected);
        if (dialogResult == null || !dialogResult.IsConfirmed)
            return;
        if (dialogResult.SelectedPlaylists.Count < 2)
        {
            StatusText = "Select at least two playlists to combine.";
            return;
        }
        if (string.IsNullOrWhiteSpace(dialogResult.NewPlaylistName))
        {
            StatusText = "Enter a name for the combined playlist.";
            return;
        }

        IsLoading = true;
        StatusText = $"Combining {dialogResult.SelectedPlaylists.Count} playlists…";
        try
        {
            var sourcePlaylistTracks = new List<List<PlaylistTrack>>();
            foreach (var playlist in dialogResult.SelectedPlaylists)
                sourcePlaylistTracks.Add(await _library.LoadPlaylistTracksAsync(playlist.Id));

            var combinedTracks = SLSKDONET.Services.Library.PlaylistCombinePersistence.CollectTracks(
                sourcePlaylistTracks,
                dialogResult.SkipDuplicateTracks);

            if (combinedTracks.Count == 0)
            {
                StatusText = "No tracks found across the selected playlists.";
                return;
            }

            // Same 500-track cap LoadSelectedPlaylistAsync applies via GetPagedPlaylistTracksAsync,
            // so a combine of several large playlists can't exceed what Suggest Flow/ReorderAsync
            // (MaxReorderTracks = 512) can handle.
            if (combinedTracks.Count > 500)
                combinedTracks = combinedTracks.Take(500).ToList();

            // Optimise the order: AI-powered greedy sort by Camelot + BPM + energy — same call
            // the single-playlist loader above makes.
            var hashes = combinedTracks.Select(t => t.TrackUniqueHash ?? "").Where(h => h.Length > 0).ToList();
            PlaylistOptimizationResult? result = null;
            try
            {
                result = await _optimizer.OptimizeAsync(hashes);
            }
            catch (Exception ex)
            {
                // Fall back to combined order if the optimizer fails (e.g. no audio features yet)
                Serilog.Log.Warning(ex, "[FlowBuilder] Optimizer failed while combining playlists; keeping combined order");
            }

            var orderedTracks = result != null
                ? ExpandOrderedTracksWithDuplicates(result.OrderedHashes, combinedTracks)
                : combinedTracks;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Clear();
                foreach (var t in orderedTracks)
                    Tracks.Add(BuildCard(t));

                SelectedPlaylist = null;
                InvalidateFlowCaches(clearSuggestedFlow: true);
            });

            var newPlaylist = await SLSKDONET.Services.Library.PlaylistCombinePersistence.SaveAsync(
                _library,
                dialogResult.NewPlaylistName,
                orderedTracks);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Clear();
                foreach (var track in newPlaylist.PlaylistTracks)
                    Tracks.Add(BuildCard(track));

                if (!Playlists.Any(playlist => playlist.Id == newPlaylist.Id))
                    Playlists.Add(newPlaylist);
                SelectedPlaylist = newPlaylist;
                _persistedOrder = Tracks.Select(t => t.Model.Id).ToList();
                RaiseHeaderChanged();

                StatusText = result?.UnanalyzedTrackCount > 0
                    ? $"Created \"{newPlaylist.SourceTitle}\" with {Tracks.Count} tracks from {dialogResult.SelectedPlaylists.Count} playlists ({result.UnanalyzedTrackCount} unanalysed, appended at end)."
                    : $"Created \"{newPlaylist.SourceTitle}\" with {Tracks.Count} tracks from {dialogResult.SelectedPlaylists.Count} playlists — transitions optimised.";
            });

            await RefreshBridgesAsync();
            await RefreshSavedTransitionsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error combining playlists: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Applies the optimizer's resolved hash order to the actual track objects, preserving
    /// duplicates: if SkipDuplicateTracks was unchecked, combinedTracks can legitimately hold the
    /// same hash more than once (the same track present in two source playlists). Grouped into
    /// lists rather than a first-wins dictionary — the optimizer's own internal Distinct() only
    /// dedupes for ordering purposes (correct — a nearest-neighbor walk can't meaningfully visit a
    /// hash twice) and must not also collapse the actual track objects the user asked to keep
    /// duplicated. All copies of a duplicated hash land together at whatever position the
    /// optimizer chose for that hash.
    /// </summary>
    internal static List<PlaylistTrack> ExpandOrderedTracksWithDuplicates(
        IReadOnlyList<string> orderedHashes,
        List<PlaylistTrack> combinedTracks)
    {
        var tracksByHash = combinedTracks
            .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash))
            .GroupBy(t => t.TrackUniqueHash!)
            .ToDictionary(g => g.Key, g => g.ToList());

        return orderedHashes
            .Where(h => tracksByHash.ContainsKey(h))
            .SelectMany(h => tracksByHash[h])
            .Concat(combinedTracks.Where(t => !orderedHashes.Contains(t.TrackUniqueHash ?? "")))
            .ToList();
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

            var eligibleAll = all.Where(IsPlayable).ToList();

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

            // Grouped rather than a direct ToDictionary: the same track can legitimately appear
            // twice on the timeline (an intentional replay), which would otherwise throw building
            // a hash-keyed lookup.
            var metadataByHash = Tracks
                .Where(track => !string.IsNullOrWhiteSpace(track.TrackHash))
                .GroupBy(track => track.TrackHash, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new ReorderTrackMetadata(g.First().Model.Artist, g.First().Model.DetectedSubGenre),
                    StringComparer.Ordinal);

            var proposal = await _playlistIntelligence.ReorderAsync(
                currentHashes,
                energyCurve: SelectedEnergyCurveOption.Pattern,
                anchorTrackHash: currentHashes.FirstOrDefault(),
                metadataByHash: metadataByHash);

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
            await RefreshAfterStructureChangeAsync();
            StatusText = $"Applied A10 suggested flow • avg flow {(appliedScore * 100):F0}%";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Writes the current on-screen set order back to the selected playlist.</summary>
    private async Task ClearAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Clear Set",
            $"This removes all {Tracks.Count} staged track(s) from the current set. Continue?",
            confirmLabel: "Clear",
            cancelLabel: "Cancel");
        if (!confirmed) return;

        Tracks.Clear();
        InvalidateFlowCaches(clearSuggestedFlow: true);
        RaiseTrackCollectionChanged();
    }

    private async Task SaveOrderToPlaylistAsync()
    {
        if (Tracks.Count == 0 || SelectedPlaylist == null) return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Save Order",
            $"This overwrites the track order in \"{SelectedPlaylist.SourceTitle}\" and cannot be undone. Continue?",
            confirmLabel: "Save",
            cancelLabel: "Cancel");
        if (!confirmed) return;

        await CommitOrderToPlaylistAsync(SelectedPlaylist.Id, SelectedPlaylist.SourceTitle);
    }

    /// <summary>
    /// Writes the current on-screen set order into <paramref name="targetPlaylistId"/>. Bridge
    /// tracks pulled in from other playlists — or, in combine mode, every track, since the new
    /// playlist starts empty — are added first; playlist tracks not staged in the flow keep their
    /// place after the planned set.
    /// </summary>
    private async Task CommitOrderToPlaylistAsync(Guid targetPlaylistId, string displayName)
    {
        IsLoading = true;
        StatusText = $"Saving set order to \"{displayName}\"…";
        try
        {
            var foreignModels = Tracks
                .Select(card => card.Model)
                .Where(model => model.PlaylistId != targetPlaylistId)
                .ToList();

            if (foreignModels.Count > 0)
            {
                await _library.AddTracksToProjectAsync(foreignModels, targetPlaylistId);
            }

            // Re-load the playlist's own rows so we order the real membership (including rows
            // just created for bridge/combined tracks, and tracks hidden from the flow as not-ready).
            var playlistTracks = await _library.LoadPlaylistTracksAsync(targetPlaylistId);

            var stagedOrder = Tracks
                .Select((card, index) => new { card.TrackHash, index })
                .Where(x => !string.IsNullOrWhiteSpace(x.TrackHash))
                .GroupBy(x => x.TrackHash, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);

            var ordered = playlistTracks
                .OrderBy(t => stagedOrder.TryGetValue(t.TrackUniqueHash ?? string.Empty, out var idx)
                    ? idx
                    : stagedOrder.Count + Math.Max(0, t.SortOrder))
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].SortOrder = i + 1;
                ordered[i].TrackNumber = i + 1;
            }

            await _library.SaveTrackOrderAsync(targetPlaylistId, ordered);

            var unstaged = ordered.Count - stagedOrder.Count;
            var message = unstaged > 0
                ? $"Saved set order to \"{displayName}\" — {stagedOrder.Count} planned tracks first, {unstaged} unstaged kept after."
                : $"Saved set order to \"{displayName}\" ({ordered.Count} tracks).";

            if (foreignModels.Count > 0)
            {
                // Bridge tracks now have rows in this playlist: reload so their transitions can be saved.
                IsLoading = false;
                await LoadSelectedPlaylistAsync();
            }
            else
            {
                _persistedOrder = Tracks.Select(t => t.Model.Id).ToList();
                await RefreshSavedTransitionsAsync();
            }
            StatusText = message;
        }
        finally
        {
            IsLoading = false;
            RaiseHeaderChanged();
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
        // Declared before assignment so the closures below can capture this exact card instance
        // (not the track/hash) — see the duplicate-track fix note in FlowTrackCardViewModel.cs.
        FlowTrackCardViewModel card = null!;
        card = new FlowTrackCardViewModel(
            track,
            onMoveLeft:  () => MoveCard(card, -1),
            onMoveRight: () => MoveCard(card, +1),
            onRemove:    () => RemoveCard(card),
            onFindBridgeToNext: () => FindBridgeToNextTrack(card),
            onSelectTransitionInspector: () => OpenTransition(card),
            onPreviewTrack: () => PreviewTrackAsync(card));
        return card;
    }

    /// <summary>
    /// Plays or stops a quick, single-track preview for one card — the same hover-preview
    /// engine the Library page uses, independent of any deck or the main player. Toggles off if
    /// this card is already previewing; stops an active transition preview first since the two
    /// use separate audio outputs and would otherwise overlap.
    /// </summary>
    private async Task PreviewTrackAsync(FlowTrackCardViewModel card)
    {
        if (_libraryPreviewPlayer == null)
        {
            StatusText = "Track preview is unavailable.";
            return;
        }

        if (!Tracks.Contains(card))
            return;

        if (_activePreviewTrackCard == card && card.IsPreviewingTrack)
        {
            _libraryPreviewPlayer.StopPreview();
            return;
        }

        if (string.IsNullOrWhiteSpace(card.FilePath) || !System.IO.File.Exists(card.FilePath))
        {
            StatusText = "Preview unavailable — this track isn't downloaded locally yet.";
            return;
        }

        // Only one preview plays at a time — also stop the editor's transition preview, which uses
        // a separate audio output and would otherwise overlap.
        if (_activePreviewTrackCard != null)
        {
            _activePreviewTrackCard.IsPreviewingTrack = false;
        }
        if (_mixTransitionVm.IsPlaying)
        {
            _mixTransitionVm.PauseCommand.Execute().Subscribe();
        }

        _activePreviewTrackCard = card;
        card.IsPreviewingTrack = true;
        StatusText = $"Previewing: {card.Artist} — {card.Title}";
        _libraryPreviewPlayer.RequestPreview(card.FilePath, card.Model.BPM);

        await Task.CompletedTask;
    }

    private void OnTrackPreviewStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_activePreviewTrackCard != null)
            {
                _activePreviewTrackCard.IsPreviewingTrack = false;
                _activePreviewTrackCard = null;
            }
        });
    }

    private void OpenTransition(FlowTrackCardViewModel currentCard)
    {
        var currentIndex = Tracks.IndexOf(currentCard);

        if (currentIndex < 0 || currentIndex >= Tracks.Count - 1)
        {
            StatusText = "No adjacent transition to open.";
            return;
        }

        var nextCard = Tracks[currentIndex + 1];
        _activeOutgoingCard = currentCard;
        _activeIncomingCard = nextCard;
        IsTransitionEditorOpen = true;
        ActiveTransitionIndex = currentIndex;
        ActiveBridge = currentCard.Bridge;
        ActivePairTitle = $"{currentCard.Title} \u2192 {nextCard.Title}";
        _mixTransitionVm.SaveBlockedReason = GetSaveBlockedReason(SelectedPlaylist?.Id, currentCard.Model, nextCard.Model);
        // Loading a new pair auto-saves the previous pair's cue edits (MixTransitionViewModel.LoadPairAsync).
        _ = LoadPairSafeAsync(SelectedPlaylist?.Id ?? currentCard.Model.PlaylistId, currentCard.Model, nextCard.Model);
    }

    private async Task LoadPairSafeAsync(Guid playlistId, PlaylistTrack outgoing, PlaylistTrack incoming)
    {
        try
        {
            await _mixTransitionVm.LoadPairAsync(playlistId, outgoing, incoming);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[FlowBuilder] Couldn't load transition {Out} -> {In}", outgoing.Id, incoming.Id);
            StatusText = $"Couldn't open this transition: {ex.Message}";
        }
    }

    private async Task CloseTransitionEditorAsync()
    {
        // Closing keeps any unsaved cue edits (auto-save, same as moving to another pair).
        if (_mixTransitionVm.IsPlaying) _mixTransitionVm.PauseCommand.Execute().Subscribe();
        await _mixTransitionVm.FlushCueEditsAsync();
        IsTransitionEditorOpen = false;
        ActiveTransitionIndex = -1;
    }

    private void MoveCard(FlowTrackCardViewModel card, int delta)
    {
        int idx     = Tracks.IndexOf(card);
        if (idx < 0) return;
        int newIdx  = Math.Clamp(idx + delta, 0, Tracks.Count - 1);
        if (newIdx == idx) return;

        Tracks.RemoveAt(idx);
        Tracks.Insert(newIdx, card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        _ = RefreshAfterStructureChangeAsync();
    }

    public void MoveCardToIndex(FlowTrackCardViewModel card, int targetIndex)
    {
        int fromIdx = Tracks.IndexOf(card);
        if (fromIdx < 0) return;
        int toIdx = Math.Clamp(targetIndex, 0, Tracks.Count - 1);
        if (fromIdx == toIdx) return;
        Tracks.RemoveAt(fromIdx);
        Tracks.Insert(toIdx, card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        _ = RefreshAfterStructureChangeAsync();
    }

    private void RemoveCard(FlowTrackCardViewModel card)
    {
        if (!Tracks.Contains(card)) return;
        Tracks.Remove(card);
        InvalidateFlowCaches(clearSuggestedFlow: true);
        _ = RefreshAfterStructureChangeAsync();
    }

    private void FindBridgeToNextTrack(FlowTrackCardViewModel currentCard)
    {
        var currentIndex = Tracks.IndexOf(currentCard);

        if (currentIndex < 0 || currentIndex >= Tracks.Count - 1)
        {
            StatusText = "No next track available.";
            return;
        }

        var nextCard = Tracks[currentIndex + 1];

        ReactiveUI.MessageBus.Current.SendMessage(
            new FindBridgeBetweenTracksEvent(
                currentCard.TrackHash,
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
        await RefreshAfterStructureChangeAsync();

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

    private async Task RefreshAfterStructureChangeAsync()
    {
        try
        {
            await RefreshBridgesAsync();
            await RefreshSavedTransitionsAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[FlowBuilder] Refreshing pair scores failed");
            StatusText = $"Couldn't refresh pair scores: {ex.Message}";
        }
        await Dispatcher.UIThread.InvokeAsync(RaiseHeaderChanged);
    }

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

        // Every adjacent-pair similarity snapshot used to be awaited one at a time here, so a
        // single drag-reorder — which only actually changes the 1-2 edges touching the moved
        // track — serialized N independent fingerprint/section-vector lookups end to end. Fetch
        // them all in parallel instead; BuildSnapshotAsync is a pure read (fingerprint store +
        // section-vector cache lookups, no shared mutable state), so concurrent calls are safe.
        var snapshotTasks = new Task<TrackSimilaritySnapshot?>[Math.Max(0, Tracks.Count - 1)];
        for (int i = 0; i < snapshotTasks.Length; i++)
        {
            snapshotTasks[i] = _trackSimilarityService.BuildSnapshotAsync(
                Tracks[i].TrackHash,
                Tracks[i + 1].TrackHash,
                TrackSimilarityProfile.BlendSafe);
        }
        var snapshots = await Task.WhenAll(snapshotTasks).ConfigureAwait(false);

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
            var snapshot = snapshots[i];
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

        if (_activeTransitionIndex >= 0 && _activeTransitionIndex < Tracks.Count)
            ActiveBridge = Tracks[_activeTransitionIndex].Bridge;
    }

    private void RaiseTrackCollectionChanged()
    {
        this.RaisePropertyChanged(nameof(HasTracks));
        this.RaisePropertyChanged(nameof(HasNoTracks));
        this.RaisePropertyChanged(nameof(ShowTrackList));
        this.RaisePropertyChanged(nameof(HasSingleTrack));
        this.RaisePropertyChanged(nameof(ShowPickTransitionHint));
        RaiseHeaderChanged();
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

public sealed record EnergyCurveOption(string Label, EnergyCurvePattern Pattern, string Description)
{
    public override string ToString() => Label;
}
