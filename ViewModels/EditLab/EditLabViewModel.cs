using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models.EditLab;
using SLSKDONET.Services.EditLab;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.EditLab;

/// <summary>What the arranger control reads and drives. Implemented by <see cref="EditLabViewModel"/>.</summary>
public interface IEditArrangerHost
{
    EditProject? Project { get; }
    Guid? SelectedClipId { get; }
    double PlayheadBar { get; }
    /// <summary>Snap step in beats (0 = free).</summary>
    int SnapBeats { get; }
    double PixelsPerBar { get; set; }

    /// <summary>Clips, lanes or the loop changed: redraw everything.</summary>
    event Action? ArrangementChanged;
    event Action? PlayheadChanged;

    void SelectClip(Guid? clipId);
    void BeginClipGesture();
    void EndClipGesture();
    void CancelClipGesture();
    void MoveClipLive(Guid clipId, double startBar, int laneIndex);
    void TrimClipStartLive(Guid clipId, double startBar);
    void TrimClipEndLive(Guid clipId, double endBar);
    void SeekToBar(double bar);
    void SetLoopRegion(double startBar, double endBar);
    string ClipLabel(EditClip clip);
    ClipMatch MatchFor(EditClip clip);
    bool ClipCanPlay(EditClip clip);
}

/// <summary>
/// The Edit Lab: build edits from library tracks and their stems on a bar grid, hear them live and
/// render them as new library tracks. All edits go through <see cref="EditProjectEditor"/> (undo),
/// projects autosave through <see cref="EditProjectStore"/>, audio comes from
/// <see cref="EditPreviewEngine"/> (live) and <see cref="EditOfflineRenderer"/> (render).
/// </summary>
public sealed partial class EditLabViewModel : ReactiveObject, IEditArrangerHost, IDisposable
{
    private readonly EditProjectStore _store;
    private readonly EditLabSourceService _sources;
    private readonly EditPreviewEngine _preview;
    private readonly IEditLabPlayerGate? _playerGate;
    private readonly INotificationService? _notifications;
    private readonly ILogger<EditLabViewModel>? _logger;
    private readonly Func<Action, Task> _ui;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Dictionary<string, CancellationTokenSource> _separations = new();
    private readonly DispatcherTimer? _playheadTimer;
    private CancellationTokenSource? _autosaveCts;
    private CancellationTokenSource? _replanCts;
    private CancellationTokenSource? _searchCts;
    private EditRenderPlan? _livePlan;
    private bool _hasProject;
    private bool _pausedSharedPlayer;
    private bool _disposed;

    public EditProjectEditor Editor { get; } = new();

