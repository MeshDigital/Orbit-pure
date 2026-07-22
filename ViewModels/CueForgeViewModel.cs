using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Engine.Snapping;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Cue &amp; Loop Forge — dedicated visual workspace for professional DJ cue generation and loop placement.
///
/// Architecture:
///   • Working draft isolation: all edits stay in-memory until Commit
///   • Auto-loads when PlayerViewModel.CurrentTrack changes (subscribes reactively)
///   • AutoGenerate: fast-path from cached AudioFeaturesEntity → AnalysisPipelineResult
///     → Engine.Cueing.CueGenerationService (no audio re-decode required)
///   • Real RGB waveform: WaveformBlob Low/Mid/High bytes fed to CueForgeWaveformControl
///   • Phrase map: PhraseSegmentsJson decoded and surfaced for waveform overlay
/// </summary>
public sealed class CueForgeViewModel : ReactiveObject, IDisposable
{
    private readonly ICuePointService _cueService;
    private readonly ILibraryService _libraryService;
    private readonly Engine.Cueing.CueGenerationService _engineCueService;
    private readonly PlayerViewModel _playerViewModel;
    private readonly CamelotKeyDisplayService _camelotKeyService;
    private readonly ILogger<CueForgeViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly DatabaseService _databaseService;
    private readonly CompositeDisposable _disposables = new();

    private const double SnapThreshold = 0.05;
    private const int MaxUndoHistory = 50;

    private readonly Stack<OrbitCue[]> _undoStack = new();
    private readonly Stack<OrbitCue[]> _redoStack = new();

    // ── Observable Collections ─────────────────────────────────────────────

    public ObservableCollection<OrbitCue> WorkingCues { get; } = new();
    public ObservableCollection<PhraseSegment> PhraseSegments { get; } = new();

