using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels.StoryGraph;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.StoryGraph;

public class StoryGraphViewModelTests
{
    private static SetTrackEntity Slot(int position, SetSlotRole role = SetSlotRole.Builder, double? energy = null)
        => new() { Id = Guid.NewGuid(), Position = position, SlotRole = role, EnergyTarget = energy };

    private static StoryGraphViewModel Create(ISetProjectService? sets = null)
        => new(null!, sets ?? new RecordingSetProjectService());

    [Fact]
    public void SetSlotRole_RecomputesLayout()
    {
        var vm = Create();
        var a = Slot(0, SetSlotRole.Opening);
        var b = Slot(1, SetSlotRole.Builder);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Peak);

        Assert.Equal(SetSlotRole.Peak, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotRole_Undo_RestoresOriginalRole()
    {
        var vm = Create();
        var a = Slot(0, SetSlotRole.Opening);
        var b = Slot(1, SetSlotRole.Builder);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Peak);
        vm.Undo();

        Assert.Equal(SetSlotRole.Builder, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotRole_Redo_ReappliesRole()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(b.Id, SetSlotRole.Closer);
        vm.Undo();
        vm.Redo();

        Assert.Equal(SetSlotRole.Closer, vm.Layout!.Nodes[1].Role);
    }

    [Fact]
    public void SetSlotEnergyTarget_Undo_RestoresOriginal()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1, energy: 40.0);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotEnergyTarget(b.Id, 85.0);
        Assert.Equal(85.0, vm.Layout!.Nodes[1].EnergyTarget);

