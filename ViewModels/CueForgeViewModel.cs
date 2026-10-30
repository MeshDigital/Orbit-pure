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
using SLSKDONET.Engine.Cueing;
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
    private readonly Services.Rekordbox.RekordboxLibraryService? _rekordbox;
    private readonly Func<Action, Task> _ui;
    private readonly CompositeDisposable _disposables = new();

    /// <summary>The cue editor for the open track (shared engine; see <see cref="CueEditorSession"/>).</summary>
    public CueEditorSession Session { get; }

    public ObservableCollection<PhraseSegment> PhraseSegments { get; } = new();

    /// <summary>The phrase list resolved for the open track (TrackPhrases when fresh, otherwise the
    /// legacy JSON column when its provenance is trusted). The phrase map, the section suggestions
    /// and Auto-generate all use this one list, so what you see and what gets cued can never come
    /// from two different analyses.</summary>
    private IReadOnlyList<PhraseSegment> _resolvedPhrases = Array.Empty<PhraseSegment>();

    // ── Cue generation profiles ─────────────────────────────────────────────
    private CueGenerationProfile _selectedCueGenerationProfile = CueGenerationProfiles.Balanced;

    /// <summary>Profiles available in the Cue Lab picker. The selected one is sticky (AppConfig).</summary>
    public IReadOnlyList<CueGenerationProfile> AvailableCueGenerationProfiles => CueGenerationProfiles.All;

    public CueGenerationProfile SelectedCueGenerationProfile
    {
        get => _selectedCueGenerationProfile;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedCueGenerationProfile)) return;
            this.RaiseAndSetIfChanged(ref _selectedCueGenerationProfile, value);
            _config.CueGenerationProfile = value.Id;
            try { _configManager?.Save(_config); }
            catch (Exception ex) { _logger.LogWarning(ex, "CueForge: could not save cue-generation profile"); }
        }
    }

    /// <summary>Checkbox list for the read-only multi-profile comparison.</summary>
    public ObservableCollection<CueGenerationProfileOption> ProfileComparisonOptions { get; } = new();

    /// <summary>One rendered line per selected profile after Compare.</summary>
    public ObservableCollection<string> ProfileComparisonRows { get; } = new();

    private string? _profileComparisonStatus;
    public string? ProfileComparisonStatus
    {
        get => _profileComparisonStatus;
        private set => this.RaiseAndSetIfChanged(ref _profileComparisonStatus, value);
    }

    private bool _isComparingProfiles;
    public bool IsComparingProfiles
    {
        get => _isComparingProfiles;
        private set => this.RaiseAndSetIfChanged(ref _isComparingProfiles, value);
    }

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
            this.RaisePropertyChanged(nameof(CurrentBrowserItem));
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
            RaiseWaveformRefreshStateChanged();
        }
    }

    private bool _isWaveformOutdated;
    /// <summary>The cached blob predates full-duration, shared-scale tri-band extraction.</summary>
    public bool IsWaveformOutdated
    {
        get => _isWaveformOutdated;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isWaveformOutdated, value);
            RaiseWaveformRefreshStateChanged();
        }
    }

    private bool _isWaveformRefreshPending;
    public bool IsWaveformRefreshPending
    {
        get => _isWaveformRefreshPending;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isWaveformRefreshPending, value);
            RaiseWaveformRefreshStateChanged();
        }
    }

    private string? _waveformRefreshError;
    private string _waveformRefreshStatus = "";
    /// <summary>Live queue/progress detail, or the reason a manually requested analysis failed.</summary>
    public string WaveformRefreshStatus
    {
        get => _waveformRefreshStatus;
        private set
        {
            this.RaiseAndSetIfChanged(ref _waveformRefreshStatus, value);
            this.RaisePropertyChanged(nameof(WaveformRefreshNoticeText));
        }
    }

    public bool HasWaveformRefreshNotice => IsWaveformOutdated || IsWaveformRefreshPending || _waveformRefreshError != null;
    public bool ShowWaveformRefreshAction => !IsWaveformRefreshPending && (IsWaveformOutdated || _waveformRefreshError != null);
    public string WaveformRefreshActionLabel => IsWaveformRefreshPending ? "Analysing…"
        : IsWaveformOutdated || IsWaveformMissing ? "Refresh full analysis" : "Re-analyse track";
    public string WaveformRefreshNoticeText => IsWaveformRefreshPending || _waveformRefreshError != null
        ? WaveformRefreshStatus
        : "This waveform uses the old analysis. Refresh the full analysis for accurate timing, bass detail and drop contrast.";

    private void RaiseWaveformRefreshStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasWaveformRefreshNotice));
        this.RaisePropertyChanged(nameof(ShowWaveformRefreshAction));
        this.RaisePropertyChanged(nameof(WaveformRefreshActionLabel));
        this.RaisePropertyChanged(nameof(WaveformRefreshNoticeText));
    }

    private void SetWaveformRefreshError(string? error)
    {
        _waveformRefreshError = error;
        RaiseWaveformRefreshStateChanged();
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

    private string _manualBpmInput = "";
    /// <summary>User-typed BPM for the manual tempo override.</summary>
    public string ManualBpmInput
    {
        get => _manualBpmInput;
        set => this.RaiseAndSetIfChanged(ref _manualBpmInput, value);
    }

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

    // ── Waveform layers / workspace ────────────────────────────────────────

    private double? _timeTillDropSeconds;
    public double? TimeTillDropSeconds
    {
        get => _timeTillDropSeconds;
        private set
        {
            this.RaiseAndSetIfChanged(ref _timeTillDropSeconds, value);
            this.RaisePropertyChanged(nameof(TimeTillDropBars));
            this.RaisePropertyChanged(nameof(TimeTillDropDisplay));
            this.RaisePropertyChanged(nameof(HasTimeTillDrop));
        }
    }

    public double? TimeTillDropBars => IntroTiming.CalculateBars(TimeTillDropSeconds, GridBpm);
    public bool HasTimeTillDrop => TimeTillDropSeconds.HasValue && TimeTillDropBars.HasValue;
    public string TimeTillDropDisplay => TimeTillDropSeconds is { } seconds && TimeTillDropBars is { } bars
        ? Utils.NumFormat.Ui($"{seconds:0.#} s · ≈{bars:0.#} bars")
        : "—";

    private bool _showEnergyCurve;
    public bool ShowEnergyCurve
    {
        get => _showEnergyCurve;
        set { if (_showEnergyCurve == value) return; this.RaiseAndSetIfChanged(ref _showEnergyCurve, value); SaveWaveformPreferences(); }
    }

    private bool _showOnsetDensity;
    public bool ShowOnsetDensity
    {
        get => _showOnsetDensity;
        set { if (_showOnsetDensity == value) return; this.RaiseAndSetIfChanged(ref _showOnsetDensity, value); SaveWaveformPreferences(); }
    }

    private bool _showVocalGhost;
    public bool ShowVocalGhost
    {
        get => _showVocalGhost;
        set { if (_showVocalGhost == value) return; this.RaiseAndSetIfChanged(ref _showVocalGhost, value); SaveWaveformPreferences(); }
    }

    private bool _showPhraseSections;
    public bool ShowPhraseSections
    {
        get => _showPhraseSections;
        set { if (_showPhraseSections == value) return; this.RaiseAndSetIfChanged(ref _showPhraseSections, value); SaveWaveformPreferences(); }
    }

    private bool _showBeatGrid;
    public bool ShowBeatGrid
    {
        get => _showBeatGrid;
        set { if (_showBeatGrid == value) return; this.RaiseAndSetIfChanged(ref _showBeatGrid, value); SaveWaveformPreferences(); }
    }

    private double _waveformGain = 1.0;
    public double WaveformGain
    {
        get => _waveformGain;
        private set => this.RaiseAndSetIfChanged(ref _waveformGain, value);
    }

    public ReactiveCommand<double, Unit> SetWaveformGainCommand { get; }

    private bool _isPreparationDockOpen = true;
    public bool IsPreparationDockOpen { get => _isPreparationDockOpen; set => this.RaiseAndSetIfChanged(ref _isPreparationDockOpen, value); }
    public ReactiveCommand<Unit, Unit> TogglePreparationDockCommand { get; }

    private bool _isGridModeOpen;
    public bool IsGridModeOpen { get => _isGridModeOpen; set => this.RaiseAndSetIfChanged(ref _isGridModeOpen, value); }

    private void SaveWaveformPreferences()
    {
        _config.WaveformShowEnergyCurve = ShowEnergyCurve;
        _config.WaveformShowOnsetDensity = ShowOnsetDensity;
        _config.WaveformShowVocalGhost = ShowVocalGhost;
        _config.WaveformShowPhraseSections = ShowPhraseSections;
        _config.WaveformShowBeatGrid = ShowBeatGrid;
        _config.WaveformGain = (float)WaveformGain;
        try { _configManager?.Save(_config); }
        catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't save waveform appearance preferences"); }
    }

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

    private bool _isBrowserOpen = true;
    /// <summary>The docked track browser on the left (playlist + its tracks, current one highlighted).</summary>
    public bool IsBrowserOpen { get => _isBrowserOpen; set => this.RaiseAndSetIfChanged(ref _isBrowserOpen, value); }

    /// <summary>True while the docked list's items are being replaced programmatically. The ListBox
    /// reports its transient selection back through the two-way binding during a rebuild (often
    /// row 1), and that is not a user click — it must never open a track.</summary>
    private bool _suppressBrowserSelection;

    /// <summary>The open track's row in the browser list (highlighted), or null when it isn't in the
    /// playlist. Selecting another row opens that track.</summary>
    public CueBrowserItem? CurrentBrowserItem
    {
        get => BrowserTracks.FirstOrDefault(t => string.Equals(t.GlobalId, TrackHash, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (_suppressBrowserSelection) return;
            if (value == null || string.Equals(value.GlobalId, TrackHash, StringComparison.OrdinalIgnoreCase)) return;
            LoadFromBrowserCommand.Execute(value);
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleBrowserCommand { get; }

    /// <summary>"Track 3 of 24", or why previous/next can't step.</summary>
    public string BrowserPosition
    {
        get
        {
            if (SelectedPlaylist == null || _allPlaylistTracks.Count == 0) return "";
            int idx = _allPlaylistTracks.FindIndex(t => string.Equals(t.GlobalId, TrackHash, StringComparison.OrdinalIgnoreCase));
            return idx < 0
                ? string.Create(CultureInfo.InvariantCulture, $"{_allPlaylistTracks.Count} tracks · the open track isn't in this playlist")
                : string.Create(CultureInfo.InvariantCulture, $"Track {idx + 1} of {_allPlaylistTracks.Count}");
        }
    }

    private void RaiseBrowserPosition()
    {
        this.RaisePropertyChanged(nameof(CurrentBrowserItem));
        this.RaisePropertyChanged(nameof(BrowserPosition));
    }

    /// <summary>
    /// After a track opens from outside the browser (restored session, Library, player): show a
    /// playlist that contains it, so previous / next step through that playlist. Keeps the current
    /// playlist when it already holds the track.
    /// </summary>
    private async Task SelectPlaylistContainingAsync(string trackHash)
    {
        if (_libraryService == null) return;
        if (_allPlaylistTracks.Any(t => string.Equals(t.GlobalId, trackHash, StringComparison.OrdinalIgnoreCase)))
        {
            await _ui(RaiseBrowserPosition);
            return;
        }
        try
        {
            var ids = await _libraryService.GetPlaylistIdsContainingTrackAsync(trackHash);
            await _ui(() =>
            {
                var match = Playlists.Where(p => ids.Contains(p.Id)).OrderByDescending(p => p.CreatedAt).FirstOrDefault();
                if (match != null) SelectedPlaylist = match;
                else RaiseBrowserPosition();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: couldn't look up the playlists of {Hash}", trackHash);
        }
    }

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
            });
            // Open on a playlist that holds the open track; otherwise the newest one.
            if (TrackHash is { } open) await SelectPlaylistContainingAsync(open);
            await _ui(() =>
            {
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

    /// <summary>Set by every browser read; a read that finishes after a newer one started is
    /// discarded so a slow response can never put the list back on an older order.</summary>
    private int _browserLoadVersion;

    private async Task LoadPlaylistBrowserAsync(Guid playlistId)
    {
        if (_libraryService == null) return;
        var version = ++_browserLoadVersion;
        BrowserStatus = "Loading tracks…";
        this.RaisePropertyChanged(nameof(HasBrowserStatus));
        try
        {
            var tracks = await _libraryService.GetPagedPlaylistTracksAsync(
                playlistId, skip: 0, take: 2000, downloadedOnly: true);
            if (version != _browserLoadVersion) return;

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
            if (version != _browserLoadVersion) return;
            _logger.LogWarning(ex, "CueForge: failed to load browser tracks for playlist {Id}", playlistId);
            BrowserStatus = "Couldn't load this playlist's tracks. See the log for details.";
            this.RaisePropertyChanged(nameof(HasBrowserStatus));
        }
    }

    /// <summary>A playlist was reordered somewhere in the app (see
    /// <see cref="PlaylistTrackOrderChangedEvent"/>). If it's the one the browser is showing,
    /// re-read it — the open track, the selection and Next/Previous must follow the new order.</summary>
    private async Task OnPlaylistOrderChangedAsync(PlaylistTrackOrderChangedEvent evt)
    {
        if (_libraryService == null) return;
        if (SelectedPlaylist is not { } selected || selected.Id != evt.PlaylistId) return;
        await LoadPlaylistBrowserAsync(selected.Id);
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
            // Replacing the items makes the ListBox re-report a selection (typically the first row).
            // That is bookkeeping, not a click, so it must not open a track — see CurrentBrowserItem.
            _suppressBrowserSelection = true;
            try
            {
                BrowserTracks.Clear();
                foreach (var t in filtered) BrowserTracks.Add(t);
            }
            finally { _suppressBrowserSelection = false; }

            BrowserStatus = filtered.Count > 0 ? ""
                : _allPlaylistTracks.Count == 0 ? "No downloaded tracks in this playlist."
                : "No tracks match the filter.";
            this.RaisePropertyChanged(nameof(HasBrowserStatus));
            RaiseBrowserPosition();
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

    /// <summary>Runs every checked profile against the current track and lists the results
    /// without changing the cue set.</summary>
    public ReactiveCommand<Unit, Unit> CompareProfilesCommand { get; }

    /// <summary>One click: empties the working set and resets the waveform view (zoom and scroll)
    /// so the track is a blank slate again. Undoable like every other cue edit.</summary>
    public ReactiveCommand<Unit, Unit> ClearCuesCommand { get; }

    /// <summary>Replaces a legacy cached waveform through the normal high-priority analysis queue.</summary>
    public ReactiveCommand<Unit, Unit> RefreshWaveformCommand { get; }

    /// <summary>Loads this track's hot and memory cues from Rekordbox's own library (read-only).</summary>
    public ReactiveCommand<Unit, Unit> ImportRekordboxCuesCommand { get; }

    /// <summary>Only offered when a Rekordbox 6/7 library and the reader are present.</summary>
    public bool CanImportRekordbox => _rekordbox?.IsAvailable == true;

    /// <summary>Beat jump: moves the playhead by <see cref="BeatJumpBars"/> bars (parameter −1 / +1),
    /// so "[IN -16] + 16 bars lands on the drop" can be checked without listening through the build.</summary>
    public ReactiveCommand<int, Unit> BeatJumpCommand { get; }
    public static IReadOnlyList<int> BeatJumpSizes { get; } = new[] { 1, 4, 8, 16, 32 };

    private int _beatJumpBars = 16;
    public int BeatJumpBars
    {
        get => _beatJumpBars;
        set => this.RaiseAndSetIfChanged(ref _beatJumpBars, BeatJumpSizes.Contains(value) ? value : 16);
    }

    /// <summary>Makes the grid beat nearest the playhead the "1" of a bar (fixes a downbeat on beat 3).</summary>
    public ReactiveCommand<Unit, Unit> SetDownbeatAtPlayheadCommand { get; }
    /// <summary>Slips the whole grid by 10 ms ("-" earlier, "+" later).</summary>
    public ReactiveCommand<string, Unit> SlipGridCommand { get; }
    /// <summary>Puts the downbeat back where the analysis had it when the track was opened.</summary>
    public ReactiveCommand<Unit, Unit> ResetGridCommand { get; }
    /// <summary>Corrects a wrong detected tempo: double / halve the grid BPM.</summary>
    public ReactiveCommand<Unit, Unit> DoubleBpmCommand { get; }
    public ReactiveCommand<Unit, Unit> HalveBpmCommand { get; }
    /// <summary>Applies the manually-typed BPM in <see cref="ManualBpmInput"/>.</summary>
    public ReactiveCommand<Unit, Unit> ApplyManualBpmCommand { get; }

    private double _analysedDownbeat;
    private bool _isGridEdited;
    public bool IsGridEdited { get => _isGridEdited; private set => this.RaiseAndSetIfChanged(ref _isGridEdited, value); }

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

    /// <summary>"Fit" when the whole track shows, else the zoom factor (between the zoom buttons).</summary>
    public string ZoomDisplay => WaveformZoom <= CueForgeGrid.MinZoom + 0.001
        ? "Fit"
        : $"{WaveformZoom.ToString("F1", CultureInfo.InvariantCulture)}×";

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
        CueSessionRegistry cueSessions,
        Services.Rekordbox.RekordboxLibraryService rekordbox)
        : this(cueService, new PlayerViewModelCueForgeTransport(playerViewModel, databaseService), config,
               libraryService, engineCueService, camelotKeyService, logger, configManager, dialogService, eventBus,
               cueSessions, ui: null, restoreSession: true, rekordbox: rekordbox)
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
        bool restoreSession = false,
        Services.Rekordbox.RekordboxLibraryService? rekordbox = null)
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
        _rekordbox = rekordbox;
        _ui = ui ?? RunOnUiThread;
        _selectedCueGenerationProfile = CueGenerationProfiles.Find(config.CueGenerationProfile);
        foreach (var profile in CueGenerationProfiles.All)
            ProfileComparisonOptions.Add(new CueGenerationProfileOption(profile, isSelected: true));
        _showEnergyCurve = config.WaveformShowEnergyCurve;
        _showOnsetDensity = config.WaveformShowOnsetDensity;
        _showVocalGhost = config.WaveformShowVocalGhost;
        _showPhraseSections = config.WaveformShowPhraseSections;
        _showBeatGrid = config.WaveformShowBeatGrid;
        WaveformGain = Math.Clamp((double)config.WaveformGain, 1.0, 2.0);

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
        // Saving is the end of the edit session: hand the player back so the playlist track that
        // was interrupted keeps playing (paused only while the cue was auditioned). Without this,
        // the mix stayed silent on the inspected track until the page was left.
        Session.Saved += (_, _) => HandBackPlayer(resumePlayback: true);

        var hasTrack = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));
        var notGenerating = this.WhenAnyValue(x => x.IsGenerating, g => !g);
        var notRefreshing = this.WhenAnyValue(x => x.IsWaveformRefreshPending, p => !p);
        var canAct = hasTrack.CombineLatest(notGenerating, (h, g) => h && g);
        var canRefresh = hasTrack.CombineLatest(notRefreshing, (h, r) => h && r);

        AutoGenerateCuesCommand = ReactiveCommand.CreateFromTask(AutoGenerateCuesAsync, canAct);
        CompareProfilesCommand = ReactiveCommand.CreateFromTask(CompareProfilesAsync, canAct);
        ClearCuesCommand = ReactiveCommand.Create(ClearCues, hasTrack);
        RefreshWaveformCommand = ReactiveCommand.CreateFromTask(RefreshWaveformAsync, canRefresh);
        ImportRekordboxCuesCommand = ReactiveCommand.CreateFromTask(ImportRekordboxCuesAsync, canAct);

        BeatJumpCommand = ReactiveCommand.Create<int>(dir => BeatJump(dir), hasTrack);
        SetDownbeatAtPlayheadCommand = ReactiveCommand.CreateFromTask(SetDownbeatAtPlayheadAsync, canAct);
        SlipGridCommand = ReactiveCommand.CreateFromTask<string>(SlipGridAsync, canAct);
        ResetGridCommand = ReactiveCommand.CreateFromTask(ResetGridAsync, this.WhenAnyValue(x => x.IsGridEdited));
        DoubleBpmCommand = ReactiveCommand.CreateFromTask(() => ApplyBpmAsync(GridBpm * 2), hasTrack);
        HalveBpmCommand = ReactiveCommand.CreateFromTask(() => ApplyBpmAsync(GridBpm / 2), hasTrack);
        ApplyManualBpmCommand = ReactiveCommand.CreateFromTask(ApplyManualBpmAsync, hasTrack);

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
        SetWaveformGainCommand = ReactiveCommand.Create<double>(gain =>
        {
            WaveformGain = Math.Clamp(gain, 1.0, 2.0);
            SaveWaveformPreferences();
        });
        TogglePreparationDockCommand = ReactiveCommand.Create(() => { IsPreparationDockOpen = !IsPreparationDockOpen; });

        LoadFromBrowserCommand = new Views.AsyncRelayCommand<CueBrowserItem>(async item =>
        {
            if (item == null) return;
            await LoadTrackAsync(item.GlobalId, item.Title, item.Artist);
        });
        ToggleBrowserCommand = ReactiveCommand.Create(() => { IsBrowserOpen = !IsBrowserOpen; });
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

        if (_eventBus != null)
        {
            _eventBus.GetEvent<TrackAnalysisUpdatedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt => _ = OnWaveformRefreshCompletedAsync(evt))
                .DisposeWith(_disposables);
            _eventBus.GetEvent<TrackAnalysisFailedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnWaveformRefreshFailed)
                .DisposeWith(_disposables);
            _eventBus.GetEvent<AnalysisProgressEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnWaveformRefreshProgress)
                .DisposeWith(_disposables);
            // The docked browser is a snapshot of one playlist. When that playlist is reordered
            // by any surface (Library's Reorder for better flow / Auto-Arrange, Flow Builder's
            // "Save order", Automix), re-read it so the list, the highlighted row, "Track N of M"
            // and Previous/Next all follow the playlist's current order.
            _eventBus.GetEvent<PlaylistTrackOrderChangedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt => _ = OnPlaylistOrderChangedAsync(evt))
                .DisposeWith(_disposables);
        }

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

    private Task RefreshWaveformAsync()
    {
        if (TrackHash == null || IsWaveformRefreshPending) return Task.CompletedTask;
        if (_eventBus == null)
        {
            const string message = "Full analysis isn't available in this session.";
            WaveformRefreshStatus = message;
            SetWaveformRefreshError(message);
            Session.SetStatus(message, isError: true);
            return Task.CompletedTask;
        }

        SetWaveformRefreshError(null);
        WaveformRefreshStatus = "Queued for full analysis…";
        IsWaveformRefreshPending = true;
        Session.SetStatus("Full analysis queued: waveform, loudness, tempo/key/grid, cues and available AI metrics. Your ORBIT cues stay local; nothing is written to Rekordbox.");
        _eventBus.Publish(new TrackAnalysisRequestedEvent(TrackHash, AnalysisTier.Tier1, IsHighPriority: true));
        return Task.CompletedTask;
    }

    private void OnWaveformRefreshProgress(AnalysisProgressEvent evt)
    {
        if (!IsWaveformRefreshPending || TrackHash == null ||
            !string.Equals(evt.TrackGlobalId, TrackHash, StringComparison.OrdinalIgnoreCase)) return;
        WaveformRefreshStatus = $"{evt.CurrentStep} · {evt.ProgressPercent}%";
    }

    private async Task OnWaveformRefreshCompletedAsync(TrackAnalysisUpdatedEvent evt)
    {
        if (!IsWaveformRefreshPending || TrackHash == null ||
            !string.Equals(evt.TrackGlobalId, TrackHash, StringComparison.OrdinalIgnoreCase)) return;

        // Reloading the track also reloads its cue session. Save a draft first, exactly as a manual
        // track switch would; if saving fails, keep the current editor and its unsaved work intact.
        if (Session.IsDirty && !await Session.SaveAsync())
        {
            IsWaveformRefreshPending = false;
            const string message = "Analysis finished, but Cue Forge couldn't reload it because saving your cue edits failed.";
            WaveformRefreshStatus = message;
            SetWaveformRefreshError(message);
            return;
        }

        SetWaveformRefreshError(null);
        IsWaveformRefreshPending = false;
        await LoadTrackAsync(TrackHash, TrackName, TrackArtist);
        Session.SetStatus("Full analysis refreshed: waveform, loudness, tempo/key/grid, cues and available AI metrics.");
    }

    private void OnWaveformRefreshFailed(TrackAnalysisFailedEvent evt)
    {
        if (!IsWaveformRefreshPending || TrackHash == null ||
            !string.Equals(evt.TrackGlobalId, TrackHash, StringComparison.OrdinalIgnoreCase)) return;
        IsWaveformRefreshPending = false;
        WaveformRefreshStatus = $"Full analysis failed: {evt.Error}";
        SetWaveformRefreshError(evt.Error);
        Session.SetStatus(WaveformRefreshStatus, isError: true);
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
        IsWaveformOutdated = false;
        IsWaveformRefreshPending = false;
        SetWaveformRefreshError(null);
        WaveformRefreshStatus = "";
        WaveformZoom = 1.0; WaveformScroll = 0;
        CurrentPlayPosition = 0;
        TimeTillDropSeconds = null;
        _resolvedPhrases = Array.Empty<PhraseSegment>();
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
                _analysedDownbeat = _downbeat;
                IsGridEdited = false;
                genre = !string.IsNullOrWhiteSpace(features.DetectedSubGenre) ? features.DetectedSubGenre : features.ElectronicSubgenre;
                _durationKnown = features.TrackDuration > 0;
                TrackDuration = features.TrackDuration > 0 ? features.TrackDuration : 300.0;
                TrackEnergyScore = features.EnergyScore;
                TimeTillDropSeconds = features.TimeTillDropSeconds is { } firstDrop && float.IsFinite(firstDrop) && firstDrop >= 0
                    ? firstDrop
                    : null;

                // Decode waveform blob: [low₀…lowN | mid₀…midN | high₀…highN]
                if (features.WaveformBlob is { Length: > 0 } blob && features.WaveformBlobSampleCount > 0 &&
                    blob.Length >= features.WaveformBlobSampleCount * 3)
                {
                    IsWaveformOutdated = !string.Equals(
                        features.AnalysisVersion,
                        Services.AudioAnalysis.WaveformExtractionService.CurrentWaveformVersion,
                        StringComparison.Ordinal);
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

                List<PhraseSegment> storedPhrases = new();
                if (_libraryService is not null)
                {
                    try { storedPhrases = PhraseSourceResolver.FromTrackPhrases(await _libraryService.GetPhrasesByHashAsync(trackHash)); }
                    catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't load stored phrase data for {Hash}", trackHash); }
                    if (version != _loadVersion) return;
                }

                // The phrase map, the section suggestions and Auto-generate all read this one list.
                // TrackPhrases is the fresher store: the legacy PhraseSegmentsJson column is only
                // written when empty, so older tracks keep boundaries from an analysis run on the
                // pre-correction (half-time) grid forever — exactly the stale "intro ends 1:27"
                // suggestion and mis-placed drop seen on Against The Grain.
                _resolvedPhrases = PhraseSourceResolver.Resolve(
                    features.PhraseSegmentsSource,
                    ParsePhraseSegments(features.PhraseSegmentsJson),
                    storedPhrases,
                    features.TrackDuration);

                var segments = _resolvedPhrases;
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
                TrackLoadError = "This track hasn't been analysed yet. Use Analyse track now below, or Re-analyse track in Preparation, to generate its complete analysis here.";
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

            // The browser follows the open track (its playlist, highlighted row, "Track 3 of 24").
            if (Playlists.Count > 0) await SelectPlaylistContainingAsync(trackHash);
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
        this.RaisePropertyChanged(nameof(TimeTillDropBars));
        this.RaisePropertyChanged(nameof(TimeTillDropDisplay));
        this.RaisePropertyChanged(nameof(HasTimeTillDrop));
    }

    /// <summary>Applies a corrected tempo, rescales the grid, and persists ManualBPM so it survives re-analysis.</summary>
    private async Task ApplyBpmAsync(double newBpm)
    {
        if (newBpm is < 40 or > 400) return; // sanity bound
        _preciseBpm = newBpm;
        Bpm = (int)Math.Round(newBpm);
        RaiseGridChanged();
        ManualBpmInput = newBpm.ToString("0.#", CultureInfo.InvariantCulture);

        if (TrackHash is not { } hash || _libraryService is null) return;
        try
        {
            await _libraryService.UpdateBpmAsync(hash, newBpm);
            _eventBus?.Publish(new TrackMetadataUpdatedEvent(hash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: failed to save BPM for {Hash}", hash);
        }
    }

    private async Task ApplyManualBpmAsync()
    {
        if (!double.TryParse(ManualBpmInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
            return;
        await ApplyBpmAsync(bpm);
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

    private sealed record PlayerBorrow(
        string? PreviousPath, string PreviousTitle, string PreviousArtist, double PreviousFraction, bool WasPlaying);
    private PlayerBorrow? _borrow;

    /// <summary>True when the shared player currently holds this track's audio.</summary>
    public bool IsAttachedToPlayer
    {
        get
        {
            if (TrackHash is null) return false;

            // The player's queue position is the authority on "what is loaded": the engine can be
            // playing a track while the cached file path still points at the previous one (see
            // PlayerViewModel.SetNowPlayingState), so comparing file paths alone said "not attached"
            // for the track that was audibly playing — no playhead, no pause icon, "sometimes".
            if (string.Equals(_transport.CurrentTrackHash, TrackHash, StringComparison.OrdinalIgnoreCase) &&
                _transport.CurrentFilePath is not null)
            {
                return true;
            }

            // Cue Forge also borrows the player for a track the queue is not on (auditioning the
            // incoming track, or a pad on a track the mix hasn't reached yet). Then the file that is
            // actually loaded is the only thing that identifies it.
            return SameAudioFile(_transport.CurrentFilePath, _trackFilePath);
        }
    }

    private static bool SameAudioFile(string? loaded, string? ours)
    {
        if (string.IsNullOrEmpty(loaded) || string.IsNullOrEmpty(ours)) return false;
        if (string.Equals(loaded, ours, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(loaded), System.IO.Path.GetFullPath(ours),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // malformed path — fall back to the hash check above
        }
    }

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
            _borrow = new PlayerBorrow(prev, _transport.Title, _transport.Artist, _transport.PositionFraction, _transport.IsPlaying);
        }
        _transport.Pause();
        _transport.LoadPaused(_trackFilePath!, TrackName, TrackArtist);
        this.RaisePropertyChanged(nameof(IsPlaying));
        return true;
    }

    /// <summary>Set while a track was opened for a specific purpose (Mix panel "Edit cues", Library
    /// "Open in Cue Forge"). Those links exist to work on an incoming/other track while something
    /// else plays, so the explicitly requested track wins for that one visit only.</summary>
    private DateTime _keepExplicitTrackUntilUtc;

    /// <summary>Marks the track currently loaded as deliberately chosen, so the next page open keeps
    /// it instead of following the player.</summary>
    public void KeepLoadedTrackForThisVisit() =>
        _keepExplicitTrackUntilUtc = DateTime.UtcNow.AddSeconds(15);

    /// <summary>
    /// The page was opened. Cue Forge shows what you can hear: when the shared player is on a
    /// different track than the one left open, follow it. This is what the constructor used to do
    /// once — but the view model is a singleton created at startup, so by the time you actually open
    /// the page it had no playing track to follow and kept whatever track was last edited.
    /// </summary>
    public async Task OnViewActivatedAsync()
    {
        bool pinned = DateTime.UtcNow < _keepExplicitTrackUntilUtc;
        _keepExplicitTrackUntilUtc = default;

        if (!pinned &&
            _transport.CurrentTrackHash is { Length: > 0 } playing &&
            !string.Equals(playing, TrackHash, StringComparison.OrdinalIgnoreCase))
        {
            var playlistBefore = SelectedPlaylist?.Id;
            await LoadTrackAsync(playing, _transport.Title, _transport.Artist);
            // Loading the playing track follows it into its own playlist, and that switch already
            // read the browser fresh — don't read it a second time.
            if (SelectedPlaylist?.Id != playlistBefore) return;
        }
        else
        {
            // Already on the open track (or the visit is pinned to it): re-read the player straight
            // away so the playhead and the play/pause icon are right the moment the page comes back
            // — even if the track was paused, seeked or advanced while another page was open.
            if (IsAttachedToPlayer)
                OnPlayerPosition(_transport.PositionFraction, _transport.LengthSeconds);
            this.RaisePropertyChanged(nameof(IsPlaying));
        }

        // The view model is a singleton, so the browser list is whatever the playlist looked like
        // the last time this page was open. Re-read it on every visit: a playlist reordered (or
        // edited) while Cue Forge was closed must show its current order here too — the list, the
        // highlighted row, "Track N of M" and Previous/Next.
        if (_libraryService != null && SelectedPlaylist is { } selected)
            await LoadPlaylistBrowserAsync(selected.Id);
    }

    /// <summary>
    /// The page was navigated away from. Stops loop audition and, if Cue Forge borrowed the shared
    /// player, gives it back to the playlist track it interrupted (at its old position) and lets it
    /// keep playing if it was playing before.
    /// </summary>
    public void OnViewDeactivated()
    {
        _keepExplicitTrackUntilUtc = default;
        IsAuditioningLoop = false;
        HandBackPlayer(resumePlayback: true);
    }

    /// <summary>
    /// Gives the shared player back to the track Cue Forge borrowed it from (the playlist's current
    /// track), at the position it had. With <paramref name="resumePlayback"/> the playlist carries on
    /// playing when it was playing before the audition — saving an edit no longer leaves the mix
    /// silent on the track that was just inspected. The queue is untouched: only the borrowed file
    /// was ever replaced, <c>CurrentTrack</c> still points at the playlist track.
    /// </summary>
    private void HandBackPlayer(bool resumePlayback)
    {
        if (_borrow is not { } borrow) return;
        _borrow = null;
        if (!IsAttachedToPlayer) return; // someone else already took the player over
        _transport.Pause();
        if (!string.IsNullOrEmpty(borrow.PreviousPath))
        {
            _transport.LoadPaused(borrow.PreviousPath!, borrow.PreviousTitle, borrow.PreviousArtist);
            _transport.SeekFraction(borrow.PreviousFraction);
            if (resumePlayback && borrow.WasPlaying) _transport.Play();
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
    private double BarSeconds => GridBpm > 0 ? 240.0 / GridBpm : 0;

    internal void BeatJump(int direction)
    {
        if (BarSeconds <= 0 || direction == 0) return;
        SetPlayhead(CurrentPlayPosition + Math.Sign(direction) * BeatJumpBars * BarSeconds);
        if (WaveformZoom > CueForgeGrid.MinZoom) WaveformScroll = CueForgeGrid.CenterOn(CurrentPlayPosition / TrackDuration, WaveformZoom);
    }

    /// <summary>
    /// The grid beat nearest the playhead becomes a downbeat: the grid's phase moves by whole beats,
    /// its tempo and beat positions stay. Bar 1 stays where the analysis put it (where the music
    /// starts, which the generator anchors the Intro cue on), moved by less than a bar to the new phase.
    /// </summary>
    internal async Task SetDownbeatAtPlayheadAsync()
    {
        if (GridBpm <= 0) return;
        double beat = 60.0 / GridBpm, bar = beat * 4;
        double nearestBeat = _downbeat + Math.Round((CurrentPlayPosition - _downbeat) / beat) * beat;
        double barOne = nearestBeat + Math.Round((_analysedDownbeat - nearestBeat) / bar) * bar;
        if (barOne < 0) barOne += bar;
        await ApplyGridAsync(barOne, $"The beat at {CueForgeGrid.FormatTime(nearestBeat)} is now the 1 of its bar. Re-run Auto-generate to re-place the auto cues.");
    }

    internal Task SlipGridAsync(string direction)
    {
        double delta = direction == "-" ? -0.010 : 0.010;
        double next = _downbeat + delta;
        if (next < 0) next += BarSeconds;
        return ApplyGridAsync(next, $"Grid moved {(delta < 0 ? "10 ms earlier" : "10 ms later")}.");
    }

    internal Task ResetGridAsync() => ApplyGridAsync(_analysedDownbeat, "Beat grid back to the analysed downbeat.");

    /// <summary>Saves the corrected downbeat to the track's analysis, so exports, mixing and the next
    /// auto-generate all use it. Cues are left where they are.</summary>
    private async Task ApplyGridAsync(double downbeat, string status)
    {
        if (TrackHash is not { } hash || _libraryService == null) return;
        try
        {
            var features = await _libraryService.GetAudioFeaturesByHashAsync(hash);
            if (features is null)
            {
                Session.SetStatus("This track has no analysis to store a beat grid in. Analyse it first.", isError: true);
                return;
            }
            features.DownbeatOffsetSeconds = downbeat;
            await _libraryService.UpdateAudioFeaturesAsync(features);
            _logger.LogInformation("CueForge: downbeat for {Hash} set to {Downbeat:F4}s (analysed {Analysed:F4}s)", hash, downbeat, _analysedDownbeat);

            await _ui(() =>
            {
                _downbeat = downbeat;
                IsGridEdited = Math.Abs(downbeat - _analysedDownbeat) > 1e-4;
                RaiseGridChanged();
                Session.UpdateGrid(downbeat);
                Session.SetStatus(status);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: saving the beat grid failed for {Hash}", hash);
            Session.SetStatus("Couldn't save the beat grid change; nothing was changed. See the log for details.", isError: true);
        }
    }

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

    internal async Task ImportRekordboxCuesAsync()
    {
        if (_rekordbox == null || Session.TrackHash is null) return;
        if (string.IsNullOrEmpty(_trackFilePath))
        {
            Session.SetStatus("This track's file location is unknown, so it can't be matched in Rekordbox.", isError: true);
            return;
        }
        try
        {
            var read = await _rekordbox.ReadCuesAsync();
            if (!read.Success)
            {
                Session.SetStatus(read.Message, isError: true);
                return;
            }
            var track = await _rekordbox.FindByPathAsync(_trackFilePath);
            if (track is not { Cues.Count: > 0 })
            {
                Session.SetStatus("Rekordbox has no cues for this file (matched by file path).");
                return;
            }
            var imported = Services.Rekordbox.RekordboxCueImport.ToOrbitCues(track);
            int hot = imported.Count(c => c.SlotIndex >= 0), loops = imported.Count(c => c.IsLoop);
            await _ui(() => Session.ReplaceAllCues(
                Services.Rekordbox.RekordboxCueImport.Merge(Session.Cues.ToList(), imported),
                $"Loaded {imported.Count} cues from Rekordbox ({hot} hot, {imported.Count - hot - loops} memory, {loops} loops). Save to keep them; Ctrl+Z undoes."));
            _logger.LogInformation("CueForge: imported {Count} Rekordbox cues for {Hash}", imported.Count, TrackHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: importing Rekordbox cues failed for {Hash}", TrackHash);
            Session.SetStatus("Couldn't load cues from Rekordbox; your cues are unchanged. See the log for details.", isError: true);
        }
    }

    private void ClearCues()
    {
        if (TrackHash is null) return;
        Session.ReplaceAllCues(Array.Empty<OrbitCue>(),
            "Cleared every cue. Ctrl+Z puts them back.");
        // "Reset the waveform" too: back to the whole-track view and the top of the track, so the
        // slate is visually clean, not just the cue list.
        WaveformZoom = 1.0;
        WaveformScroll = 0;
        if (!IsPlaying) SetPlayhead(0);
    }

    private async Task CompareProfilesAsync()
    {
        if (TrackHash is null || _engineCueService is null || _libraryService is null) return;

        var selected = ProfileComparisonOptions.Where(o => o.IsSelected).Select(o => o.Profile).ToList();
        if (selected.Count == 0)
        {
            ProfileComparisonStatus = "Select at least one profile to compare.";
            return;
        }

        IsComparingProfiles = true;
        ProfileComparisonStatus = "Comparing…";
        try
        {
            var features = await _libraryService.GetAudioFeaturesByHashAsync(TrackHash);
            if (features is null)
            {
                ProfileComparisonStatus = "No analysis data for this track.";
                return;
            }

            var analysis = BuildAnalysisResultFromFeatures(features, _resolvedPhrases);
            double downbeat = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0.0;
            var rows = new List<string>();

            foreach (var profile in selected)
            {
                var (cues, path) = _engineCueService.GenerateCuesWithPath(
                    trackHash: TrackHash,
                    analysis: analysis,
                    downbeatAnchor: downbeat,
                    vocalStart: features.VocalStartSeconds.HasValue ? (double?)features.VocalStartSeconds.Value : null,
                    vocalEnd: features.VocalEndSeconds.HasValue ? (double?)features.VocalEndSeconds.Value : null,
                    vocalIntensity: features.VocalIntensity > 0 ? (double?)features.VocalIntensity : null,
                    profile: profile);

                var drops = cues.Where(c => c.Type == CuePointType.Drop)
                    .OrderBy(c => c.TimestampInSeconds).ToList();
                string d1 = drops.Count > 0 ? drops[0].TimestampInSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "-";
                string d2 = drops.Count > 1 ? drops[1].TimestampInSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "-";
                rows.Add($"{profile.Name,-22} {path,-15} D1 {d1,7}  D2 {d2,7}  {cues.Count} cues");
            }

            await _ui(() =>
            {
                ProfileComparisonRows.Clear();
                foreach (var row in rows) ProfileComparisonRows.Add(row);
                ProfileComparisonStatus = $"Compared {rows.Count} profile(s); cues were not changed.";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: profile comparison failed for {Hash}", TrackHash);
            ProfileComparisonStatus = "Comparison failed; the cue set was not changed.";
        }
        finally { IsComparingProfiles = false; }
    }

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

            var analysis = BuildAnalysisResultFromFeatures(features, _resolvedPhrases);
            double downbeat = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0.0;

            var generatedEntities = _engineCueService.GenerateCues(
                trackHash: TrackHash,
                analysis: analysis,
                downbeatAnchor: downbeat,
                vocalStart: features.VocalStartSeconds.HasValue ? (double?)features.VocalStartSeconds.Value : null,
                vocalEnd: features.VocalEndSeconds.HasValue ? (double?)features.VocalEndSeconds.Value : null,
                vocalIntensity: features.VocalIntensity > 0 ? (double?)features.VocalIntensity : null,
                profile: SelectedCueGenerationProfile);

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
                ResolvePadConflicts(merged);
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

    /// <summary>
    /// Generated cues keep the pads the shared drop layout gave them (A B C / D E F / G, or no pad for
    /// countdowns routed to memory cues), so Cue Forge, the background generator and the other editors
    /// agree. Your own cues win a contested pad; the generated cue then becomes a memory cue.
    /// (This used to re-pack every generated cue by role, putting the drops on A and B here only.)
    /// </summary>
    internal static void ResolvePadConflicts(List<OrbitCue> cues)
    {
        var userPads = cues.Where(c => c.Source == CueSource.User && !c.IsLoop && c.SlotIndex >= 0)
            .Select(c => c.SlotIndex).ToHashSet();
        foreach (var c in cues.Where(c => c.Source == CueSource.Auto && !c.IsLoop && userPads.Contains(c.SlotIndex)))
            c.SlotIndex = -1;
    }

    /// <summary>Role colours for generated cues, except drops, countdowns and [OUT], which keep the
    /// layout's per-drop colours (Drop 1 and Drop 2 groups stay distinguishable).</summary>
    private static void ApplyRekordboxColors(List<OrbitCue> cues)
    {
        foreach (var c in cues.Where(c => c.Source == CueSource.Auto && !Engine.Cueing.DropCountdownCues.IsLayoutCue(c.Name)))
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
    // background pipeline also calls, so both routes score cues against identical signals. The
    // phrase list is passed explicitly because the view model resolves it from TrackPhrases, which
    // the builder cannot reach on its own.
    internal static Engine.Analysis.AnalysisPipelineResult BuildAnalysisResultFromFeatures(
        AudioFeaturesEntity f, IReadOnlyList<PhraseSegment>? phrases = null) =>
        Engine.Analysis.AnalysisPipelineResultBuilder.Build(f, phrases);

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
