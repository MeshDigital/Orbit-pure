using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
///   • Working draft isolation: edits stay in <see cref="WorkingCues"/> until Save (Ctrl+S); switching
///     tracks auto-saves a dirty draft first so work is never silently lost.
///   • Persistence goes through <see cref="ICuePointService"/> (the CuePoints table) — the same rows
///     the Rekordbox export, workstation and Flow Builder read.
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

    private const int MaxUndoHistory = 50;
    private const string LoopColor = "#E0641B";   // Rekordbox loop orange (cue data, not UI chrome)
    private const string DefaultCueColor = "#28E214"; // Rekordbox default hot cue green

    private readonly List<OrbitCue[]> _undoStack = new();
    private readonly Stack<OrbitCue[]> _redoStack = new();
    private readonly HashSet<OrbitCue> _trackedCues = new();

    // ── Observable Collections ─────────────────────────────────────────────

    public ObservableCollection<OrbitCue> WorkingCues { get; } = new();
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

    /// <summary>Bumped on every in-place cue edit (rename, recolour, move) so the waveform redraws
    /// without the collection itself changing.</summary>
    private int _cueRevision;
    public int CueRevision { get => _cueRevision; private set => this.RaiseAndSetIfChanged(ref _cueRevision, value); }

    // ── Harmonic / Vocal Context ───────────────────────────────────────────

    private string _currentCamelotKey = "—";
    public string CurrentCamelotKey { get => _currentCamelotKey; private set => this.RaiseAndSetIfChanged(ref _currentCamelotKey, value); }

    private string _compatibleCamelotKeys = "";
    public string CompatibleCamelotKeys { get => _compatibleCamelotKeys; private set => this.RaiseAndSetIfChanged(ref _compatibleCamelotKeys, value); }

    private bool _isInVocalRegion;
    public bool IsInVocalRegion { get => _isInVocalRegion; private set => this.RaiseAndSetIfChanged(ref _isInVocalRegion, value); }

    // ── Snap ───────────────────────────────────────────────────────────────

    private bool _snapToGrid = true;
    public bool SnapToGrid { get => _snapToGrid; set => this.RaiseAndSetIfChanged(ref _snapToGrid, value); }

    public static IReadOnlyList<string> SnapResolutions => CueForgeGrid.SnapResolutions;

    private string _snapResolution = "Beat";
    public string SnapResolution
    {
        get => _snapResolution;
        set
        {
            if (!CueForgeGrid.SnapResolutions.Contains(value)) value = "Beat";
            this.RaiseAndSetIfChanged(ref _snapResolution, value);
            this.RaisePropertyChanged(nameof(SnapBeats));
        }
    }

    public int SnapBeats => CueForgeGrid.ResolutionBeats(SnapResolution);

    /// <summary>Applies the current snap setting (grid anchored at the downbeat, precise BPM).</summary>
    public double SnapTime(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, TrackDuration);
        if (!SnapToGrid) return seconds;
        return Math.Clamp(CueForgeGrid.Snap(seconds, GridBpm, GridOffset, SnapBeats), 0, TrackDuration);
    }

    // ── Selection & inline editing ─────────────────────────────────────────

    private OrbitCue? _selectedCue;
    public OrbitCue? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (ReferenceEquals(_selectedCue, value)) return;
            if (_selectedCue != null) _selectedCue.PropertyChanged -= OnSelectedCuePropertyChanged;
            this.RaiseAndSetIfChanged(ref _selectedCue, value);
            if (_selectedCue != null) _selectedCue.PropertyChanged += OnSelectedCuePropertyChanged;
            _editingCueName = value?.Name ?? "";
            this.RaisePropertyChanged(nameof(EditingCueName));
            RaiseSelectionDerived();
            foreach (var pad in HotCuePads) pad.IsSelected = pad.Cue != null && ReferenceEquals(pad.Cue, value);
        }
    }

    private void OnSelectedCuePropertyChanged(object? sender, PropertyChangedEventArgs e) => RaiseSelectionDerived();

    private void RaiseSelectionDerived()
    {
        this.RaisePropertyChanged(nameof(HasSelectedCue));
        this.RaisePropertyChanged(nameof(IsSelectedCueLoop));
        this.RaisePropertyChanged(nameof(IsSelectedCuePoint));
        this.RaisePropertyChanged(nameof(SelectedCueRole));
        this.RaisePropertyChanged(nameof(SelectedCueSlot));
        this.RaisePropertyChanged(nameof(SelectedCuePositionDisplay));
        this.RaisePropertyChanged(nameof(SelectedCueDetail));
        this.RaisePropertyChanged(nameof(SelectedCueConfidence));
        this.RaisePropertyChanged(nameof(IsSelectedCueAuto));
        this.RaisePropertyChanged(nameof(ActiveLoop));
        this.RaisePropertyChanged(nameof(HasActiveLoop));
        this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
    }

    public bool HasSelectedCue => SelectedCue != null;
    public bool IsSelectedCueLoop => SelectedCue?.IsLoop == true;
    public bool IsSelectedCuePoint => SelectedCue is { IsLoop: false };
    public bool IsSelectedCueAuto => SelectedCue?.Source == CueSource.Auto;
    public double SelectedCueConfidence => SelectedCue?.Confidence ?? 0;

    public string SelectedCuePositionDisplay => SelectedCue == null ? ""
        : $"{CueForgeGrid.FormatTime(SelectedCue.Timestamp)}  ·  {CueForgeGrid.FormatBarBeat(SelectedCue.Timestamp, GridBpm, GridOffset)}";

    public string SelectedCueDetail => SelectedCue switch
    {
        null => "",
        { IsLoop: true } l => $"Loop · {FormatLoopLength(l.Timestamp, l.LoopEndSeconds)}",
        { Source: CueSource.Auto } a => $"Auto-generated · {(int)Math.Round(a.Confidence * 100)}% confidence",
        _ => "Your cue",
    };

    private string _editingCueName = "";
    /// <summary>Text in the inspector's name box. Applied by <see cref="CommitCueName"/> (Enter / focus
    /// out), thrown away by <see cref="RevertCueName"/> (Esc).</summary>
    public string EditingCueName { get => _editingCueName; set => this.RaiseAndSetIfChanged(ref _editingCueName, value ?? ""); }

    public void CommitCueName()
    {
        var cue = SelectedCue;
        if (cue == null || TrackHash is null) return;
        var name = EditingCueName.Trim();
        if (name.Length == 0) { RevertCueName(); return; }
        if (name == cue.Name) return;
        PushSnapshot();
        cue.Name = name;
        MarkCueFieldEdited(cue);
    }

    public void RevertCueName() => EditingCueName = SelectedCue?.Name ?? "";

    /// <summary>Raised when something asks for the selected cue's name box to take focus (pad /
    /// list context menu "Rename").</summary>
    public event EventHandler? RenameRequested;

    public void RequestRename(OrbitCue cue)
    {
        SelectedCue = cue;
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    public CueRole SelectedCueRole
    {
        get => SelectedCue?.Role ?? CueRole.Custom;
        set { if (SelectedCue != null && SelectedCue.Role != value) SetSelectedCueRole(value); }
    }

    public CueSlotOption? SelectedCueSlot
    {
        get => SelectedCue == null ? null : CueSlotOptions.FirstOrDefault(o => o.Value == Math.Clamp(SelectedCue.SlotIndex, -1, 7));
        set { if (value != null && SelectedCue != null && SelectedCue.SlotIndex != value.Value) SetSelectedCueSlot(value.Value); }
    }

    // ── Save state ─────────────────────────────────────────────────────────

    private bool _hasUncommittedChanges;
    public bool HasUncommittedChanges
    {
        get => _hasUncommittedChanges;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasUncommittedChanges, value);
            this.RaisePropertyChanged(nameof(IsSaved));
        }
    }

    /// <summary>Track loaded and nothing unsaved — drives the quiet "Saved" indicator.</summary>
    public bool IsSaved => HasTrackLoaded && !HasUncommittedChanges;

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }

    private bool _canRedo;
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; private set => this.RaiseAndSetIfChanged(ref _isGenerating, value); }

    // ── Loops ──────────────────────────────────────────────────────────────

    private double? _pendingLoopIn;
    /// <summary>Loop IN set with the IN button, waiting for OUT.</summary>
    public double? PendingLoopIn
    {
        get => _pendingLoopIn;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pendingLoopIn, value);
            this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
        }
    }

    /// <summary>The loop the loop tools act on: the selected loop, else the most recent one.</summary>
    public OrbitCue? ActiveLoop => SelectedCue is { IsLoop: true } l ? l : WorkingCues.LastOrDefault(c => c.IsLoop);
    public bool HasActiveLoop => ActiveLoop != null;

    public string ActiveLoopDisplay => PendingLoopIn is { } inAt
        ? $"IN {CueForgeGrid.FormatTime(inAt)} — press OUT"
        : ActiveLoop is { } l ? $"{FormatLoopLength(l.Timestamp, l.LoopEndSeconds)} at {CueForgeGrid.FormatTime(l.Timestamp)}"
        : "No loop";

    private bool _isAuditioningLoop;
    /// <summary>When on, playback wraps from the active loop's end back to its start.</summary>
    public bool IsAuditioningLoop
    {
        get => _isAuditioningLoop;
        set
        {
            var loop = ActiveLoop;
            if (value && loop == null) value = false;
            if (value == _isAuditioningLoop) { this.RaisePropertyChanged(); return; }
            this.RaiseAndSetIfChanged(ref _isAuditioningLoop, value);
            if (value && loop != null) PlayFrom(loop.Timestamp);
        }
    }

    // ── Option lists ───────────────────────────────────────────────────────

    public static IReadOnlyList<CueRole> CueRoleOptions { get; } = Enum.GetValues<CueRole>();

    /// <summary>Rekordbox hot cue palette (cue data written to the export, not UI chrome).</summary>
    public static IReadOnlyList<string> CueColorSwatches { get; } = new[]
    {
        "#E62828", "#E0641B", "#B4BE04", "#28E214", "#10B176", "#305AFF", "#AA72FF", "#DE44CF",
    };

    public static IReadOnlyList<CueSlotOption> CueSlotOptions { get; } =
        Enumerable.Range(0, 8).Select(i => new CueSlotOption($"Hot cue {(char)('A' + i)}", i))
            .Append(new CueSlotOption("Memory cue", -1))
            .ToList();

    // ── Hot Cue Pads ──────────────────────────────────────────────────────

    public IReadOnlyList<HotCuePadInfo> HotCuePads { get; } =
        Enumerable.Range(0, 8).Select(i => new HotCuePadInfo { Slot = i, PadLabel = ((char)('A' + i)).ToString() }).ToList();

    private void RefreshHotCuePads()
    {
        foreach (var pad in HotCuePads)
        {
            var cue = WorkingCues.FirstOrDefault(c => c.SlotIndex == pad.Slot && !c.IsLoop);
            pad.Assign(cue);
            pad.IsSelected = cue != null && ReferenceEquals(cue, SelectedCue);
        }
        this.RaisePropertyChanged(nameof(MemoryCueCount));
        this.RaisePropertyChanged(nameof(CueCountDisplay));
    }

    public int MemoryCueCount => WorkingCues.Count(c => !c.IsLoop && c.SlotIndex < 0);

    public string CueCountDisplay
    {
        get
        {
            int hot = WorkingCues.Count(c => !c.IsLoop && c.SlotIndex >= 0);
            int loops = WorkingCues.Count(c => c.IsLoop);
            return $"{hot} hot · {MemoryCueCount} memory · {loops} loop{(loops == 1 ? "" : "s")}";
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

    // ── Commands ───────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddCueAtPlayheadCommand { get; }
    public ReactiveCommand<double, Unit> AddCueAtCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoGenerateCuesCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> DeleteCueCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCueCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopInCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopOutCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopHalfCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopDoubleCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLoopCommand { get; }
    public ReactiveCommand<Unit, Unit> CommitChangesCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscardChangesCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomToFitCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCueCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCueCommand { get; }
    /// <summary>Parameter: bar count as an int or a numeric string (XAML literal).</summary>
    public ReactiveCommand<object, Unit> SetQuickLoopCommand { get; }
    /// <summary>Pad click: assigned → jump there and play; empty → set a hot cue at the playhead.</summary>
    public ReactiveCommand<HotCuePadInfo, Unit> TriggerPadCommand { get; }
    /// <summary>Pad menu "Clear pad": the cue stays, as a memory cue.</summary>
    public ReactiveCommand<HotCuePadInfo, Unit> ClearPadCommand { get; }
    public ReactiveCommand<double, Unit> SeekPlayheadCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> AuditionCueCommand { get; }
    public ReactiveCommand<string, Unit> NudgeCueCommand { get; }
    public ReactiveCommand<Unit, Unit> BackToMixTransitionCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> SelectCueCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> CueDragStartedCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> CueUpdatedCommand { get; }
    public ReactiveCommand<CueRole, Unit> SetSelectedCueRoleCommand { get; }
    public ReactiveCommand<string, Unit> SetSelectedCueColorCommand { get; }
    public ReactiveCommand<int, Unit> SetSelectedCueSlotCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    /// <summary>CDJ-style CUE: playing → back to the cue point and pause; paused → set the cue point here.</summary>
    public ReactiveCommand<Unit, Unit> CueButtonCommand { get; }
    public System.Windows.Input.ICommand PlaybackToggleCommand => PlayPauseCommand;
    public ReactiveCommand<Unit, Unit> SetDropAtPlayheadCommand { get; }
    /// <summary>Shift+1 / Shift+2: make the playhead Drop 1 or Drop 2.</summary>
    public ReactiveCommand<int, Unit> SetNumberedDropAtPlayheadCommand { get; }
    /// <summary>Waveform right-click "Drop here" (seconds).</summary>
    public ReactiveCommand<double, Unit> SetDropAtCommand { get; }

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

    private string _lastCommitMessage = "";
    /// <summary>Status line: last save/edit result or hint. <see cref="HasCommitError"/> marks failures.</summary>
    public string LastCommitMessage { get => _lastCommitMessage; private set => this.RaiseAndSetIfChanged(ref _lastCommitMessage, value); }

    private bool _hasCommitError;
    public bool HasCommitError { get => _hasCommitError; private set => this.RaiseAndSetIfChanged(ref _hasCommitError, value); }

    private string? _trackLoadError;
    /// <summary>Set when <see cref="LoadTrackAsync"/> fails partway through so the UI isn't left silently half-loaded.</summary>
    public string? TrackLoadError { get => _trackLoadError; private set => this.RaiseAndSetIfChanged(ref _trackLoadError, value); }

    private void SetStatus(string message, bool isError = false)
    {
        HasCommitError = isError;
        LastCommitMessage = message;
    }

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
        IEventBus eventBus)
        : this(cueService, new PlayerViewModelCueForgeTransport(playerViewModel, databaseService), config,
               libraryService, engineCueService, camelotKeyService, logger, configManager, dialogService, eventBus,
               ui: null, restoreSession: true)
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

        var hasTrack = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));
        var notGenerating = this.WhenAnyValue(x => x.IsGenerating, g => !g);
        var canAct = hasTrack.CombineLatest(notGenerating, (h, g) => h && g);
        var hasSelection = this.WhenAnyValue(x => x.SelectedCue).Select(c => c != null);

        AddCueAtPlayheadCommand = ReactiveCommand.CreateFromTask(() => AddCueAtTimeAsync(SnapTime(CurrentPlayPosition)), canAct);
        AddCueAtCommand = ReactiveCommand.CreateFromTask<double>(t => AddCueAtTimeAsync(SnapTime(t)), canAct);
        SetDropAtPlayheadCommand = ReactiveCommand.Create(() => SetDropAt(CurrentPlayPosition, null), canAct);
        SetNumberedDropAtPlayheadCommand = ReactiveCommand.Create<int>(n => SetDropAt(CurrentPlayPosition, n), canAct);
        SetDropAtCommand = ReactiveCommand.Create<double>(t => SetDropAt(t, null), canAct);
        AutoGenerateCuesCommand = ReactiveCommand.CreateFromTask(AutoGenerateCuesAsync, canAct);
        DeleteCueCommand = ReactiveCommand.Create<OrbitCue>(DeleteCue, canAct);
        DeleteSelectedCueCommand = ReactiveCommand.Create(() => { if (SelectedCue != null) DeleteCue(SelectedCue); },
            canAct.CombineLatest(hasSelection, (a, s) => a && s));
        SetLoopInCommand = ReactiveCommand.Create(SetLoopIn, canAct);
        SetLoopOutCommand = ReactiveCommand.Create(SetLoopOut, canAct);
        var hasLoop = this.WhenAnyValue(x => x.HasActiveLoop);
        LoopHalfCommand = ReactiveCommand.Create(() => ScaleLoop(0.5), hasLoop);
        LoopDoubleCommand = ReactiveCommand.Create(() => ScaleLoop(2.0), hasLoop);
        ClearLoopCommand = ReactiveCommand.Create(ClearLoop, hasLoop);
        CommitChangesCommand = ReactiveCommand.CreateFromTask(CommitChangesAsync,
            this.WhenAnyValue(x => x.HasUncommittedChanges, x => x.TrackHash, (d, h) => d && h != null));
        DiscardChangesCommand = ReactiveCommand.CreateFromTask(DiscardChangesAsync,
            this.WhenAnyValue(x => x.HasUncommittedChanges, x => x.TrackHash, (d, h) => d && h != null));
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));

        ZoomInCommand     = ReactiveCommand.Create(() => ZoomAroundPlayhead(1.5));
        ZoomOutCommand    = ReactiveCommand.Create(() => ZoomAroundPlayhead(1 / 1.5));
        ZoomToFitCommand  = ReactiveCommand.Create(() => { WaveformZoom = 1.0; WaveformScroll = 0; });

        PreviousCueCommand = ReactiveCommand.Create(NavigateToPreviousCue, canAct);
        NextCueCommand     = ReactiveCommand.Create(NavigateToNextCue, canAct);
        BackToMixTransitionCommand = ReactiveCommand.Create(BackToMixTransition, this.WhenAnyValue(x => x.HasMixOrigin));
        SetQuickLoopCommand = ReactiveCommand.Create<object>(param =>
        {
            if (param is int i) SetQuickLoop(i);
            else if (int.TryParse(param?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bars)) SetQuickLoop(bars);
        }, canAct);
        TriggerPadCommand = ReactiveCommand.Create<HotCuePadInfo>(TriggerPad, canAct);
        ClearPadCommand = ReactiveCommand.Create<HotCuePadInfo>(ClearPad, canAct);
        SeekPlayheadCommand = ReactiveCommand.Create<double>(SetPlayhead);

        AuditionCueCommand = ReactiveCommand.Create<OrbitCue>(AuditionCue);
        // <string, Unit>: XAML literals like CommandParameter="-1" arrive as strings; a typed <int>
        // command throws from ICommand.Execute and used to take the whole app down.
        NudgeCueCommand    = ReactiveCommand.Create<string>(NudgeCue, canAct);
        SelectCueCommand   = ReactiveCommand.Create<OrbitCue>(cue => SelectedCue = cue);
        // Fired by CueForgeWaveformControl the instant a cue/loop handle is hit, before it moves, so
        // the undo snapshot captures the real pre-drag position.
        CueDragStartedCommand = ReactiveCommand.Create<OrbitCue>(c => { PushSnapshot(); _dragStart = (c, c.Timestamp); });
        // Fired on release once a drag moved something (the control mutates the cue in place).
        CueUpdatedCommand = ReactiveCommand.Create<OrbitCue>(cue =>
        {
            bool wasAuto = cue.Source == CueSource.Auto;
            if (wasAuto) cue.Source = CueSource.User;
            if (cue.IsLoop && cue.LoopEndSeconds < cue.Timestamp)
                (cue.Timestamp, cue.LoopEndSeconds) = (cue.LoopEndSeconds, cue.Timestamp);
            if (cue.IsLoop) cue.Name = FormatLoopLength(cue.Timestamp, cue.LoopEndSeconds);
            HasUncommittedChanges = true;
            double from = _dragStart.Cue == cue ? _dragStart.From : cue.Timestamp;
            ApplyCueMoved(cue, from, wasAuto);
            ResortWorkingCues();
            RefreshHotCuePads();
        });
        SetSelectedCueRoleCommand  = ReactiveCommand.Create<CueRole>(SetSelectedCueRole, canAct);
        SetSelectedCueColorCommand = ReactiveCommand.Create<string>(SetSelectedCueColor, canAct);
        SetSelectedCueSlotCommand  = ReactiveCommand.Create<int>(SetSelectedCueSlot, canAct);
        PlayPauseCommand = ReactiveCommand.Create(TogglePlayPause, hasTrack);
        CueButtonCommand = ReactiveCommand.Create(CueButton, hasTrack);

        LoadFromBrowserCommand = new Views.AsyncRelayCommand<CueBrowserItem>(async item =>
        {
            if (item == null) return;
            await LoadTrackAsync(item.GlobalId, item.Title, item.Artist);
        });
        PreviousTrackCommand = ReactiveCommand.CreateFromTask(() => StepTrackAsync(-1), hasTrack);
        NextTrackCommand = ReactiveCommand.CreateFromTask(() => StepTrackAsync(1), hasTrack);

        WorkingCues.CollectionChanged += OnWorkingCuesChanged;

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

    private void OnWorkingCuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Track in-place edits (rename, recolour, drag) so the waveform and pads stay current.
        var now = new HashSet<OrbitCue>(WorkingCues);
        foreach (var gone in _trackedCues.Where(c => !now.Contains(c)).ToList())
        {
            gone.PropertyChanged -= OnCuePropertyChanged;
            _trackedCues.Remove(gone);
        }
        foreach (var added in now.Where(c => !_trackedCues.Contains(c)))
        {
            added.PropertyChanged += OnCuePropertyChanged;
            _trackedCues.Add(added);
        }

        if (!_suppressDirty) HasUncommittedChanges = true;
        if (SelectedCue != null && !now.Contains(SelectedCue)) SelectedCue = null;
        RefreshHotCuePads();
        this.RaisePropertyChanged(nameof(ActiveLoop));
        this.RaisePropertyChanged(nameof(HasActiveLoop));
        this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
        CueRevision++;
    }

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        CueRevision++;
        if (e.PropertyName is nameof(OrbitCue.Name) or nameof(OrbitCue.Color) or nameof(OrbitCue.SlotIndex)
            or nameof(OrbitCue.Timestamp) or nameof(OrbitCue.Role))
            RefreshHotCuePads();
        if (e.PropertyName is nameof(OrbitCue.Timestamp) or nameof(OrbitCue.LoopEndSeconds) or nameof(OrbitCue.IsLoop))
            this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
    }

    private bool _suppressDirty;

    /// <summary>Replaces the working set without marking it dirty (load/revert).</summary>
    private void LoadWorkingCuesClean(IEnumerable<OrbitCue> cues)
    {
        _suppressDirty = true;
        try
        {
            WorkingCues.Clear();
            foreach (var c in cues.OrderBy(c => c.Timestamp)) WorkingCues.Add(c);
        }
        finally { _suppressDirty = false; }
        HasUncommittedChanges = false;
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
        if (HasUncommittedChanges && TrackHash is not null &&
            !string.Equals(TrackHash, trackHash, StringComparison.OrdinalIgnoreCase))
        {
            string previousTitle = TrackTitle;
            await CommitChangesAsync();
            if (!HasCommitError) SetStatus($"Auto-saved cues for \"{previousTitle}\" before switching tracks.");
        }

        int version = ++_loadVersion;
        IsAuditioningLoop = false;
        PendingLoopIn = null;
        SelectedCue = null;
        TrackHash = trackHash;
        TrackName = string.IsNullOrWhiteSpace(title) ? "Unknown track" : title!;
        TrackArtist = artist ?? "";
        TrackTitle = string.IsNullOrWhiteSpace(title) ? "Unknown track"
            : string.IsNullOrWhiteSpace(artist) ? title!
            : $"{artist} — {title}";

        _undoStack.Clear(); _redoStack.Clear();
        UpdateUndoRedoState();
        WaveformLow = null; WaveformMid = null; WaveformHigh = null;
        IsWaveformMissing = false;
        WaveformZoom = 1.0; WaveformScroll = 0;
        CurrentPlayPosition = 0;
        _trackFilePath = null;
        _durationKnown = false;
        TrackLoadError = null;
        LoadWorkingCuesClean(Array.Empty<OrbitCue>());

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
            var cues = entities.Select(OrbitCue.FromEntity).ToList();

            var features = _libraryService == null ? null : await _libraryService.GetAudioFeaturesByHashAsync(trackHash);
            if (version != _loadVersion) return;
            if (features is not null)
            {
                Bpm = (int)Math.Round(Math.Max(60, features.Bpm));
                _preciseBpm = features.Bpm > 0 ? features.Bpm : Bpm;
                _downbeat = features.DownbeatOffsetSeconds > 0 ? features.DownbeatOffsetSeconds : 0.0;
                _genre = !string.IsNullOrWhiteSpace(features.DetectedSubGenre) ? features.DetectedSubGenre : features.ElectronicSubgenre;
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
                UpdateCamelotKeyDisplay("");
                EnergyCurveData = null; VocalDensityCurveData = null; OnsetDensityCurveData = null;
                await _ui(() => PhraseSegments.Clear());
                TrackLoadError = "This track hasn't been analysed yet, so there's no waveform, tempo or key. Analyse it from the Library, then reopen it here.";
            }
            RaiseGridChanged();

            await _ui(() => LoadWorkingCuesClean(cues));
            RefreshHotCuePads();
            _logger.LogInformation("CueForge: loaded {Count} cues for {Hash} (BPM={Bpm} Dur={Dur:F0}s)",
                cues.Count, trackHash, GridBpm, TrackDuration);

            // Persist last-opened track so we can restore it on next launch
            _config.CueForgeLastTrackHash = trackHash;
            _config.CueForgeLastTrackTitle = title;
            _config.CueForgeLastTrackArtist = artist;
            try { _configManager?.Save(_config); }
            catch (Exception ex) { _logger.LogWarning(ex, "CueForge: couldn't remember the last opened track"); }
        }
        catch (Exception ex)
        {
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
        this.RaisePropertyChanged(nameof(SelectedCuePositionDisplay));
    }

    public void ClearWorkingDraft()
    {
        _loadVersion++;
        IsAuditioningLoop = false;
        SelectedCue = null;
        TrackHash = null; TrackTitle = "No track loaded"; TrackName = ""; TrackArtist = "";
        LoadWorkingCuesClean(Array.Empty<OrbitCue>());
        PhraseSegments.Clear();
        _undoStack.Clear(); _redoStack.Clear();
        UpdateUndoRedoState();
        CurrentCamelotKey = "—"; CompatibleCamelotKeys = "";
        WaveformLow = null; WaveformMid = null; WaveformHigh = null;
        IsWaveformMissing = false;
        EnergyCurveData = null; VocalDensityCurveData = null; OnsetDensityCurveData = null;
        PendingLoopIn = null;
        IsInVocalRegion = false;
        _trackFilePath = null; _preciseBpm = 0; _downbeat = 0;
        Bpm = 120; TrackDuration = 300.0; TrackEnergyScore = 0;
        RaiseGridChanged();
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
            SetStatus("Can't play this track: its audio file wasn't found on disk.", isError: true);
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

        if (IsAuditioningLoop && ActiveLoop is { } loop && loop.LoopEndSeconds > loop.Timestamp &&
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
            _transportCue = SnapTime(CurrentPlayPosition);
            SetPlayhead(_transportCue);
        }
        this.RaisePropertyChanged(nameof(IsPlaying));
    }

    // ── Command Implementations ────────────────────────────────────────────

    /// <summary>Adds a new cue at an explicit timestamp and selects it — used by the toolbar /
    /// A key (snapped playhead), the waveform's "Add cue here", and MixTransitionViewModel's
    /// "Add cue here" (which loads the track first, then calls this with the clicked time).</summary>
    public Task AddCueAtTimeAsync(double seconds) => Task.FromResult(AddCueAt(seconds, null));

    private OrbitCue? AddCueAt(double seconds, int? slot)
    {
        if (TrackHash is null) return null;
        PushSnapshot();
        int nextSlot = slot ?? Enumerable.Range(0, 8).Cast<int?>()
            .FirstOrDefault(i => WorkingCues.All(c => c.IsLoop || c.SlotIndex != i)) ?? -1;
        var cue = new OrbitCue
        {
            Timestamp = Math.Clamp(seconds, 0, TrackDuration),
            Name = nextSlot >= 0 ? $"Cue {(char)('A' + nextSlot)}" : $"Memory {MemoryCueCount + 1}",
            Color = nextSlot >= 0 ? CueColorSwatches[nextSlot % CueColorSwatches.Count] : DefaultCueColor,
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = nextSlot,
            Confidence = 1.0,
        };
        InsertCueSorted(cue);
        ClearAutoCues(cue);
        SelectedCue = cue;
        SetStatus($"Added {cue.Name} at {CueForgeGrid.FormatTime(cue.Timestamp)}");
        return cue;
    }

    private async Task AutoGenerateCuesAsync()
    {
        if (TrackHash is null) return;
        if (_libraryService == null || _engineCueService == null) return;

        IsGenerating = true;
        try
        {
            _logger.LogInformation("CueForge: auto-generating cues for {Hash}", TrackHash);

            var features = await _libraryService.GetAudioFeaturesByHashAsync(TrackHash);
            if (features is null)
            {
                _logger.LogWarning("CueForge: no features found for {Hash}", TrackHash);
                SetStatus("No analysis data for this track. Analyse it from the Library first.", isError: true);
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

            var userCues = WorkingCues.Where(c => c.Source == CueSource.User).ToList();
            PushSnapshot();
            await _ui(() =>
            {
                var merged = generatedEntities.Select(OrbitCue.FromEntity).ToList();
                foreach (var u in userCues)
                {
                    bool conflicts = merged.Any(c => !c.IsLoop && Math.Abs(c.Timestamp - u.Timestamp) < 1.0);
                    if (!conflicts) merged.Add(u);
                }
                ReplaceWorkingCues(merged.OrderBy(c => c.Timestamp).ToList());
                AssignSlotsByRolePriority();
                ApplyRekordboxColors();
                RefreshHotCuePads();
            });

            _logger.LogInformation("CueForge: generated {Count} cues", generatedEntities.Count);
            SetStatus($"Generated {generatedEntities.Count} cues. Your own cues were kept. Ctrl+Z to undo.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: auto-generate failed for {Hash}", TrackHash);
            SetStatus("Auto-generate failed; your cues are unchanged. See the log for details.", isError: true);
        }
        finally { IsGenerating = false; }
    }

    // A single delete is undoable, so no confirm dialog — the status line points at Ctrl+Z instead.
    private void DeleteCue(OrbitCue? cue)
    {
        if (TrackHash is null || cue == null || !WorkingCues.Contains(cue)) return;
        PushSnapshot();
        double? outBefore = cue.Role == CueRole.Drop ? OutBefore() : null;
        WorkingCues.Remove(cue);
        if (cue.Role == CueRole.Drop)
        {
            var left = Engine.Cueing.DropCountdownCues.RemoveFor(WorkingCues, cue.Timestamp, DropBpm);
            ReplaceWorkingCues(Engine.Cueing.DropCountdownCues.Refresh(left, CountdownBars, DropBpm, _downbeat, TrackDuration, outBefore));
        }
        if (cue.IsLoop && IsAuditioningLoop && ActiveLoop == null) IsAuditioningLoop = false;
        SetStatus($"Deleted \"{cue.Name}\". Ctrl+Z to undo.");
    }

    private void SetLoopIn()
    {
        if (TrackHash is null) return;
        PendingLoopIn = SnapTime(CurrentPlayPosition);
    }

    private void SetLoopOut()
    {
        if (TrackHash is null) return;
        double outAt = SnapTime(CurrentPlayPosition);
        if (PendingLoopIn is { } inAt)
        {
            PendingLoopIn = null;
            if (outAt <= inAt) outAt = Math.Min(TrackDuration, inAt + BeatSeconds);
            CreateLoop(inAt, outAt);
            return;
        }
        // No IN pending: move the active loop's end here, or make a one-bar loop ending here.
        if (ActiveLoop is { } loop && outAt > loop.Timestamp)
        {
            PushSnapshot();
            loop.LoopEndSeconds = outAt;
            loop.Name = FormatLoopLength(loop.Timestamp, outAt);
            MarkCueFieldEdited(loop);
            return;
        }
        CreateLoop(Math.Max(0, outAt - BarSeconds), outAt);
    }

    private double BeatSeconds => GridBpm > 0 ? 60.0 / GridBpm : 0.5;
    private double BarSeconds => BeatSeconds * 4;

    private OrbitCue CreateLoop(double inAt, double outAt)
    {
        PushSnapshot();
        var loop = new OrbitCue
        {
            Timestamp = inAt,
            LoopEndSeconds = Math.Min(outAt, TrackDuration),
            IsLoop = true,
            Name = FormatLoopLength(inAt, outAt),
            Color = LoopColor,
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = -1, // memory loop; never steals hot cue pad A
            Confidence = 1.0,
        };
        InsertCueSorted(loop);
        SelectedCue = loop;
        SetStatus($"Loop {loop.Name} at {CueForgeGrid.FormatTime(inAt)}");
        return loop;
    }

    private void ScaleLoop(double factor)
    {
        var loop = ActiveLoop;
        if (loop is null || TrackHash is null) return;
        double len = (loop.LoopEndSeconds - loop.Timestamp) * factor;
        if (len < BeatSeconds / 4) return;
        PushSnapshot();
        loop.LoopEndSeconds = Math.Min(loop.Timestamp + len, TrackDuration);
        loop.Name = FormatLoopLength(loop.Timestamp, loop.LoopEndSeconds);
        MarkCueFieldEdited(loop);
    }

    private void ClearLoop()
    {
        var loop = ActiveLoop;
        if (loop is null) return;
        IsAuditioningLoop = false;
        DeleteCue(loop);
    }

    /// <summary>1/2/4/8/16-bar loop: resizes the selected loop, otherwise starts a new loop at the
    /// selected cue (or the snapped playhead).</summary>
    private void SetQuickLoop(int bars)
    {
        if (TrackHash is null || bars <= 0) return;
        double length = BarSeconds * bars;
        if (SelectedCue is { IsLoop: true } loop)
        {
            PushSnapshot();
            loop.LoopEndSeconds = Math.Min(loop.Timestamp + length, TrackDuration);
            loop.Name = FormatLoopLength(loop.Timestamp, loop.LoopEndSeconds);
            MarkCueFieldEdited(loop);
            return;
        }
        double start = SelectedCue is { IsLoop: false } cue ? cue.Timestamp : SnapTime(CurrentPlayPosition);
        CreateLoop(start, Math.Min(start + length, TrackDuration));
    }

    private async Task CommitChangesAsync()
    {
        if (TrackHash is null) return;
        var hash = TrackHash;
        _logger.LogInformation("CueForge: saving {Count} cues for {Hash}", WorkingCues.Count, hash);
        try
        {
            var entities = WorkingCues.Select(c => c.ToEntity(hash)).ToList();
            await _cueService.DeleteAllByTrackIdAsync(hash);
            if (entities.Count > 0)
                await _cueService.CreateManyAsync(entities);
            int hotCount = WorkingCues.Count(c => !c.IsLoop && c.SlotIndex >= 0);
            int memCount = WorkingCues.Count(c => !c.IsLoop && c.SlotIndex < 0);
            int loopCount = WorkingCues.Count(c => c.IsLoop);
            HasUncommittedChanges = false;
            SetStatus($"Saved {hotCount} hot cue{(hotCount != 1 ? "s" : "")}" +
                (memCount > 0 ? $", {memCount} memory" : "") +
                (loopCount > 0 ? $", {loopCount} loop{(loopCount != 1 ? "s" : "")}" : ""));
        }
        catch (Exception ex)
        {
            // WorkingCues is left untouched so nothing is lost; the user can retry.
            _logger.LogError(ex, "CueForge: commit failed for {Hash}", hash);
            SetStatus("Save failed; your cues were NOT written. See the log, then try again.", isError: true);
        }
    }

    private async Task DiscardChangesAsync()
    {
        if (TrackHash is null) return;

        // Revert also wipes the undo stack, so it can't be undone: confirm first.
        if (_dialogService != null)
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Revert to saved",
                "This throws away every unsaved cue and loop edit for this track. It can't be undone. Continue?",
                confirmLabel: "Revert",
                cancelLabel: "Cancel");
            if (!confirmed) return;
        }

        try
        {
            var entities = await _cueService.GetByTrackIdAsync(TrackHash);
            await _ui(() => LoadWorkingCuesClean(entities.Select(OrbitCue.FromEntity)));
            _undoStack.Clear(); _redoStack.Clear(); UpdateUndoRedoState();
            SetStatus("Reverted to the saved cues.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: discard failed for {Hash}", TrackHash);
            SetStatus("Revert failed; your working cues were left as they are. See the log.", isError: true);
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(TakeSnapshot());
        var s = _undoStack[^1]; _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(s);
        UpdateUndoRedoState();
        HasUncommittedChanges = true;
        SetStatus("Undone.");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(TakeSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        UpdateUndoRedoState();
        HasUncommittedChanges = true;
        SetStatus("Redone.");
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

    // ── Snapshot / Undo ────────────────────────────────────────────────────

    private void PushSnapshot()
    {
        _redoStack.Clear();
        if (_undoStack.Count >= MaxUndoHistory) _undoStack.RemoveAt(0);
        _undoStack.Add(TakeSnapshot());
        UpdateUndoRedoState();
    }

    private OrbitCue[] TakeSnapshot() => WorkingCues.Select(c => c.Clone()).ToArray();

    private void RestoreSnapshot(OrbitCue[] s)
    {
        var selected = SelectedCue;
        var restored = s.Select(c => c.Clone()).ToList();
        ReplaceWorkingCues(restored);
        if (selected != null)
            SelectedCue = restored.FirstOrDefault(c => c.IsLoop == selected.IsLoop &&
                                                       Math.Abs(c.Timestamp - selected.Timestamp) < 0.001 &&
                                                       c.Name == selected.Name);
        RefreshHotCuePads();
    }

    private void UpdateUndoRedoState() { CanUndo = _undoStack.Count > 0; CanRedo = _redoStack.Count > 0; }

    private void InsertCueSorted(OrbitCue cue)
    {
        int idx = WorkingCues.Count(c => c.Timestamp <= cue.Timestamp);
        WorkingCues.Insert(idx, cue);
    }

    /// <summary>Keeps the list in time order after a drag/nudge moved a cue past a neighbour.</summary>
    private void ResortWorkingCues()
    {
        for (int i = 0; i < WorkingCues.Count; i++)
        {
            int target = i;
            for (int j = i + 1; j < WorkingCues.Count; j++)
                if (WorkingCues[j].Timestamp < WorkingCues[target].Timestamp) target = j;
            if (target != i) WorkingCues.Move(target, i);
        }
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

    private string FormatLoopLength(double inSec, double outSec)
    {
        double len = outSec - inSec;
        if (GridBpm <= 0) return $"{len.ToString("F1", CultureInfo.InvariantCulture)} s";
        double beats = len / BeatSeconds;
        double bars = beats / 4;
        if (bars >= 1 && Math.Abs(bars - Math.Round(bars)) < 0.05) return $"{Math.Round(bars):F0} bar{(Math.Round(bars) == 1 ? "" : "s")}";
        if (beats >= 0.9 && Math.Abs(beats - Math.Round(beats)) < 0.05) return $"{Math.Round(beats):F0} beat{(Math.Round(beats) == 1 ? "" : "s")}";
        return beats < 1 ? $"1/{Math.Round(1 / beats):F0} beat" : $"{len.ToString("F1", CultureInfo.InvariantCulture)} s";
    }

    // ── Transport navigation ───────────────────────────────────────────────

    private void NavigateToPreviousCue()
    {
        var prev = WorkingCues
            .Where(c => !c.IsLoop && c.Timestamp < CurrentPlayPosition - 0.1)
            .OrderByDescending(c => c.Timestamp)
            .FirstOrDefault();
        if (prev == null) return;
        SelectedCue = prev;
        SetPlayhead(prev.Timestamp);
    }

    private void NavigateToNextCue()
    {
        var next = WorkingCues
            .Where(c => !c.IsLoop && c.Timestamp > CurrentPlayPosition + 0.1)
            .OrderBy(c => c.Timestamp)
            .FirstOrDefault();
        if (next == null) return;
        SelectedCue = next;
        SetPlayhead(next.Timestamp);
    }

    // ── Hot cue pads ───────────────────────────────────────────────────────

    private void TriggerPad(HotCuePadInfo? pad)
    {
        if (pad == null || TrackHash is null) return;
        if (pad.Cue is { } cue && WorkingCues.Contains(cue))
        {
            SelectedCue = cue;
            _transportCue = cue.Timestamp;
            PlayFrom(cue.Timestamp);
            return;
        }
        AddCueAt(SnapTime(CurrentPlayPosition), pad.Slot);
    }

    private void ClearPad(HotCuePadInfo? pad)
    {
        if (pad?.Cue is not { } cue || TrackHash is null) return;
        PushSnapshot();
        cue.SlotIndex = -1;
        MarkCueFieldEdited(cue);
        SetStatus($"Pad {pad.PadLabel} cleared; \"{cue.Name}\" is now a memory cue.");
    }

    // ── Audition with pre-roll ─────────────────────────────────────────────

    private void AuditionCue(OrbitCue? cue)
    {
        if (cue == null) return;
        double preRoll = GridBpm > 0 ? 4.0 * 60.0 / GridBpm : 2.0; // 4 beats before cue
        PlayFrom(Math.Max(0, cue.Timestamp - preRoll));
    }

    // ── Keyboard nudge ─────────────────────────────────────────────────────

    /// <summary>"-1"/"1" or "-beat"/"+beat", "-bar"/"+bar", "-fine"/"+fine" (10 ms). Every step replays
    /// the track from the cue so it can be fine-tuned by ear; a moved Drop takes its countdown cues along.</summary>
    private void NudgeCue(string step)
    {
        if (SelectedCue == null || TrackHash is null) return;
        double bpm = GridBpm;
        if (bpm <= 0) return;
        double beat = 60.0 / bpm;
        double delta = step switch
        {
            "-1" or "-beat" => -beat, "1" or "+beat" => beat,
            "-bar" => -4 * beat, "+bar" => 4 * beat,
            "-fine" => -FineNudgeSeconds, "+fine" => FineNudgeSeconds,
            _ => 0,
        };
        if (delta == 0) return;
        PushSnapshot();
        var cue = SelectedCue;
        double from = cue.Timestamp;
        double moved = Math.Clamp(cue.Timestamp + delta, 0, TrackDuration);
        if (cue.IsLoop) cue.LoopEndSeconds = Math.Clamp(cue.LoopEndSeconds + (moved - from), 0, TrackDuration);
        cue.Timestamp = moved;
        bool wasAuto = cue.Source == CueSource.Auto;
        cue.Source = CueSource.User;
        ApplyCueMoved(cue, from, wasAuto);
        ResortWorkingCues();
        HasUncommittedChanges = true;
        RefreshHotCuePads();
        PlayFrom(cue.Timestamp);
    }

    private const double FineNudgeSeconds = 0.010;

    // ── Drop countdowns ────────────────────────────────────────────────────

    private string? _genre;

    public static IReadOnlyList<string> DropCountdownModes => Engine.Cueing.DropCountdownCues.Modes;

    /// <summary>Cue template (see Engine.Cueing.DropCountdownCues) — the same app-wide setting the
    /// Flow Builder cue editor and automatic cue generation use.</summary>
    public string DropCountdownMode
    {
        get => Engine.Cueing.DropCountdownCues.Normalize(_config.DropCountdownMode, _config.CustomCountdownBars).Mode;
        set
        {
            if (value == DropCountdownMode) return;
            _config.DropCountdownMode = value;
            try { _configManager?.Save(_config); } catch (Exception ex) { _logger.LogWarning(ex, "Couldn't save the cue template"); }
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsCustomTemplate));
        }
    }

    /// <summary>Bars before the drop for the Custom template, e.g. "32,16,8".</summary>
    public string CustomCountdownBars
    {
        get => Engine.Cueing.DropCountdownCues.Normalize(_config.DropCountdownMode, _config.CustomCountdownBars).CustomBars;
        set
        {
            if (value == CustomCountdownBars) return;
            _config.CustomCountdownBars = value;
            try { _configManager?.Save(_config); } catch (Exception ex) { _logger.LogWarning(ex, "Couldn't save the custom cue template"); }
            this.RaisePropertyChanged();
        }
    }

    public bool IsCustomTemplate => DropCountdownMode == Engine.Cueing.DropCountdownCues.Custom;

    private (OrbitCue? Cue, double From) _dragStart;
    private double DropBpm => GridBpm;
    private IReadOnlyList<int> CountdownBars =>
        Engine.Cueing.DropCountdownCues.ResolveBars(DropCountdownMode, _genre, DropBpm, CustomCountdownBars);

    /// <summary>
    /// Drop button / key D, Shift+1, Shift+2: a drop at the playhead, snapped to the bar, with
    /// everything else automatic (numbering, build-in cues, pads, [OUT], auto cues removed) — see
    /// Engine.Cueing.DropCountdownCues.PlaceDrop. One undo step.
    /// </summary>
    private void SetDropAt(double seconds, int? number)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        double bar = DropBpm > 0 ? 240.0 / DropBpm : 0;
        double t = bar > 0 ? _downbeat + Math.Round((seconds - _downbeat) / bar) * bar : seconds;
        t = Math.Clamp(t, 0, TrackDuration);
        int autoBefore = WorkingCues.Count(c => c.Source == CueSource.Auto);
        var list = Engine.Cueing.DropCountdownCues.PlaceDrop(WorkingCues, t, number, CountdownBars, DropBpm, _downbeat, TrackDuration, out var drop);
        int cleared = autoBefore - list.Count(c => c.Source == CueSource.Auto);
        ReplaceWorkingCues(list);
        SelectedCue = drop;
        HasUncommittedChanges = true;
        RefreshHotCuePads();
        SetStatus($"{drop.Name} at {CueForgeGrid.FormatTime(drop.Timestamp)}"
            + (cleared > 0 ? $" · {cleared} auto cue{(cleared == 1 ? "" : "s")} removed. Ctrl+Z to undo." : ""));
    }

    /// <summary>A cue was dragged or nudged from <paramref name="from"/>. Moving an auto cue takes
    /// manual control (the other auto cues go); a drop takes its build-in cues and [OUT] along.</summary>
    private void ApplyCueMoved(OrbitCue cue, double from, bool wasAuto)
    {
        if (wasAuto) ClearAutoCues(cue);
        if (cue.Role != CueRole.Drop) return;
        double? outBefore = OutBefore(cue, from);
        var list = Engine.Cueing.DropCountdownCues.Rebuild(WorkingCues, cue, CountdownBars, DropBpm, previousDropTime: from);
        ReplaceWorkingCues(Engine.Cueing.DropCountdownCues.Refresh(list, CountdownBars, DropBpm, _downbeat, TrackDuration, outBefore));
    }

    private void ClearAutoCues(OrbitCue keep)
    {
        int before = WorkingCues.Count;
        var list = Engine.Cueing.DropCountdownCues.WithoutAutoCues(WorkingCues, keep);
        if (list.Count == before) return;
        ReplaceWorkingCues(list);
        SetStatus($"{before - list.Count} auto cue{(before - list.Count == 1 ? "" : "s")} removed; your cues take over. Ctrl+Z to undo.");
    }

    private double? OutBefore(OrbitCue? moved = null, double movedFrom = 0)
    {
        var drops = WorkingCues.Where(c => c.Role == CueRole.Drop && !c.IsLoop)
            .Select(c => c == moved ? movedFrom : c.Timestamp).ToList();
        return drops.Count == 0 ? null : Engine.Cueing.DropCountdownCues.OutTime(drops.Max(), DropBpm, _downbeat, TrackDuration);
    }

    private void ReplaceWorkingCues(IReadOnlyList<OrbitCue> cues)
    {
        var selected = SelectedCue;
        WorkingCues.Clear();
        foreach (var c in cues) WorkingCues.Add(c);
        if (selected != null && cues.Contains(selected)) SelectedCue = selected;
    }

    // ── Inspector edits ────────────────────────────────────────────────────

    private void SetSelectedCueRole(CueRole role)
    {
        if (SelectedCue is null || TrackHash is null) return;
        var cue = SelectedCue;
        var previousRole = cue.Role;
        if (role == previousRole) return;
        if (role == CueRole.Drop)
        {
            // Picking Drop as the role = placing a drop: same automation as the Drop button.
            SetDropAt(cue.Timestamp, null);
            return;
        }
        PushSnapshot();
        double? outBefore = previousRole == CueRole.Drop ? OutBefore() : null;
        cue.Role = role;
        MarkCueFieldEdited(cue);
        if (previousRole == CueRole.Drop)
        {
            var left = Engine.Cueing.DropCountdownCues.RemoveFor(WorkingCues, cue.Timestamp, DropBpm);
            ReplaceWorkingCues(Engine.Cueing.DropCountdownCues.Refresh(left, CountdownBars, DropBpm, _downbeat, TrackDuration, outBefore));
        }
    }

    private void SetSelectedCueColor(string hexColor)
    {
        if (SelectedCue is null || TrackHash is null || string.IsNullOrWhiteSpace(hexColor)) return;
        if (string.Equals(SelectedCue.Color, hexColor, StringComparison.OrdinalIgnoreCase)) return;
        PushSnapshot();
        SelectedCue.Color = hexColor;
        MarkCueFieldEdited(SelectedCue);
    }

    private void SetSelectedCueSlot(int slotIndex)
    {
        if (SelectedCue is null || TrackHash is null || SelectedCue.IsLoop) return;
        slotIndex = Math.Clamp(slotIndex, -1, 7);
        PushSnapshot();

        // A hot cue slot holds one cue: whoever had it becomes a memory cue.
        if (slotIndex >= 0)
        {
            var priorHolder = WorkingCues.FirstOrDefault(c => !c.IsLoop && c.SlotIndex == slotIndex && c != SelectedCue);
            if (priorHolder is not null) priorHolder.SlotIndex = -1;
        }

        SelectedCue.SlotIndex = slotIndex;
        MarkCueFieldEdited(SelectedCue);
    }

    /// <summary>Flags the session dirty after a direct field edit (rename, recolour, role, slot) and
    /// turns the cue into a user cue so auto-analysis never overwrites it. Callers that want the
    /// edit undoable call PushSnapshot() before mutating.</summary>
    public void MarkCueFieldEdited(OrbitCue cue)
    {
        if (TrackHash is null) return;
        cue.Source = CueSource.User;
        HasUncommittedChanges = true;
        RefreshHotCuePads();
        RaiseSelectionDerived();
    }

    // ── Slot auto-assignment ───────────────────────────────────────────────

    private void AssignSlotsByRolePriority()
    {
        foreach (var c in WorkingCues.Where(c => c.Source == CueSource.Auto && !c.IsLoop))
            c.SlotIndex = -1;

        var rolePriority = new[] { CueRole.Drop, CueRole.Breakdown, CueRole.Build, CueRole.Intro, CueRole.KickIn, CueRole.Outro, CueRole.Vocals, CueRole.Custom };
        int slot = 0;
        foreach (var role in rolePriority)
        {
            foreach (var cue in WorkingCues.Where(c => c.Source == CueSource.Auto && c.Role == role && !c.IsLoop).OrderBy(c => c.Timestamp))
            {
                while (slot < 8 && WorkingCues.Any(c => c.Source == CueSource.User && !c.IsLoop && c.SlotIndex == slot))
                    slot++;
                if (slot >= 8) break;
                cue.SlotIndex = slot++;
            }
            if (slot >= 8) break;
        }
    }

    // ── Rekordbox color mapping ────────────────────────────────────────────

    private void ApplyRekordboxColors()
    {
        foreach (var c in WorkingCues.Where(c => c.Source == CueSource.Auto))
            c.Color = RekordboxColorForRole(c.Role);
    }

    public static string RekordboxColorForRole(CueRole role) => role switch
    {
        CueRole.Drop or CueRole.Climax         => "#FF0000", // Red
        CueRole.Build                           => "#FF6600", // Orange
        CueRole.Breakdown or CueRole.Breakdown2 => "#8800FF", // Purple
        CueRole.Intro                           => "#00CCFF", // Cyan
        CueRole.Outro                           => "#0044FF", // Blue
        CueRole.KickIn                          => "#FFFF00", // Yellow
        CueRole.Vocals                          => "#FF66AA", // Pink
        CueRole.Bridge                          => "#00FF88", // Green
        _                                       => "#FFFFFF",  // White
    };

    public void Dispose()
    {
        WorkingCues.CollectionChanged -= OnWorkingCuesChanged;
        foreach (var c in _trackedCues) c.PropertyChanged -= OnCuePropertyChanged;
        _trackedCues.Clear();
        if (_selectedCue != null) _selectedCue.PropertyChanged -= OnSelectedCuePropertyChanged;
        _disposables.Dispose();
    }
}

/// <summary>A selectable hot-cue-slot option for the inspector (0-7 = A-H, -1 = memory cue).</summary>
public sealed record CueSlotOption(string Label, int Value)
{
    public override string ToString() => Label;
}