        vm.Undo();
        Assert.Equal(40.0, vm.Layout!.Nodes[1].EnergyTarget);
    }

    [Fact]
    public void SetSlotEnergyTarget_SliderBurst_IsOneUndoStep()
    {
        var vm = Create();
        var a = Slot(0, energy: 40.0);
        vm.Load(new[] { a }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotEnergyTarget(a.Id, 45.0);
        vm.SetSlotEnergyTarget(a.Id, 60.0);
        vm.SetSlotEnergyTarget(a.Id, 72.0);
        Assert.Equal(72.0, vm.Layout!.Nodes[0].EnergyTarget);

        vm.Undo();
        Assert.Equal(40.0, vm.Layout!.Nodes[0].EnergyTarget);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void CanUndo_TracksMutations()
    {
        var vm = Create();
        var a = Slot(0);
        var b = Slot(1);
        vm.Load(new[] { a, b }, Array.Empty<SetStoryEdgeEntity>());

        Assert.False(vm.CanUndo);
        vm.SetSlotRole(b.Id, SetSlotRole.Peak);
        Assert.True(vm.CanUndo);
        vm.Undo();
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task EveryEdit_UndoAndRedo_IsWrittenThrough()
    {
        var sets = new RecordingSetProjectService();
        var vm = Create(sets);
        var a = Slot(0);
        vm.Load(new[] { a }, Array.Empty<SetStoryEdgeEntity>());

        vm.SetSlotRole(a.Id, SetSlotRole.Peak);
        vm.Undo();
        vm.Redo();
        await vm.FlushWritesAsync();

        Assert.Equal(3, sets.Updates.Count);
        Assert.Equal(SetSlotRole.Peak, sets.Updates.Last().Role);
    }

    [Fact]
    public void Selection_DrivesInspectorState()
    {
        var vm = Create();
        var a = Slot(0, SetSlotRole.Opening, energy: 30.0);
        vm.Load(new[] { a }, Array.Empty<SetStoryEdgeEntity>());

        Assert.False(vm.HasSelection);
        vm.SelectedSlotId = a.Id;

        Assert.True(vm.HasSelection);
        Assert.Equal(SetSlotRole.Opening, vm.SelectedSlotRole);
        Assert.Equal(30.0, vm.SelectedSlotEnergyTarget);
    }

    [Fact]
    public void Load_DropsSelectionOfRemovedSlot()
    {
        var vm = Create();
        var a = Slot(0);
        vm.Load(new[] { a }, Array.Empty<SetStoryEdgeEntity>());
        vm.SelectedSlotId = a.Id;

        vm.Load(new[] { Slot(0) }, Array.Empty<SetStoryEdgeEntity>());

        Assert.Null(vm.SelectedSlotId);
    }

    /// <summary>
    /// Regression for F1: role/energy edits used to live only in memory, and AddTrackAsync reloaded
    /// from the DB — silently discarding them.
    /// </summary>
    [Fact]
    public async Task Edits_SurviveAddTrackReload_AndAreStored()
    {
        using var database = new SetsTestDatabase("story-graph-vm");
        var sets = new SetProjectService(database.Factory);
        var set = await sets.CreateSetAsync("Friday");
        await sets.AppendTrackAsync(set.Id, "hash-a");
        var slotB = await sets.AppendTrackAsync(set.Id, "hash-b");

        var vm = new StoryGraphViewModel(database.Factory, sets);
        await vm.LoadSetAsync(set.Id);

        vm.SetSlotRole(slotB!.Id, SetSlotRole.Peak);
        vm.SetSlotEnergyTarget(slotB.Id, 80.0);
        await vm.AddTrackAsync("hash-c");
        await vm.FlushWritesAsync();

        Assert.Equal(3, vm.Layout!.Nodes.Count);
        var node = vm.Layout.Nodes.Single(n => n.SlotId == slotB.Id);
        Assert.Equal(SetSlotRole.Peak, node.Role);
        Assert.Equal(80.0, node.EnergyTarget);

        using var db = database.Factory.CreateDbContext();
        var stored = await db.SetTracks.SingleAsync(t => t.Id == slotB.Id);
        Assert.Equal(SetSlotRole.Peak, stored.SlotRole);
        Assert.Equal(80.0, stored.EnergyTarget);
    }

    [Fact]
    public async Task Undo_IsStored()
    {
        using var database = new SetsTestDatabase("story-graph-undo");
        var sets = new SetProjectService(database.Factory);
        var set = await sets.CreateSetAsync("Friday");
        var slot = await sets.AppendTrackAsync(set.Id, "hash-a");

        var vm = new StoryGraphViewModel(database.Factory, sets);
        await vm.LoadSetAsync(set.Id);
        vm.SetSlotRole(slot!.Id, SetSlotRole.Closer);
        await vm.FlushWritesAsync();
        vm.Undo();
        await vm.FlushWritesAsync();

        using var db = database.Factory.CreateDbContext();
        Assert.Equal(SetSlotRole.Builder, (await db.SetTracks.SingleAsync(t => t.Id == slot.Id)).SlotRole);
    }

    /// <summary>
    /// Regression for F5: view models resumed on the thread pool after <c>ConfigureAwait(false)</c>
    /// and then touched bound state. The labels lookup here completes on another thread, so any
    /// continuation that doesn't return to the caller's context would raise Layout off it.
    /// </summary>
    [Fact]
    public async Task LoadSetAsync_RaisesLayoutOnCallersContext()
    {
        using var database = new SetsTestDatabase("story-graph-thread");
        var realSets = new SetProjectService(database.Factory);
        var set = await realSets.CreateSetAsync("Friday");
        await realSets.AppendTrackAsync(set.Id, "hash-a");
        var sets = new RecordingSetProjectService { LabelsCompleteOnPool = true };

        var layoutThreads = new ConcurrentBag<int>();
        var contextThread = await SingleThreadContext.RunAsync(async () =>
        {
            var vm = new StoryGraphViewModel(database.Factory, sets);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StoryGraphViewModel.Layout))
                    layoutThreads.Add(Environment.CurrentManagedThreadId);
            };
            await vm.LoadSetAsync(set.Id);
        });

        Assert.NotEmpty(layoutThreads);
        Assert.All(layoutThreads, id => Assert.Equal(contextThread, id));
    }

    [Fact]
    public async Task TrackPicker_FillsItemsOnCallersContext()
    {
        var sets = new RecordingSetProjectService { SearchCompletesOnPool = true };
        var itemThreads = new ConcurrentBag<int>();

        var contextThread = await SingleThreadContext.RunAsync(async () =>
        {
            using var picker = new TrackPickerViewModel(sets);
            picker.Items.CollectionChanged += (_, _) => itemThreads.Add(Environment.CurrentManagedThreadId);
            await picker.LoadAsync();
        });

        Assert.NotEmpty(itemThreads);
        Assert.All(itemThreads, id => Assert.Equal(contextThread, id));
    }
}