    public EditLabViewModel(
        EditProjectStore store,
        EditLabSourceService sources,
        EditPreviewEngine preview,
        IEditLabPlayerGate? playerGate = null,
        INotificationService? notifications = null,
        ILogger<EditLabViewModel>? logger = null,
        Func<Action, Task>? ui = null)
    {
        _store = store;
        _sources = sources;
        _preview = preview;
        _playerGate = playerGate;
        _notifications = notifications;
        _logger = logger;
        _ui = ui ?? (a => Dispatcher.UIThread.InvokeAsync(a).GetTask());

        Editor.Changed += OnEditorChanged;
        _preview.Ended += OnPreviewEnded;
        _preview.Error += OnPreviewError;

        if (ui == null)
        {
            _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _playheadTimer.Tick += (_, _) => UpdatePlayheadFromEngine();
        }

        if (_playerGate != null)
        {
            _subscriptions.Add(_playerGate.IsPlayingChanged.Skip(1).Where(p => p).Subscribe(started =>
                _ = _ui(() =>
                {
                    if (!IsPlaying) return;
                    _pausedSharedPlayer = false; // the user took the player back on purpose
                    StopPreview();
                    Status = "Preview stopped: the main player started.";
                })));
        }

        var hasProject = this.WhenAnyValue(x => x.HasProject);
        var hasSelection = this.WhenAnyValue(x => x.SelectedClip).Select(c => c != null);

        NewProjectCommand = ReactiveCommand.CreateFromTask(() => Guard(NewProjectAsync));
        OpenProjectCommand = ReactiveCommand.CreateFromTask<EditProjectSummary>(s => Guard(() => OpenProjectAsync(s.Id)));
        DeleteProjectCommand = ReactiveCommand.CreateFromTask<EditProjectSummary>(s => Guard(() => DeleteProjectAsync(s)));
        CloseProjectCommand = ReactiveCommand.CreateFromTask(() => Guard(CloseProjectAsync), hasProject);
        AddTrackCommand = ReactiveCommand.CreateFromTask<EditLibraryHit>(hit => Guard(() => AddTrackAsync(hit.Hash, hit.Title, hit.Artist, null)), hasProject);
        AddLaneCommand = ReactiveCommand.Create(() => { Editor.AddLane(); }, hasProject);
        UndoCommand = ReactiveCommand.Create(() => { Editor.Undo(); }, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(() => { Editor.Redo(); }, this.WhenAnyValue(x => x.CanRedo));
        DeleteClipCommand = ReactiveCommand.Create(DeleteSelectedClip, hasSelection);
        DuplicateClipCommand = ReactiveCommand.Create(DuplicateSelectedClip, hasSelection);
        SplitClipCommand = ReactiveCommand.Create(SplitSelectedClipAtPlayhead, hasSelection);
        PlayPauseCommand = ReactiveCommand.CreateFromTask(() => Guard(TogglePlayAsync), hasProject);
        StopCommand = ReactiveCommand.Create(StopAndRewind, hasProject);
        LoopToSelectionCommand = ReactiveCommand.Create(LoopToSelection, hasSelection);
        ClearLoopCommand = ReactiveCommand.Create(() => Editor.SetLoop(null, null), this.WhenAnyValue(x => x.HasLoop));
        ZoomInCommand = ReactiveCommand.Create(() => { PixelsPerBar = Math.Min(MaxPixelsPerBar, PixelsPerBar * 1.5); });
        ZoomOutCommand = ReactiveCommand.Create(() => { PixelsPerBar = Math.Max(MinPixelsPerBar, PixelsPerBar / 1.5); });

        InitializeTemplates();
        InitializeRender();

        _ = Guard(RefreshRecentAsync);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> NewProjectCommand { get; }
    public ReactiveCommand<EditProjectSummary, Unit> OpenProjectCommand { get; }
    public ReactiveCommand<EditProjectSummary, Unit> DeleteProjectCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseProjectCommand { get; }
    public ReactiveCommand<EditLibraryHit, Unit> AddTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> AddLaneCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteClipCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateClipCommand { get; }
    public ReactiveCommand<Unit, Unit> SplitClipCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> LoopToSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearLoopCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }

    // ── Project state ───────────────────────────────────────────────────────

    public EditProject? Project => HasProject ? Editor.Project : null;

    public bool HasProject
    {
        get => _hasProject;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasProject, value);
            this.RaisePropertyChanged(nameof(HasNoProject));
        }
    }

    public bool HasNoProject => !HasProject;

    public string ProjectName
    {
        get => HasProject ? Editor.Project.Name : "Edit Lab";
        set { if (HasProject && !string.IsNullOrWhiteSpace(value) && value != Editor.Project.Name) Editor.Rename(value); }
    }

    public string BaseDescription => Editor.Project.BaseSource is { } b
        ? $"Base: {b.DisplayName}"
        : "Add a track to set the tempo and key";

    public string BpmText
    {
        get => Editor.Project.Bpm.ToString("0.0#", CultureInfo.InvariantCulture);
        set
        {
            if (!HasProject) return;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) && bpm is >= 40 and <= 250)
            {
                if (Math.Abs(bpm - Editor.Project.Bpm) > 1e-6) Editor.SetTempo(bpm);
            }
            else
            {
                Status = "Tempo must be a number between 40 and 250 BPM.";
                this.RaisePropertyChanged();
            }
        }
    }

    public string KeyText
    {
        get => Editor.Project.Key ?? "—";
        set
        {
            if (!HasProject) return;
            var key = EditKeyMath.Normalize(value);
            if (key == null && !string.IsNullOrWhiteSpace(value) && value != "—")
            {
                Status = "Key must be Camelot (8A) or standard (Am, F#).";
                this.RaisePropertyChanged();
                return;
            }
            if (key != Editor.Project.Key) Editor.SetKey(key);
        }
    }

    public bool AutoMatchTempo
    {
        get => Editor.Project.AutoMatchTempo;
        set { if (HasProject && value != Editor.Project.AutoMatchTempo) Editor.SetAutoMatch(value, null); }
    }

    public bool AutoMatchKey
    {
        get => Editor.Project.AutoMatchKey;
        set { if (HasProject && value != Editor.Project.AutoMatchKey) Editor.SetAutoMatch(null, value); }
    }

    public bool CanUndo => Editor.CanUndo;
    public bool CanRedo => Editor.CanRedo;

    public ObservableCollection<EditSourceItemViewModel> Sources { get; } = new();
    public ObservableCollection<EditLaneItemViewModel> Lanes { get; } = new();
    public ObservableCollection<EditProjectSummary> RecentProjects { get; } = new();

    public bool HasSources => Sources.Count > 0;
    public bool HasNoSources => HasProject && Sources.Count == 0;
    public bool HasClips => Editor.Project.AllClips.Any();
    public bool ShowArrangerHint => HasProject && !HasClips;
    public bool HasRecentProjects => RecentProjects.Count > 0;
    public string ArrangerHint => HasSources
        ? "Open a track's … menu in the bin and add a clip (full mix or a stem), or apply a template."
        : "Add tracks to the bin first: search the library on the left.";

    private EditClipInspectorViewModel? _selectedClip;
    public EditClipInspectorViewModel? SelectedClip
    {
        get => _selectedClip;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedClip, value);
            this.RaisePropertyChanged(nameof(HasSelectedClip));
            this.RaisePropertyChanged(nameof(HasNoSelectedClip));
        }
    }
    public bool HasSelectedClip => SelectedClip != null;
    public bool HasNoSelectedClip => HasProject && SelectedClip == null;

    private string _status = "";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _problems = "";
    /// <summary>Why some clips can't play yet (stems not separated, missing files).</summary>
    public string Problems { get => _problems; private set { this.RaiseAndSetIfChanged(ref _problems, value); this.RaisePropertyChanged(nameof(HasProblems)); } }
    public bool HasProblems => !string.IsNullOrEmpty(Problems);

    public bool CanSeparateStems => _sources.CanSeparate;
    public string SeparatorUnavailableReason => _sources.SeparatorUnavailableReason;

    // ── Library picker ──────────────────────────────────────────────────────

    private string _pickerQuery = "";
    public string PickerQuery
    {
        get => _pickerQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _pickerQuery, value);
            _ = SearchLibraryAsync(value);
        }
    }

    public ObservableCollection<EditLibraryHit> PickerResults { get; } = new();

    private string _pickerStatus = "Type at least two letters of a title or artist.";
    public string PickerStatus { get => _pickerStatus; private set { this.RaiseAndSetIfChanged(ref _pickerStatus, value); this.RaisePropertyChanged(nameof(HasPickerStatus)); } }
    public bool HasPickerStatus => !string.IsNullOrEmpty(PickerStatus);

    private async Task SearchLibraryAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            {
                PickerResults.Clear();
                PickerStatus = "Type at least two letters of a title or artist.";
                return;
            }
            var hits = await _sources.SearchAsync(query);
            if (cts.IsCancellationRequested) return;
            await _ui(() =>
            {
                PickerResults.Clear();
                foreach (var h in hits) PickerResults.Add(h);
                PickerStatus = hits.Count == 0 ? "No downloaded tracks match." : "";
            });
        }
        catch (OperationCanceledException)
        {
            // intentional: a newer keystroke replaced this search
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Edit Lab: library search failed");
            PickerStatus = $"Search failed: {ex.Message}";
        }
    }

    // ── Project lifecycle ───────────────────────────────────────────────────

    private async Task NewProjectAsync()
    {
        StopPreview();
        await LoadProjectAsync(new EditProject { Name = $"Edit {DateTime.Now:yyyy-MM-dd HH:mm}" });
        await SaveNowAsync();
        Status = "New edit. Add tracks from the library to get started.";
    }

    /// <summary>"Open in Edit Lab" from the Library: a new project with the track as its base.</summary>
    public async Task OpenWithTrackAsync(string trackHash, string? title, string? artist, string? filePath)
    {
        StopPreview();
        var project = new EditProject { Name = $"{(string.IsNullOrWhiteSpace(title) ? "Track" : title)} edit" };
        await LoadProjectAsync(project);
        await AddTrackAsync(trackHash, title, artist, filePath);
        await SaveNowAsync();
    }

    public async Task OpenProjectAsync(Guid id)
    {
        StopPreview();
        var project = await _store.LoadAsync(id) ?? throw new InvalidOperationException("That project file no longer exists.");
        await LoadProjectAsync(project);
        // Stem caches can be evicted between sessions: re-check what is still on disk.
        foreach (var source in project.Sources.ToList())
        {
            await _sources.RefreshStemsAsync(source);
        }
        await _ui(() => { RebuildFromModel(); RefreshStemStates(); });
        Status = $"Opened \"{project.Name}\".";
    }

    private async Task LoadProjectAsync(EditProject project)
    {
        await _ui(() =>
        {
            Editor.Load(project);
            HasProject = true;
            Sources.Clear();
            Lanes.Clear();
            SelectedClip = null;
            PlayheadBar = 0;
            RebuildFromModel();
        });
    }

    private async Task CloseProjectAsync()
    {
        StopPreview();
        if (HasProject) await SaveNowAsync();
        await _ui(() =>
        {
            HasProject = false;
            Sources.Clear();
            Lanes.Clear();
            SelectedClip = null;
            Editor.Load(new EditProject());
            RaiseProjectProperties();
        });
        await RefreshRecentAsync();
    }

    private async Task DeleteProjectAsync(EditProjectSummary summary)
    {
        if (HasProject && Editor.Project.Id == summary.Id) await CloseProjectAsync();
        await _store.DeleteAsync(summary.Id);
        await RefreshRecentAsync();
        Status = $"Deleted \"{summary.Name}\".";
    }

    public async Task RefreshRecentAsync()
    {
        var recent = await _store.ListRecentAsync(12);
        await _ui(() =>
        {
            RecentProjects.Clear();
            foreach (var r in recent) RecentProjects.Add(r);
            this.RaisePropertyChanged(nameof(HasRecentProjects));
        });
    }

    public async Task AddTrackAsync(string hash, string? title, string? artist, string? filePath)
    {
        if (!HasProject) await LoadProjectAsync(new EditProject { Name = $"{title ?? "Track"} edit" });
        Status = $"Adding \"{title ?? hash}\"…";
        var source = await _sources.LoadSourceAsync(hash, title, artist, filePath);
        await _ui(() =>
        {
            Editor.AddSource(source);
            RefreshStemStates();
            Status = source.Bpm > 0
                ? $"Added \"{source.Title}\"."
                : $"Added \"{source.Title}\" — it hasn't been analysed, so it can't be tempo-matched. Analyse it in the Library for BPM, key and downbeat.";
        });
    }

    // ── Editor → view models ────────────────────────────────────────────────

    public event Action? ArrangementChanged;
    public event Action? PlayheadChanged;

    private void OnEditorChanged()
    {
        RebuildFromModel();
        ArrangementChanged?.Invoke();
        if (!HasProject) return;
        ScheduleAutosave();
        if (IsPlaying) ScheduleReplan();
    }

    private void RebuildFromModel()
    {
        var p = Editor.Project;
        SyncCollection(Sources, p.Sources, s => s.TrackHash, (vm, s) => vm.Hash == s.TrackHash,
            s => new EditSourceItemViewModel(this, s), (vm, s) => vm.Update(s, s.TrackHash == p.BaseTrackHash));
        SyncCollection(Lanes, p.Lanes, l => l.Id.ToString(), (vm, l) => vm.Id == l.Id,
            l => new EditLaneItemViewModel(Editor, l), (vm, l) => vm.Update(l));

        if (SelectedClip != null)
        {
            var clip = p.FindClip(SelectedClip.Id);
            if (clip == null) SelectedClip = null;
            else SelectedClip.Update(clip);
        }

        Problems = HasProject ? string.Join("  ", EditRenderPlan.Compile(p, 44100).Problems) : "";
        RaiseProjectProperties();
    }

    private void RaiseProjectProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(Project), nameof(ProjectName), nameof(BaseDescription), nameof(BpmText), nameof(KeyText),
                     nameof(AutoMatchTempo), nameof(AutoMatchKey), nameof(CanUndo), nameof(CanRedo), nameof(HasSources),
                     nameof(HasNoSources), nameof(HasClips), nameof(ShowArrangerHint), nameof(ArrangerHint), nameof(HasLoop),
                     nameof(LoopDisplay), nameof(PositionDisplay), nameof(HasNoSelectedClip), nameof(LengthDisplay),
                 })
            this.RaisePropertyChanged(name);
        RaiseTemplateProperties();
        RaiseRenderProperties();
    }

    private static void SyncCollection<TVm, TModel>(ObservableCollection<TVm> target, IReadOnlyList<TModel> models,
        Func<TModel, string> key, Func<TVm, TModel, bool> same, Func<TModel, TVm> create, Action<TVm, TModel> update)
    {
        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            int found = -1;
            for (int j = i; j < target.Count; j++)
                if (same(target[j], model)) { found = j; break; }
            if (found < 0) target.Insert(i, create(model));
            else if (found != i) target.Move(found, i);
            update(target[i], model);
        }
        while (target.Count > models.Count) target.RemoveAt(target.Count - 1);
    }

    // ── Sources ─────────────────────────────────────────────────────────────

    private void RefreshStemStates()
    {
        foreach (var item in Sources)
        {
            if (_separations.ContainsKey(item.Hash)) continue;
            item.Stems = EditLabSourceService.HasAllStems(item.Source) ? StemAvailability.Ready
                : _sources.CanSeparate ? StemAvailability.NotSeparated
                : StemAvailability.Unavailable;
        }
    }

    public async Task SeparateAsync(EditSourceItemViewModel item)
    {
        if (_separations.ContainsKey(item.Hash)) return;
        var cts = new CancellationTokenSource();
        _separations[item.Hash] = cts;
        item.Stems = StemAvailability.Separating;
        item.SeparationProgress = 0;
        Status = $"Separating \"{item.Title}\" into vocals, drums, bass and other…";
        try
        {
            var progress = new Progress<float>(p => item.SeparationProgress = p);
            var paths = await _sources.SeparateAsync(item.Source, progress, cts.Token);
            await _ui(() =>
            {
                _separations.Remove(item.Hash);
                Editor.SetStemPaths(item.Hash, paths);
                RefreshStemStates();
                Status = $"Stems ready for \"{item.Title}\".";
            });
            ScheduleAutosave();
        }
        catch (OperationCanceledException)
        {
            _separations.Remove(item.Hash);
            RefreshStemStates();
            Status = "Stem separation cancelled.";
        }
        catch (Exception ex)
        {
            _separations.Remove(item.Hash);
            RefreshStemStates();
            _logger?.LogError(ex, "Edit Lab: stem separation failed for {Hash}", item.Hash);
            Status = $"Stem separation failed: {ex.Message}";
            _notifications?.Show("Stem separation failed", ex.Message, NotificationType.Error);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void CancelSeparation(EditSourceItemViewModel item)
    {
        if (_separations.TryGetValue(item.Hash, out var cts)) cts.Cancel();
    }

    public void SetBase(EditSourceItemViewModel item) => Editor.SetBase(item.Hash);

    public void RemoveSource(EditSourceItemViewModel item)
    {
        CancelSeparation(item);
        Editor.RemoveSource(item.Hash);
    }

    public void AddClipFromSource(EditSourceItemViewModel item, string stemName)
    {
        if (!Enum.TryParse<EditStem>(stemName, out var stem)) return;
        var lane = SelectedClip != null ? Editor.Project.LaneOf(SelectedClip.Id) : null;
        double start = EditProjectEditor.Snap(PlayheadBar, 4);
        // Don't stack on top of the selected clip: start after the lane's last clip when the playhead is inside it.
        if (lane != null && lane.Clips.Any(c => c.StartBar <= start && c.EndBar > start))
            start = lane.Clips.Max(c => c.EndBar);
        var clip = Editor.AddClip(item.Hash, stem, start, lane?.Id);
        SelectClip(clip.Id);
        Status = $"Added {EditProjectEditor.StemLabel(stem).ToLowerInvariant()} of \"{item.Title}\" at bar {start + 1:0.##}.";
    }

    // ── Arranger host ───────────────────────────────────────────────────────

    public Guid? SelectedClipId => SelectedClip?.Id;

    private double _playheadBar;
    public double PlayheadBar
    {
        get => _playheadBar;
        private set
        {
            if (Math.Abs(_playheadBar - value) < 1e-9) return;
            _playheadBar = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(PositionDisplay));
            PlayheadChanged?.Invoke();
        }
    }

    public string PositionDisplay
    {
        get
        {
            double beats = PlayheadBar * Editor.Project.BeatsPerBar;
            int bar = (int)Math.Floor(PlayheadBar + 1e-9) + 1;
            int beat = (int)Math.Floor(beats + 1e-9) % Editor.Project.BeatsPerBar + 1;
            return $"{bar}.{beat}  {CueForgeGrid.FormatTime(EditMatching.BarToSeconds(Editor.Project, PlayheadBar))}";
        }
    }

    public string LengthDisplay => string.Create(CultureInfo.InvariantCulture,
        $"{Editor.Project.LengthBars:0.##} bars · {CueForgeGrid.FormatTime(EditMatching.BarToSeconds(Editor.Project, Editor.Project.LengthBars))}");

    public static IReadOnlyList<string> SnapOptions { get; } = new[] { "Beat", "Bar", "Off" };
    private string _snap = "Beat";
    public string Snap { get => _snap; set { this.RaiseAndSetIfChanged(ref _snap, value); this.RaisePropertyChanged(nameof(SnapBeats)); } }
    public int SnapBeats => Snap switch { "Bar" => Editor.Project.BeatsPerBar, "Off" => 0, _ => 1 };

    public const double MinPixelsPerBar = 6, MaxPixelsPerBar = 400;
    private double _pixelsPerBar = 48;
    public double PixelsPerBar
    {
        get => _pixelsPerBar;
        set { this.RaiseAndSetIfChanged(ref _pixelsPerBar, Math.Clamp(value, MinPixelsPerBar, MaxPixelsPerBar)); ArrangementChanged?.Invoke(); }
    }

    public void SelectClip(Guid? clipId)
    {
        var clip = clipId is { } id ? Editor.Project.FindClip(id) : null;
        if (clip == null) SelectedClip = null;
        else if (SelectedClip?.Id == clip.Id) SelectedClip.Update(clip);
        else SelectedClip = new EditClipInspectorViewModel(Editor, clip);
        ArrangementChanged?.Invoke();
    }

    public void BeginClipGesture() => Editor.BeginGesture();
    public void EndClipGesture() => Editor.EndGesture();
    public void CancelClipGesture() => Editor.CancelGesture();
    public void MoveClipLive(Guid clipId, double startBar, int laneIndex) => Editor.MoveClip(clipId, startBar, laneIndex);
    public void TrimClipStartLive(Guid clipId, double startBar) => Editor.TrimStart(clipId, startBar);
    public void TrimClipEndLive(Guid clipId, double endBar) => Editor.TrimEnd(clipId, endBar);

    public void SeekToBar(double bar)
    {
        PlayheadBar = Math.Max(0, bar);
        if (IsPlaying && _livePlan != null) _preview.Play(_livePlan, _livePlan.BarToFrame(PlayheadBar), LoopFrames(_livePlan));
    }

    public void SetLoopRegion(double startBar, double endBar) => Editor.SetLoop(Math.Min(startBar, endBar), Math.Max(startBar, endBar));

    public string ClipLabel(EditClip clip)
    {
        var source = Editor.Project.FindSource(clip.SourceHash);
        var name = source == null ? "Missing" : EditProjectEditor.ShortName(source);
        return clip.Stem == EditStem.FullMix ? name : $"{name} · {EditProjectEditor.StemLabel(clip.Stem).ToLowerInvariant()}";
    }

    public ClipMatch MatchFor(EditClip clip) => EditMatching.Resolve(Editor.Project, clip, Editor.Project.FindSource(clip.SourceHash));

    public bool ClipCanPlay(EditClip clip)
    {
        var source = Editor.Project.FindSource(clip.SourceHash);
        return source != null && EditRenderPlan.FilesFor(clip.Stem).All(s => EditRenderPlan.DefaultAudioPath(source, s) != null);
    }

    private void DeleteSelectedClip()
    {
        if (SelectedClip is not { } sel) return;
        Editor.DeleteClip(sel.Id);
        SelectedClip = null;
    }

    private void DuplicateSelectedClip()
    {
        if (SelectedClip is not { } sel) return;
        if (Editor.DuplicateClip(sel.Id) is { } copy) SelectClip(copy.Id);
    }

    private void SplitSelectedClipAtPlayhead()
    {
        if (SelectedClip is not { } sel) return;
        if (Editor.SplitClip(sel.Id, PlayheadBar) is { } right) SelectClip(right.Id);
        else Status = "Move the playhead inside the selected clip to split it there.";
    }

    // ── Loop ────────────────────────────────────────────────────────────────

    public bool HasLoop => Editor.Project.HasLoopRegion;

    public string LoopDisplay => Editor.Project is { HasLoopRegion: true } p
        ? string.Create(CultureInfo.InvariantCulture, $"Loop {p.LoopStartBar + 1:0.##}–{p.LoopEndBar + 1:0.##}")
        : "No loop";

    private void LoopToSelection()
    {
        if (SelectedClip is not { } sel || Editor.Project.FindClip(sel.Id) is not { } clip) return;
        Editor.SetLoop(clip.StartBar, clip.EndBar);
    }

    private (long, long)? LoopFrames(EditRenderPlan plan) => Editor.Project is { HasLoopRegion: true } p
        ? (plan.BarToFrame(p.LoopStartBar!.Value), plan.BarToFrame(p.LoopEndBar!.Value))
        : null;

    // ── Preview transport ───────────────────────────────────────────────────

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => this.RaiseAndSetIfChanged(ref _isPlaying, value); }

    private bool _isPreparing;
    public bool IsPreparing { get => _isPreparing; private set => this.RaiseAndSetIfChanged(ref _isPreparing, value); }

    private async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            StopPreview();
            return;
        }
        if (!HasClips)
        {
            Status = "Nothing to play yet: add a clip first.";
            return;
        }

        IsPreparing = true;
        try
        {
            Status = "Preparing audio…";
            var plan = await _sources.CompilePlanAsync(Editor.Project);
            if (plan.Clips.Count == 0)
            {
                Status = plan.Problems.Count > 0 ? string.Join(" ", plan.Problems) : "No audible clips (all muted?).";
                return;
            }
            if (_playerGate?.IsPlaying == true)
            {
                _playerGate.Pause();
                _pausedSharedPlayer = true;
            }
            _livePlan = plan;
            double startBar = PlayheadBar >= Editor.Project.LengthBars ? 0 : PlayheadBar;
            if (Editor.Project is { HasLoopRegion: true } p && (startBar < p.LoopStartBar || startBar >= p.LoopEndBar)) startBar = p.LoopStartBar!.Value;
            _preview.Play(plan, plan.BarToFrame(startBar), LoopFrames(plan));
            IsPlaying = true;
            _playheadTimer?.Start();
            Status = plan.Problems.Count > 0 ? "Playing — some clips are silent: " + string.Join(" ", plan.Problems) : "Playing preview.";
        }
        finally
        {
            IsPreparing = false;
        }
    }

    private void ScheduleReplan()
    {
        _replanCts?.Cancel();
        var cts = _replanCts = new CancellationTokenSource();
        _ = Guard(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token);
                var plan = await _sources.CompilePlanAsync(Editor.Project, cts.Token);
                if (cts.IsCancellationRequested || !IsPlaying) return;
                long frame = _livePlan != null && _livePlan.SampleRate == plan.SampleRate ? _preview.PositionFrame : plan.BarToFrame(PlayheadBar);
                _livePlan = plan;
                _preview.Play(plan, frame, LoopFrames(plan));
            }
            catch (OperationCanceledException)
            {
                // intentional: a newer edit superseded this re-plan
            }
        });
    }

    private void UpdatePlayheadFromEngine()
    {
        if (!IsPlaying || _livePlan == null) return;
        PlayheadBar = _livePlan.FrameToBar(_preview.PositionFrame);
    }

    public void StopPreview()
    {
        _replanCts?.Cancel();
        _playheadTimer?.Stop();
        if (IsPlaying) _preview.Stop();
        IsPlaying = false;
    }

    private void StopAndRewind()
    {
        StopPreview();
        PlayheadBar = Editor.Project is { HasLoopRegion: true } p ? p.LoopStartBar!.Value : 0;
    }

    private void OnPreviewEnded() => _ = _ui(() =>
    {
        _playheadTimer?.Stop();
        IsPlaying = false;
        Status = "Reached the end of the edit.";
    });

    private void OnPreviewError(string message) => _ = _ui(() => Status = message);

    /// <summary>The page was navigated away from: stop the preview. The shared player is left as it
    /// was (paused where Edit Lab paused it, its track and position untouched).</summary>
    public void OnViewDeactivated()
    {
        StopPreview();
        if (_pausedSharedPlayer) Status = "The main player was paused for the preview; press play there to resume.";
        _pausedSharedPlayer = false;
        if (HasProject) _ = Guard(SaveNowAsync);
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    private void ScheduleAutosave()
    {
        _autosaveCts?.Cancel();
        var cts = _autosaveCts = new CancellationTokenSource();
        _ = Guard(async () =>
        {
            try
            {
                await Task.Delay(600, cts.Token);
                await SaveNowAsync();
            }
            catch (OperationCanceledException)
            {
                // intentional: a newer edit restarted the autosave delay
            }
        });
    }

    private async Task SaveNowAsync()
    {
        if (!HasProject) return;
        var snapshot = EditProjectStore.Deserialize(EditProjectStore.Serialize(Editor.Project));
        try
        {
            await _store.SaveAsync(snapshot);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "Edit Lab: couldn't save project {Id}", snapshot.Id);
            Status = $"Couldn't save the project: {ex.Message}";
        }
    }

    /// <summary>Runs an action, turning any failure into a logged status message (never an unobserved exception).</summary>
    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Edit Lab action failed");
            Status = ex.Message;
            _notifications?.Show("Edit Lab", ex.Message, NotificationType.Error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPreview();
        Editor.Changed -= OnEditorChanged;
        _preview.Ended -= OnPreviewEnded;
        _preview.Error -= OnPreviewError;
        foreach (var s in _subscriptions) s.Dispose();
        foreach (var cts in _separations.Values) cts.Cancel();
        _autosaveCts?.Cancel();
        _searchCts?.Cancel();
        _preview.Dispose();
    }
}
