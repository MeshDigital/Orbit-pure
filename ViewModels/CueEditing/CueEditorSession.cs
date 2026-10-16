using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>What a session needs to know about the track it edits.</summary>
/// <param name="Bpm">Precise (fractional) tempo the grid uses; 0 when unknown.</param>
/// <param name="Downbeat">Seconds of the first downbeat; the grid is anchored here.</param>
public sealed record CueTrackInfo(string Hash, string Title, string? FilePath, double Bpm, double Downbeat, double Duration, string? Genre = null);

/// <summary>A selectable hot-cue-slot option for an inspector (0-7 = A-H, -1 = memory cue).</summary>
public sealed record CueSlotOption(string Label, int Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The one cue-editing engine. Each instance edits one track's cues: the working set, selection,
/// add / move / rename / recolour / role / delete, hot cue pads A–H, loops, drops with their
/// build-in cues, snapping on the track's beat grid, undo / redo, dirty tracking and load / save
/// through <see cref="ICuePointService"/> (the CuePoints table the Rekordbox export, workstation
/// and Flow Builder all read).
///
/// Hosts: Cue Forge (<see cref="CueForgeViewModel"/>) and each Flow Builder / Mix sidepanel deck
/// (<see cref="TrackCueEditorViewModel"/>). A session has no player or view dependency; it plays
/// through the host's <see cref="ICueAudition"/>. Sessions on the same track are linked through
/// <see cref="CueSessionRegistry"/> (see its rule).
///
/// Nothing is written until <see cref="SaveAsync"/>. A failed save keeps the working set and the
/// dirty flag and reports the failure in <see cref="StatusText"/> (<see cref="IsStatusError"/>).
/// Any cue the user touches becomes a user cue, so auto-analysis never overwrites it.
/// </summary>
public sealed class CueEditorSession : ReactiveObject, IDisposable
{
    public const int MaxUndoHistory = 50;
    /// <summary>Fine nudge step (Ctrl+arrow): 10 ms, for lining a drop up exactly on the transient.</summary>
    public const double FineNudgeSeconds = 0.010;
    private const string LoopColor = "#E0641B";       // Rekordbox loop orange (cue data, not UI chrome)
    private const string DefaultCueColor = "#28E214"; // Rekordbox default hot cue green

    private readonly ICuePointService _cueService;
    private readonly ICueAudition _audition;
    private readonly CueSessionRegistry _registry;
    private readonly ILogger _logger;
    private readonly List<OrbitCue[]> _undoStack = new();
    private readonly Stack<OrbitCue[]> _redoStack = new();
    private readonly HashSet<OrbitCue> _trackedCues = new();

    // Each cue's position and source as of the last finished edit. The waveforms move a cue in
    // place while it is dragged; this is the "before" of that drag (undo step, CueMoved, auto→user).
    private Dictionary<OrbitCue, (double Time, double LoopEnd, CueSource Source)> _baseline = new();
    private OrbitCue? _dragStartedCue;
    private bool _disposed;

    public CueEditorSession(ICuePointService cueService, ICueAudition audition, CueSessionRegistry? registry = null, ILogger? logger = null)
    {
        _cueService = cueService;
        _audition = audition;
        _registry = registry ?? new CueSessionRegistry(new AppConfig());
        _logger = logger ?? NullLogger.Instance;
        _registry.Add(this);

        var canAct = this.WhenAnyValue(x => x.TrackHash, x => x.IsLocked, (h, locked) => !string.IsNullOrEmpty(h) && !locked);
        var hasSelection = this.WhenAnyValue(x => x.SelectedCue).Select(c => c != null);
        var canActOnSelection = canAct.CombineLatest(hasSelection, (a, s) => a && s);
        var canSaveOrRevert = this.WhenAnyValue(x => x.IsDirty, x => x.TrackHash, x => x.HasExternalChanges,
            (dirty, hash, external) => hash != null && (dirty || external));

        AddCueAtPlayheadCommand = ReactiveCommand.Create(() => { AddCue(SnapTime(_audition.PositionSeconds), null); }, canAct);
        AddCueAtCommand = ReactiveCommand.Create<double>(t => { AddCue(SnapTime(t), null); }, canAct);
        DeleteCueCommand = ReactiveCommand.Create<OrbitCue>(DeleteCue, canAct);
        DeleteSelectedCueCommand = ReactiveCommand.Create(() => { if (SelectedCue != null) DeleteCue(SelectedCue); }, canActOnSelection);
        SelectCueCommand = ReactiveCommand.Create<OrbitCue>(cue => SelectedCue = cue);
        // Cue Forge's waveform fires this the instant a drag starts moving, before the cue changes.
        CueDragStartedCommand = ReactiveCommand.Create<OrbitCue>(cue =>
        {
            if (TrackHash is null || !Cues.Contains(cue)) return;
            PushSnapshot();
            _dragStartedCue = cue;
        });
        // Fired on release once a waveform moved a cue (or loop edge) in place.
        CueUpdatedCommand = ReactiveCommand.Create<OrbitCue>(OnCueDragged);
        // <string, Unit>: XAML literals like CommandParameter="-beat" arrive as strings.
        NudgeCueCommand = ReactiveCommand.Create<string>(NudgeCue, canActOnSelection);
        SetSelectedCueRoleCommand = ReactiveCommand.Create<CueRole>(SetSelectedCueRole, canAct);
        SetSelectedCueColorCommand = ReactiveCommand.Create<string>(SetSelectedCueColor, canAct);
        SetSelectedCueSlotCommand = ReactiveCommand.Create<int>(SetSelectedCueSlot, canAct);
        TriggerPadCommand = ReactiveCommand.Create<HotCuePadInfo>(TriggerPad, canAct);
        ClearPadCommand = ReactiveCommand.Create<HotCuePadInfo>(ClearPad, canAct);

        SetLoopInCommand = ReactiveCommand.Create(SetLoopIn, canAct);
        SetLoopOutCommand = ReactiveCommand.Create(SetLoopOut, canAct);
        var hasLoop = this.WhenAnyValue(x => x.HasActiveLoop);
        LoopHalfCommand = ReactiveCommand.Create(() => ScaleLoop(0.5), hasLoop);
        LoopDoubleCommand = ReactiveCommand.Create(() => ScaleLoop(2.0), hasLoop);
        ClearLoopCommand = ReactiveCommand.Create(ClearLoop, hasLoop);
        // Parameter: bar count as an int (x:Int32) or a numeric string (XAML literal).
        SetQuickLoopCommand = ReactiveCommand.Create<object>(param =>
        {
            if (param is int i) SetQuickLoop(i);
            else if (int.TryParse(param?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bars)) SetQuickLoop(bars);
        }, canAct);

        SetDropAtPlayheadCommand = ReactiveCommand.Create(() => SetDropAt(_audition.PositionSeconds, null), canAct);
        SetNumberedDropAtPlayheadCommand = ReactiveCommand.Create<int>(n => SetDropAt(_audition.PositionSeconds, n), canAct);
        SetDropAtCommand = ReactiveCommand.Create<double>(t => SetDropAt(t, null), canAct);

        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));
        SaveCommand = ReactiveCommand.CreateFromTask(async () => { await SaveAsync(); }, canSaveOrRevert);
        RevertCommand = ReactiveCommand.CreateFromTask(RevertAsync, canSaveOrRevert);

        Cues.CollectionChanged += OnCuesCollectionChanged;
    }

    // ── Host options ───────────────────────────────────────────────────────

    /// <summary>Shown in the other editor's warnings, e.g. "Cue Forge", "Flow Builder".</summary>
    public string EditorName { get; init; } = "another editor";

    /// <summary>Play the track from a cue right after adding, dragging or dropping it (Flow Builder:
    /// you place cues by ear). Nudges always replay from the cue.</summary>
    public bool AuditionAfterEdits { get; init; }

    /// <summary>Snap a dragged cue on release. Off when the host's waveform already snaps live
    /// (Cue Forge's does, and lets Shift place freely).</summary>
    public bool SnapOnDragRelease { get; init; }

    /// <summary>Asked before Revert throws away unsaved edits; null = no confirmation.</summary>
    public Func<Task<bool>>? ConfirmRevert { get; set; }

    private bool _isLocked;
    /// <summary>Set by the host while it works on the cues itself (e.g. generating): edits are disabled.</summary>
    public bool IsLocked { get => _isLocked; set => this.RaiseAndSetIfChanged(ref _isLocked, value); }

    // ── Events ─────────────────────────────────────────────────────────────

    /// <summary>A cue moved (drag or nudge) from one time to another; a host whose mix point sat on
    /// that cue moves it along.</summary>
    public event EventHandler<(double From, double To)>? CueMoved;

    /// <summary>This session wrote its cues to the store.</summary>
    public event EventHandler? Saved;

    /// <summary>The working set was replaced from the store (revert, reload, or another editor saved).</summary>
    public event EventHandler? Reloaded;

    /// <summary>The loop the loop tools were acting on was removed.</summary>
    public event EventHandler? ActiveLoopRemoved;

    /// <summary>Something asked for the selected cue's name box to take focus (F2, menu "Rename").</summary>
    public event EventHandler? RenameRequested;

    // ── Track ──────────────────────────────────────────────────────────────

    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set
        {
            this.RaiseAndSetIfChanged(ref _trackHash, value);
            this.RaisePropertyChanged(nameof(HasTrack));
            this.RaisePropertyChanged(nameof(IsSaved));
        }
    }

    public bool HasTrack => TrackHash != null;
    public string TrackTitle { get; private set; } = string.Empty;
    public string? FilePath { get; private set; }

    private string? _genre;
    /// <summary>Grid tempo (precise BPM); 0 when unknown.</summary>
    public double Bpm { get; private set; }
    /// <summary>Downbeat offset (seconds) the grid is anchored at.</summary>
    public double GridOffset { get; private set; }
    public double Duration { get; private set; } = 300.0;

    private double BeatSeconds => Bpm > 0 ? 60.0 / Bpm : 0.5;
    private double BarSeconds => BeatSeconds * 4;

    /// <summary>Opens a track for editing: a clean working set from <paramref name="stored"/>, no history.</summary>
    public void Load(CueTrackInfo track, IEnumerable<CuePointEntity> stored)
    {
        PendingLoopIn = null;
        SelectedCue = null;
        _dragStartedCue = null;
        TrackTitle = track.Title;
        FilePath = track.FilePath;
        _genre = track.Genre;
        Bpm = track.Bpm > 0 ? track.Bpm : 0;
        GridOffset = track.Downbeat > 0 ? track.Downbeat : 0;
        Duration = track.Duration > 0 ? track.Duration : 300.0;
        _undoStack.Clear(); _redoStack.Clear();
        UpdateUndoRedoState();
        TrackHash = track.Hash;
        LoadCuesClean(stored.Select(OrbitCue.FromEntity));
        HasExternalChanges = false;
        RaiseGridChanged();

        var dirtyPeer = _registry.PeersOf(this).FirstOrDefault(p => p.IsDirty);
        SetStatus(dirtyPeer == null ? string.Empty
            : $"This track has unsaved cue edits in {dirtyPeer.EditorName}. They show up here once they're saved there.");
    }

    /// <summary>Closes the track: nothing loaded, nothing to save.</summary>
    public void Unload()
    {
        PendingLoopIn = null;
        SelectedCue = null;
        _dragStartedCue = null;
        TrackHash = null;
        TrackTitle = string.Empty;
        FilePath = null;
        LoadCuesClean(Array.Empty<OrbitCue>());
        _undoStack.Clear(); _redoStack.Clear();
        UpdateUndoRedoState();
        HasExternalChanges = false;
        SetStatus(string.Empty);
    }

    /// <summary>The track's length became known later (e.g. from the player).</summary>
    public void UpdateDuration(double seconds)
    {
        if (seconds > 0) Duration = seconds;
    }

    private void RaiseGridChanged()
    {
        this.RaisePropertyChanged(nameof(Bpm));
        this.RaisePropertyChanged(nameof(GridOffset));
        this.RaisePropertyChanged(nameof(Duration));
        this.RaisePropertyChanged(nameof(TemplateHint));
        this.RaisePropertyChanged(nameof(SelectedCuePositionDisplay));
    }

    // ── Cues ───────────────────────────────────────────────────────────────

    /// <summary>Time-ordered working set. Edited in place; <see cref="OrbitCue"/> raises change notifications.</summary>
    public ObservableCollection<OrbitCue> Cues { get; } = new();

    public bool HasCues => Cues.Count > 0;

    private int _cueRevision;
    /// <summary>Bumped on every change to the set or to any cue in it, so views redraw.</summary>
    public int CueRevision { get => _cueRevision; private set => this.RaiseAndSetIfChanged(ref _cueRevision, value); }

    private bool _loadingClean;

    private void OnCuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Track in-place edits (rename, recolour, drag) so pads and views stay current.
        var now = new HashSet<OrbitCue>(Cues);
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
        if (_loadingClean) return;
        if (SelectedCue != null && !now.Contains(SelectedCue)) SelectedCue = null;
        AfterSetChanged();
    }

    private void AfterSetChanged()
    {
        RefreshHotCuePads();
        this.RaisePropertyChanged(nameof(HasCues));
        this.RaisePropertyChanged(nameof(ActiveLoop));
        this.RaisePropertyChanged(nameof(HasActiveLoop));
        this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
        CueRevision++;
    }

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        CueRevision++;
        if (e.PropertyName is nameof(OrbitCue.Name) or nameof(OrbitCue.Color) or nameof(OrbitCue.SlotIndex)
            or nameof(OrbitCue.Timestamp) or nameof(OrbitCue.Role) or nameof(OrbitCue.IsLoop))
            RefreshHotCuePads();
        if (e.PropertyName is nameof(OrbitCue.Timestamp) or nameof(OrbitCue.LoopEndSeconds) or nameof(OrbitCue.IsLoop))
            this.RaisePropertyChanged(nameof(ActiveLoopDisplay));
        if (ReferenceEquals(sender, SelectedCue)) RaiseSelectionDerived();
    }

    /// <summary>Replaces the working set without marking it dirty (load / revert / reload).</summary>
    private void LoadCuesClean(IEnumerable<OrbitCue> cues)
    {
        _loadingClean = true;
        try
        {
            Cues.Clear();
            foreach (var c in cues.OrderBy(c => c.Timestamp)) Cues.Add(c);
        }
        finally { _loadingClean = false; }
        if (SelectedCue != null && !Cues.Contains(SelectedCue)) SelectedCue = null;
        AfterSetChanged();
        IsDirty = false;
        RefreshBaseline();
    }

    private void ReplaceCues(IReadOnlyList<OrbitCue> cues)
    {
        var selected = SelectedCue;
        Cues.Clear();
        foreach (var c in cues) Cues.Add(c);
        if (selected != null && cues.Contains(selected)) SelectedCue = selected;
        RefreshBaseline();
    }

    private void InsertCueSorted(OrbitCue cue)
    {
        int idx = Cues.Count(c => c.Timestamp <= cue.Timestamp);
        Cues.Insert(idx, cue);
    }

    /// <summary>Keeps the list in time order after a drag/nudge moved a cue past a neighbour.</summary>
    private void ResortCues()
    {
        for (int i = 0; i < Cues.Count; i++)
        {
            int target = i;
            for (int j = i + 1; j < Cues.Count; j++)
                if (Cues[j].Timestamp < Cues[target].Timestamp) target = j;
            if (target != i) Cues.Move(target, i);
        }
    }

    private void RefreshBaseline() =>
        _baseline = Cues.ToDictionary(c => c, c => (c.Timestamp, c.LoopEndSeconds, c.Source));

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
        seconds = Math.Clamp(seconds, 0, Duration);
        if (!SnapToGrid) return seconds;
        return Math.Clamp(CueForgeGrid.Snap(seconds, Bpm, GridOffset, SnapBeats), 0, Duration);
    }

    // ── Selection & inspector ──────────────────────────────────────────────

    private OrbitCue? _selectedCue;
    public OrbitCue? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (ReferenceEquals(_selectedCue, value)) return;
            this.RaiseAndSetIfChanged(ref _selectedCue, value);
            _editingCueName = value?.Name ?? "";
            this.RaisePropertyChanged(nameof(EditingCueName));
            RaiseSelectionDerived();
            foreach (var pad in HotCuePads) pad.IsSelected = pad.Cue != null && ReferenceEquals(pad.Cue, value);
        }
    }

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
        : $"{CueForgeGrid.FormatTime(SelectedCue.Timestamp)}  ·  {CueForgeGrid.FormatBarBeat(SelectedCue.Timestamp, Bpm, GridOffset)}";

    public string SelectedCueDetail => SelectedCue switch
    {
        null => "",
        { IsLoop: true } l => $"Loop · {FormatLoopLength(l.Timestamp, l.LoopEndSeconds)}",
        { Source: CueSource.Auto } a => $"Auto-generated · {(int)Math.Round(a.Confidence * 100)}% confidence",
        _ => "Your cue",
    };

    private string _editingCueName = "";
    /// <summary>Text in a name box. Applied by <see cref="CommitCueName"/> (Enter / focus out),
    /// thrown away by <see cref="RevertCueName"/> (Esc).</summary>
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
        FieldEdited(cue, $"Renamed to \"{name}\"");
    }

    public void RevertCueName() => EditingCueName = SelectedCue?.Name ?? "";

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

    /// <summary>Rekordbox colour for a structural role (used when cues are generated).</summary>
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
        _                                       => "#FFFFFF", // White
    };

    // ── Save state ─────────────────────────────────────────────────────────

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDirty, value);
            this.RaisePropertyChanged(nameof(IsSaved));
        }
    }

    /// <summary>Track loaded and nothing unsaved — drives a quiet "Saved" indicator.</summary>
    public bool IsSaved => HasTrack && !IsDirty;

    private bool _hasExternalChanges;
    /// <summary>Another editor saved this track while this one had unsaved edits.</summary>
    public bool HasExternalChanges { get => _hasExternalChanges; private set => this.RaiseAndSetIfChanged(ref _hasExternalChanges, value); }

    private bool _canUndo, _canRedo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    private string _statusText = "";
    /// <summary>Last edit / save result or hint. <see cref="IsStatusError"/> marks failures.</summary>
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _isStatusError;
    public bool IsStatusError { get => _isStatusError; private set => this.RaiseAndSetIfChanged(ref _isStatusError, value); }

    public void SetStatus(string message, bool isError = false)
    {
        IsStatusError = isError;
        StatusText = message;
    }

    // ── Hot cue pads ───────────────────────────────────────────────────────

    public IReadOnlyList<HotCuePadInfo> HotCuePads { get; } =
        Enumerable.Range(0, 8).Select(i => new HotCuePadInfo { Slot = i, PadLabel = ((char)('A' + i)).ToString() }).ToList();

    private void RefreshHotCuePads()
    {
        foreach (var pad in HotCuePads)
        {
            var cue = Cues.FirstOrDefault(c => c.SlotIndex == pad.Slot && !c.IsLoop);
            pad.Assign(cue);
            pad.IsSelected = cue != null && ReferenceEquals(cue, SelectedCue);
        }
        this.RaisePropertyChanged(nameof(MemoryCueCount));
        this.RaisePropertyChanged(nameof(CueCountDisplay));
    }

    public int MemoryCueCount => Cues.Count(c => !c.IsLoop && c.SlotIndex < 0);

    public string CueCountDisplay
    {
        get
        {
            int hot = Cues.Count(c => !c.IsLoop && c.SlotIndex >= 0);
            int loops = Cues.Count(c => c.IsLoop);
            return $"{hot} hot · {MemoryCueCount} memory · {loops} loop{(loops == 1 ? "" : "s")}";
        }
    }

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
    public OrbitCue? ActiveLoop => SelectedCue is { IsLoop: true } l ? l : Cues.LastOrDefault(c => c.IsLoop);
    public bool HasActiveLoop => ActiveLoop != null;

    public string ActiveLoopDisplay => PendingLoopIn is { } inAt
        ? $"IN {CueForgeGrid.FormatTime(inAt)} — press OUT"
        : ActiveLoop is { } l ? $"{FormatLoopLength(l.Timestamp, l.LoopEndSeconds)} at {CueForgeGrid.FormatTime(l.Timestamp)}"
        : "No loop";

    // ── Drop cue template ──────────────────────────────────────────────────

    public static IReadOnlyList<string> DropCountdownModes => DropCountdownCues.Modes;

    /// <summary>App-wide drop cue template (see <see cref="CueTemplateSettings"/>).</summary>
    public string DropCountdownMode
    {
        get => _registry.Templates.Mode;
        set => _registry.Templates.SetMode(value);
    }

    /// <summary>Bars before the drop for the Custom template, e.g. "32,16,8".</summary>
    public string CustomCountdownBars
    {
        get => _registry.Templates.CustomBars;
        set => _registry.Templates.SetCustomBars(value);
    }

    public bool IsCustomTemplate => DropCountdownMode == DropCountdownCues.Custom;

    /// <summary>What the template does for this track, e.g. "Auto → DnB / Bass".</summary>
    public string TemplateHint
    {
        get
        {
            var bars = CountdownBars;
            string what = bars.Count == 0 ? "no build-in cues" : string.Join(" ", bars.Select(b => $"−{b}")) + " bars";
            return DropCountdownMode == DropCountdownCues.Auto && TrackHash != null
                ? $"Auto → {DropCountdownCues.AutoTemplate(_genre, Bpm)}"
                : what;
        }
    }

    private IReadOnlyList<int> CountdownBars => _registry.Templates.ResolveBars(_genre, Bpm);

    internal void OnTemplateChanged()
    {
        this.RaisePropertyChanged(nameof(DropCountdownMode));
        this.RaisePropertyChanged(nameof(CustomCountdownBars));
        this.RaisePropertyChanged(nameof(IsCustomTemplate));
        this.RaisePropertyChanged(nameof(TemplateHint));
    }

    // ── Commands ───────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddCueAtPlayheadCommand { get; }
    /// <summary>Waveform right-click "Add cue here" (seconds; snapped).</summary>
    public ReactiveCommand<double, Unit> AddCueAtCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> DeleteCueCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCueCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> SelectCueCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> CueDragStartedCommand { get; }
    public ReactiveCommand<OrbitCue, Unit> CueUpdatedCommand { get; }
    /// <summary>"-beat"/"+beat" (or "-1"/"1"), "-bar"/"+bar", "-fine"/"+fine" (10 ms).</summary>
    public ReactiveCommand<string, Unit> NudgeCueCommand { get; }
    public ReactiveCommand<CueRole, Unit> SetSelectedCueRoleCommand { get; }
    public ReactiveCommand<string, Unit> SetSelectedCueColorCommand { get; }
    public ReactiveCommand<int, Unit> SetSelectedCueSlotCommand { get; }
    /// <summary>Pad click: assigned → jump there and play; empty → set a hot cue at the playhead.</summary>
    public ReactiveCommand<HotCuePadInfo, Unit> TriggerPadCommand { get; }
    /// <summary>Pad menu "Clear pad": the cue stays, as a memory cue.</summary>
    public ReactiveCommand<HotCuePadInfo, Unit> ClearPadCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopInCommand { get; }
    public ReactiveCommand<Unit, Unit> SetLoopOutCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopHalfCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopDoubleCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLoopCommand { get; }
    /// <summary>1/2/4/8/16-bar loop: resizes the selected loop, otherwise starts a memory loop at the
    /// selected cue (or the snapped playhead).</summary>
    public ReactiveCommand<object, Unit> SetQuickLoopCommand { get; }
    /// <summary>Drop button / key D: a drop at the playhead.</summary>
    public ReactiveCommand<Unit, Unit> SetDropAtPlayheadCommand { get; }
    /// <summary>Drop 1 / Drop 2 at the playhead.</summary>
    public ReactiveCommand<int, Unit> SetNumberedDropAtPlayheadCommand { get; }
    /// <summary>Waveform right-click "Drop here" (seconds).</summary>
    public ReactiveCommand<double, Unit> SetDropAtCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }

    // ── Edits ──────────────────────────────────────────────────────────────

    /// <summary>Adds a cue at <paramref name="seconds"/> (already snapped) and selects it. Takes
    /// <paramref name="slot"/> or the next free pad (a memory cue when all eight are taken).</summary>
    private OrbitCue? AddCue(double seconds, int? slot)
    {
        if (TrackHash is null) return null;
        PushSnapshot();
        int nextSlot = slot ?? Enumerable.Range(0, 8).Cast<int?>()
            .FirstOrDefault(i => Cues.All(c => c.IsLoop || c.SlotIndex != i)) ?? -1;
        var cue = new OrbitCue
        {
            Timestamp = Math.Clamp(seconds, 0, Duration),
            Name = nextSlot >= 0 ? $"Cue {(char)('A' + nextSlot)}" : $"Memory {MemoryCueCount + 1}",
            Color = nextSlot >= 0 ? CueColorSwatches[nextSlot % CueColorSwatches.Count] : DefaultCueColor,
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = nextSlot,
            Confidence = 1.0,
        };
        InsertCueSorted(cue);
        int cleared = ClearAutoCues(cue);
        SelectedCue = cue;
        Changed($"Added {cue.Name} at {CueForgeGrid.FormatTime(cue.Timestamp)}" + AutoClearedNote(cleared));
        if (AuditionAfterEdits) _audition.PlayFrom(cue.Timestamp);
        return cue;
    }

    // A single delete is undoable, so no confirm dialog — the status line points at Ctrl+Z instead.
    private void DeleteCue(OrbitCue? cue)
    {
        if (TrackHash is null || cue == null || !Cues.Contains(cue)) return;
        bool wasActiveLoop = cue.IsLoop && ReferenceEquals(cue, ActiveLoop);
        PushSnapshot();
        double? outBefore = cue.Role == CueRole.Drop ? OutBefore() : null;
        Cues.Remove(cue);
        if (cue.Role == CueRole.Drop)
        {
            var left = DropCountdownCues.RemoveFor(Cues, cue.Timestamp, Bpm);
            ReplaceCues(DropCountdownCues.Refresh(left, CountdownBars, Bpm, GridOffset, Duration, outBefore));
        }
        Changed($"Deleted \"{cue.Name}\". Ctrl+Z to undo.");
        if (wasActiveLoop) ActiveLoopRemoved?.Invoke(this, EventArgs.Empty);
    }

    private void OnCueDragged(OrbitCue cue)
    {
        bool startedHere = ReferenceEquals(_dragStartedCue, cue);
        _dragStartedCue = null;
        if (TrackHash is null || !Cues.Contains(cue)) return;
        var origin = _baseline.TryGetValue(cue, out var b) ? b : (Time: cue.Timestamp, LoopEnd: cue.LoopEndSeconds, Source: cue.Source);

        if (SnapOnDragRelease)
        {
            if (Math.Abs(cue.Timestamp - origin.Time) > 1e-6) cue.Timestamp = SnapTime(cue.Timestamp);
            if (cue.IsLoop && Math.Abs(cue.LoopEndSeconds - origin.LoopEnd) > 1e-6) cue.LoopEndSeconds = SnapTime(cue.LoopEndSeconds);
        }
        bool moved = Math.Abs(cue.Timestamp - origin.Time) > 1e-6 ||
                     (cue.IsLoop && Math.Abs(cue.LoopEndSeconds - origin.LoopEnd) > 1e-6);
        if (!moved)
        {
            // A click on a marker isn't an edit: undo the waveform's "now a user cue" too.
            cue.Source = origin.Source;
            if (startedHere && _undoStack.Count > 0) { _undoStack.RemoveAt(_undoStack.Count - 1); UpdateUndoRedoState(); }
            return;
        }

        // Without a drag-start notification (Flow Builder's waveform), build the undo step from
        // the positions before the drag.
        if (!startedHere) PushSnapshot(fromBaseline: true);
        bool wasAuto = origin.Source == CueSource.Auto;
        cue.Source = CueSource.User;
        if (cue.IsLoop && cue.LoopEndSeconds < cue.Timestamp)
            (cue.Timestamp, cue.LoopEndSeconds) = (cue.LoopEndSeconds, cue.Timestamp);
        if (cue.IsLoop) cue.Name = FormatLoopLength(cue.Timestamp, cue.LoopEndSeconds);
        int cleared = ApplyCueMoved(cue, origin.Time, wasAuto);
        ResortCues();
        SelectedCue = cue;
        Changed($"Moved {cue.Name} to {CueForgeGrid.FormatTime(cue.Timestamp)}" + AutoClearedNote(cleared));
        if (Math.Abs(cue.Timestamp - origin.Time) > 1e-6) CueMoved?.Invoke(this, (origin.Time, cue.Timestamp));
        if (AuditionAfterEdits) _audition.PlayFrom(cue.Timestamp);
    }

    /// <summary>Every step replays the track from the cue so it can be fine-tuned by ear; a moved
    /// Drop takes its countdown cues along.</summary>
    private void NudgeCue(string step)
    {
        if (SelectedCue is not { } cue || TrackHash is null || Bpm <= 0) return;
        double beat = 60.0 / Bpm;
        double delta = step switch
        {
            "-1" or "-beat" => -beat, "1" or "+beat" => beat,
            "-bar" => -4 * beat, "+bar" => 4 * beat,
            "-fine" => -FineNudgeSeconds, "+fine" => FineNudgeSeconds,
            _ => 0,
        };
        if (delta == 0) return;
        PushSnapshot();
        double from = cue.Timestamp;
        double moved = Math.Clamp(cue.Timestamp + delta, 0, Duration);
        if (cue.IsLoop) cue.LoopEndSeconds = Math.Clamp(cue.LoopEndSeconds + (moved - from), 0, Duration);
        cue.Timestamp = moved;
        bool wasAuto = cue.Source == CueSource.Auto;
        cue.Source = CueSource.User;
        int cleared = ApplyCueMoved(cue, from, wasAuto);
        ResortCues();
        Changed($"Moved {cue.Name} to {CueForgeGrid.FormatTime(cue.Timestamp)}" + AutoClearedNote(cleared));
        CueMoved?.Invoke(this, (from, cue.Timestamp));
        _audition.PlayFrom(cue.Timestamp);
    }

    /// <summary>
    /// A drop at <paramref name="seconds"/>, snapped to the bar, with everything else automatic
    /// (numbering, build-in cues, pads, [OUT], auto cues removed) — see
    /// <see cref="DropCountdownCues.PlaceDrop"/>. One undo step.
    /// </summary>
    private void SetDropAt(double seconds, int? number)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        double bar = Bpm > 0 ? 240.0 / Bpm : 0;
        double t = bar > 0 ? GridOffset + Math.Round((seconds - GridOffset) / bar) * bar : seconds;
        t = Math.Clamp(t, 0, Duration);
        int autoBefore = Cues.Count(c => c.Source == CueSource.Auto);
        var list = DropCountdownCues.PlaceDrop(Cues, t, number, CountdownBars, Bpm, GridOffset, Duration, out var drop);
        int cleared = autoBefore - list.Count(c => c.Source == CueSource.Auto);
        ReplaceCues(list);
        SelectedCue = drop;
        Changed($"{drop.Name} at {CueForgeGrid.FormatTime(drop.Timestamp)}" + AutoClearedNote(cleared));
        // While listening, keep playing; otherwise (when the host plays by ear) let the drop be heard.
        if (AuditionAfterEdits && !_audition.IsPlaying) _audition.PlayFrom(drop.Timestamp);
    }

    /// <summary>A cue was dragged or nudged from <paramref name="from"/>. Moving an auto cue takes
    /// manual control (the other auto cues go); a drop takes its build-in cues and [OUT] along.
    /// Returns how many auto cues were removed.</summary>
    private int ApplyCueMoved(OrbitCue cue, double from, bool wasAuto)
    {
        int cleared = wasAuto ? ClearAutoCues(cue) : 0;
        if (cue.Role != CueRole.Drop) return cleared;
        double? outBefore = OutBefore(cue, from);
        var list = DropCountdownCues.Rebuild(Cues, cue, CountdownBars, Bpm, previousDropTime: from);
        ReplaceCues(DropCountdownCues.Refresh(list, CountdownBars, Bpm, GridOffset, Duration, outBefore));
        return cleared;
    }

    /// <summary>Taking manual control: once you place or move a cue yourself, the analysis' auto
    /// cues leave the track. Returns how many were removed.</summary>
    private int ClearAutoCues(OrbitCue keep)
    {
        int before = Cues.Count;
        var list = DropCountdownCues.WithoutAutoCues(Cues, keep);
        if (list.Count == before) return 0;
        ReplaceCues(list);
        return before - list.Count;
    }

    private static string AutoClearedNote(int cleared) =>
        cleared > 0 ? $" · {cleared} auto cue{(cleared == 1 ? "" : "s")} removed; your cues take over. Ctrl+Z to undo." : "";

    /// <summary>[OUT] position for the drops as they were before an edit (to move it along).</summary>
    private double? OutBefore(OrbitCue? moved = null, double movedFrom = 0)
    {
        var drops = Cues.Where(c => c.Role == CueRole.Drop && !c.IsLoop)
            .Select(c => c == moved ? movedFrom : c.Timestamp).ToList();
        return drops.Count == 0 ? null : DropCountdownCues.OutTime(drops.Max(), Bpm, GridOffset, Duration);
    }

    private void SetSelectedCueRole(CueRole role)
    {
        if (SelectedCue is not { } cue || TrackHash is null) return;
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
        if (previousRole == CueRole.Drop)
        {
            // A drop that stopped being a drop leaves no orphaned countdowns behind.
            var left = DropCountdownCues.RemoveFor(Cues, cue.Timestamp, Bpm);
            ReplaceCues(DropCountdownCues.Refresh(left, CountdownBars, Bpm, GridOffset, Duration, outBefore));
        }
        FieldEdited(cue, $"{cue.Name} is now {role}");
    }

    private void SetSelectedCueColor(string hexColor)
    {
        if (SelectedCue is not { } cue || TrackHash is null || string.IsNullOrWhiteSpace(hexColor)) return;
        if (string.Equals(cue.Color, hexColor, StringComparison.OrdinalIgnoreCase)) return;
        PushSnapshot();
        cue.Color = hexColor;
        FieldEdited(cue, $"Recoloured {cue.Name}");
    }

    private void SetSelectedCueSlot(int slotIndex)
    {
        if (SelectedCue is not { } cue || TrackHash is null || cue.IsLoop) return;
        slotIndex = Math.Clamp(slotIndex, -1, 7);
        if (cue.SlotIndex == slotIndex) return;
        PushSnapshot();
        // A hot cue pad holds one cue: whoever had it becomes a memory cue.
        if (slotIndex >= 0)
        {
            var priorHolder = Cues.FirstOrDefault(c => !c.IsLoop && c.SlotIndex == slotIndex && c != cue);
            if (priorHolder is not null) priorHolder.SlotIndex = -1;
        }
        cue.SlotIndex = slotIndex;
        FieldEdited(cue, slotIndex >= 0 ? $"{cue.Name} is on pad {(char)('A' + slotIndex)}" : $"{cue.Name} is a memory cue");
    }

    private void TriggerPad(HotCuePadInfo? pad)
    {
        if (pad == null || TrackHash is null) return;
        if (pad.Cue is { } cue && Cues.Contains(cue))
        {
            SelectedCue = cue;
            _audition.JumpToCue(cue);
            return;
        }
        AddCue(SnapTime(_audition.PositionSeconds), pad.Slot);
    }

    private void ClearPad(HotCuePadInfo? pad)
    {
        if (pad?.Cue is not { } cue || TrackHash is null) return;
        PushSnapshot();
        cue.SlotIndex = -1;
        FieldEdited(cue, $"Pad {pad.PadLabel} cleared; \"{cue.Name}\" is now a memory cue.");
    }

    // ── Loop editing ───────────────────────────────────────────────────────

    private void SetLoopIn()
    {
        if (TrackHash is null) return;
        PendingLoopIn = SnapTime(_audition.PositionSeconds);
    }

    private void SetLoopOut()
    {
        if (TrackHash is null) return;
        double outAt = SnapTime(_audition.PositionSeconds);
        if (PendingLoopIn is { } inAt)
        {
            PendingLoopIn = null;
            if (outAt <= inAt) outAt = Math.Min(Duration, inAt + BeatSeconds);
            CreateLoop(inAt, outAt);
            return;
        }
        // No IN pending: move the active loop's end here, or make a one-bar loop ending here.
        if (ActiveLoop is { } loop && outAt > loop.Timestamp)
        {
            PushSnapshot();
            loop.LoopEndSeconds = outAt;
            loop.Name = FormatLoopLength(loop.Timestamp, outAt);
            FieldEdited(loop, $"Loop {loop.Name}");
            return;
        }
        CreateLoop(Math.Max(0, outAt - BarSeconds), outAt);
    }

    private OrbitCue CreateLoop(double inAt, double outAt)
    {
        PushSnapshot();
        var loop = new OrbitCue
        {
            Timestamp = inAt,
            LoopEndSeconds = Math.Min(outAt, Duration),
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
        Changed($"Loop {loop.Name} at {CueForgeGrid.FormatTime(inAt)}");
        return loop;
    }

    private void ScaleLoop(double factor)
    {
        var loop = ActiveLoop;
        if (loop is null || TrackHash is null) return;
        double len = (loop.LoopEndSeconds - loop.Timestamp) * factor;
        if (len < BeatSeconds / 4) return;
        PushSnapshot();
        loop.LoopEndSeconds = Math.Min(loop.Timestamp + len, Duration);
        loop.Name = FormatLoopLength(loop.Timestamp, loop.LoopEndSeconds);
        FieldEdited(loop, $"Loop {loop.Name}");
    }

    private void ClearLoop()
    {
        var loop = ActiveLoop;
        if (loop is null) return;
        DeleteCue(loop);
    }

    private void SetQuickLoop(int bars)
    {
        if (TrackHash is null || bars <= 0) return;
        double length = BarSeconds * bars;
        if (SelectedCue is { IsLoop: true } loop)
        {
            PushSnapshot();
            loop.LoopEndSeconds = Math.Min(loop.Timestamp + length, Duration);
            loop.Name = FormatLoopLength(loop.Timestamp, loop.LoopEndSeconds);
            FieldEdited(loop, $"Loop {loop.Name}");
            return;
        }
        double start = SelectedCue is { IsLoop: false } cue ? cue.Timestamp : SnapTime(_audition.PositionSeconds);
        CreateLoop(start, Math.Min(start + length, Duration));
    }

    private string FormatLoopLength(double inSec, double outSec)
    {
        double len = outSec - inSec;
        if (Bpm <= 0) return $"{len.ToString("F1", CultureInfo.InvariantCulture)} s";
        double beats = len / BeatSeconds;
        double bars = beats / 4;
        if (bars >= 1 && Math.Abs(bars - Math.Round(bars)) < 0.05) return $"{Math.Round(bars):F0} bar{(Math.Round(bars) == 1 ? "" : "s")}";
        if (beats >= 0.9 && Math.Abs(beats - Math.Round(beats)) < 0.05) return $"{Math.Round(beats):F0} beat{(Math.Round(beats) == 1 ? "" : "s")}";
        return beats < 1 ? $"1/{Math.Round(1 / beats):F0} beat" : $"{len.ToString("F1", CultureInfo.InvariantCulture)} s";
    }

    /// <summary>Replaces the whole working set as one undoable edit (e.g. a host's auto-generate).</summary>
    public void ReplaceAllCues(IEnumerable<OrbitCue> cues, string status)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        ReplaceCues(cues.OrderBy(c => c.Timestamp).ToList());
        Changed(status);
    }

    /// <summary>Ends an in-place field edit: a user cue now, dirty, pads/inspector refreshed.</summary>
    private void FieldEdited(OrbitCue cue, string status)
    {
        cue.Source = CueSource.User;
        Changed(status);
    }

    private void Changed(string status)
    {
        IsDirty = true;
        RefreshHotCuePads();
        RaiseSelectionDerived();
        RefreshBaseline();
        SetStatus(status);
    }

    // ── Undo / redo ────────────────────────────────────────────────────────

    private void PushSnapshot(bool fromBaseline = false)
    {
        _redoStack.Clear();
        if (_undoStack.Count >= MaxUndoHistory) _undoStack.RemoveAt(0);
        _undoStack.Add(fromBaseline ? TakeBaselineSnapshot() : TakeSnapshot());
        UpdateUndoRedoState();
    }

    private OrbitCue[] TakeSnapshot() => Cues.Select(c => c.Clone()).ToArray();

    private OrbitCue[] TakeBaselineSnapshot() => Cues.Select(c =>
    {
        var copy = c.Clone();
        if (_baseline.TryGetValue(c, out var b)) { copy.Timestamp = b.Time; copy.LoopEndSeconds = b.LoopEnd; copy.Source = b.Source; }
        return copy;
    }).OrderBy(c => c.Timestamp).ToArray();

    private void RestoreSnapshot(OrbitCue[] snapshot)
    {
        var selected = SelectedCue;
        var restored = snapshot.Select(c => c.Clone()).ToList();
        ReplaceCues(restored);
        if (selected != null)
            SelectedCue = restored.FirstOrDefault(c => c.IsLoop == selected.IsLoop &&
                                                       Math.Abs(c.Timestamp - selected.Timestamp) < 0.001 &&
                                                       c.Name == selected.Name);
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(TakeSnapshot());
        var s = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(s);
        UpdateUndoRedoState();
        Changed("Undone.");
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(TakeSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        UpdateUndoRedoState();
        Changed("Redone.");
    }

    private void UpdateUndoRedoState() { CanUndo = _undoStack.Count > 0; CanRedo = _redoStack.Count > 0; }

    // ── Persistence ────────────────────────────────────────────────────────

    /// <summary>Writes the working set (replacing all of the track's cue rows). On failure the
    /// working set and the dirty flag are kept, the error is shown, and false is returned.</summary>
    public async Task<bool> SaveAsync()
    {
        if (TrackHash is not { } hash) return false;
        var snapshot = Cues.ToList();
        _logger.LogInformation("Cue editor ({Editor}): saving {Count} cues for {Hash}", EditorName, snapshot.Count, hash);
        try
        {
            var entities = snapshot.Select(c => c.ToEntity(hash)).ToList();
            await _cueService.DeleteAllByTrackIdAsync(hash);
            if (entities.Count > 0)
                await _cueService.CreateManyAsync(entities);
        }
        catch (Exception ex)
        {
            // The working set is untouched, so nothing is lost; the user can retry.
            _logger.LogError(ex, "Cue editor ({Editor}): save failed for {Hash}", EditorName, hash);
            SetStatus("Save failed; your cues were NOT written. See the log, then try again.", isError: true);
            return false;
        }

        if (TrackHash == hash) IsDirty = false;
        HasExternalChanges = false;
        int hotCount = snapshot.Count(c => !c.IsLoop && c.SlotIndex >= 0);
        int memCount = snapshot.Count(c => !c.IsLoop && c.SlotIndex < 0);
        int loopCount = snapshot.Count(c => c.IsLoop);
        SetStatus($"Saved {hotCount} hot cue{(hotCount != 1 ? "s" : "")}" +
                  (memCount > 0 ? $", {memCount} memory" : "") +
                  (loopCount > 0 ? $", {loopCount} loop{(loopCount != 1 ? "s" : "")}" : ""));
        Saved?.Invoke(this, EventArgs.Empty);
        await _registry.NotifyStoreChangedAsync(this);
        return true;
    }

    /// <summary>Saves only when there are unsaved edits. True when something was written.</summary>
    public async Task<bool> SaveIfDirtyAsync() => IsDirty && await SaveAsync();

    /// <summary>Throws away unsaved edits and loads the stored cues. Undoable (Ctrl+Z brings the edits back).</summary>
    public async Task RevertAsync()
    {
        if (TrackHash is null) return;
        if (ConfirmRevert != null && !await ConfirmRevert()) return;
        await ReloadFromStoreAsync("Reverted to the saved cues. Ctrl+Z brings your edits back.", undoable: true);
    }

    /// <summary>Replaces the working set with the stored cues (after a host wrote them, e.g. cue
    /// generation). <paramref name="undoable"/> keeps the previous set one Ctrl+Z away; otherwise
    /// the history is cleared like a fresh load.</summary>
    public async Task<bool> ReloadFromStoreAsync(string status, bool undoable)
    {
        if (TrackHash is not { } hash) return false;
        try
        {
            var stored = await _cueService.GetByTrackIdAsync(hash);
            if (TrackHash != hash) return false;
            if (undoable) PushSnapshot();
            else { _undoStack.Clear(); _redoStack.Clear(); UpdateUndoRedoState(); }
            PendingLoopIn = null;
            LoadCuesClean(stored.Select(OrbitCue.FromEntity));
            HasExternalChanges = false;
            SetStatus(status);
            Reloaded?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cue editor ({Editor}): reload failed for {Hash}", EditorName, hash);
            SetStatus("Couldn't load the saved cues; your working cues were left as they are. See the log.", isError: true);
            return false;
        }
    }

    /// <summary>A host wrote this track's cues itself (e.g. cue generation): tell the other editors.</summary>
    public Task NotifyStoreChangedAsync() => _registry.NotifyStoreChangedAsync(this);

    internal async Task OnStoreChangedElsewhereAsync(CueEditorSession source)
    {
        if (_disposed || TrackHash is null) return;
        if (IsDirty)
        {
            HasExternalChanges = true;
            SetStatus($"{source.EditorName} just saved this track's cues. Save keeps your version (it replaces theirs); Revert loads theirs.", isError: true);
            return;
        }
        await ReloadFromStoreAsync($"Updated with the cues saved in {source.EditorName}.", undoable: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.Remove(this);
        Cues.CollectionChanged -= OnCuesCollectionChanged;
        foreach (var c in _trackedCues) c.PropertyChanged -= OnCuePropertyChanged;
        _trackedCues.Clear();
    }
}
