using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Cue Forge — the dedicated workspace for placing, editing and auditioning a track's cues and loops.
///
/// Architecture:
///   • All cue editing (working set, selection, pads, loops, drops, snapping, undo, save) lives in
///     <see cref="Session"/>, the same <see cref="CueEditorSession"/> engine Flow Builder's decks use.
///     This class is the host: track picker, waveform data, transport, zoom and the page chrome.
///   • Switching tracks auto-saves a dirty draft first so work is never silently lost.
///   • Audio: Cue Forge only borrows the shared player when you actually audition (play, pad, nudge
///     replay). Opening or browsing tracks never interrupts what's playing, and leaving the page
///     hands the player back to the track it had before (see <see cref="OnViewDeactivated"/>).
///   • Grid: snapping, bar numbers and the drawn beat grid all use the precise BPM anchored at the
///     downbeat (<see cref="CueForgeGrid"/>).
/// </summary>
public sealed class CueForgeViewModel : ReactiveObject, IDisposable
{
    private readonly ICuePointService _cueService;
    private readonly ILibraryService? _libraryService;
    private readonly Engine.Cueing.CueGenerationService? _engineCueService;
    private readonly ICueForgeTransport _transport;
    private readonly CamelotKeyDisplayService? _camelotKeyService;
    private readonly ILogger _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager? _configManager;
    private readonly IDialogService? _dialogService;
    private readonly IEventBus? _eventBus;
    private readonly Func<Action, Task> _ui;
    private readonly CompositeDisposable _disposables = new();

    /// <summary>The cue editor for the open track (shared engine; see <see cref="CueEditorSession"/>).</summary>
    public CueEditorSession Session { get; }

    public ObservableCollection<PhraseSegment> PhraseSegments { get; } = new();