    // ── Track Context ──────────────────────────────────────────────────────

    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set => this.RaiseAndSetIfChanged(ref _trackHash, value);
    }

    private string _trackTitle = "No track loaded";
    public string TrackTitle
    {
        get => _trackTitle;
        private set => this.RaiseAndSetIfChanged(ref _trackTitle, value);
    }

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

    // The track's own known duration, independent of PlayerViewModel — Cue Forge
    // learns this from cached AudioFeaturesEntity as soon as a track loads, well
    // before (or even without) playback ever starting, unlike Player.TotalTimeStr.
    public string TrackDurationDisplay => TimeSpan.FromSeconds(TrackDuration).ToString(@"m\:ss");

    private int _bpm = 120;
    public int Bpm
    {
        get => _bpm;
        private set => this.RaiseAndSetIfChanged(ref _bpm, value);
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
        }
    }

    // Formatted with an explicit invariant culture rather than XAML StringFormat: on
    // locales that use a comma decimal separator, StringFormat rendered "0,00s" instead
    // of "0.00s", inconsistent with every other numeric readout in this page.
    public string CurrentPlayPositionDisplay =>
        $"⏱ {CurrentPlayPosition.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}s";

    // ── Waveform Data ──────────────────────────────────────────────────────

    private byte[]? _waveformLow;
    public byte[]? WaveformLow
    {
        get => _waveformLow;
        private set { this.RaiseAndSetIfChanged(ref _waveformLow, value); this.RaisePropertyChanged(nameof(MaxZoomLevel)); }
    }

    // Caps zoom to roughly what the loaded track's actual sample resolution can usefully show —
    // a flat 32x cap let you zoom in well past the point of real detail, especially on shorter
    // tracks near the waveform blob's sample-count floor, showing a handful of interpolated
    // points stretched across the whole screen rather than a genuinely detailed waveform.
    public double MaxZoomLevel => WaveformLow is { Length: > 0 } arr
        ? Math.Clamp(arr.Length / 40.0, 4.0, 24.0)
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

    private string _currentCamelotKey = "?";
    public string CurrentCamelotKey { get => _currentCamelotKey; private set => this.RaiseAndSetIfChanged(ref _currentCamelotKey, value); }

    private string _compatibleCamelotKeys = "";
    public string CompatibleCamelotKeys { get => _compatibleCamelotKeys; private set => this.RaiseAndSetIfChanged(ref _compatibleCamelotKeys, value); }

    private bool _isInVocalRegion;
    public bool IsInVocalRegion { get => _isInVocalRegion; private set => this.RaiseAndSetIfChanged(ref _isInVocalRegion, value); }

    // ── Edit State ─────────────────────────────────────────────────────────

    private bool _snapToGrid = true;
    public bool SnapToGrid { get => _snapToGrid; set => this.RaiseAndSetIfChanged(ref _snapToGrid, value); }

    private string _quantizeBeats = "16 beats";
    public string QuantizeBeats { get => _quantizeBeats; set => this.RaiseAndSetIfChanged(ref _quantizeBeats, value); }

    private OrbitCue? _selectedCue;
    public OrbitCue? SelectedCue { get => _selectedCue; set => this.RaiseAndSetIfChanged(ref _selectedCue, value); }

    private bool _hasUncommittedChanges;
    public bool HasUncommittedChanges { get => _hasUncommittedChanges; private set => this.RaiseAndSetIfChanged(ref _hasUncommittedChanges, value); }

    private bool _canUndo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }

    private bool _canRedo;
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; private set => this.RaiseAndSetIfChanged(ref _isGenerating, value); }

    // Active loop in/out for the Loop section controls
    private double? _loopInSeconds;
    public double? LoopInSeconds
    {
        get => _loopInSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _loopInSeconds, value);
            this.RaisePropertyChanged(nameof(LoopInDisplay));
            this.RaisePropertyChanged(nameof(HasStagedLoop));
        }
    }

    private double? _loopOutSeconds;
    public double? LoopOutSeconds
    {
        get => _loopOutSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _loopOutSeconds, value);
            this.RaisePropertyChanged(nameof(LoopOutDisplay));
            this.RaisePropertyChanged(nameof(HasStagedLoop));
        }
    }

    public string LoopInDisplay => LoopInSeconds.HasValue
        ? $"IN: {LoopInSeconds.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}s"
        : "IN: —";
    public string LoopOutDisplay => LoopOutSeconds.HasValue
        ? $"OUT: {LoopOutSeconds.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}s"
        : "OUT: —";

    // When on, playback wraps back to LoopInSeconds every time it crosses LoopOutSeconds
    // (driven by the position-sync subscription below), so a staged loop can be auditioned
    // repeatedly before it's committed as a cue.
    private bool _isAuditioningLoop;
    public bool IsAuditioningLoop
    {
        get => _isAuditioningLoop;
        set
        {
            if (value && (!LoopInSeconds.HasValue || !LoopOutSeconds.HasValue)) value = false;
            this.RaiseAndSetIfChanged(ref _isAuditioningLoop, value);
            if (value)
            {
                SeekToSeconds(LoopInSeconds!.Value);
                if (!_playerViewModel.IsPlaying)
                    _playerViewModel.TogglePlayPauseCommand?.Execute(null);
            }
        }
    }

    public bool HasStagedLoop => LoopInSeconds.HasValue && LoopOutSeconds.HasValue;

    // ── Cue detail panel option lists ───────────────────────────────────────

    public static IReadOnlyList<CueRole> CueRoleOptions { get; } = Enum.GetValues<CueRole>();

    public static IReadOnlyList<string> CueColorSwatches { get; } = new[]
    {
        "#FF0000", "#FF8800", "#FFFF00", "#00FF88", "#00FFFF", "#0088FF", "#8800FF", "#FFFFFF",
    };

    public static IReadOnlyList<CueSlotOption> CueSlotOptions { get; } =
        Enumerable.Range(0, 8).Select(i => new CueSlotOption(((char)('A' + i)).ToString(), i))
            .Append(new CueSlotOption("Memory", -1))
            .ToList();

    public int GetQuantizeBeatCount() => _quantizeBeats switch
    {
        "Off" => 0, "1 beat" => 1, "2 beats" => 2, "4 beats" => 4, "8 beats" => 8,
        "16 beats" => 16, "32 beats" => 32, "64 beats (16 bars)" => 64, "128 beats (32 bars)" => 128, _ => 16
    };

    // ── Hot Cue Pads ──────────────────────────────────────────────────────

    public ObservableCollection<HotCuePadInfo> HotCuePads { get; } = new(
        Enumerable.Range(0, 8).Select(i => new HotCuePadInfo
        {
            Slot = i,
            PadLabel = ((char)('A' + i)).ToString()
        }));

    private void RefreshHotCuePads()
    {
        HotCuePads.Clear();
        for (int i = 0; i < 8; i++)
        {
            var cue = WorkingCues.FirstOrDefault(c => c.SlotIndex == i && !c.IsLoop);
            if (cue != null)
            {
                HotCuePads.Add(new HotCuePadInfo
                {
                    Slot = i,
                    PadLabel = ((char)('A' + i)).ToString(),
                    CueName = cue.Name,
                    TimestampDisplay = TimeSpan.FromSeconds(cue.Timestamp).ToString(@"mm\:ss"),
                    Background = HexWithAlpha(cue.Color, 0.22f),
                    BorderColor = HexWithAlpha(cue.Color, 0.55f),
                    Foreground = cue.Color,
                    IsAssigned = true
                });
            }
            else
            {
                HotCuePads.Add(new HotCuePadInfo
                {
                    Slot = i,
                    PadLabel = ((char)('A' + i)).ToString(),
                    Background = "#1A1A26",
                    BorderColor = "#2A2A3A",
                    Foreground = "#333344"
                });
            }
        }
    }

    private static string HexWithAlpha(string hexColor, float alpha)
    {
        var hex = hexColor.TrimStart('#');
        if (hex.Length == 6)
        {
            byte a = (byte)(Math.Clamp(alpha, 0f, 1f) * 255);
            return $"#{a:X2}{hex.ToUpperInvariant()}";
        }
        return hexColor;
    }

    // ── Playlist Browser ───────────────────────────────────────────────────

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
        set { this.RaiseAndSetIfChanged(ref _browserQuery, value); ApplyBrowserFilter(value); }
    }

    public System.Windows.Input.ICommand LoadFromBrowserCommand { get; private set; } = null!;

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

    private async Task LoadAllPlaylistsAsync()
    {
        try
        {
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Playlists.Clear();
                foreach (var job in jobs.OrderByDescending(j => j.CreatedAt))
                    Playlists.Add(job);
                if (Playlists.Count > 0 && SelectedPlaylist == null)
                    SelectedPlaylist = Playlists[0];
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: failed to load playlist list");
        }
    }

    private async Task LoadPlaylistBrowserAsync(Guid playlistId)
    {
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
                    Artist   = t.Artist
                })
                .ToList();

            _allPlaylistTracks.Clear();
            _allPlaylistTracks.AddRange(items);
            ApplyBrowserFilter(_browserQuery);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: failed to load browser tracks for playlist {Id}", playlistId);
        }
    }

    private void ApplyBrowserFilter(string query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? (IEnumerable<CueBrowserItem>)_allPlaylistTracks
            : _allPlaylistTracks.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase));

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BrowserTracks.Clear();
            foreach (var t in filtered) BrowserTracks.Add(t);
        });
    }

    // ── Commands ───────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddCueAtPlayheadCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoGenerateCuesCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> UpdateCueCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> DeleteCueCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopInCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopOutCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopHalfCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopDoubleCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLoopCommand { get; }
    public ReactiveCommand<Unit, Unit> CommitChangesCommand { get; }
    public ReactiveCommand<Unit, Unit> DiscardChangesCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> PinLoopStartAsCueCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit, Unit> ScrollLeftCommand { get; }
    public ReactiveCommand<Unit, Unit> ScrollRightCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousCueCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCueCommand { get; }
    public ReactiveCommand<object, Unit> SetQuickLoopCommand { get; }
    public ReactiveCommand<HotCuePadInfo, Unit> JumpToCueCommand { get; }
    public ReactiveCommand<double, Unit> SeekPlayheadCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> AuditionCueCommand { get; }
    public ReactiveCommand<int, Unit> NudgeCueCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> SelectCueCommand { get; }
    public ReactiveCommand<CueRole, Unit> SetSelectedCueRoleCommand { get; }
    public ReactiveCommand<string, Unit> SetSelectedCueColorCommand { get; }
    public ReactiveCommand<int, Unit> SetSelectedCueSlotCommand { get; }
    public System.Windows.Input.ICommand PlaybackToggleCommand => _playerViewModel.TogglePlayPauseCommand;

    // Expose the underlying PlayerViewModel so CueForge AXAML can bind transport props directly
    public PlayerViewModel Player => _playerViewModel;

    // ── Waveform Navigation State ──────────────────────────────────────────

    private double _waveformZoom = 1.0;
    public double WaveformZoom
    {
        get => _waveformZoom;
        set
        {
            this.RaiseAndSetIfChanged(ref _waveformZoom, Math.Clamp(value, 1.0, MaxZoomLevel));
            this.RaisePropertyChanged(nameof(WaveformScrollMaximum));
            this.RaisePropertyChanged(nameof(ZoomDisplay));
            ClampWaveformScroll();
        }
    }

    public string ZoomDisplay => $"{WaveformZoom.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}×";

    private double _waveformScroll = 0.0;
    public double WaveformScroll
    {
        get => _waveformScroll;
        set => this.RaiseAndSetIfChanged(ref _waveformScroll,
            Math.Clamp(value, 0.0, WaveformScrollMaximum));
    }

    public double WaveformScrollMaximum => Math.Max(0.0, 1.0 - 1.0 / Math.Max(1.0, _waveformZoom));

    private void ClampWaveformScroll() =>
        WaveformScroll = Math.Clamp(_waveformScroll, 0.0, WaveformScrollMaximum);

    // ── Track Metadata Extras ──────────────────────────────────────────────

    private int _trackEnergyScore;
    public int TrackEnergyScore
    {
        get => _trackEnergyScore;
        private set => this.RaiseAndSetIfChanged(ref _trackEnergyScore, value);
    }

    private string _lastCommitMessage = "";
    public string LastCommitMessage
    {
        get => _lastCommitMessage;
        private set => this.RaiseAndSetIfChanged(ref _lastCommitMessage, value);
    }

    private bool _hasCommitError;
    public bool HasCommitError
    {
        get => _hasCommitError;
        private set => this.RaiseAndSetIfChanged(ref _hasCommitError, value);
    }

    // True when track has at least one Intro, one Drop, and one Outro cue
    public bool IsExportReady => TrackHash != null &&
        WorkingCues.Any(c => c.Role == CueRole.Intro) &&
        WorkingCues.Any(c => c.Role == CueRole.Drop) &&
        WorkingCues.Any(c => c.Role == CueRole.Outro);

    // ── Constructor ────────────────────────────────────────────────────────

    public CueForgeViewModel(
        ICuePointService cueService,
        ILibraryService libraryService,
        Engine.Cueing.CueGenerationService engineCueService,
        PlayerViewModel playerViewModel,
        CamelotKeyDisplayService camelotKeyService,
        ILogger<CueForgeViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        DatabaseService databaseService)
    {
        _cueService = cueService;
        _libraryService = libraryService;
        _engineCueService = engineCueService;
        _playerViewModel = playerViewModel;
        _camelotKeyService = camelotKeyService;
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _databaseService = databaseService;

        var hasTrack = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));
        var notGenerating = this.WhenAnyValue(x => x.IsGenerating, g => !g);
        var canAct = hasTrack.CombineLatest(notGenerating, (h, g) => h && g);

        AddCueAtPlayheadCommand = ReactiveCommand.CreateFromTask(AddCueAtPlayheadAsync, canAct);
        AutoGenerateCuesCommand = ReactiveCommand.CreateFromTask(AutoGenerateCuesAsync, canAct);
        UpdateCueCommand = ReactiveCommand.CreateFromTask<OrbitCue>(UpdateCueAsync, canAct);
        DeleteCueCommand = ReactiveCommand.CreateFromTask<OrbitCue>(DeleteCueAsync, canAct);
        SetLoopInCommand = ReactiveCommand.CreateFromTask(SetLoopInAsync, canAct);
        SetLoopOutCommand = ReactiveCommand.CreateFromTask(SetLoopOutAsync, canAct);
        LoopHalfCommand = ReactiveCommand.Create(HalveLoop);
        LoopDoubleCommand = ReactiveCommand.Create(DoubleLoop);
        ClearLoopCommand = ReactiveCommand.CreateFromTask(ClearLoopAsync, canAct);
        CommitChangesCommand = ReactiveCommand.CreateFromTask(CommitChangesAsync);
        DiscardChangesCommand = ReactiveCommand.CreateFromTask(DiscardChangesAsync);
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));
        PinLoopStartAsCueCommand = ReactiveCommand.Create(PinLoopStartAsCue,
            this.WhenAnyValue(x => x.LoopInSeconds, x => x.TrackHash,
                (lo, h) => lo.HasValue && !string.IsNullOrEmpty(h)));

        ZoomInCommand     = ReactiveCommand.Create(() => { WaveformZoom = Math.Clamp(WaveformZoom * 1.5, 1.0, MaxZoomLevel); });
        ZoomOutCommand    = ReactiveCommand.Create(() => { WaveformZoom = Math.Clamp(WaveformZoom / 1.5, 1.0, MaxZoomLevel); });
        ScrollLeftCommand = ReactiveCommand.Create(() =>
        {
            double step = (1.0 / Math.Max(1.0, WaveformZoom)) * 0.2;
            WaveformScroll = Math.Max(0, WaveformScroll - step);
        });
        ScrollRightCommand = ReactiveCommand.Create(() =>
        {
            double step = (1.0 / Math.Max(1.0, WaveformZoom)) * 0.2;
            WaveformScroll = Math.Min(WaveformScrollMaximum, WaveformScroll + step);
        });

        PreviousCueCommand = ReactiveCommand.Create(NavigateToPreviousCue, canAct);
        NextCueCommand     = ReactiveCommand.Create(NavigateToNextCue, canAct);
        SetQuickLoopCommand = ReactiveCommand.Create<object>(param =>
        {
            if (int.TryParse(param?.ToString(), out int bars)) SetQuickLoop(bars);
        }, canAct);
        JumpToCueCommand    = ReactiveCommand.Create<HotCuePadInfo>(JumpToCue);
        SeekPlayheadCommand = ReactiveCommand.Create<double>(SeekToSeconds);

        AuditionCueCommand = ReactiveCommand.Create<OrbitCue>(AuditionCue);
        NudgeCueCommand    = ReactiveCommand.Create<int>(NudgeCue, canAct);
        SelectCueCommand   = ReactiveCommand.Create<OrbitCue>(cue => SelectedCue = cue);
        SetSelectedCueRoleCommand  = ReactiveCommand.Create<CueRole>(SetSelectedCueRole, canAct);
        SetSelectedCueColorCommand = ReactiveCommand.Create<string>(SetSelectedCueColor, canAct);
        SetSelectedCueSlotCommand  = ReactiveCommand.Create<int>(SetSelectedCueSlot, canAct);

        LoadFromBrowserCommand = new Views.AsyncRelayCommand<CueBrowserItem>(async item =>
        {
            if (item == null) return;
            await LoadTrackAsync(item.GlobalId, item.Title, item.Artist);
        });

        WorkingCues.CollectionChanged += (_, _) =>
        {
            HasUncommittedChanges = true;
            this.RaisePropertyChanged(nameof(IsExportReady));
            RefreshHotCuePads();
        };

        // Load all playlists for the browser sidebar (fires immediately)
        _ = LoadAllPlaylistsAsync();

        // Restore last session
        if (!string.IsNullOrEmpty(_config.CueForgeLastTrackHash))
            _ = LoadTrackAsync(_config.CueForgeLastTrackHash,
                               _config.CueForgeLastTrackTitle,
                               _config.CueForgeLastTrackArtist);

        // Auto-load when PlayerViewModel.CurrentTrack changes
        this.WhenAnyValue(x => x._playerViewModel.CurrentTrack)
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                if (track?.GlobalId is { Length: > 0 } hash)
                    _ = LoadTrackAsync(hash, track.Title, track.Artist);
                else
                    ClearWorkingDraft();
            })
            .DisposeWith(_disposables);

        // Playhead sync at ~30 fps
        this.WhenAnyValue(x => x._playerViewModel.Position, x => x._playerViewModel.LengthMs,
            (pos, len) => (pos, len))
            .Throttle(TimeSpan.FromMilliseconds(33))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(p =>
            {
                if (p.len > 0) CurrentPlayPosition = p.pos * (p.len / 1000.0);
                UpdateVocalWarning();

                if (IsAuditioningLoop && LoopInSeconds.HasValue && LoopOutSeconds.HasValue &&
                    CurrentPlayPosition >= LoopOutSeconds.Value)
                {
                    SeekToSeconds(LoopInSeconds.Value);
                }
            })
            .DisposeWith(_disposables);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task LoadTrackAsync(string trackHash, string? title = null, string? artist = null)
    {
        TrackHash = trackHash;
        TrackTitle = string.IsNullOrWhiteSpace(title) ? "Unknown Track"
            : string.IsNullOrWhiteSpace(artist) ? title
            : $"{artist} — {title}";

        _undoStack.Clear(); _redoStack.Clear();
        HasUncommittedChanges = false;
        UpdateUndoRedoState();

        // Load this track's audio into the shared player engine so Play/Seek/Audition in the
        // embedded transport actually have something to play. LoadTrackAsync used to only pull
        // metadata/waveform/cues — it never told PlayerViewModel which file to load, so opening
        // Cue Forge on a track that wasn't already playing elsewhere left the transport
        // pointing at whatever (if anything) was previously loaded. Skip the reload if this
        // exact file is already loaded so we don't interrupt playback that's already running.
        try
        {
            var resolvedPath = await _databaseService.GetLocalFilePathByHashAsync(trackHash);
            if (!string.IsNullOrEmpty(resolvedPath) &&
                !string.Equals(_playerViewModel.CurrentFilePath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                _playerViewModel.LoadTrackPaused(resolvedPath, title ?? TrackTitle, artist ?? "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueForge: could not load audio for {Hash}; transport will have nothing to play", trackHash);
        }

        // Load cues from DB
        var entities = await _cueService.GetByTrackIdAsync(trackHash);
        var cues = entities.Select(EntityToOrbitCue).ToList();

        // Load audio features for waveform data + analysis metadata
        var features = await _libraryService.GetAudioFeaturesByHashAsync(trackHash);
        if (features is not null)
        {
            Bpm = (int)Math.Round(Math.Max(60, features.Bpm));
            TrackDuration = features.TrackDuration > 0 ? features.TrackDuration : 300.0;
            TrackEnergyScore = features.EnergyScore;

            // Decode waveform blob: [low₀…lowN | mid₀…midN | high₀…highN]
            if (features.WaveformBlob is { Length: > 0 } blob && features.WaveformBlobSampleCount > 0)
            {
                int n = features.WaveformBlobSampleCount;
                int expected = n * 3;
                if (blob.Length >= expected)
                {
                    var low = new byte[n]; var mid = new byte[n]; var high = new byte[n];
                    Buffer.BlockCopy(blob, 0, low, 0, n);
                    Buffer.BlockCopy(blob, n, mid, 0, n);
                    Buffer.BlockCopy(blob, n * 2, high, 0, n);
                    WaveformLow = low; WaveformMid = mid; WaveformHigh = high;
                }
            }

            // Energy curve
            EnergyCurveData = ParseJsonFloatArray(features.EnergyCurveJson);

            // Vocal density
            VocalDensityCurveData = ParseJsonFloatArray(features.VocalDensityCurveJson);

            // Beat grid → onset density
            var beatGrid = ParseJsonFloatArray(features.BeatGridJson)?.Select(f => (double)f).ToList()
                           ?? new List<double>();
            if (beatGrid.Count > 0)
            {
                var densityEngine = new OnsetDensityEngine();
                OnsetDensityCurveData = densityEngine.ComputeOnsetDensityCurve(beatGrid, TrackDuration);
            }

            // Phrase segments
            var segments = ParsePhraseSegments(features.PhraseSegmentsJson);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                PhraseSegments.Clear();
                foreach (var s in segments) PhraseSegments.Add(s);
            });

            // Camelot key
            UpdateCamelotKeyDisplay(features.CamelotKey);
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            WorkingCues.Clear();
            HasUncommittedChanges = false;
            foreach (var c in cues) WorkingCues.Add(c);
        });

        this.RaisePropertyChanged(nameof(IsExportReady));
        RefreshHotCuePads();
        _logger.LogInformation("CueForge: loaded {Count} cues for {Hash} (BPM={Bpm} Dur={Dur:F0}s)",
            cues.Count, trackHash, Bpm, TrackDuration);

        // Persist last-opened track so we can restore it on next launch
        _config.CueForgeLastTrackHash = trackHash;
        _config.CueForgeLastTrackTitle = title;
        _config.CueForgeLastTrackArtist = artist;
        _configManager.Save(_config);
    }

    public void ClearWorkingDraft()
    {
        TrackHash = null; TrackTitle = "No track loaded";
        WorkingCues.Clear(); PhraseSegments.Clear();
        _undoStack.Clear(); _redoStack.Clear();
        HasUncommittedChanges = false; UpdateUndoRedoState();
        CurrentCamelotKey = "?"; CompatibleCamelotKeys = "";
        WaveformLow = null; WaveformMid = null; WaveformHigh = null;
        EnergyCurveData = null; VocalDensityCurveData = null; OnsetDensityCurveData = null;
        LoopInSeconds = null; LoopOutSeconds = null;
        IsInVocalRegion = false;
    }

    public void UpdateCamelotKeyDisplay(string camelotKey)
    {
        if (string.IsNullOrEmpty(camelotKey)) { CurrentCamelotKey = "?"; CompatibleCamelotKeys = ""; return; }
        CurrentCamelotKey = camelotKey;
        var compatible = _camelotKeyService.GetCompatibleKeys(camelotKey);
        var others = compatible.Where(k => k != camelotKey).ToList();
        CompatibleCamelotKeys = others.Count > 0 ? string.Join(", ", others) : "—";
    }

    // ── Command Implementations ────────────────────────────────────────────

    private async Task AddCueAtPlayheadAsync()
    {
        if (TrackHash is null) return;
        PushSnapshot();
        int nextSlot = Enumerable.Range(0, 8).FirstOrDefault(i => WorkingCues.All(c => c.SlotIndex != i), -1);
        var cue = new OrbitCue
        {
            Timestamp = CurrentPlayPosition,
            Name = $"Cue {WorkingCues.Count(c => !c.IsLoop) + 1}",
            Color = "#FFFF00",
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = nextSlot
        };
        InsertCueSorted(cue);
        await Task.CompletedTask;
    }

    private async Task AutoGenerateCuesAsync()
    {
        if (TrackHash is null) return;

        IsGenerating = true;
        try
        {
            _logger.LogInformation("CueForge: auto-generating cues for {Hash}", TrackHash);

            var features = await _libraryService.GetAudioFeaturesByHashAsync(TrackHash);
            if (features is null)
            {
                _logger.LogWarning("CueForge: no features found for {Hash}", TrackHash);
                HasCommitError = true;
                LastCommitMessage = "✗ No analysis data found for this track — analyse it first.";
                return;
            }

            // Reconstruct AnalysisPipelineResult from cached features (fast path — no audio re-decode)
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
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                WorkingCues.Clear();
                foreach (var e in generatedEntities)
                    WorkingCues.Add(EntityToOrbitCue(e));
                // Re-merge user cues that don't conflict with auto cues
                foreach (var u in userCues)
                {
                    bool conflicts = WorkingCues.Any(c => !c.IsLoop && Math.Abs(c.Timestamp - u.Timestamp) < 1.0);
                    if (!conflicts) InsertCueSorted(u);
                }
                AssignSlotsByRolePriority();
                ApplyRekordboxColors();
            });

            _logger.LogInformation("CueForge: generated {Count} cues", generatedEntities.Count);
            HasCommitError = false;
            LastCommitMessage = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: auto-generate failed for {Hash}", TrackHash);
            HasCommitError = true;
            LastCommitMessage = "✗ Auto-generate failed — working cues unchanged. Check logs.";
        }
        finally { IsGenerating = false; }
    }

    private async Task UpdateCueAsync(OrbitCue cue) { if (TrackHash is null) return; PushSnapshot(); cue.Source = CueSource.User; await Task.CompletedTask; }

    private async Task DeleteCueAsync(OrbitCue cue) { if (TrackHash is null) return; PushSnapshot(); WorkingCues.Remove(cue); await Task.CompletedTask; }

    private async Task SetLoopInAsync()
    {
        LoopInSeconds = CurrentPlayPosition;
        if (LoopOutSeconds.HasValue && LoopOutSeconds <= LoopInSeconds)
        {
            LoopOutSeconds = null;
            IsAuditioningLoop = false;
        }
        await Task.CompletedTask;
    }

    private async Task SetLoopOutAsync()
    {
        if (!LoopInSeconds.HasValue) { LoopInSeconds = Math.Max(0, CurrentPlayPosition - 4.0); }
        LoopOutSeconds = CurrentPlayPosition;
        if (LoopOutSeconds <= LoopInSeconds) LoopOutSeconds = LoopInSeconds.Value + 1.0;

        // Materialise as a cue in the working draft
        PushSnapshot();
        var existing = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (existing is not null) WorkingCues.Remove(existing);
        WorkingCues.Add(new OrbitCue
        {
            Timestamp = LoopInSeconds!.Value,
            LoopEndSeconds = LoopOutSeconds.Value,
            IsLoop = true, Name = FormatLoopLength(LoopInSeconds.Value, LoopOutSeconds.Value),
            Color = "#00FF88", Source = CueSource.User, Role = CueRole.Custom
        });
        await Task.CompletedTask;
    }

    private void HalveLoop()
    {
        var loop = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (loop is null || !LoopInSeconds.HasValue || !LoopOutSeconds.HasValue) return;
        double mid = LoopInSeconds.Value + (LoopOutSeconds.Value - LoopInSeconds.Value) / 2;
        LoopOutSeconds = mid; loop.LoopEndSeconds = mid;
        loop.Name = FormatLoopLength(LoopInSeconds.Value, mid);
    }

    private void DoubleLoop()
    {
        var loop = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (loop is null || !LoopInSeconds.HasValue || !LoopOutSeconds.HasValue) return;
        double extended = LoopInSeconds.Value + (LoopOutSeconds.Value - LoopInSeconds.Value) * 2;
        extended = Math.Min(extended, TrackDuration);
        LoopOutSeconds = extended; loop.LoopEndSeconds = extended;
        loop.Name = FormatLoopLength(LoopInSeconds.Value, extended);
    }

    private void PinLoopStartAsCue()
    {
        if (!LoopInSeconds.HasValue || TrackHash is null) return;
        PushSnapshot();
        InsertCueSorted(new OrbitCue
        {
            Timestamp = LoopInSeconds.Value,
            Name = "Loop Pin",
            Color = "#00FF88",
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = -1,
            Confidence = 1.0,
        });
    }

    private async Task ClearLoopAsync()
    {
        var loop = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (loop is null) return;
        PushSnapshot(); WorkingCues.Remove(loop);
        LoopInSeconds = null; LoopOutSeconds = null;
        IsAuditioningLoop = false;
        await Task.CompletedTask;
    }

    private async Task CommitChangesAsync()
    {
        if (TrackHash is null) return;
        _logger.LogInformation("CueForge: committing {Count} cues for {Hash}", WorkingCues.Count, TrackHash);
        try
        {
            await _cueService.DeleteAllByTrackIdAsync(TrackHash);
            int hotCount = WorkingCues.Count(c => !c.IsLoop && c.SlotIndex >= 0);
            int memCount = WorkingCues.Count(c => !c.IsLoop && c.SlotIndex < 0);
            int loopCount = WorkingCues.Count(c => c.IsLoop);
            if (WorkingCues.Count > 0)
                await _cueService.CreateManyAsync(WorkingCues.Select((c, i) => OrbitCueToEntity(c, TrackHash, i)).ToList());
            HasUncommittedChanges = false;
            HasCommitError = false;
            LastCommitMessage = $"✓ Saved — {hotCount} hot cue{(hotCount != 1 ? "s" : "")}" +
                (memCount > 0 ? $", {memCount} memory" : "") +
                (loopCount > 0 ? $", {loopCount} loop" : "");
        }
        catch (Exception ex)
        {
            // WorkingCues is left untouched (working draft isolation) so nothing is lost —
            // the user can retry Commit once the underlying issue (DB locked, disk full) clears.
            _logger.LogError(ex, "CueForge: commit failed for {Hash}", TrackHash);
            HasCommitError = true;
            LastCommitMessage = "✗ Save failed — cues were NOT written. Check logs and retry.";
        }
    }

    private async Task DiscardChangesAsync()
    {
        if (TrackHash is null) return;
        try
        {
            var entities = await _cueService.GetByTrackIdAsync(TrackHash);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                WorkingCues.Clear();
                foreach (var e in entities) WorkingCues.Add(EntityToOrbitCue(e));
            });
            HasUncommittedChanges = false; _undoStack.Clear(); _redoStack.Clear(); UpdateUndoRedoState();
            HasCommitError = false;
            LastCommitMessage = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CueForge: discard failed for {Hash}", TrackHash);
            HasCommitError = true;
            LastCommitMessage = "✗ Discard failed — working cues left as-is. Check logs.";
        }
    }

    private void Undo() { if (_undoStack.Count == 0) return; _redoStack.Push(TakeSnapshot()); RestoreSnapshot(_undoStack.Pop()); UpdateUndoRedoState(); HasUncommittedChanges = true; }
    private void Redo() { if (_redoStack.Count == 0) return; _undoStack.Push(TakeSnapshot()); RestoreSnapshot(_redoStack.Pop()); UpdateUndoRedoState(); HasUncommittedChanges = true; }

    // ── Snap Helpers ───────────────────────────────────────────────────────

    public double SnapToBeatGrid(double seconds)
    {
        if (!SnapToGrid || Bpm <= 0) return seconds;
        double beat = 60.0 / Bpm;
        double nearest = Math.Round(seconds / beat) * beat;
        return Math.Abs(seconds - nearest) < SnapThreshold ? nearest : seconds;
    }

    public double SnapToQuantizeGrid(double seconds)
    {
        int q = GetQuantizeBeatCount();
        if (q <= 0 || Bpm <= 0) return SnapToBeatGrid(seconds);
        double grid = 60.0 / Bpm * q;
        double nearest = Math.Round(seconds / grid) * grid;
        return Math.Abs(seconds - nearest) < SnapThreshold ? nearest : SnapToBeatGrid(seconds);
    }

    // ── Vocal Warning ──────────────────────────────────────────────────────

    private float[]? _vocalCurveForWarning;

    private void UpdateVocalWarning()
    {
        _vocalCurveForWarning ??= VocalDensityCurveData;
        if (_vocalCurveForWarning is null || _vocalCurveForWarning.Length == 0) { IsInVocalRegion = false; return; }
        int idx = (int)Math.Clamp(CurrentPlayPosition / TrackDuration * _vocalCurveForWarning.Length, 0, _vocalCurveForWarning.Length - 1);
        IsInVocalRegion = _vocalCurveForWarning[idx] > 0.4f;
    }

    // ── Snapshot / Undo ────────────────────────────────────────────────────

    private void PushSnapshot()
    {
        _redoStack.Clear();
        if (_undoStack.Count >= MaxUndoHistory) { var arr = _undoStack.ToArray(); _undoStack.Clear(); for (int i = arr.Length - 2; i >= 0; i--) _undoStack.Push(arr[i]); }
        _undoStack.Push(TakeSnapshot()); UpdateUndoRedoState();
    }

    private OrbitCue[] TakeSnapshot() => WorkingCues.Select(c => new OrbitCue { Timestamp = c.Timestamp, Name = c.Name, Color = c.Color, Source = c.Source, Role = c.Role, SlotIndex = c.SlotIndex, Confidence = c.Confidence, IsLoop = c.IsLoop, LoopEndSeconds = c.LoopEndSeconds }).ToArray();
    private void RestoreSnapshot(OrbitCue[] s) { WorkingCues.Clear(); foreach (var c in s) WorkingCues.Add(c); }
    private void UpdateUndoRedoState() { CanUndo = _undoStack.Count > 0; CanRedo = _redoStack.Count > 0; }

    private void InsertCueSorted(OrbitCue cue)
    {
        int idx = WorkingCues.Count(c => c.Timestamp <= cue.Timestamp);
        WorkingCues.Insert(idx, cue);
    }

    // ── Fast-path AnalysisPipelineResult reconstruction ────────────────────

    // internal (not private) so SLSKDONET.Tests can cover the real/fallback signal wiring
    // directly, without standing up the full CueForgeViewModel dependency graph.
    internal static Engine.Analysis.AnalysisPipelineResult BuildAnalysisResultFromFeatures(AudioFeaturesEntity f)
    {
        var result = new Engine.Analysis.AnalysisPipelineResult
        {
            Bpm = f.Bpm,
            DurationSeconds = f.TrackDuration,
            EnergyCurve = ParseJsonFloatArray(f.EnergyCurveJson) ?? Array.Empty<float>(),
            EssentiaInstrumentalProbability = f.InstrumentalProbability,
            EssentiaAggressiveProbability = 0f,
            EssentiaDanceability = f.Danceability,
            // Without this, GenerateCues' PhraseSegments.Count >= 2 check never passes here,
            // so Auto-Generate could never reach the ML path (1) even when EDMFormer phrase
            // data was already cached — it silently fell through to the DSP/heuristic paths.
            PhraseSegments = ParsePhraseSegments(f.PhraseSegmentsJson),
        };

        // Real multi-candidate drop signals (SubBassDropoutEngine / SpectralFluxNoveltyEngine,
        // computed once at analysis time in AudioAnalysisService). Falls back to the old
        // single-collapsed-float reconstruction only for tracks analysed before this existed
        // and not yet re-analysed — GenerateCues' DSP scoring path can compare many real
        // candidates instead of always being handed exactly one guess.
        var dropouts = ParseJsonDoubleList(f.SubBassDropoutTimestampsJson);
        var returns = ParseJsonDoubleList(f.SubBassReturnTimestampsJson);
        var signatures = ParseJsonNoveltySignatures(f.NoveltyDropSignaturesJson);

        if (dropouts.Count > 0) result.SubBassDropoutTimestamps = dropouts;
        if (returns.Count > 0)
        {
            result.SubBassReturnTimestamps = returns;
        }
        else if (f.DropTimeSeconds.HasValue && f.DropConfidence > 0.4f)
        {
            result.SubBassReturnTimestamps = new List<double> { f.DropTimeSeconds.Value };
        }

        if (signatures.Count > 0)
        {
            result.NoveltyDropSignatures = signatures
                .Select(s => (s.DropSeconds, s.BuildStartSeconds, s.Strength))
                .ToList();
        }
        else if (f.CueDrop.HasValue)
        {
            double buildStart = f.CueBuild.HasValue
                ? f.CueBuild.Value
                : Math.Max(0, f.CueDrop.Value - 16 * (60.0 / Math.Max(1, f.Bpm)));
            result.NoveltyDropSignatures = new List<(double, double, float)> { (f.CueDrop.Value, buildStart, f.DropConfidence) };
        }

        return result;
    }

    private static List<double> ParseJsonDoubleList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<double>>(json) ?? new(); } catch { return new(); }
    }

    private static List<Engine.Analysis.NoveltyDropSignatureDto> ParseJsonNoveltySignatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<Engine.Analysis.NoveltyDropSignatureDto>>(json) ?? new(); } catch { return new(); }
    }

    // ── Model ↔ Entity mapping ─────────────────────────────────────────────

    private static OrbitCue EntityToOrbitCue(CuePointEntity e) => new()
    {
        Timestamp = e.TimestampInSeconds, Name = e.Label, Color = e.Color,
        Source = e.IsAutoGenerated ? CueSource.Auto : CueSource.User,
        Role = MapEntityType(e.Type), Confidence = e.Confidence,
        IsLoop = e.IsLoop, LoopEndSeconds = e.LoopEndSeconds, SlotIndex = e.SlotIndex
    };

    private static CuePointEntity OrbitCueToEntity(OrbitCue c, string hash, int index) => new()
    {
        Id = Guid.NewGuid(), TrackUniqueHash = hash,
        TimestampInSeconds = c.Timestamp, Label = c.Name, Color = c.Color,
        IsAutoGenerated = c.Source == CueSource.Auto, Type = MapCueRole(c.Role),
        Confidence = (float)c.Confidence, IsLoop = c.IsLoop, LoopEndSeconds = c.LoopEndSeconds,
        SlotIndex = c.SlotIndex, CreatedAt = DateTime.UtcNow
    };

    private static CueRole MapEntityType(CuePointType t) => t switch
    {
        CuePointType.Intro => CueRole.Intro, CuePointType.Outro => CueRole.Outro,
        CuePointType.Drop => CueRole.Drop, CuePointType.Breakdown => CueRole.Breakdown,
        CuePointType.Build => CueRole.Build, CuePointType.PhraseBoundary => CueRole.PhraseStart,
        _ => CueRole.Custom
    };

    private static CuePointType MapCueRole(CueRole r) => r switch
    {
        CueRole.Intro => CuePointType.Intro, CueRole.Outro => CuePointType.Outro,
        CueRole.Drop => CuePointType.Drop, CueRole.Breakdown => CuePointType.Breakdown,
        CueRole.Breakdown2 => CuePointType.Breakdown, CueRole.Build => CuePointType.Build,
        CueRole.PhraseStart => CuePointType.PhraseBoundary, _ => CuePointType.PhraseBoundary
    };

    // ── Utility ────────────────────────────────────────────────────────────

    private static float[]? ParseJsonFloatArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return null;
        try { return JsonSerializer.Deserialize<float[]>(json); } catch { return null; }
    }

    private static List<PhraseSegment> ParsePhraseSegments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try { return JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new(); } catch { return new(); }
    }

    private string FormatLoopLength(double inSec, double outSec)
    {
        if (Bpm <= 0) return $"{outSec - inSec:F1}s Loop";
        double bars = (outSec - inSec) / (60.0 / Bpm * 4);
        return bars < 1 ? $"{bars * 4:F0} beat loop" : $"{bars:F0} bar loop";
    }

    // ── Transport navigation ───────────────────────────────────────────────

    // Seeks the audio player to a position in seconds and updates the visual playhead.
    // The 30fps subscription from PlayerViewModel.Position will keep CurrentPlayPosition
    // in sync afterwards, so we only need to trigger the audio seek here.
    private void SeekToSeconds(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, TrackDuration);
        CurrentPlayPosition = seconds;
        if (_playerViewModel.LengthMs > 0)
            _playerViewModel.Seek((float)(seconds * 1000.0 / _playerViewModel.LengthMs));
    }

    private void NavigateToPreviousCue()
    {
        var prev = WorkingCues
            .Where(c => !c.IsLoop && c.Timestamp < CurrentPlayPosition - 0.1)
            .OrderByDescending(c => c.Timestamp)
            .FirstOrDefault();
        if (prev == null) return;
        SelectedCue = prev;
        SeekToSeconds(prev.Timestamp);
    }

    private void NavigateToNextCue()
    {
        var next = WorkingCues
            .Where(c => !c.IsLoop && c.Timestamp > CurrentPlayPosition + 0.1)
            .OrderBy(c => c.Timestamp)
            .FirstOrDefault();
        if (next == null) return;
        SelectedCue = next;
        SeekToSeconds(next.Timestamp);
    }

    // ── Quick loop ─────────────────────────────────────────────────────────

    private void SetQuickLoop(int bars)
    {
        if (Bpm <= 0 || TrackHash is null) return;
        double barDuration = 60.0 / Bpm * 4;
        double loopIn = LoopInSeconds ?? CurrentPlayPosition;
        double loopOut = Math.Min(loopIn + barDuration * bars, TrackDuration);
        LoopInSeconds = loopIn;
        LoopOutSeconds = loopOut;

        PushSnapshot();
        var existing = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (existing != null) WorkingCues.Remove(existing);
        WorkingCues.Add(new OrbitCue
        {
            Timestamp = loopIn,
            LoopEndSeconds = loopOut,
            IsLoop = true,
            Name = FormatLoopLength(loopIn, loopOut),
            Color = "#00FF88",
            Source = CueSource.User,
            Role = CueRole.Custom
        });
    }

    // ── Hot cue pad jump ───────────────────────────────────────────────────

    private void JumpToCue(HotCuePadInfo pad)
    {
        if (!pad.IsAssigned) return;
        var cue = WorkingCues.FirstOrDefault(c => c.SlotIndex == pad.Slot && !c.IsLoop);
        if (cue != null) SeekToSeconds(cue.Timestamp);
    }

    // ── Audition with pre-roll ─────────────────────────────────────────────

    private void AuditionCue(OrbitCue cue)
    {
        if (_playerViewModel.LengthMs <= 0) return;
        double preRoll = Bpm > 0 ? 4.0 * 60.0 / Bpm : 2.0; // 4 beats before cue
        double seekSec = Math.Max(0, cue.Timestamp - preRoll);
        _playerViewModel.Seek((float)(seekSec * 1000.0 / _playerViewModel.LengthMs));
    }

    // ── Keyboard nudge (±1 beat) ───────────────────────────────────────────

    private void NudgeCue(int direction)
    {
        if (SelectedCue == null || Bpm <= 0 || TrackHash is null) return;
        PushSnapshot();
        double beat = 60.0 / Bpm;
        SelectedCue.Timestamp = Math.Clamp(SelectedCue.Timestamp + direction * beat, 0, TrackDuration);
        HasUncommittedChanges = true;
        RefreshHotCuePads();
    }

    // ── Cue detail panel edits ──────────────────────────────────────────────

    private void SetSelectedCueRole(CueRole role)
    {
        if (SelectedCue is null || TrackHash is null) return;
        PushSnapshot();
        SelectedCue.Role = role;
        MarkSelectedCueEdited();
    }

    private void SetSelectedCueColor(string hexColor)
    {
        if (SelectedCue is null || TrackHash is null) return;
        PushSnapshot();
        SelectedCue.Color = hexColor;
        MarkSelectedCueEdited();
    }

    private void SetSelectedCueSlot(int slotIndex)
    {
        if (SelectedCue is null || TrackHash is null || SelectedCue.IsLoop) return;
        PushSnapshot();

        // A hot cue slot can only ever hold one cue — clear whoever's already there.
        if (slotIndex >= 0)
        {
            var priorHolder = WorkingCues.FirstOrDefault(c => !c.IsLoop && c.SlotIndex == slotIndex && c != SelectedCue);
            if (priorHolder is not null) priorHolder.SlotIndex = -1;
        }

        SelectedCue.SlotIndex = slotIndex;
        MarkSelectedCueEdited();
    }

    private void MarkSelectedCueEdited()
    {
        if (SelectedCue is null) return;
        MarkCueFieldEdited(SelectedCue);
    }

    /// <summary>
    /// Flags the session dirty after a direct field edit (rename, recolor, role, slot).
    /// OrbitCue has no property-changed notification of its own, so — unlike structural
    /// edits (add/delete/nudge) — these edits can't push a per-keystroke undo snapshot;
    /// callers that need undo call PushSnapshot() themselves before mutating. This also
    /// means the cue list's own bindings (Role/SlotLabel/color swatch) won't notice an
    /// in-place field edit, so re-inserting the same reference forces ItemsControl to
    /// regenerate that row.
    /// </summary>
    public void MarkCueFieldEdited(OrbitCue cue)
    {
        if (TrackHash is null) return;
        cue.Source = CueSource.User;
        HasUncommittedChanges = true;
        this.RaisePropertyChanged(nameof(IsExportReady));
        RefreshHotCuePads();

        int idx = WorkingCues.IndexOf(cue);
        if (idx >= 0) WorkingCues[idx] = cue;
    }

    // ── Slot auto-assignment ───────────────────────────────────────────────

    private void AssignSlotsByRolePriority()
    {
        // Clear auto-assigned slots; preserve user-assigned slots
        foreach (var c in WorkingCues.Where(c => c.Source == CueSource.Auto && !c.IsLoop))
            c.SlotIndex = -1;

        var rolePriority = new[] { CueRole.Drop, CueRole.Breakdown, CueRole.Build, CueRole.Intro, CueRole.KickIn, CueRole.Outro, CueRole.Vocals, CueRole.Custom };
        int slot = 0;
        foreach (var role in rolePriority)
        {
            foreach (var cue in WorkingCues.Where(c => c.Source == CueSource.Auto && c.Role == role && !c.IsLoop).OrderBy(c => c.Timestamp))
            {
                while (slot < 8 && WorkingCues.Any(c => c.Source == CueSource.User && c.SlotIndex == slot))
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

    public void Dispose() => _disposables.Dispose();
}

/// <summary>A selectable hot-cue-slot option for the cue detail panel (0-7 = A-H, -1 = Memory).</summary>
public sealed record CueSlotOption(string Label, int Value);