/// <summary>Records slot writes; optionally completes lookups on the thread pool.</summary>
internal sealed class RecordingSetProjectService : ISetProjectService
{
    public List<(Guid SlotId, SetSlotRole Role, double? Energy)> Updates { get; } = new();
    public bool LabelsCompleteOnPool { get; init; }
    public bool SearchCompletesOnPool { get; init; }

    public Task<SetListEntity> CreateSetAsync(string? name = null)
        => Task.FromResult(new SetListEntity { Name = name ?? "Set" });

    public Task<IReadOnlyList<SetSummary>> GetSetsAsync()
        => Task.FromResult<IReadOnlyList<SetSummary>>(Array.Empty<SetSummary>());

    public Task<IReadOnlyList<SetTrackEntity>> GetSlotsAsync(Guid setId)
        => Task.FromResult<IReadOnlyList<SetTrackEntity>>(Array.Empty<SetTrackEntity>());

    public Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash)
        => Task.FromResult<SetTrackEntity?>(new SetTrackEntity { SetListId = setId, TrackUniqueHash = trackUniqueHash });

    public Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget)
    {
        lock (Updates)
            Updates.Add((slotId, role, energyTarget));
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTrackLabelsAsync(IEnumerable<string> trackUniqueHashes)
    {
        var labels = trackUniqueHashes.ToDictionary(h => h, h => "Label " + h);
        if (LabelsCompleteOnPool)
            await Task.Run(() => Thread.Sleep(5)).ConfigureAwait(false);
        return labels;
    }

    public async Task<IReadOnlyList<SetTrackPickerItem>> SearchLibraryAsync(string? query, int limit = 200)
    {
        if (SearchCompletesOnPool)
            await Task.Run(() => Thread.Sleep(5)).ConfigureAwait(false);
        return new[] { new SetTrackPickerItem("h1", "Title", "Artist") };
    }

    public Task<bool> RenameSetAsync(Guid setId, string name) => Task.FromResult(true);
    public Task<bool> DeleteSetAsync(Guid setId) => Task.FromResult(true);
    public Task<SetListEntity?> DuplicateSetAsync(Guid setId, string? name = null) => Task.FromResult<SetListEntity?>(new SetListEntity { Name = name ?? "Copy" });
    public Task<SetTrackEntity?> InsertTrackAsync(Guid setId, string trackUniqueHash, int position) => Task.FromResult<SetTrackEntity?>(new SetTrackEntity { SetListId = setId, TrackUniqueHash = trackUniqueHash, Position = position });
    public Task<bool> MoveTrackAsync(Guid slotId, int newPosition) => Task.FromResult(true);
    public Task<bool> RemoveTrackAsync(Guid slotId) => Task.FromResult(true);
    public Task<bool> UpdateSlotNotesAsync(Guid slotId, string? notes) => Task.FromResult(true);
    public Task<SetStoryEdgeEntity?> AddAlternateAsync(Guid setId, Guid fromSlotId, Guid toSlotId, double probability = 0.5, string? conditionText = null) => Task.FromResult<SetStoryEdgeEntity?>(new SetStoryEdgeEntity { SetProjectId = setId, FromSlotId = fromSlotId, ToSlotId = toSlotId, Probability = probability, ConditionText = conditionText });
    public Task<bool> RemoveAlternateAsync(Guid edgeId) => Task.FromResult(true);
}

/// <summary>
/// Runs async code on one dedicated thread with a posting <see cref="SynchronizationContext"/>,
/// the way the Avalonia UI thread behaves. Returns that thread's managed id.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public static Task<int> RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new SingleThreadContext();
            SetSynchronizationContext(context);
            var task = body();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);

            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                callback(state);

            if (task.IsFaulted)
                done.SetException(task.Exception!.InnerExceptions);
            else
                done.SetResult(Environment.CurrentManagedThreadId);
        }) { IsBackground = true };
        thread.Start();
        return done.Task;
    }
}
