using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels;

/// <summary>How a placed or moved cue lands on the track's grid.</summary>
public enum CueSnapMode { Off, Beat, Bar, Phrase }

/// <summary>
/// Edits one track's cue points in place — the per-instance cue editor behind the Flow Builder
/// transition editor's decks (one for the outgoing track, one for the incoming track).
///
/// Unlike <see cref="CueForgeViewModel"/> (an app-wide singleton bound to the main player's current
/// track), each instance owns its own working copy and auditions through the library preview
/// player, so two tracks can be edited side by side without taking over playback. Persistence is
/// the same as Cue Forge's commit: replace all of the track's cue rows with the working set. Any
/// cue the user touches becomes a user cue, so auto-analysis never overwrites it.
/// </summary>
public sealed class TrackCueEditorViewModel : ReactiveObject
{
    private readonly ICuePointService _cueService;
    private readonly ILibraryPreviewPlayer? _previewPlayer;
    private readonly ILogger _logger;
    private readonly List<List<OrbitCue>> _undo = new();
    private readonly List<List<OrbitCue>> _redo = new();
    private List<OrbitCue> _cues = new();

    public TrackCueEditorViewModel(string deckLabel, ICuePointService cueService, ILibraryPreviewPlayer? previewPlayer, ILogger logger)
    {
        DeckLabel = deckLabel;
        _cueService = cueService;
        _previewPlayer = previewPlayer;
        _logger = logger;

        var hasTrack = this.WhenAnyValue(x => x.TrackHash).Select(h => !string.IsNullOrEmpty(h));
        var hasSelection = this.WhenAnyValue(x => x.SelectedCue).Select(c => c != null);

        SelectCueCommand = ReactiveCommand.Create<OrbitCue>(c => SelectedCue = c);
        AddCueAtCommand = ReactiveCommand.Create<double>(AddCueAt, hasTrack);
        AddCueAtAuditionCommand = ReactiveCommand.Create(() => AddCueAt(LastAuditionSeconds), hasTrack);
        DeleteSelectedCommand = ReactiveCommand.Create(DeleteSelected, hasSelection);
        CueUpdatedCommand = ReactiveCommand.Create<OrbitCue>(OnCueDragged);
        NudgeSelectedCommand = ReactiveCommand.Create<string>(NudgeSelected, hasSelection);
        SetSelectedColorCommand = ReactiveCommand.Create<string>(color => EditSelected(c => c.Color = color), hasSelection);
        SetSelectedLoopBarsCommand = ReactiveCommand.Create<int>(SetSelectedLoopBars, hasSelection);
        AuditionSelectedCommand = ReactiveCommand.Create(() => { if (SelectedCue != null) Audition(SelectedCue.Timestamp); }, hasSelection);
        UseSelectedAsTriggerCommand = ReactiveCommand.Create(() => { if (SelectedCue != null) TriggerRequested?.Invoke(this, SelectedCue.Timestamp); }, hasSelection);
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));
        SaveCommand = ReactiveCommand.CreateFromTask(async () => { if (await SaveAsync()) CuesChanged?.Invoke(this, EventArgs.Empty); }, this.WhenAnyValue(x => x.IsDirty));
        RevertCommand = ReactiveCommand.CreateFromTask(RevertAsync, this.WhenAnyValue(x => x.IsDirty));
        SetDropHereCommand = ReactiveCommand.Create(() => SetDropAt(ListenSeconds), hasTrack);
        SetDropAtCommand = ReactiveCommand.Create<double>(t => SetDropAt(t), hasTrack);
        SetNumberedDropHereCommand = ReactiveCommand.Create<int>(n => SetDropAt(ListenSeconds, n), hasTrack);
        var canGenerate = this.WhenAnyValue(x => x.TrackHash, x => x.IsGenerating, (h, g) => !string.IsNullOrEmpty(h) && !g);
        GenerateCuesCommand = ReactiveCommand.CreateFromTask<string>(GenerateCuesAsync, canGenerate);
    }

    // ── Generate / Drop (same flow as the library's "Regenerate Cues" and Cue Forge) ─────────

    /// <summary>Regenerates this track's auto cues and returns (ok, message). Set by the host; the
    /// same service as the library's right-click "Regenerate Cues" (ORBIT / + CUE-DETR / CUE-DETR only).</summary>
    public Func<string, string?, SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode, Task<(bool Ok, string Message)>>? CueGenerator { get; set; }

    /// <summary>Cues were regenerated or explicitly saved — the host re-plans the transition from them.</summary>
    public event EventHandler? CuesChanged;

    /// <summary>This deck started playing (a waveform click or a cue) — the host routes the drop keys here.</summary>
    public event EventHandler? Auditioned;

    public ReactiveCommand<Unit, Unit> SetDropHereCommand { get; }
    /// <summary>Right-click "Drop here" on the waveform (seconds).</summary>
    public ReactiveCommand<double, Unit> SetDropAtCommand { get; }
    /// <summary>Keys 1 / 2: make this Drop 1 or Drop 2 where you are listening.</summary>
    public ReactiveCommand<int, Unit> SetNumberedDropHereCommand { get; }
    /// <summary>Parameter "orbit", "compare" or "ai".</summary>
    public ReactiveCommand<string, Unit> GenerateCuesCommand { get; }

    private bool _isGenerating;
    public bool IsGenerating { get => _isGenerating; private set => this.RaiseAndSetIfChanged(ref _isGenerating, value); }

    private async Task GenerateCuesAsync(string? modeText)
    {
        if (TrackHash is not { } hash || CueGenerator == null) return;
        var mode = modeText switch
        {
            "compare" => SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.Compare,
            "ai" => SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.AiOnly,
            _ => SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.Orbit,
        };
        // Unsaved edits are saved first: they become your own cues, which generation never replaces.
        if (IsDirty && !await SaveAsync()) return;
        IsGenerating = true;
        StatusText = mode == SLSKDONET.Engine.Analysis.CueDetr.CueSourceMode.Orbit ? "Generating cues…" : "Generating cues with CUE-DETR (~10 s the first time)…";
        try
        {
            var (ok, message) = await CueGenerator(hash, _filePath, mode);
            if (ok && TrackHash == hash)
            {
                var stored = await _cueService.GetByTrackIdAsync(hash);
                PushUndo();
                SetCues(stored.Select(OrbitCue.FromEntity).ToList());
                SelectedCue = null;
                IsDirty = false;
                CuesChanged?.Invoke(this, EventArgs.Empty);
            }
            StatusText = message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cue generation failed for {Hash}", hash);
            StatusText = "✗ Couldn't generate cues";
        }
        finally { IsGenerating = false; }
    }

    /// <summary>
    /// The one-click drop (◆ Drop, right-click "Drop here", keys D / 1 / 2): puts a drop where you
    /// are listening — snapped to the bar — and lets <see cref="DropCountdownCues.PlaceDrop"/> do
    /// the rest: numbering by position (or <paramref name="number"/>), [IN -n] countdowns from the
    /// cue template, pads A–C / D–F, [OUT] on G after the last drop, and removing the analysis'
    /// auto cues. One undo step.
    /// </summary>
    private void SetDropAt(double seconds, int? number = null)
    {
        if (TrackHash is null) return;
        PushUndo();
        double t = SnapDrop(seconds);
        int autoBefore = _cues.Count(c => c.Source == CueSource.Auto);
        var list = DropCountdownCues.PlaceDrop(_cues, t, number, CountdownBars, _bpm, _downbeat, _duration, out var drop);
        int autoCleared = autoBefore - list.Count(c => c.Source == CueSource.Auto);
        SetCues(list);
        SelectedCue = drop;
        int countdowns = list.Count(c => DropCountdownCues.IsCountdownFor(c, drop.Timestamp, _bpm));
        MarkDirty($"{drop.Name} at {drop.TimestampDisplay}"
                  + (countdowns > 0 ? $" · {countdowns} build-in cue{(countdowns == 1 ? "" : "s")}" : "")
                  + (autoCleared > 0 ? $" · {autoCleared} auto cue{(autoCleared == 1 ? "" : "s")} removed (↶ undo)" : ""));
        // While listening, keep playing; otherwise let the drop be heard.
        if (_previewPlayer?.IsPreviewPlaying != true) Audition(drop.Timestamp);
    }

    /// <summary>Where you are listening on this deck: the preview's live position when it is
    /// playing this track, else the last audition point.</summary>
    public double ListenSeconds =>
        _previewPlayer is { IsPreviewPlaying: true } p && p.CurrentPreviewPath == _filePath && p.PositionSeconds is { } pos
            ? pos
            : LastAuditionSeconds;

    /// <summary>Drops land on a bar line (a phrase line with Phrase snapping; unsnapped only when snapping is Off).</summary>
    private double SnapDrop(double seconds)
    {
        if (SnapMode == CueSnapMode.Off || SnapMode == CueSnapMode.Phrase) return Snap(seconds);
        var mode = SnapMode;
        _snapMode = CueSnapMode.Bar;
        try { return Snap(seconds); } finally { _snapMode = mode; }
    }

    private IReadOnlyList<int> CountdownBars => DropCountdownCues.ResolveBars(DropCountdownMode, _genre, _bpm, CustomCountdownBars);

    /// <summary>[OUT] position for the drops as they were before an edit (to move it along).</summary>
    private double? OutBefore(OrbitCue? moved = null, double movedFrom = 0)
    {
        var drops = _cues.Where(c => c.Role == CueRole.Drop && !c.IsLoop)
            .Select(c => c == moved ? movedFrom : c.Timestamp).ToList();
        return drops.Count == 0 ? null : DropCountdownCues.OutTime(drops.Max(), _bpm, _downbeat, _duration);
    }

    /// <summary>Taking manual control: once you place or move a cue yourself, the analysis' auto
    /// cues leave the waveform (and the track, when saved). Returns how many were removed.</summary>
    private int ClearAutoCues(ref List<OrbitCue> list, OrbitCue keep)
    {
        int before = list.Count;
        list = DropCountdownCues.WithoutAutoCues(list, keep);
        return before - list.Count;
    }

    /// <summary>"Outgoing" / "Incoming" — used in status text.</summary>
    public string DeckLabel { get; }

    /// <summary>A cue was picked as this deck's transition trigger point (seconds).</summary>
    public event EventHandler<double>? TriggerRequested;

    /// <summary>A cue moved from one time to another — the host moves the trigger along with it
    /// when that cue was the trigger.</summary>
    public event EventHandler<(double From, double To)>? CueMoved;

    // ── Track ───────────────────────────────────────────────────────────────────────────────

    private string? _trackHash;
    public string? TrackHash { get => _trackHash; private set => this.RaiseAndSetIfChanged(ref _trackHash, value); }

    public string TrackTitle { get; private set; } = string.Empty;
    private string? _filePath;
    private string? _genre;
    private double _bpm;
    private double _downbeat;
    private double _duration;

    /// <summary>Where this deck was last auditioned — "Add cue at audition point" places a cue here.</summary>
    public double LastAuditionSeconds { get; set; }

    // ── Cues ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Time-ordered working set. Replaced (not mutated) on every change so the waveform
    /// and the chip list re-render — <see cref="OrbitCue"/> doesn't raise property changes.</summary>
    public IReadOnlyList<OrbitCue> Cues => _cues;

    private OrbitCue? _selectedCue;
    public OrbitCue? SelectedCue
    {
        get => _selectedCue;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCue, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectedName));
            this.RaisePropertyChanged(nameof(SelectedRole));
            this.RaisePropertyChanged(nameof(SelectedSlot));
            this.RaisePropertyChanged(nameof(SelectedLoopLabel));
        }
    }

    public bool HasSelection => SelectedCue != null;

    public string SelectedName
    {
        get => SelectedCue?.Name ?? string.Empty;
        set { if (SelectedCue != null && SelectedCue.Name != value) EditSelected(c => c.Name = value, refreshNameBinding: false); }
    }

    public static IReadOnlyList<CueRole> Roles { get; } = Enum.GetValues<CueRole>();

    public CueRole SelectedRole
    {
        get => SelectedCue?.Role ?? CueRole.Custom;
        set
        {
            if (SelectedCue == null || SelectedCue.Role == value) return;
            EditSelected(c => { c.Role = value; c.Color = CueForgeViewModel.RekordboxColorForRole(value); });
        }
    }

    public sealed record SlotOption(int Index, string Label);

    /// <summary>Hot cue A–H (pad 0–7) or a memory cue (no pad).</summary>
    public static IReadOnlyList<SlotOption> SlotOptions { get; } =
        new[] { new SlotOption(-1, "Memory") }
            .Concat(Enumerable.Range(0, 8).Select(i => new SlotOption(i, $"Hot {(char)('A' + i)}")))
            .ToList();

    public SlotOption? SelectedSlot
    {
        get => SelectedCue == null ? null : SlotOptions.FirstOrDefault(o => o.Index == SelectedCue.SlotIndex) ?? SlotOptions[0];
        set
        {
            if (SelectedCue == null || value == null || SelectedCue.SlotIndex == value.Index) return;
            EditSelected(c =>
            {
                // A pad holds one cue: whoever had this pad becomes a memory cue.
                if (value.Index >= 0)
                    foreach (var other in _cues.Where(o => o != c && o.SlotIndex == value.Index)) other.SlotIndex = -1;
                c.SlotIndex = value.Index;
            });
        }
    }

    public string SelectedLoopLabel =>
        SelectedCue is { IsLoop: true } c && _bpm > 0
            ? $"Loop {Math.Round((c.LoopEndSeconds - c.Timestamp) / (240.0 / _bpm))} bars"
            : "No loop";

    public static IReadOnlyList<string> CuePalette { get; } =
        new[] { "#FF0000", "#FF6600", "#FFFF00", "#00FF88", "#00CCFF", "#0044FF", "#8800FF", "#FF66AA", "#FFFFFF" };

    // ── Drop countdowns ─────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<string> DropCountdownModes => DropCountdownCues.Modes;

    private string _dropCountdownMode = DropCountdownCues.Auto;
    /// <summary>Countdown cues placed before a Drop whenever one is set, moved or renamed (see
    /// <see cref="DropCountdownCues"/>). Shared app setting — the host persists changes via
    /// <see cref="DropCountdownModeChanged"/>.</summary>
    public string DropCountdownMode
    {
        get => _dropCountdownMode;
        set
        {
            if (_dropCountdownMode == value) return;
            this.RaiseAndSetIfChanged(ref _dropCountdownMode, value);
            this.RaisePropertyChanged(nameof(IsCustomTemplate));
            this.RaisePropertyChanged(nameof(TemplateHint));
            DropCountdownModeChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<string>? DropCountdownModeChanged;

    /// <summary>Sets the template without raising <see cref="DropCountdownModeChanged"/> (loading it from config).</summary>
    public void InitDropCountdownMode(string mode, string? customBars = null)
    {
        (_dropCountdownMode, _customCountdownBars) = DropCountdownCues.Normalize(mode, customBars ?? _customCountdownBars);
        this.RaisePropertyChanged(nameof(DropCountdownMode));
        this.RaisePropertyChanged(nameof(CustomCountdownBars));
        this.RaisePropertyChanged(nameof(IsCustomTemplate));
        this.RaisePropertyChanged(nameof(TemplateHint));
    }

    private string _customCountdownBars = DropCountdownCues.DefaultCustomBars;
    /// <summary>Bars before the drop for the Custom template, e.g. "32,16,8".</summary>
    public string CustomCountdownBars
    {
        get => _customCountdownBars;
        set
        {
            if (_customCountdownBars == value) return;
            this.RaiseAndSetIfChanged(ref _customCountdownBars, value);
            this.RaisePropertyChanged(nameof(TemplateHint));
            CustomCountdownBarsChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<string>? CustomCountdownBarsChanged;

    public bool IsCustomTemplate => DropCountdownMode == DropCountdownCues.Custom;

    /// <summary>What the template does for this track, e.g. "Auto → DnB / Bass (−16 −8)".</summary>
    public string TemplateHint
    {
        get
        {
            var bars = CountdownBars;
            string what = bars.Count == 0 ? "no build-in cues" : string.Join(" ", bars.Select(b => $"−{b}")) + " bars";
            return DropCountdownMode == DropCountdownCues.Auto && TrackHash != null
                ? $"Auto → {DropCountdownCues.AutoTemplate(_genre, _bpm)}"
                : what;
        }
    }

    // ── Main-player hold ────────────────────────────────────────────────────────────────────

    /// <summary>Host hook: pause the main player if it's playing; return true if it was paused
    /// (so it can be resumed later). Auditioning a cue holds the main mix until the cues are saved.</summary>
    public Func<bool>? HoldMainPlayback { get; set; }
    /// <summary>Host hook: resume the main player after a hold.</summary>
    public Action? ResumeMainPlayback { get; set; }
    private bool _holdingMainPlayback;

    /// <summary>Stops this deck's audition and resumes the main player if the audition paused it.</summary>
    public void EndAudition()
    {
        _previewPlayer?.StopPreview();
        if (_holdingMainPlayback)
        {
            _holdingMainPlayback = false;
            ResumeMainPlayback?.Invoke();
        }
    }

    // ── Snapping ────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CueSnapMode> SnapModes { get; } = Enum.GetValues<CueSnapMode>();

    private CueSnapMode _snapMode = CueSnapMode.Bar;
    public CueSnapMode SnapMode { get => _snapMode; set => this.RaiseAndSetIfChanged(ref _snapMode, value); }

    /// <summary>Snaps to this track's grid (BPM + downbeat — the same grid auto cues use).</summary>
    public double Snap(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, _duration > 0 ? _duration : double.MaxValue);
        if (_bpm <= 0 || SnapMode == CueSnapMode.Off) return seconds;
        double beats = SnapMode switch { CueSnapMode.Beat => 1, CueSnapMode.Bar => 4, _ => 32 };
        double period = 60.0 / _bpm * beats;
        double snapped = _downbeat + Math.Round((seconds - _downbeat) / period) * period;
        return Math.Clamp(snapped, 0, _duration > 0 ? _duration : double.MaxValue);
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────

    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => this.RaiseAndSetIfChanged(ref _isDirty, value); }

    private bool _canUndo, _canRedo;
    public bool CanUndo { get => _canUndo; private set => this.RaiseAndSetIfChanged(ref _canUndo, value); }
    public bool CanRedo { get => _canRedo; private set => this.RaiseAndSetIfChanged(ref _canRedo, value); }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    // ── Commands ────────────────────────────────────────────────────────────────────────────

    public ReactiveCommand<OrbitCue, Unit> SelectCueCommand { get; }
    public ReactiveCommand<double, Unit> AddCueAtCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCueAtAuditionCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    /// <summary>Bound to the waveform's CueUpdatedCommand — fires after a cue drag ends.</summary>
    public ReactiveCommand<OrbitCue, Unit> CueUpdatedCommand { get; }
    /// <summary>"-bar", "-beat", "+beat", "+bar".</summary>
    public ReactiveCommand<string, Unit> NudgeSelectedCommand { get; }
    public ReactiveCommand<string, Unit> SetSelectedColorCommand { get; }
    /// <summary>0 clears the loop; otherwise loops that many bars from the cue.</summary>
    public ReactiveCommand<int, Unit> SetSelectedLoopBarsCommand { get; }
    public ReactiveCommand<Unit, Unit> AuditionSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> UseSelectedAsTriggerCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }

    // ── Loading / saving ────────────────────────────────────────────────────────────────────

    public void Load(string trackHash, string title, string? filePath, double bpm, double downbeat, double duration,
        IEnumerable<CuePointEntity> cues, string? genre = null)
    {
        TrackHash = trackHash;
        TrackTitle = title;
        _filePath = filePath;
        _genre = genre;
        _bpm = bpm;
        _downbeat = downbeat;
        _duration = duration;
        _undo.Clear();
        _redo.Clear();
        UpdateUndoState();
        SetCues(cues.Select(OrbitCue.FromEntity).ToList());
        SelectedCue = null;
        IsDirty = false;
        StatusText = string.Empty;
    }

    /// <summary>Saves the working set if it has unsaved edits. Returns true when something was written.</summary>
    public async Task<bool> SaveIfDirtyAsync() => IsDirty && await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (TrackHash is not { } hash) return false;
        try
        {
            await _cueService.DeleteAllByTrackIdAsync(hash);
            if (_cues.Count > 0)
                await _cueService.CreateManyAsync(_cues.Select(c => c.ToEntity(hash)).ToList());
            IsDirty = false;
            StatusText = $"✓ Saved {_cues.Count} cue{(_cues.Count == 1 ? "" : "s")}";
            EndAudition(); // cue is set — the held mix carries on
            return true;
        }
        catch (Exception ex)
        {
            // The working set is untouched, so nothing is lost — the next save retries.
            _logger.LogError(ex, "Cue save failed for {Hash}", hash);
            StatusText = "✗ Save failed — cues were not written";
            return false;
        }
    }

    private async Task RevertAsync()
    {
        if (TrackHash is not { } hash) return;
        var stored = await _cueService.GetByTrackIdAsync(hash);
        PushUndo();
        SetCues(stored.Select(OrbitCue.FromEntity).ToList());
        SelectedCue = null;
        IsDirty = false;
        StatusText = "Reverted to saved cues";
    }

    // ── Edits ───────────────────────────────────────────────────────────────────────────────

    private void AddCueAt(double seconds)
    {
        if (TrackHash is null) return;
        PushUndo();
        int freePad = Enumerable.Range(0, 8).FirstOrDefault(i => _cues.All(c => c.SlotIndex != i), -1);
        var cue = new OrbitCue
        {
            Timestamp = Snap(seconds),
            Name = $"Cue {_cues.Count(c => !c.IsLoop) + 1}",
            Role = CueRole.Custom,
            Color = "#FFFF00",
            Source = CueSource.User,
            SlotIndex = freePad,
            Confidence = 1.0,
        };
        var list = _cues.Append(cue).ToList();
        int cleared = ClearAutoCues(ref list, cue);
        SetCues(list);
        SelectedCue = cue;
        MarkDirty($"Added {cue.Name} at {cue.TimestampDisplay}" + (cleared > 0 ? $" · {cleared} auto cues removed (↶ undo)" : ""));
        Audition(cue.Timestamp);
    }

    private void DeleteSelected()
    {
        if (SelectedCue is not { } cue) return;
        PushUndo();
        var remaining = _cues.Where(c => c != cue).ToList();
        if (cue.Role == CueRole.Drop)
        {
            double? outBefore = OutBefore();
            remaining = DropCountdownCues.RemoveFor(remaining, cue.Timestamp, _bpm);
            remaining = DropCountdownCues.Refresh(remaining, CountdownBars, _bpm, _downbeat, _duration, outBefore);
        }
        SetCues(remaining);
        SelectedCue = null;
        MarkDirty($"Deleted {cue.Name}");
    }

    private void OnCueDragged(OrbitCue cue)
    {
        // The waveform has already moved the cue instance; snap it and record the edit. The undo
        // snapshot is taken from the pre-drag state held in the last published list's clones.
        double before = _dragOrigin.TryGetValue(cue, out var t) ? t : cue.Timestamp;
        bool wasAuto = cue.Source == CueSource.Auto;
        cue.Timestamp = Snap(cue.Timestamp);
        cue.Source = CueSource.User;
        if (Math.Abs(cue.Timestamp - before) < 1e-6) { SetCues(_cues); return; }

        _undo.Add(_cues.Select(c => c == cue ? WithTimestamp(c, before) : c.Clone()).ToList());
        _redo.Clear();
        UpdateUndoState();
        // Moving an auto cue = taking manual control: the other auto cues go.
        var afterDrag = _cues.ToList();
        if (wasAuto) ClearAutoCues(ref afterDrag, cue);
        if (cue.Role == CueRole.Drop)
        {
            double? outBefore = OutBefore(cue, before);
            afterDrag = DropCountdownCues.Rebuild(afterDrag, cue, CountdownBars, _bpm, previousDropTime: before);
            afterDrag = DropCountdownCues.Refresh(afterDrag, CountdownBars, _bpm, _downbeat, _duration, outBefore);
        }
        SetCues(afterDrag);
        SelectedCue = cue;
        MarkDirty($"Moved {cue.Name} to {cue.TimestampDisplay}");
        CueMoved?.Invoke(this, (before, cue.Timestamp));
        Audition(cue.Timestamp);
    }

    private static OrbitCue WithTimestamp(OrbitCue c, double t) { var copy = c.Clone(); copy.Timestamp = t; return copy; }

    private void NudgeSelected(string step)
    {
        if (SelectedCue is not { } cue || _bpm <= 0) return;
        double beat = 60.0 / _bpm;
        double delta = step switch
        {
            "-bar" => -4 * beat, "-beat" => -beat, "+beat" => beat, "+bar" => 4 * beat,
            "-fine" => -FineNudgeSeconds, "+fine" => FineNudgeSeconds,
            _ => 0,
        };
        if (delta == 0) return;
        double before = cue.Timestamp;
        EditSelected(c =>
        {
            c.Timestamp = Math.Clamp(c.Timestamp + delta, 0, _duration > 0 ? _duration : double.MaxValue);
            if (c.IsLoop) c.LoopEndSeconds += c.Timestamp - before;
        });
        CueMoved?.Invoke(this, (before, cue.Timestamp));
        // Every step restarts the preview from the cue, so fine-tuning is by ear.
        Audition(cue.Timestamp);
    }

    /// <summary>Fine nudge step (Ctrl+arrow): 10 ms, for lining a drop up exactly on the transient.</summary>
    public const double FineNudgeSeconds = 0.010;

    private void SetSelectedLoopBars(int bars)
    {
        if (SelectedCue == null) return;
        EditSelected(c =>
        {
            if (bars <= 0 || _bpm <= 0) { c.IsLoop = false; c.LoopEndSeconds = 0; }
            else { c.IsLoop = true; c.LoopEndSeconds = Math.Min(c.Timestamp + bars * 240.0 / _bpm, _duration > 0 ? _duration : double.MaxValue); }
        });
    }

    /// <summary>Applies an edit to the selected cue as one undoable step and marks it a user cue.</summary>
    private void EditSelected(Action<OrbitCue> edit, bool refreshNameBinding = true)
    {
        if (SelectedCue is not { } cue) return;
        PushUndo();
        var previousName = cue.Name;
        var previousRole = cue.Role;
        bool wasAuto = cue.Source == CueSource.Auto;
        double previousTime = cue.Timestamp;
        edit(cue);
        cue.Source = CueSource.User;
        var keep = cue;
        var cues = _cues.ToList();
        bool moved = Math.Abs(cue.Timestamp - previousTime) > 1e-6;
        if (previousRole == CueRole.Drop && cue.Role != CueRole.Drop)
        {
            // A drop that stopped being a drop leaves no orphaned countdowns behind.
            double? outBefore = OutBefore(cue, previousTime);
            cues = DropCountdownCues.RemoveFor(cues, previousTime, _bpm);
            cues = DropCountdownCues.Refresh(cues, CountdownBars, _bpm, _downbeat, _duration, outBefore);
        }
        else if (cue.Role == CueRole.Drop && previousRole != CueRole.Drop)
        {
            // Picking Drop as the role = placing a drop: same automation as ◆ Drop.
            cue.Role = previousRole;
            cues = DropCountdownCues.PlaceDrop(cues, cue.Timestamp, null, CountdownBars, _bpm, _downbeat, _duration, out keep);
        }
        else if (cue.Role == CueRole.Drop && moved)
        {
            double? outBefore = OutBefore(cue, previousTime);
            if (wasAuto) ClearAutoCues(ref cues, cue);
            cues = DropCountdownCues.Rebuild(cues, cue, CountdownBars, _bpm, previousDropTime: previousTime);
            cues = DropCountdownCues.Refresh(cues, CountdownBars, _bpm, _downbeat, _duration, outBefore);
        }
        else if (moved && wasAuto)
        {
            ClearAutoCues(ref cues, cue);
        }
        SetCues(cues);
        _selectedCue = keep;
        this.RaisePropertyChanged(nameof(SelectedCue));
        this.RaisePropertyChanged(nameof(SelectedRole));
        this.RaisePropertyChanged(nameof(SelectedSlot));
        this.RaisePropertyChanged(nameof(SelectedLoopLabel));
        if (refreshNameBinding) this.RaisePropertyChanged(nameof(SelectedName));
        MarkDirty($"Edited {cue.Name}");
    }


    /// <summary>Plays this deck from <paramref name="seconds"/> through the preview player, pausing
    /// the main player first if it's playing (resumed on save or <see cref="EndAudition"/>).</summary>
    public void Audition(double seconds)
    {
        if (string.IsNullOrEmpty(_filePath) || _previewPlayer == null) return;
        LastAuditionSeconds = seconds;
        Auditioned?.Invoke(this, EventArgs.Empty);
        if (!_holdingMainPlayback && HoldMainPlayback?.Invoke() == true) _holdingMainPlayback = true;
        // startSeconds 0 means "hover preview" (debounced) to the preview player — use a hair above.
        _previewPlayer.RequestPreview(_filePath, _bpm > 0 ? _bpm : null, Math.Max(seconds, 0.001));
        StatusText = $"▶ {TrackTitle} @ {TimeSpan.FromSeconds(seconds):m\\:ss\\.f}";
    }

    // ── Undo ────────────────────────────────────────────────────────────────────────────────

    private void PushUndo()
    {
        _undo.Add(_cues.Select(c => c.Clone()).ToList());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        UpdateUndoState();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_cues.Select(c => c.Clone()).ToList());
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        UpdateUndoState();
        SetCues(previous);
        SelectedCue = null;
        MarkDirty("Undo");
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_cues.Select(c => c.Clone()).ToList());
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        UpdateUndoState();
        SetCues(next);
        SelectedCue = null;
        MarkDirty("Redo");
    }

    private void UpdateUndoState()
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    // Pre-drag positions: the waveform mutates the cue instance while dragging, so the published
    // list remembers each cue's time at publish time to build a correct undo step.
    private Dictionary<OrbitCue, double> _dragOrigin = new();

    private void SetCues(List<OrbitCue> cues)
    {
        _cues = cues.OrderBy(c => c.Timestamp).ToList();
        _dragOrigin = _cues.ToDictionary(c => c, c => c.Timestamp);
        this.RaisePropertyChanged(nameof(Cues));
        this.RaisePropertyChanged(nameof(HasCues));
    }

    public bool HasCues => _cues.Count > 0;

    private void MarkDirty(string status)
    {
        IsDirty = true;
        StatusText = status;
    }
}