    // ── Track Context ──────────────────────────────────────────────────────

    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set
        {
            this.RaiseAndSetIfChanged(ref _trackHash, value);
            this.RaisePropertyChanged(nameof(HasTrackLoaded));
            this.RaisePropertyChanged(nameof(IsWaveformLoading));
            this.RaisePropertyChanged(nameof(HasNoTrack));
        }
    }

    /// <summary>Gates the BPM/duration/energy header readouts so they don't show stale defaults.</summary>
    public bool HasTrackLoaded => TrackHash != null;
    public bool HasNoTrack => TrackHash == null;

    /// <summary>True between a track being selected and its waveform bytes arriving.</summary>
    public bool IsWaveformLoading => HasTrackLoaded && WaveformLow == null && !_waveformMissing;

    private bool _waveformMissing;
    /// <summary>The track loaded but has no waveform data (not analysed yet).</summary>
    public bool IsWaveformMissing
    {
        get => _waveformMissing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _waveformMissing, value);
            this.RaisePropertyChanged(nameof(IsWaveformLoading));
        }
    }

    private string _trackTitle = "No track loaded";
    /// <summary>"Artist — Title" (kept for callers that show one line).</summary>
    public string TrackTitle { get => _trackTitle; private set => this.RaiseAndSetIfChanged(ref _trackTitle, value); }

    private string _trackName = "";
    public string TrackName { get => _trackName; private set => this.RaiseAndSetIfChanged(ref _trackName, value); }

    private string _trackArtist = "";
    public string TrackArtist { get => _trackArtist; private set => this.RaiseAndSetIfChanged(ref _trackArtist, value); }

    private double _trackDuration = 300.0;
    public double TrackDuration
    {
        get => _trackDuration;
        private set
        {
            this.RaiseAndSetIfChanged(ref _trackDuration, value);
            this.RaisePropertyChanged(nameof(TrackDurationDisplay));
            Session.UpdateDuration(value);
        }
    }

    private bool _durationKnown;

    public string TrackDurationDisplay => CueForgeGrid.FormatTime(TrackDuration);

    private int _bpm = 120;
    /// <summary>Rounded BPM (header + legacy bindings). The grid uses <see cref="GridBpm"/>.</summary>
    public int Bpm { get => _bpm; private set => this.RaiseAndSetIfChanged(ref _bpm, value); }

    private double _preciseBpm;
    /// <summary>Precise tempo the beat grid, snapping and bar numbers use.</summary>
    public double GridBpm => _preciseBpm > 0 ? _preciseBpm : Bpm;

    private double _downbeat;
    /// <summary>Downbeat offset (seconds) the grid is anchored at.</summary>
    public double GridOffset => _downbeat;

    public string BpmDisplay => !HasTrackLoaded || GridBpm <= 0 ? "—"
        : Math.Abs(GridBpm - Math.Round(GridBpm)) < 0.05
            ? Math.Round(GridBpm).ToString(CultureInfo.InvariantCulture)
            : GridBpm.ToString("F1", CultureInfo.InvariantCulture);

    // ── Playback State ─────────────────────────────────────────────────────

    private double _currentPlayPosition;
    public double CurrentPlayPosition
    {
        get => _currentPlayPosition;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPlayPosition, value);
            this.RaisePropertyChanged(nameof(CurrentPlayPositionDisplay));
            this.RaisePropertyChanged(nameof(CurrentBarBeatDisplay));
        }
    }

    public string CurrentPlayPositionDisplay => CueForgeGrid.FormatTime(CurrentPlayPosition);
    public string CurrentBarBeatDisplay => CueForgeGrid.FormatBarBeat(CurrentPlayPosition, GridBpm, GridOffset);

    /// <summary>True while the shared player is playing THIS track.</summary>
    public bool IsPlaying => IsAttachedToPlayer && _transport.IsPlaying;

    // ── Waveform Data ──────────────────────────────────────────────────────

    private byte[]? _waveformLow;
    public byte[]? WaveformLow
    {
        get => _waveformLow;
        private set
        {
            this.RaiseAndSetIfChanged(ref _waveformLow, value);
            this.RaisePropertyChanged(nameof(MaxZoomLevel));
            this.RaisePropertyChanged(nameof(IsWaveformLoading));
        }
    }

    /// <summary>Caps zoom to what the waveform's sample resolution can usefully show.</summary>
    public double MaxZoomLevel => WaveformLow is { Length: > 0 } arr
        ? Math.Clamp(arr.Length / 40.0, 4.0, 32.0)
        : 16.0;

    private byte[]? _waveformMid;
    public byte[]? WaveformMid { get => _waveformMid; private set => this.RaiseAndSetIfChanged(ref _waveformMid, value); }

    private byte[]? _waveformHigh;
    public byte[]? WaveformHigh { get => _waveformHigh; private set => this.RaiseAndSetIfChanged(ref _waveformHigh, value); }

    private float[]? _energyCurveData;
    public float[]? EnergyCurveData { get => _energyCurveData; set => this.RaiseAndSetIfChanged(ref _energyCurveData, value); }

    private float[]? _vocalDensityCurveData;
    public float[]? VocalDensityCurveData { get => _vocalDensityCurveData; set => this.RaiseAndSetIfChanged(ref _vocalDensityCurveData, value); }

    private float[]? _onsetDensityCurveData;
    public float[]? OnsetDensityCurveData { get => _onsetDensityCurveData; set => this.RaiseAndSetIfChanged(ref _onsetDensityCurveData, value); }

    // ── Harmonic / Vocal Context ───────────────────────────────────────────

    private string _currentCamelotKey = "—";
    public string CurrentCamelotKey { get => _currentCamelotKey; private set => this.RaiseAndSetIfChanged(ref _currentCamelotKey, value); }

    private string _compatibleCamelotKeys = "";
    public string CompatibleCamelotKeys { get => _compatibleCamelotKeys; private set => this.RaiseAndSetIfChanged(ref _compatibleCamelotKeys, value); }

    private bool _isInVocalRegion;
    public bool IsInVocalRegion { get => _isInVocalRegion; private set => this.RaiseAndSetIfChanged(ref _isInVocalRegion, value); }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isGenerating, value);
            Session.IsLocked = value;
        }
    }

    // ── Loop audition ──────────────────────────────────────────────────────

    private bool _isAuditioningLoop;
    /// <summary>When on, playback wraps from the active loop's end back to its start.</summary>
    public bool IsAuditioningLoop
    {
        get => _isAuditioningLoop;
        set
        {
            var loop = Session.ActiveLoop;
            if (value && loop == null) value = false;
            if (value == _isAuditioningLoop) { this.RaisePropertyChanged(); return; }
            this.RaiseAndSetIfChanged(ref _isAuditioningLoop, value);
            if (value && loop != null) PlayFrom(loop.Timestamp);
        }
    }

    // ── Track picker ───────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    private PlaylistJob? _selectedPlaylist;
    public PlaylistJob? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPlaylist, value);
            if (value != null) _ = LoadPlaylistBrowserAsync(value.Id);
        }
    }

    private readonly List<CueBrowserItem> _allPlaylistTracks = new();
    public ObservableCollection<CueBrowserItem> BrowserTracks { get; } = new();

    private string _browserQuery = "";
    public string BrowserQuery
    {
        get => _browserQuery;
        set { this.RaiseAndSetIfChanged(ref _browserQuery, value ?? ""); ApplyBrowserFilter(_browserQuery); }
    }

    private string _browserStatus = "Loading playlists…";
    /// <summary>Empty/loading/error text for the track picker; empty when tracks are listed.</summary>
    public string BrowserStatus { get => _browserStatus; private set => this.RaiseAndSetIfChanged(ref _browserStatus, value); }
    public bool HasBrowserStatus => !string.IsNullOrEmpty(BrowserStatus);

    public System.Windows.Input.ICommand LoadFromBrowserCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> NextTrackCommand { get; }

    /// <summary>Called by TrackOperationsViewModel when opening from a specific playlist context.</summary>
    public void SetPlaylistContext(PlaylistJob? playlist)
    {
        if (playlist == null) return;
        var existing = Playlists.FirstOrDefault(p => p.Id == playlist.Id);
        if (existing != null)
            SelectedPlaylist = existing;
        else
            _ = LoadPlaylistBrowserAsync(playlist.Id);
    }

    // ── Mix Transition origin (for the "Back to mix" link) ─────────────────

    private Guid? _mixOriginPlaylistId;
    private Guid? _mixOriginOutgoingTrackId;
    private Guid? _mixOriginIncomingTrackId;

    public bool HasMixOrigin => _mixOriginPlaylistId.HasValue;

    /// <summary>Called by MixTransitionViewModel.OpenTrackInCueForgeAsync right after LoadTrackAsync,
    /// so the Mix Editor → Cue Forge link has a way back.</summary>
    public void SetMixTransitionOrigin(Guid playlistId, Guid outgoingTrackId, Guid incomingTrackId)
    {
        _mixOriginPlaylistId = playlistId;
        _mixOriginOutgoingTrackId = outgoingTrackId;
        _mixOriginIncomingTrackId = incomingTrackId;
        this.RaisePropertyChanged(nameof(HasMixOrigin));
    }

    private void BackToMixTransition()
    {
        if (_mixOriginPlaylistId is not { } playlistId ||
            _mixOriginOutgoingTrackId is not { } outgoingId ||
            _mixOriginIncomingTrackId is not { } incomingId)
        {
            return;
        }
        _eventBus?.Publish(new OpenLibraryForPlaylistEvent(playlistId, outgoingId, incomingId));
    }

    private async Task LoadAllPlaylistsAsync()
    {
        if (_libraryService == null) { BrowserStatus = ""; return; }
        try
        {
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            await _ui(() =>
            {
                Playlists.Clear();
                foreach (var job in jobs.OrderByDescending(j => j.CreatedAt))
                    Playlists.Add(job);
                if (Playlists.Count == 0) BrowserStatus = "No playlists yet. Import or create one in the Library.";
                if (Playlists.Count > 0 && SelectedPlaylist == null)
                    SelectedPlaylist = Playlists[0];
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: failed to load playlist list");
            BrowserStatus = "Couldn't load playlists. See the log for details.";
        }
        this.RaisePropertyChanged(nameof(HasBrowserStatus));
    }

    private async Task LoadPlaylistBrowserAsync(Guid playlistId)
    {
        if (_libraryService == null) return;
        BrowserStatus = "Loading tracks…";
        this.RaisePropertyChanged(nameof(HasBrowserStatus));
        try
        {
            var tracks = await _libraryService.GetPagedPlaylistTracksAsync(
                playlistId, skip: 0, take: 2000, downloadedOnly: true);

            var items = tracks
                .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash))
                .Select(t => new CueBrowserItem
                {
                    GlobalId = t.TrackUniqueHash,
                    Title    = string.IsNullOrEmpty(t.Title) ? "Unknown" : t.Title,
                    Artist   = t.Artist ?? ""
                })
                .ToList();

            _allPlaylistTracks.Clear();
            _allPlaylistTracks.AddRange(items);
            ApplyBrowserFilter(_browserQuery);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: failed to load browser tracks for playlist {Id}", playlistId);
            BrowserStatus = "Couldn't load this playlist's tracks. See the log for details.";
            this.RaisePropertyChanged(nameof(HasBrowserStatus));
        }
    }

    private void ApplyBrowserFilter(string query)
    {
        var filtered = (string.IsNullOrWhiteSpace(query)
            ? _allPlaylistTracks
            : _allPlaylistTracks.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();

        _ = _ui(() =>
        {
            BrowserTracks.Clear();
            foreach (var t in filtered) BrowserTracks.Add(t);
            BrowserStatus = filtered.Count > 0 ? ""
                : _allPlaylistTracks.Count == 0 ? "No downloaded tracks in this playlist."
                : "No tracks match the filter.";
            this.RaisePropertyChanged(nameof(HasBrowserStatus));
        });
    }

    private async Task StepTrackAsync(int direction)
    {
        var list = BrowserTracks.Count > 0 ? BrowserTracks.ToList() : _allPlaylistTracks;
        if (list.Count == 0) return;
        int idx = list.FindIndex(t => string.Equals(t.GlobalId, TrackHash, StringComparison.OrdinalIgnoreCase));
        int next = idx < 0 ? (direction > 0 ? 0 : list.Count - 1) : idx + direction;
        if (next < 0 || next >= list.Count) return;
        var item = list[next];
        await LoadTrackAsync(item.GlobalId, item.Title, item.Artist);
    }

    // ── Commands (host: transport, view, generation) ───────────────────────

    public ReactiveCommand<Unit, Unit> AutoGenerateCuesCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomToFitCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCueCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCueCommand { get; }
    public ReactiveCommand<double, Unit> SeekPlayheadCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> AuditionCueCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToMixTransitionCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    /// <summary>CDJ-style CUE: playing → back to the cue point and pause; paused → set the cue point here.</summary>
    public ReactiveCommand<Unit, Unit> CueButtonCommand { get; }
    public System.Windows.Input.ICommand PlaybackToggleCommand => PlayPauseCommand;

    // ── Waveform Navigation State ──────────────────────────────────────────

    private double _waveformZoom = 1.0;
    public double WaveformZoom
    {
        get => _waveformZoom;
        set
        {
            this.RaiseAndSetIfChanged(ref _waveformZoom, Math.Clamp(value, CueForgeGrid.MinZoom, MaxZoomLevel));
            this.RaisePropertyChanged(nameof(WaveformScrollMaximum));
            this.RaisePropertyChanged(nameof(ZoomDisplay));
            WaveformScroll = _waveformScroll;
        }
    }

    public string ZoomDisplay => $"{WaveformZoom.ToString("F1", CultureInfo.InvariantCulture)}×";

    private double _waveformScroll;
    /// <summary>Fraction of the track at the left edge of the detail view.</summary>
    public double WaveformScroll
    {
        get => _waveformScroll;
        set => this.RaiseAndSetIfChanged(ref _waveformScroll, CueForgeGrid.ClampScroll(value, _waveformZoom));
    }

    public double WaveformScrollMaximum => CueForgeGrid.MaxScroll(_waveformZoom);

    private bool _followPlayhead = true;
    /// <summary>Page the detail view along with playback so the playhead stays visible.</summary>
    public bool FollowPlayhead { get => _followPlayhead; set => this.RaiseAndSetIfChanged(ref _followPlayhead, value); }

    /// <summary>Zooms around a point of the view (0..1 across), keeping that point fixed.</summary>
    public void ZoomAt(double factor, double anchor)
    {
        double oldZoom = WaveformZoom;
        double newZoom = Math.Clamp(oldZoom * factor, CueForgeGrid.MinZoom, MaxZoomLevel);
        if (Math.Abs(newZoom - oldZoom) < 1e-9) return;
        double scroll = CueForgeGrid.ZoomAround(WaveformScroll, oldZoom, newZoom, anchor);
        WaveformZoom = newZoom;
        WaveformScroll = scroll;
    }

    private void ZoomAroundPlayhead(double factor)
    {
        double window = 1.0 / Math.Max(1.0, WaveformZoom);
        double playFrac = TrackDuration > 0 ? CurrentPlayPosition / TrackDuration : 0;
        double anchor = (playFrac - WaveformScroll) / window;
        ZoomAt(factor, anchor is >= 0 and <= 1 ? anchor : 0.5);
    }

    // ── Track Metadata Extras ──────────────────────────────────────────────

    private int _trackEnergyScore;
    public int TrackEnergyScore { get => _trackEnergyScore; private set => this.RaiseAndSetIfChanged(ref _trackEnergyScore, value); }

    private string? _trackLoadError;
    /// <summary>Set when <see cref="LoadTrackAsync"/> fails partway through so the UI isn't left silently half-loaded.</summary>
    public string? TrackLoadError { get => _trackLoadError; private set => this.RaiseAndSetIfChanged(ref _trackLoadError, value); }

    // ── Constructors ───────────────────────────────────────────────────────

    public CueForgeViewModel(
        ICuePointService cueService,
        ILibraryService libraryService,
        Engine.Cueing.CueGenerationService engineCueService,
        PlayerViewModel playerViewModel,
        CamelotKeyDisplayService camelotKeyService,
        ILogger<CueForgeViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        DatabaseService databaseService,
        IDialogService dialogService,
        IEventBus eventBus,
        CueSessionRegistry cueSessions)
        : this(cueService, new PlayerViewModelCueForgeTransport(playerViewModel, databaseService), config,
               libraryService, engineCueService, camelotKeyService, logger, configManager, dialogService, eventBus,
               cueSessions, ui: null, restoreSession: true)
    {
    }

    /// <summary>Core constructor; the optional services are null in unit tests. <paramref name="ui"/>
    /// marshals collection updates to the UI thread (tests pass an inline runner).</summary>
    internal CueForgeViewModel(
        ICuePointService cueService,
        ICueForgeTransport transport,
        AppConfig config,
        ILibraryService? libraryService = null,
        Engine.Cueing.CueGenerationService? engineCueService = null,
        CamelotKeyDisplayService? camelotKeyService = null,
        ILogger? logger = null,
        ConfigManager? configManager = null,
        IDialogService? dialogService = null,
        IEventBus? eventBus = null,
        CueSessionRegistry? cueSessions = null,
        Func<Action, Task>? ui = null,
        bool restoreSession = false)
    {
        _cueService = cueService;
        _transport = transport;
        _config = config;
        _libraryService = libraryService;
        _engineCueService = engineCueService;
        _camelotKeyService = camelotKeyService;
        _logger = logger ?? NullLogger.Instance;
        _configManager = configManager;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _ui = ui ?? RunOnUiThread;

        Session = new CueEditorSession(cueService, new SharedPlayerAudition(this),
            cueSessions ?? new CueSessionRegistry(config, configManager), _logger)
        {
            EditorName = "Cue Forge",
            // The detail waveform snaps live while dragging (Shift places freely), so no re-snap on release.
            SnapOnDragRelease = false,
            AuditionAfterEdits = false,
        };
        Session.ConfirmRevert = ConfirmRevertAsync;
        Session.ActiveLoopRemoved += (_, _) => IsAuditioningLoop = false;

        var hasTrack = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));
        var notGenerating = this.WhenAnyValue(x => x.IsGenerating, g => !g);
        var canAct = hasTrack.CombineLatest(notGenerating, (h, g) => h && g);

        AutoGenerateCuesCommand = ReactiveCommand.CreateFromTask(AutoGenerateCuesAsync, canAct);

        ZoomInCommand     = ReactiveCommand.Create(() => ZoomAroundPlayhead(1.5));
        ZoomOutCommand    = ReactiveCommand.Create(() => ZoomAroundPlayhead(1 / 1.5));
        ZoomToFitCommand  = ReactiveCommand.Create(() => { WaveformZoom = 1.0; WaveformScroll = 0; });

        PreviousCueCommand = ReactiveCommand.Create(NavigateToPreviousCue, canAct);
        NextCueCommand     = ReactiveCommand.Create(NavigateToNextCue, canAct);
        BackToMixTransitionCommand = ReactiveCommand.Create(BackToMixTransition, this.WhenAnyValue(x => x.HasMixOrigin));
        SeekPlayheadCommand = ReactiveCommand.Create<double>(SetPlayhead);
        AuditionCueCommand = ReactiveCommand.Create<OrbitCue>(AuditionCue);
        PlayPauseCommand = ReactiveCommand.Create(TogglePlayPause, hasTrack);
        CueButtonCommand = ReactiveCommand.Create(CueButton, hasTrack);

        LoadFromBrowserCommand = new Views.AsyncRelayCommand<CueBrowserItem>(async item =>
        {
            if (item == null) return;
            await LoadTrackAsync(item.GlobalId, item.Title, item.Artist);
        });
        PreviousTrackCommand = ReactiveCommand.CreateFromTask(() => StepTrackAsync(-1), hasTrack);
        NextTrackCommand = ReactiveCommand.CreateFromTask(() => StepTrackAsync(1), hasTrack);

        _transport.CurrentTrackChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track => _ = OnPlayerTrackChangedAsync(track))
            .DisposeWith(_disposables);

        _transport.IsPlayingChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsPlaying)))
            .DisposeWith(_disposables);

        // Playhead sync at ~30 fps — only while the player actually holds this track, so the
        // playhead never shows some other song's position.
        _transport.PositionChanged
            .Sample(TimeSpan.FromMilliseconds(33))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(p => OnPlayerPosition(p.Fraction, p.LengthSeconds))
            .DisposeWith(_disposables);

        if (libraryService != null) _ = LoadAllPlaylistsAsync();
        else BrowserStatus = "";

        if (restoreSession)
        {
            // Follow whatever the player is already on; otherwise reopen the last Cue Forge track.
            if (_transport.CurrentTrackHash is { Length: > 0 } current)
                _ = LoadTrackAsync(current, _transport.Title, _transport.Artist);
            else if (!string.IsNullOrEmpty(_config.CueForgeLastTrackHash))
                _ = LoadTrackAsync(_config.CueForgeLastTrackHash,
                                   _config.CueForgeLastTrackTitle,
                                   _config.CueForgeLastTrackArtist);
        }
    }

    private static Task RunOnUiThread(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { action(); return Task.CompletedTask; }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    /// <summary>Revert is undoable, but it still throws away a working set the user may not expect
    /// to lose in one click, so Cue Forge asks first.</summary>
    private async Task<bool> ConfirmRevertAsync()
    {
        if (_dialogService == null) return true;
        return await _dialogService.ConfirmAsync(
            "Revert to saved",
            "This replaces every unsaved cue and loop edit for this track with the saved cues. Ctrl+Z brings them back. Continue?",
            confirmLabel: "Revert",
            cancelLabel: "Cancel");
    }

    // ── Player track changes ───────────────────────────────────────────────

    /// <summary>The player's queue moved to another track (natural advance or the user playing
    /// something). Cue Forge follows it, auto-saving the previous draft first so work is never lost.</summary>
    private async Task OnPlayerTrackChangedAsync(CueForgeTrackRef? track)
    {
        // Whoever changed tracks owns the player now; never "hand back" over their choice.
        _borrow = null;
        this.RaisePropertyChanged(nameof(IsPlaying));
        if (track == null) return; // player stopped: keep the current draft open
        if (string.Equals(track.Hash, TrackHash, StringComparison.OrdinalIgnoreCase)) return;
        await LoadTrackAsync(track.Hash, track.Title, track.Artist);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    private int _loadVersion;
    private string? _trackFilePath;

    public async Task LoadTrackAsync(string trackHash, string? title = null, string? artist = null)
    {
        // Never silently drop unsaved edits for the track being left.
        string? autoSaved = null;
        if (Session.IsDirty && Session.TrackHash is not null &&
            !string.Equals(Session.TrackHash, trackHash, StringComparison.OrdinalIgnoreCase))
        {
            string previousTitle = TrackTitle;
            if (!await Session.SaveAsync())
            {
                // The save error is on the status line; stay on this track so the edits survive.
                Session.SetStatus($"Couldn't save the cues for \"{previousTitle}\", so Cue Forge stayed on it. Fix the problem (see the log), save, then switch.", isError: true);
                return;
            }
            autoSaved = $"Auto-saved cues for \"{previousTitle}\" before switching tracks.";
        }

        int version = ++_loadVersion;
        IsAuditioningLoop = false;
        Session.Unload();
        TrackHash = trackHash;
        TrackName = string.IsNullOrWhiteSpace(title) ? "Unknown track" : title!;
        TrackArtist = artist ?? "";
        TrackTitle = string.IsNullOrWhiteSpace(title) ? "Unknown track"
            : string.IsNullOrWhiteSpace(artist) ? title!
            : $"{artist} — {title}";

        WaveformLow = null; WaveformMid = null; WaveformHigh = null;
        IsWaveformMissing = false;
        WaveformZoom = 1.0; WaveformScroll = 0;
        CurrentPlayPosition = 0;
        _trackFilePath = null;
        _durationKnown = false;
        TrackLoadError = null;
        if (autoSaved != null) Session.SetStatus(autoSaved);

        // Resolve the audio file but don't load it into the shared player yet: Cue Forge only takes
        // the player over when you audition, so opening a track never interrupts other playback.
        try
        {
            _trackFilePath = await _transport.ResolveFilePathAsync(trackHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: could not resolve audio for {Hash}", trackHash);
        }
        if (version != _loadVersion) return;
        if (IsAttachedToPlayer) OnPlayerPosition(_transport.PositionFraction, _transport.LengthSeconds);
        this.RaisePropertyChanged(nameof(IsPlaying));

        try
        {
            var entities = await _cueService.GetByTrackIdAsync(trackHash);
            if (version != _loadVersion) return;

            var features = _libraryService == null ? null : await _libraryService.GetAudioFeaturesByHashAsync(trackHash);
            if (version != _loadVersion) return;
            string? genre = null;
            if (features is not null)
            {
                Bpm = (int)Math.Round(Math.Max(60, features.Bpm));
                _preciseBpm = features.Bpm > 0 ? features.Bpm : Bpm;
                _downbeat = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0.0;
                genre = !string.IsNullOrWhiteSpace(features.DetectedSubGenre) ? features.DetectedSubGenre : features.ElectronicSubgenre;
                _durationKnown = features.TrackDuration > 0;
                TrackDuration = features.TrackDuration > 0 ? features.TrackDuration : 300.0;
                TrackEnergyScore = features.EnergyScore;

                // Decode waveform blob: [low₀…lowN | mid₀…midN | high₀…highN]
                if (features.WaveformBlob is { Length: > 0 } blob && features.WaveformBlobSampleCount > 0 &&
                    blob.Length >= features.WaveformBlobSampleCount * 3)
                {
                    int n = features.WaveformBlobSampleCount;
                    var low = new byte[n]; var mid = new byte[n]; var high = new byte[n];
                    Buffer.BlockCopy(blob, 0, low, 0, n);
                    Buffer.BlockCopy(blob, n, mid, 0, n);
                    Buffer.BlockCopy(blob, n * 2, high, 0, n);
                    WaveformLow = low; WaveformMid = mid; WaveformHigh = high;
                }
                else IsWaveformMissing = true;

                EnergyCurveData = ParseJsonFloatArray(features.EnergyCurveJson);
                VocalDensityCurveData = ParseJsonFloatArray(features.VocalDensityCurveJson);

                var beatGrid = ParseJsonFloatArray(features.BeatGridJson)?.Select(f => (double)f).ToList()
                               ?? new List<double>();
                OnsetDensityCurveData = beatGrid.Count > 0
                    ? new OnsetDensityEngine().ComputeOnsetDensityCurve(beatGrid, TrackDuration)
                    : null;

                var segments = ParsePhraseSegments(features.PhraseSegmentsJson);
                await _ui(() =>
                {
                    PhraseSegments.Clear();
                    foreach (var s in segments) PhraseSegments.Add(s);
                });

                UpdateCamelotKeyDisplay(features.CamelotKey);
            }
            else
            {
                IsWaveformMissing = true;
                _preciseBpm = 0; _downbeat = 0; Bpm = 120;
                TrackDuration = 300.0;
                UpdateCamelotKeyDisplay("");
                EnergyCurveData = null; VocalDensityCurveData = null; OnsetDensityCurveData = null;
                await _ui(() => PhraseSegments.Clear());
                TrackLoadError = "This track hasn't been analysed yet, so there's no waveform, tempo or key. Analyse it from the Library, then reopen it here.";
            }
            RaiseGridChanged();

            var info = new CueTrackInfo(trackHash, TrackTitle, _trackFilePath, GridBpm, _downbeat, TrackDuration, genre);
            await _ui(() =>
            {
                Session.Load(info, entities);
                // Phrase analysis (read-only) feeds the Intro end / Outro suggestions.
                Session.SetStructureHints(PhraseSegments.ToList());
                if (autoSaved != null && !Session.IsStatusError) Session.SetStatus(autoSaved);
            });
            _logger.LogInformation("CueForge: loaded {Count} cues for {Hash} (BPM={Bpm} Dur={Dur:F0}s)",
                entities.Count, trackHash, GridBpm, TrackDuration);

            // Persist last-opened track so we can restore it on next launch
            _config.CueForgeLastTrackHash = trackHash;
            _config.CueForgeLastTrackTitle = title;
            _config.CueForgeLastTrackArtist = artist;
            try { _configManager?.Save(_config); }
            catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't remember the last opened track"); }
        }
        catch (Exception ex)
        {
            // The session stays unloaded, so nothing can be edited or saved over the track's real cues.
            _logger.LogError(ex, "CueForge: failed to load cues/features for {Hash}", trackHash);
            TrackLoadError = $"Couldn't fully load this track's cue data: {ex.Message}";
        }
    }

    private void RaiseGridChanged()
    {
        this.RaisePropertyChanged(nameof(GridBpm));
        this.RaisePropertyChanged(nameof(GridOffset));
        this.RaisePropertyChanged(nameof(BpmDisplay));
        this.RaisePropertyChanged(nameof(CurrentBarBeatDisplay));
    }

    public void UpdateCamelotKeyDisplay(string camelotKey)
    {
        if (string.IsNullOrEmpty(camelotKey)) { CurrentCamelotKey = "—"; CompatibleCamelotKeys = ""; return; }
        CurrentCamelotKey = camelotKey;
        var compatible = _camelotKeyService?.GetCompatibleKeys(camelotKey) ?? new List<string>();
        var others = compatible.Where(k => k != camelotKey).ToList();
        CompatibleCamelotKeys = others.Count > 0 ? $"Mixes with {string.Join(", ", others)}" : "";
    }

    // ── Shared player (borrow / hand back) ─────────────────────────────────

    /// <summary>The session plays through the shared player, borrowed only while auditioning.</summary>
    private sealed class SharedPlayerAudition : ICueAudition
    {
        private readonly CueForgeViewModel _vm;
        public SharedPlayerAudition(CueForgeViewModel vm) => _vm = vm;
        public double PositionSeconds => _vm.CurrentPlayPosition;
        public bool IsPlaying => _vm.IsPlaying;
        public void PlayFrom(double seconds) => _vm.PlayFrom(seconds);
        public void JumpToCue(OrbitCue cue)
        {
            _vm._transportCue = cue.Timestamp;
            _vm.PlayFrom(cue.Timestamp);
        }
        /// <summary>Pauses the shared player, only when it holds this track (never someone else's audio).</summary>
        public void Stop()
        {
            if (!_vm.IsAttachedToPlayer) return;
            _vm._transport.Pause();
            _vm.RaisePropertyChanged(nameof(IsPlaying));
        }
    }

    private sealed record PlayerBorrow(string? PreviousPath, string PreviousTitle, string PreviousArtist, double PreviousFraction);
    private PlayerBorrow? _borrow;

    /// <summary>True when the shared player currently holds this track's audio.</summary>
    public bool IsAttachedToPlayer =>
        TrackHash != null &&
        ((_trackFilePath != null && string.Equals(_transport.CurrentFilePath, _trackFilePath, StringComparison.OrdinalIgnoreCase)) ||
         string.Equals(_transport.CurrentTrackHash, TrackHash, StringComparison.OrdinalIgnoreCase) && _transport.CurrentFilePath != null &&
         (_trackFilePath == null || string.Equals(_transport.CurrentFilePath, _trackFilePath, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Loads this track into the shared player if it isn't there yet, remembering what
    /// was loaded before so <see cref="OnViewDeactivated"/> can hand it back.</summary>
    private bool EnsurePlayerHasTrack()
    {
        if (TrackHash is null) return false;
        if (IsAttachedToPlayer) return true;
        if (string.IsNullOrEmpty(_trackFilePath))
        {
            Session.SetStatus("Can't play this track: its audio file wasn't found on disk.", isError: true);
            return false;
        }
        if (_borrow == null)
        {
            var prev = _transport.CurrentFilePath;
            _borrow = new PlayerBorrow(prev, _transport.Title, _transport.Artist, _transport.PositionFraction);
        }
        _transport.Pause();
        _transport.LoadPaused(_trackFilePath!, TrackName, TrackArtist);
        this.RaisePropertyChanged(nameof(IsPlaying));
        return true;
    }

    /// <summary>The page was navigated away from. Stops loop audition and, if Cue Forge borrowed
    /// the shared player, pauses it and reloads the track it had before (at its old position), so
    /// the main player is never left on a track its queue doesn't know about.</summary>
    public void OnViewDeactivated()
    {
        IsAuditioningLoop = false;
        if (_borrow is not { } borrow) return;
        _borrow = null;
        if (!IsAttachedToPlayer) return; // someone else already took the player over
        _transport.Pause();
        if (!string.IsNullOrEmpty(borrow.PreviousPath))
        {
            _transport.LoadPaused(borrow.PreviousPath!, borrow.PreviousTitle, borrow.PreviousArtist);
            _transport.SeekFraction(borrow.PreviousFraction);
        }
        this.RaisePropertyChanged(nameof(IsPlaying));
    }

    /// <summary>Length to map player fractions with: the player's own once it matches this track,
    /// else the analysed duration (right after loading, the player still reports the old file's).</summary>
    private double EffectiveLength(double playerLengthSeconds)
    {
        if (playerLengthSeconds <= 0) return TrackDuration;
        if (!_durationKnown) return playerLengthSeconds;
        double tolerance = Math.Max(2.0, TrackDuration * 0.02);
        return Math.Abs(playerLengthSeconds - TrackDuration) <= tolerance ? playerLengthSeconds : TrackDuration;
    }

    private void OnPlayerPosition(double fraction, double lengthSeconds)
    {
        if (!IsAttachedToPlayer || _isScrubbing) return;
        if (!_durationKnown && lengthSeconds > 0 && Math.Abs(TrackDuration - lengthSeconds) > 0.5)
            TrackDuration = lengthSeconds;
        CurrentPlayPosition = fraction * EffectiveLength(lengthSeconds);
        UpdateVocalWarning();

        if (IsAuditioningLoop && Session.ActiveLoop is { } loop && loop.LoopEndSeconds > loop.Timestamp &&
            CurrentPlayPosition >= loop.LoopEndSeconds)
        {
            SeekPlayer(loop.Timestamp);
        }

        if (FollowPlayhead && _transport.IsPlaying && TrackDuration > 0)
            WaveformScroll = CueForgeGrid.FollowScroll(CurrentPlayPosition / TrackDuration, WaveformScroll, WaveformZoom);
    }

    private bool _isScrubbing;
    /// <summary>Set by the waveform while the mouse is held down scrubbing, so player position
    /// updates don't fight the pointer.</summary>
    public void SetScrubbing(bool scrubbing) => _isScrubbing = scrubbing;

    /// <summary>Waveform click / scrub: moves the playhead. Seeks the player only if it already
    /// holds this track — clicking never interrupts other playback.</summary>
    private void SetPlayhead(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, TrackDuration);
        CurrentPlayPosition = seconds;
        UpdateVocalWarning();
        if (IsAttachedToPlayer) SeekPlayer(seconds);
    }

    private void SeekPlayer(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, TrackDuration);
        double length = EffectiveLength(_transport.LengthSeconds);
        if (length > 0) _transport.SeekFraction(seconds / length);
        CurrentPlayPosition = seconds;
    }

    /// <summary>Takes the player if needed, seeks and plays.</summary>
    private void PlayFrom(double seconds)
    {
        if (!EnsurePlayerHasTrack()) return;
        SeekPlayer(seconds);
        _transport.Play();
        this.RaisePropertyChanged(nameof(IsPlaying));
    }

    private void TogglePlayPause()
    {
        if (IsPlaying)
        {
            _transport.Pause();
        }
        else
        {
            if (!EnsurePlayerHasTrack()) return;
            SeekPlayer(CurrentPlayPosition);
            _transport.Play();
        }
        this.RaisePropertyChanged(nameof(IsPlaying));
    }

    private double _transportCue;

    private void CueButton()
    {
        if (IsPlaying)
        {
            _transport.Pause();
            SeekPlayer(_transportCue);
        }
        else
        {
            _transportCue = Session.SnapTime(CurrentPlayPosition);
            SetPlayhead(_transportCue);
        }
        this.RaisePropertyChanged(nameof(IsPlaying));
    }

    // ── Auto-generate ──────────────────────────────────────────────────────

    private async Task AutoGenerateCuesAsync()
    {
        if (TrackHash is null || Session.TrackHash is null) return;
        if (_libraryService == null || _engineCueService == null) return;

        IsGenerating = true;
        try
        {
            _logger.LogInformation("CueForge: auto-generating cues for {Hash}", TrackHash);

            var features = await _libraryService.GetAudioFeaturesByHashAsync(TrackHash);
            if (features is null)
            {
                _logger.LogWarning("CueForge: no features found for {Hash}", TrackHash);
                Session.SetStatus("No analysis data for this track. Analyse it from the Library first.", isError: true);
                return;
            }

            var analysis = BuildAnalysisResultFromFeatures(features);
            double downbeat = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0.0;

            var generatedEntities = _engineCueService.GenerateCues(
                trackHash: TrackHash,
                analysis: analysis,
                downbeatAnchor: downbeat,
                vocalStart: features.VocalStartSeconds.HasValue ? (double?)features.VocalStartSeconds.Value : null,
                vocalEnd: features.VocalEndSeconds.HasValue ? (double?)features.VocalEndSeconds.Value : null,
                vocalIntensity: features.VocalIntensity > 0 ? (double?)features.VocalIntensity : null);

            var userCues = Session.Cues.Where(c => c.Source == CueSource.User).ToList();
            await _ui(() =>
            {
                var merged = generatedEntities.Select(OrbitCue.FromEntity).ToList();
                foreach (var u in userCues)
                {
                    // Section markers are always kept: they bound regions, they don't compete with cues.
                    bool conflicts = !u.IsSectionMarker && merged.Any(c => !c.IsLoop && Math.Abs(c.Timestamp - u.Timestamp) < 1.0);
                    if (!conflicts) merged.Add(u);
                }
                AssignSlotsByRolePriority(merged);
                ApplyRekordboxColors(merged);
                Session.ReplaceAllCues(merged, $"Generated {generatedEntities.Count} cues. Your own cues were kept. Ctrl+Z to undo.");
            });

            _logger.LogInformation("CueForge: generated {Count} cues", generatedEntities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: auto-generate failed for {Hash}", TrackHash);
            Session.SetStatus("Auto-generate failed; your cues are unchanged. See the log for details.", isError: true);
        }
        finally { IsGenerating = false; }
    }

    /// <summary>Generated cues get pads by structural importance; your own cues keep theirs.</summary>
    private static void AssignSlotsByRolePriority(List<OrbitCue> cues)
    {
        foreach (var c in cues.Where(c => c.Source == CueSource.Auto && !c.IsLoop))
            c.SlotIndex = -1;

        var rolePriority = new[] { CueRole.Drop, CueRole.Breakdown, CueRole.Build, CueRole.Intro, CueRole.KickIn, CueRole.Outro, CueRole.Vocals, CueRole.Custom };
        int slot = 0;
        foreach (var role in rolePriority)
        {
            foreach (var cue in cues.Where(c => c.Source == CueSource.Auto && c.Role == role && !c.IsLoop).OrderBy(c => c.Timestamp))
            {
                while (slot < 8 && cues.Any(c => c.Source == CueSource.User && !c.IsLoop && c.SlotIndex == slot))
                    slot++;
                if (slot >= 8) break;
                cue.SlotIndex = slot++;
            }
            if (slot >= 8) break;
        }
    }

    private static void ApplyRekordboxColors(List<OrbitCue> cues)
    {
        foreach (var c in cues.Where(c => c.Source == CueSource.Auto))
            c.Color = CueEditorSession.RekordboxColorForRole(c.Role);
    }

    // ── Vocal Warning ──────────────────────────────────────────────────────

    private void UpdateVocalWarning()
    {
        // Reads the CURRENT track's curve (it used to cache the first track's curve forever).
        var curve = VocalDensityCurveData;
        if (curve is null || curve.Length == 0 || TrackDuration <= 0) { IsInVocalRegion = false; return; }
        int idx = (int)Math.Clamp(CurrentPlayPosition / TrackDuration * curve.Length, 0, curve.Length - 1);
        IsInVocalRegion = curve[idx] > 0.4f;
    }

    // ── Fast-path AnalysisPipelineResult reconstruction ────────────────────

    // Delegates to Engine.Analysis.AnalysisPipelineResultBuilder, which AnalyzeTrackStructureJob's
    // background pipeline also calls, so both routes score cues against identical signals.
    internal static Engine.Analysis.AnalysisPipelineResult BuildAnalysisResultFromFeatures(AudioFeaturesEntity f) =>
        Engine.Analysis.AnalysisPipelineResultBuilder.Build(f);

    // ── Utility ────────────────────────────────────────────────────────────

    private float[]? ParseJsonFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return null;
        try { return JsonSerializer.Deserialize<float[]>(json); }
        catch (Exception ex) { _logger.LogWarning(ex, "CueForge: ignoring unreadable curve data"); return null; }
    }

    private List<PhraseSegment> ParsePhraseSegments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new(); }
        catch (Exception ex) { _logger.LogWarning(ex, "CueForge: ignoring unreadable phrase data"); return new(); }
    }

    // ── Transport navigation ───────────────────────────────────────────────

    private void NavigateToPreviousCue()
    {
        var prev = Session.Cues
            .Where(c => !c.IsLoop && c.Timestamp < CurrentPlayPosition - 0.1)
            .OrderByDescending(c => c.Timestamp)
            .FirstOrDefault();
        if (prev == null) return;
        Session.SelectedCue = prev;
        SetPlayhead(prev.Timestamp);
    }

    private void NavigateToNextCue()
    {
        var next = Session.Cues
            .Where(c => !c.IsLoop && c.Timestamp > CurrentPlayPosition + 0.1)
            .OrderBy(c => c.Timestamp)
            .FirstOrDefault();
        if (next == null) return;
        Session.SelectedCue = next;
        SetPlayhead(next.Timestamp);
    }

    // ── Audition with pre-roll ─────────────────────────────────────────────

    private void AuditionCue(OrbitCue? cue)
    {
        if (cue == null) return;
        double preRoll = GridBpm > 0 ? 4.0 * 60.0 / GridBpm : 2.0; // 4 beats before cue
        PlayFrom(Math.Max(0, cue.Timestamp - preRoll));
    }

    public void Dispose()
    {
        Session.Dispose();
        _disposables.Dispose();
    }
}
