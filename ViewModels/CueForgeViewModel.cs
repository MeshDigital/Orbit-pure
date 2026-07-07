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
        private set => this.RaiseAndSetIfChanged(ref _trackDuration, value);
    }

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
        set => this.RaiseAndSetIfChanged(ref _currentPlayPosition, value);
    }

    // ── Waveform Data ──────────────────────────────────────────────────────

    private byte[]? _waveformLow;
    public byte[]? WaveformLow { get => _waveformLow; private set => this.RaiseAndSetIfChanged(ref _waveformLow, value); }

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
    public double? LoopInSeconds { get => _loopInSeconds; set => this.RaiseAndSetIfChanged(ref _loopInSeconds, value); }

    private double? _loopOutSeconds;
    public double? LoopOutSeconds { get => _loopOutSeconds; set => this.RaiseAndSetIfChanged(ref _loopOutSeconds, value); }

    public int GetQuantizeBeatCount() => _quantizeBeats switch
    {
        "Off" => 0, "1 beat" => 1, "2 beats" => 2, "4 beats" => 4, "8 beats" => 8,
        "16 beats" => 16, "32 beats" => 32, "64 beats (16 bars)" => 64, "128 beats (32 bars)" => 128, _ => 16
    };

    // ── Playlist Browser ───────────────────────────────────────────────────

    // Full list of tracks from the active playlist (set by caller when navigating here)
    private readonly List<PlaylistTrackViewModel> _allPlaylistTracks = new();

    public ObservableCollection<PlaylistTrackViewModel> BrowserTracks { get; } = new();

    private string _browserQuery = "";
    public string BrowserQuery
    {
        get => _browserQuery;
        set { this.RaiseAndSetIfChanged(ref _browserQuery, value); ApplyBrowserFilter(value); }
    }

    public System.Windows.Input.ICommand LoadFromBrowserCommand { get; private set; } = null!;

    public void SetPlaylistTracks(IEnumerable<PlaylistTrackViewModel>? tracks)
    {
        _allPlaylistTracks.Clear();
        if (tracks != null) _allPlaylistTracks.AddRange(tracks);
        ApplyBrowserFilter(_browserQuery);
    }

    private void ApplyBrowserFilter(string query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allPlaylistTracks
            : _allPlaylistTracks.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

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
        ConfigManager configManager)
    {
        _cueService = cueService;
        _libraryService = libraryService;
        _engineCueService = engineCueService;
        _playerViewModel = playerViewModel;
        _camelotKeyService = camelotKeyService;
        _logger = logger;
        _config = config;
        _configManager = configManager;

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

        LoadFromBrowserCommand = new Views.AsyncRelayCommand<PlaylistTrackViewModel>(async track =>
        {
            if (track == null) return;
            await LoadTrackAsync(track.GlobalId, track.Title, track.Artist);
        });

        WorkingCues.CollectionChanged += (_, _) =>
        {
            HasUncommittedChanges = true;
            this.RaisePropertyChanged(nameof(IsExportReady));
        };

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

        // Load cues from DB
        var entities = await _cueService.GetByTrackIdAsync(trackHash);
        var cues = entities.Select(EntityToOrbitCue).ToList();

        // Load audio features for waveform data + analysis metadata
        var features = await _libraryService.GetAudioFeaturesByHashAsync(trackHash);
        if (features is not null)
        {
            Bpm = (int)Math.Round(Math.Max(60, features.Bpm));
            TrackDuration = features.TrackDuration > 0 ? features.TrackDuration : 300.0;

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
            if (features is null) { _logger.LogWarning("CueForge: no features found for {Hash}", TrackHash); return; }

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

            PushSnapshot();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                WorkingCues.Clear();
                foreach (var e in generatedEntities)
                {
                    WorkingCues.Add(EntityToOrbitCue(e));
                }
            });

            _logger.LogInformation("CueForge: generated {Count} cues", generatedEntities.Count);
        }
        finally { IsGenerating = false; }
    }

    private async Task UpdateCueAsync(OrbitCue cue) { if (TrackHash is null) return; PushSnapshot(); cue.Source = CueSource.User; await Task.CompletedTask; }

    private async Task DeleteCueAsync(OrbitCue cue) { if (TrackHash is null) return; PushSnapshot(); WorkingCues.Remove(cue); await Task.CompletedTask; }

    private async Task SetLoopInAsync() { LoopInSeconds = CurrentPlayPosition; if (LoopOutSeconds.HasValue && LoopOutSeconds <= LoopInSeconds) LoopOutSeconds = null; await Task.CompletedTask; }

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
        await Task.CompletedTask;
    }

    private async Task CommitChangesAsync()
    {
        if (TrackHash is null) return;
        _logger.LogInformation("CueForge: committing {Count} cues for {Hash}", WorkingCues.Count, TrackHash);
        await _cueService.DeleteAllByTrackIdAsync(TrackHash);
        if (WorkingCues.Count > 0)
            await _cueService.CreateManyAsync(WorkingCues.Select((c, i) => OrbitCueToEntity(c, TrackHash, i)).ToList());
        HasUncommittedChanges = false;
    }

    private async Task DiscardChangesAsync()
    {
        if (TrackHash is null) return;
        var entities = await _cueService.GetByTrackIdAsync(TrackHash);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            WorkingCues.Clear();
            foreach (var e in entities) WorkingCues.Add(EntityToOrbitCue(e));
        });
        HasUncommittedChanges = false; _undoStack.Clear(); _redoStack.Clear(); UpdateUndoRedoState();
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

    private static Engine.Analysis.AnalysisPipelineResult BuildAnalysisResultFromFeatures(AudioFeaturesEntity f)
    {
        var result = new Engine.Analysis.AnalysisPipelineResult
        {
            Bpm = f.Bpm,
            DurationSeconds = f.TrackDuration,
            EnergyCurve = ParseJsonFloatArray(f.EnergyCurveJson) ?? Array.Empty<float>(),
            EssentiaInstrumentalProbability = f.InstrumentalProbability,
            EssentiaAggressiveProbability = 0f,
            EssentiaDanceability = f.Danceability,
        };

        // Seed SubBassReturnTimestamps from stored DropTimeSeconds
        if (f.DropTimeSeconds.HasValue && f.DropConfidence > 0.4f)
            result.SubBassReturnTimestamps = new List<double> { f.DropTimeSeconds.Value };

        // Seed NoveltyDropSignatures from CueDrop / CueBuild
        var noveltyDrops = new List<(double, double, float)>();
        if (f.CueDrop.HasValue)
        {
            double buildStart = f.CueBuild.HasValue
                ? f.CueBuild.Value
                : Math.Max(0, f.CueDrop.Value - 16 * (60.0 / Math.Max(1, f.Bpm)));
            noveltyDrops.Add((f.CueDrop.Value, buildStart, f.DropConfidence));
        }
        result.NoveltyDropSignatures = noveltyDrops;

        return result;
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

    public void Dispose() => _disposables.Dispose();
}
