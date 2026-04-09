using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Workstation;

/// <summary>
/// Manages cue points for a single workstation deck.
/// Provides add / update / delete with undo-redo, and persists to DB via ICuePointService.
/// </summary>
public sealed class CueEditorViewModel : ReactiveObject, IDisposable
{
    private readonly ICuePointService _cueService;
    private readonly CompositeDisposable _disposables = new();

    // Simple snapshot-based undo/redo
    private readonly Stack<OrbitCue[]> _undoStack = new();
    private readonly Stack<OrbitCue[]> _redoStack = new();
    private const int MaxHistory = 50;

    // ── Public state ──────────────────────────────────────────────────────────

    public ObservableCollection<OrbitCue> Cues { get; } = new();

    private string? _trackHash;
    public string? TrackHash
    {
        get => _trackHash;
        private set => this.RaiseAndSetIfChanged(ref _trackHash, value);
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

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Add a new user cue at the given timestamp (seconds).</summary>
    public ReactiveCommand<double, Unit> AddCueAtPositionCommand { get; }

    /// <summary>Called by WaveformControl.CueUpdatedCommand after a drag-to-move.</summary>
    public ReactiveCommand<OrbitCue, Unit> UpdateCueCommand { get; }

    /// <summary>Remove a specific cue.</summary>
    public ReactiveCommand<OrbitCue, Unit> DeleteCueCommand { get; }

    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    /// <summary>Delete user-edited cues; reload auto-generated ones from DB.</summary>
    public ReactiveCommand<Unit, Unit> ResetToAutoCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public CueEditorViewModel(ICuePointService cueService)
    {
        _cueService = cueService;

        var hasHash = this.WhenAnyValue(x => x.TrackHash, h => !string.IsNullOrEmpty(h));

        AddCueAtPositionCommand = ReactiveCommand.CreateFromTask<double>(AddCueAtPositionAsync, hasHash);
        UpdateCueCommand        = ReactiveCommand.CreateFromTask<OrbitCue>(UpdateCueAsync, hasHash);
        DeleteCueCommand        = ReactiveCommand.CreateFromTask<OrbitCue>(DeleteCueAsync, hasHash);
        UndoCommand             = ReactiveCommand.Create(Undo,
            this.WhenAnyValue(x => x.CanUndo));
        RedoCommand             = ReactiveCommand.Create(Redo,
            this.WhenAnyValue(x => x.CanRedo));
        ResetToAutoCommand      = ReactiveCommand.CreateFromTask(ResetToAutoAsync, hasHash);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Load cue points for a track from the database.</summary>
    public async Task LoadCuesAsync(string trackHash)
    {
        TrackHash = trackHash;
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoRedoState();

        var entities = await _cueService.GetByTrackIdAsync(trackHash);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Cues.Clear();
            foreach (var e in entities)
                Cues.Add(EntityToOrbitCue(e));
        });
    }

    /// <summary>Clear cues when a new track is loaded or deck is cleared.</summary>
    public void ClearCues()
    {
        TrackHash = null;
        Cues.Clear();
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoRedoState();
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task AddCueAtPositionAsync(double timestampSeconds)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        var cue = new OrbitCue
        {
            Timestamp = timestampSeconds,
            Name      = $"Cue {Cues.Count + 1}",
            Color     = "#FFFF00",
            Source    = CueSource.User,
            Role      = CueRole.Custom
        };
        Cues.Add(cue);
        await SaveAllAsync();
    }

