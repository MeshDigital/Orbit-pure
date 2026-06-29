using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Workstation;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Cue & Loop Forge — a dedicated visual workspace for professional cue generation and loop placement.
/// Manages a working draft of cues with strict isolation: changes stay in-memory until committed.
/// </summary>
public sealed class CueForgeViewModel : ReactiveObject, IDisposable
{
    private readonly ICuePointService _cueService;
    private readonly CueGenerationService _cueGenerationService;
    private readonly PlayerViewModel _playerViewModel;
    private readonly ILogger<CueForgeViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();

    // Undo/Redo management
    private readonly Stack<OrbitCue[]> _undoStack = new();
    private readonly Stack<OrbitCue[]> _redoStack = new();
    private const int MaxUndoHistory = 50;

    // ── Observable Collections ──────────────────────────────────────

    /// <summary>
    /// Working draft of cues for the current track. This is isolated from the DB until Commit.
    /// </summary>
    public ObservableCollection<OrbitCue> WorkingCues { get; } = new();

    // ── State Properties ────────────────────────────────────────────

    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set => this.RaiseAndSetIfChanged(ref _trackHash, value);
    }

    private double _currentPlayPosition = 0.0;
    public double CurrentPlayPosition
    {
        get => _currentPlayPosition;
        set => this.RaiseAndSetIfChanged(ref _currentPlayPosition, value);
    }

    private bool _snapToGrid = true;
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set => this.RaiseAndSetIfChanged(ref _snapToGrid, value);
    }

    private int _quantizeBeats = 16;
    public int QuantizeBeats
    {
        get => _quantizeBeats;
        set => this.RaiseAndSetIfChanged(ref _quantizeBeats, value);
    }

    private OrbitCue? _selectedCue;
    public OrbitCue? SelectedCue
    {
        get => _selectedCue;
        set => this.RaiseAndSetIfChanged(ref _selectedCue, value);
    }

    private bool _hasUncommittedChanges;
    public bool HasUncommittedChanges
    {
        get => _hasUncommittedChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasUncommittedChanges, value);
    }

    private bool _canUndo;
    public bool CanUndo
    {
        get => _canUndo;
        private set => this.RaiseAndSetIfChanged(ref _canUndo, value);
    }

    private bool _canRedo;
    public bool CanRedo
    {
        get => _canRedo;
        private set => this.RaiseAndSetIfChanged(ref _canRedo, value);
    }

    // ── Commands ────────────────────────────────────────────────────

    /// <summary>Add a new cue at the current playhead position.</summary>
    public ReactiveCommand<Unit, Unit> AddCueAtPlayheadCommand { get; }

    /// <summary>Trigger auto-generation: delete auto cues, regenerate from analysis.</summary>
    public ReactiveCommand<Unit, Unit> AutoGenerateCuesCommand { get; }

    /// <summary>Update an existing cue (name, color, slot).</summary>
    public ReactiveCommand<OrbitCue, Unit> UpdateCueCommand { get; }

    /// <summary>Delete a cue from the working draft.</summary>
    public ReactiveCommand<OrbitCue, Unit> DeleteCueCommand { get; }

    /// <summary>Set a loop: (inSeconds, outSeconds). Creates or updates loop cue.</summary>
    public ReactiveCommand<(double InSeconds, double OutSeconds), Unit> SetLoopCommand { get; }

    /// <summary>Clear any active loop.</summary>
    public ReactiveCommand<Unit, Unit> ClearLoopCommand { get; }

    /// <summary>Commit working draft to database.</summary>
    public ReactiveCommand<Unit, Unit> CommitChangesCommand { get; }

    /// <summary>Discard all changes; reload from database.</summary>
    public ReactiveCommand<Unit, Unit> DiscardChangesCommand { get; }

    /// <summary>Undo last change.</summary>
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }

    /// <summary>Redo last undone change.</summary>
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    // ── Constructor ─────────────────────────────────────────────────

    public CueForgeViewModel(
        ICuePointService cueService,
        CueGenerationService cueGenerationService,
        PlayerViewModel playerViewModel,
        ILogger<CueForgeViewModel> logger)
    {
        _cueService = cueService;
        _cueGenerationService = cueGenerationService;
        _playerViewModel = playerViewModel;
        _logger = logger;

        var hasTrack = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));

        // Command Setup
        AddCueAtPlayheadCommand = ReactiveCommand.CreateFromTask(AddCueAtPlayheadAsync, hasTrack);
        AutoGenerateCuesCommand = ReactiveCommand.CreateFromTask(AutoGenerateCuesAsync, hasTrack);
        UpdateCueCommand = ReactiveCommand.CreateFromTask<OrbitCue>(UpdateCueAsync, hasTrack);
        DeleteCueCommand = ReactiveCommand.CreateFromTask<OrbitCue>(DeleteCueAsync, hasTrack);
        SetLoopCommand = ReactiveCommand.CreateFromTask<(double, double)>(SetLoopAsync, hasTrack);
        ClearLoopCommand = ReactiveCommand.CreateFromTask(ClearLoopAsync, hasTrack);
        CommitChangesCommand = ReactiveCommand.CreateFromTask(CommitChangesAsync);
        DiscardChangesCommand = ReactiveCommand.CreateFromTask(DiscardChangesAsync);
        UndoCommand = ReactiveCommand.Create(Undo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(Redo, this.WhenAnyValue(x => x.CanRedo));

        // Monitor collection changes to detect uncommitted edits
        WorkingCues.CollectionChanged += (s, e) => HasUncommittedChanges = true;
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>Load cues for a track into the working draft.</summary>
    public async Task LoadTrackAsync(string trackHash)
    {
        TrackHash = trackHash;
        _undoStack.Clear();
        _redoStack.Clear();
        HasUncommittedChanges = false;
        UpdateUndoRedoState();

        var entities = await _cueService.GetByTrackIdAsync(trackHash);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            WorkingCues.Clear();
            foreach (var e in entities)
                WorkingCues.Add(EntityToOrbitCue(e));
        });

        _logger.LogInformation("Loaded {Count} cues for track {Hash}", entities.Count, trackHash);
    }

    /// <summary>Clear cues when navigating away.</summary>
    public void ClearWorkingDraft()
    {
        TrackHash = null;
        WorkingCues.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        HasUncommittedChanges = false;
        UpdateUndoRedoState();
    }

    // ── Command Implementations ─────────────────────────────────────

    private async Task AddCueAtPlayheadAsync()
    {
        if (TrackHash is null) return;
        PushSnapshot();

        var cue = new OrbitCue
        {
            Timestamp = CurrentPlayPosition,
            Name = $"Cue {WorkingCues.Count + 1}",
            Color = "#FFFF00",
            Source = CueSource.User,
            Role = CueRole.Custom,
            SlotIndex = -1
        };
        WorkingCues.Add(cue);

        // Sort by timestamp
        var sorted = WorkingCues.OrderBy(c => c.Timestamp).ToList();
        WorkingCues.Clear();
        foreach (var c in sorted) WorkingCues.Add(c);
    }

    private async Task AutoGenerateCuesAsync()
    {
        if (TrackHash is null) return;
        PushSnapshot();

        _logger.LogInformation("Auto-generating cues for track {Hash}", TrackHash);
        // TODO: Fetch StructuralAnalysisResult for track and regenerate cues
        // For now, placeholder. Will be implemented when StructuralAnalysisResult is available.
        await Task.CompletedTask;
    }

    private async Task UpdateCueAsync(OrbitCue cue)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        cue.Source = CueSource.User;
        await Task.CompletedTask;
    }

    private async Task DeleteCueAsync(OrbitCue cue)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        WorkingCues.Remove(cue);
        await Task.CompletedTask;
    }

    private async Task SetLoopAsync((double InSeconds, double OutSeconds) loop)
    {
        if (TrackHash is null) return;
        PushSnapshot();

        // Remove any existing loop
        var existingLoop = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (existingLoop != null) WorkingCues.Remove(existingLoop);

        WorkingCues.Add(new OrbitCue
        {
            Timestamp = loop.InSeconds,
            LoopEndSeconds = loop.OutSeconds,
            IsLoop = true,
            Name = "Loop",
            Color = "#00FF88",
            Source = CueSource.User,
            Role = CueRole.Custom
        });
    }

    private async Task ClearLoopAsync()
    {
        if (TrackHash is null) return;
        var loopCue = WorkingCues.FirstOrDefault(c => c.IsLoop);
        if (loopCue == null) return;
        PushSnapshot();
        WorkingCues.Remove(loopCue);
        await Task.CompletedTask;
    }

    private async Task CommitChangesAsync()
    {
        if (TrackHash is null) return;

        _logger.LogInformation("Committing {Count} cues to track {Hash}", WorkingCues.Count, TrackHash);

        // Delete all existing cues for this track, then persist the working draft
        await _cueService.DeleteAllByTrackIdAsync(TrackHash);

        if (WorkingCues.Count > 0)
        {
            var entities = WorkingCues.Select((c, i) => OrbitCueToEntity(c, TrackHash, i)).ToList();
            await _cueService.CreateManyAsync(entities);
        }

        HasUncommittedChanges = false;
    }

    private async Task DiscardChangesAsync()
    {
        if (TrackHash is null) return;

        _logger.LogInformation("Discarding changes for track {Hash}", TrackHash);

        // Reload from database
        var entities = await _cueService.GetByTrackIdAsync(TrackHash);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            WorkingCues.Clear();
            foreach (var e in entities)
                WorkingCues.Add(EntityToOrbitCue(e));
        });

        HasUncommittedChanges = false;
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoRedoState();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(TakeSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        UpdateUndoRedoState();
        HasUncommittedChanges = true;
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(TakeSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        UpdateUndoRedoState();
        HasUncommittedChanges = true;
    }

    // ── Snapshot/Undo-Redo Helpers ──────────────────────────────────

    private void PushSnapshot()
    {
        _redoStack.Clear();
        if (_undoStack.Count >= MaxUndoHistory)
        {
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--)
                _undoStack.Push(arr[i]);
        }
        _undoStack.Push(TakeSnapshot());
        UpdateUndoRedoState();
    }

    private OrbitCue[] TakeSnapshot() =>
        WorkingCues.Select(c => new OrbitCue
        {
            Timestamp = c.Timestamp,
            Name = c.Name,
            Color = c.Color,
            Source = c.Source,
            Role = c.Role,
            SlotIndex = c.SlotIndex,
            Confidence = c.Confidence,
            IsLoop = c.IsLoop,
            LoopEndSeconds = c.LoopEndSeconds,
        }).ToArray();

    private void RestoreSnapshot(OrbitCue[] snapshot)
    {
        WorkingCues.Clear();
        foreach (var c in snapshot) WorkingCues.Add(c);
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    // ── Model ↔ Entity Mapping ──────────────────────────────────────

    private static OrbitCue EntityToOrbitCue(CuePointEntity e) => new()
    {
        Timestamp = e.TimestampInSeconds,
        Name = e.Label,
        Color = e.Color,
        Source = e.IsAutoGenerated ? CueSource.Auto : CueSource.User,
        Role = MapEntityType(e.Type),
        Confidence = e.Confidence,
        IsLoop = e.IsLoop,
        LoopEndSeconds = e.LoopEndSeconds,
        SlotIndex = e.SlotIndex
    };

    private static CuePointEntity OrbitCueToEntity(OrbitCue c, string hash, int index) => new()
    {
        Id = Guid.NewGuid(),
        TrackUniqueHash = hash,
        TimestampInSeconds = c.Timestamp,
        Label = c.Name,
        Color = c.Color,
        IsAutoGenerated = c.Source == CueSource.Auto,
        Type = MapCueRole(c.Role),
        Confidence = (float)c.Confidence,
        IsLoop = c.IsLoop,
        LoopEndSeconds = c.LoopEndSeconds,
        SlotIndex = c.SlotIndex,
        CreatedAt = DateTime.UtcNow,
    };

    private static CueRole MapEntityType(CuePointType t) => t switch
    {
        CuePointType.Intro => CueRole.Intro,
        CuePointType.Outro => CueRole.Outro,
        CuePointType.Drop => CueRole.Drop,
        CuePointType.Breakdown => CueRole.Breakdown,
        CuePointType.Build => CueRole.Build,
        CuePointType.PhraseBoundary => CueRole.PhraseStart,
        _ => CueRole.Custom
    };

    private static CuePointType MapCueRole(CueRole r) => r switch
    {
        CueRole.Intro => CuePointType.Intro,
        CueRole.Outro => CuePointType.Outro,
        CueRole.Drop => CuePointType.Drop,
        CueRole.Breakdown => CuePointType.Breakdown,
        CueRole.Breakdown2 => CuePointType.Breakdown,
        CueRole.Build => CuePointType.Build,
        CueRole.PhraseStart => CuePointType.PhraseBoundary,
        _ => CuePointType.PhraseBoundary
    };

    public void Dispose() => _disposables.Dispose();
}