    private async Task UpdateCueAsync(OrbitCue cue)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        cue.Source = CueSource.User;  // mark as user-edited when moved
        await SaveAllAsync();
    }

    private async Task DeleteCueAsync(OrbitCue cue)
    {
        if (TrackHash is null) return;
        PushSnapshot();
        Cues.Remove(cue);
        await SaveAllAsync();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(TakeSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        UpdateUndoRedoState();
        _ = SaveAllAsync();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(TakeSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        UpdateUndoRedoState();
        _ = SaveAllAsync();
    }

    private async Task ResetToAutoAsync()
    {
        if (TrackHash is null) return;
        PushSnapshot();

        // Delete all user-placed cues (auto-generated ones stay)
        // Then reload whatever is left in DB
        var entitiesToDelete = await _cueService.GetByTrackIdAsync(TrackHash);
        var userOnly = entitiesToDelete.Where(e => !e.IsAutoGenerated).ToList();
        if (userOnly.Count > 0)
        {
            // Remove user cues: delete all then re-insert only auto ones
            await _cueService.DeleteAllByTrackIdAsync(TrackHash);
            var autoOnly = entitiesToDelete.Where(e => e.IsAutoGenerated).ToList();
            if (autoOnly.Count > 0)
                await _cueService.CreateManyAsync(autoOnly);
        }

        var remaining = await _cueService.GetByTrackIdAsync(TrackHash);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Cues.Clear();
            foreach (var e in remaining)
                Cues.Add(EntityToOrbitCue(e));
        });
    }

    // ── Snapshot helpers ──────────────────────────────────────────────────────

    private void PushSnapshot()
    {
        _redoStack.Clear();
        // Trim if over limit
        if (_undoStack.Count >= MaxHistory)
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
        Cues.Select(c => new OrbitCue
        {
            Timestamp  = c.Timestamp,
            Name       = c.Name,
            Color      = c.Color,
            Source     = c.Source,
            Role       = c.Role,
            SlotIndex  = c.SlotIndex,
            Confidence = c.Confidence
        }).ToArray();

    private void RestoreSnapshot(OrbitCue[] snapshot)
    {
        Cues.Clear();
        foreach (var c in snapshot) Cues.Add(c);
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task SaveAllAsync()
    {
        if (TrackHash is null) return;

        // Snapshot current cues on UI thread before going async
        var snapshot = Cues.Select(c => c).ToArray();

        await _cueService.DeleteAllByTrackIdAsync(TrackHash);

        if (snapshot.Length > 0)
        {
            var entities = snapshot.Select((c, i) => OrbitCueToEntity(c, TrackHash, i)).ToList();
            await _cueService.CreateManyAsync(entities);
        }
    }

    // ── Model ↔ Entity mapping ─────────────────────────────────────────────────

    private static OrbitCue EntityToOrbitCue(CuePointEntity e) => new()
    {
        Timestamp  = e.TimestampInSeconds,
        Name       = e.Label,
        Color      = e.Color,
        Source     = e.IsAutoGenerated ? CueSource.Auto : CueSource.User,
        Role       = MapEntityType(e.Type),
        Confidence = e.Confidence
    };

    private static CuePointEntity OrbitCueToEntity(OrbitCue c, string hash, int slotIndex = 0) => new()
    {
        TrackUniqueHash    = hash,
        TimestampInSeconds = c.Timestamp,
        Label              = c.Name,
        Color              = c.Color,
        IsAutoGenerated    = c.Source == CueSource.Auto,
        Type               = MapCueRole(c.Role),
        Confidence         = (float)c.Confidence
    };

    private static CueRole MapEntityType(CuePointType t) => t switch
    {
        CuePointType.Intro          => CueRole.Intro,
        CuePointType.Outro          => CueRole.Outro,
        CuePointType.Drop           => CueRole.Drop,
        CuePointType.Breakdown      => CueRole.Breakdown,
        CuePointType.Build          => CueRole.Build,
        CuePointType.PhraseBoundary => CueRole.PhraseStart,
        _                           => CueRole.Custom
    };

    private static CuePointType MapCueRole(CueRole r) => r switch
    {
        CueRole.Intro      => CuePointType.Intro,
        CueRole.Outro      => CuePointType.Outro,
        CueRole.Drop       => CuePointType.Drop,
        CueRole.Breakdown  => CuePointType.Breakdown,
        CueRole.Breakdown2 => CuePointType.Breakdown,
        CueRole.Build      => CuePointType.Build,
        CueRole.PhraseStart => CuePointType.PhraseBoundary,
        _                  => CuePointType.PhraseBoundary
    };

    public void Dispose() => _disposables.Dispose();
}
