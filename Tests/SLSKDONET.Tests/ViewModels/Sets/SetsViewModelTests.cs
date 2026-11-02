using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels.Sets;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Sets;

/// <summary>Sets page view model against a real SetProjectService on an isolated SQLite file.</summary>
public sealed class SetsViewModelTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("sets-vm");
    private readonly SetProjectService _service;
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly List<(string Message, NotificationType Type)> _toasts = new();

    public SetsViewModelTests()
    {
        _service = new SetProjectService(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private SetsViewModel Create(ISetProjectService? service = null)
    {
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationType>(), It.IsAny<TimeSpan?>(), It.IsAny<string?>()))
            .Callback<string, string, NotificationType, TimeSpan?, string?>((_, message, type, _, _) => _toasts.Add((message, type)));
        return new SetsViewModel(service ?? _service, _dialogs.Object, notifications.Object);
    }

    [Fact]
    public async Task Load_NoSets_ShowsEmptyState()
    {
        var vm = Create();

        await vm.LoadAsync();

        Assert.True(vm.HasNoSets);
        Assert.False(vm.ShowNoSelection);
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task Load_WithSets_AsksForSelection_ThenSelectingLoadsSlots()
    {
        _db.SeedLibrary(("h1", "Noisia", "Machine Gun"));
        var set = await _service.CreateSetAsync("Friday");
        await _service.AppendTrackAsync(set.Id, "h1");
        var vm = Create();

        await vm.LoadAsync();
        Assert.True(vm.ShowNoSelection);

        vm.SelectedSet = vm.Sets.Single();
        await vm.PendingSlotLoad;

        Assert.True(vm.HasSlots);
        Assert.Equal("Machine Gun", vm.Slots.Single().Title);
        Assert.Equal("—", vm.Slots.Single().Bpm); // unanalysed: shown as missing, not 120
        Assert.Equal("1 track · Draft", vm.SelectedSummary);
    }

    [Fact]
    public async Task SelectingEmptySet_ShowsSetEmptyState()
    {
        await _service.CreateSetAsync("Empty");
        var vm = Create();
        await vm.LoadAsync();

        vm.SelectedSet = vm.Sets.Single();
        await vm.PendingSlotLoad;

        Assert.True(vm.IsSetEmpty);
        Assert.False(vm.HasSlots);
    }

    [Fact]
    public async Task NewSet_UsesPromptedName_AndSelectsIt()
    {
        _dialogs.Setup(d => d.ShowPromptAsync("New set", It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("Club X");
        var vm = Create();
        await vm.LoadAsync();

        await vm.NewSetCommand.Execute();

        Assert.Equal("Club X", vm.SelectedSet?.Name);
        Assert.Single(await _service.GetSetsAsync());
    }

    [Fact]
    public async Task NewSet_CancelledPrompt_CreatesNothing()
    {
        _dialogs.Setup(d => d.ShowPromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((string?)null);
        var vm = Create();
        await vm.LoadAsync();

        await vm.NewSetCommand.Execute();

        Assert.Empty(await _service.GetSetsAsync());
    }

    [Fact]
    public async Task Rename_UpdatesStoreAndRail()
    {
        await _service.CreateSetAsync("Friday");
        _dialogs.Setup(d => d.ShowPromptAsync("Rename set", It.IsAny<string>(), "Friday")).ReturnsAsync("Saturday");
        var vm = Create();
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();

        await vm.RenameSetCommand.Execute();

        Assert.Equal("Saturday", vm.SelectedSet?.Name);
        Assert.Equal("Saturday", (await _service.GetSetsAsync()).Single().Name);
    }

    [Fact]
    public async Task Duplicate_SelectsTheCopy()
    {
        var set = await _service.CreateSetAsync("Friday");
        await _service.AppendTrackAsync(set.Id, "a");
        var vm = Create();
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();

        await vm.DuplicateSetCommand.Execute();
        await vm.PendingSlotLoad;

        Assert.Equal("Friday (copy)", vm.SelectedSet?.Name);
        Assert.Equal(2, vm.Sets.Count);
        Assert.Single(vm.Slots);
    }

    [Fact]
    public async Task Delete_RequiresConfirmation()
    {
        await _service.CreateSetAsync("Friday");
        _dialogs.Setup(d => d.ConfirmAsync("Delete set", It.IsAny<string>(), "Delete", "Cancel")).ReturnsAsync(false);
        var vm = Create();
        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single();

        await vm.DeleteSetCommand.Execute();
        Assert.Single(await _service.GetSetsAsync());

        _dialogs.Setup(d => d.ConfirmAsync("Delete set", It.IsAny<string>(), "Delete", "Cancel")).ReturnsAsync(true);
        await vm.DeleteSetCommand.Execute();

        Assert.Empty(await _service.GetSetsAsync());
        Assert.Null(vm.SelectedSet);
        Assert.True(vm.HasNoSets);
    }

    private async Task<(SetsViewModel Vm, Guid SetId)> OpenSetWithTracksAsync(params string[] hashes)
    {
        var set = await _service.CreateSetAsync("Friday");
        foreach (var hash in hashes)
            await _service.AppendTrackAsync(set.Id, hash);
        var vm = Create();
        await vm.OpenSetAsync(set.Id);
        await vm.PendingSlotLoad;
        return (vm, set.Id);
    }

    private static string[] Order(SetsViewModel vm) => vm.Slots.Select(s => s.Slot.TrackUniqueHash).ToArray();

    [Fact]
    public async Task Lane_InterleavesCardsAndChips_WithKnowledgeEvidence()
    {
        _db.SeedLibrary(("a", "X", "A"), ("b", "Y", "B"), ("c", "Z", "C"));
        _db.SeedFeatures(("a", 174f, "8A"), ("b", 174f, "9A"), ("c", 140f, "3B"));
        using (var db = _db.Factory.CreateDbContext())
        {
            db.TransitionKnowledge.Add(new SLSKDONET.Data.Entities.TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", EvidenceCount = 2, Verified = true });
            await db.SaveChangesAsync();
        }

        var (vm, _) = await OpenSetWithTracksAsync("a", "b", "c");

        Assert.Equal(5, vm.LaneItems.Count); // card, chip, card, chip, card
        var chips = vm.LaneItems.OfType<TransitionChipViewModel>().ToList();
        Assert.Equal(2, chips.Count);
        Assert.True(chips[0].IsVerified);
        Assert.Equal(TransitionEvidenceState.Algorithmic, chips[1].Evidence);
        Assert.True(chips[0].Score > chips[1].Score); // 174→174 adjacent keys beats 174→140 clash
        Assert.True(chips[1].HasWarnings);
    }

    [Fact]
    public async Task MoveSlot_UndoRedo_RoundTripsOrder_AndKeepsSelection()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "b", "c");
        vm.SelectSlot(vm.Slots[0]);

        await vm.MoveSlotRightCommand.Execute();
        Assert.Equal(new[] { "b", "a", "c" }, Order(vm));
        Assert.Equal("a", vm.SelectedSlot?.Slot.TrackUniqueHash);
        Assert.True(vm.CanUndo);

        await vm.UndoCommand.Execute();
        Assert.Equal(new[] { "a", "b", "c" }, Order(vm));

        await vm.RedoCommand.Execute();
        Assert.Equal(new[] { "b", "a", "c" }, Order(vm));
    }

    [Fact]
    public async Task RemoveSlot_Undo_RestoresTheSameSlotWithItsSettings()
    {
        var (vm, setId) = await OpenSetWithTracksAsync("a", "b", "c");
        var b = vm.Slots[1];
        await _service.UpdateSlotAsync(b.SlotId, SLSKDONET.Data.Entities.SetSlotRole.Peak, 90);
        await vm.OpenSetAsync(setId);
        await vm.PendingSlotLoad;
        vm.SelectSlot(vm.Slots[1]);

        await vm.RemoveSlotCommand.Execute();
        Assert.Equal(new[] { "a", "c" }, Order(vm));
        Assert.Equal("c", vm.SelectedSlot?.Slot.TrackUniqueHash); // selection moves to the neighbour

        await vm.UndoCommand.Execute();
        Assert.Equal(new[] { "a", "b", "c" }, Order(vm));
        var restored = vm.Slots[1].Slot;
        Assert.Equal((b.SlotId, SLSKDONET.Data.Entities.SetSlotRole.Peak, 90.0), (restored.SlotId, restored.Role, restored.EnergyTarget));
    }

    [Fact]
    public async Task InsertTrack_AtIndex_ThenUndoRedo()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "c");

        await vm.InsertTrackAsync("b", 1);
        Assert.Equal(new[] { "a", "b", "c" }, Order(vm));
        Assert.Equal("b", vm.SelectedSlot?.Slot.TrackUniqueHash);

        await vm.UndoCommand.Execute();
        Assert.Equal(new[] { "a", "c" }, Order(vm));

        await vm.RedoCommand.Execute();
        Assert.Equal(new[] { "a", "b", "c" }, Order(vm));
    }

    [Fact]
    public async Task SwitchingSets_ClearsUndoHistory()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "b");
        await _service.CreateSetAsync("Other");
        vm.SelectSlot(vm.Slots[0]);
        await vm.MoveSlotRightCommand.Execute();
        Assert.True(vm.CanUndo);

        await vm.LoadAsync();
        vm.SelectedSet = vm.Sets.Single(s => s.Name == "Other");
        await vm.PendingSlotLoad;

        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task KeyboardSelection_WrapsWithinBounds()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "b");

        await vm.SelectNextSlotCommand.Execute();     // nothing selected → first
        Assert.Equal("a", vm.SelectedSlot?.Slot.TrackUniqueHash);
        await vm.SelectNextSlotCommand.Execute();
        await vm.SelectNextSlotCommand.Execute();     // clamped at the end
        Assert.Equal("b", vm.SelectedSlot?.Slot.TrackUniqueHash);
        Assert.True(vm.Slots[1].IsSelected);
        Assert.False(vm.Slots[0].IsSelected);
    }

    [Fact]
    public async Task AddFromPool_InsertsAfterSelectedSlot()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "c");
        vm.SelectSlot(vm.Slots[0]);

        await vm.AddFromPoolAsync(new PoolTrackViewModel(new SetPoolTrack("b", "B", "Y", null, null)));

        Assert.Equal(new[] { "a", "b", "c" }, Order(vm));
    }

    [Fact]
    public async Task StartFromPlaylist_AppendsReportsSkipped_AndUndoesAsOneStep()
    {
        _db.SeedLibrary(("a", "X", "A"), ("b", "Y", "B"));
        var playlistId = _db.SeedPlaylist(("a", 0), ("gone", 1), ("b", 2));
        var playlist = new SLSKDONET.Models.PlaylistJob { Id = playlistId, SourceTitle = "Warmup" };
        var library = new Mock<ILibraryService>();
        library.Setup(l => l.LoadAllPlaylistJobsAsync()).ReturnsAsync(new List<SLSKDONET.Models.PlaylistJob> { playlist });
        _dialogs.Setup(d => d.ShowProjectPickerAsync(It.IsAny<IEnumerable<SLSKDONET.Models.PlaylistJob>>())).ReturnsAsync(playlist);
        var set = await _service.CreateSetAsync("Friday");
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(n => n.Show(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<NotificationType>(), It.IsAny<TimeSpan?>(), It.IsAny<string?>()))
            .Callback<string, string, NotificationType, TimeSpan?, string?>((_, message, type, _, _) => _toasts.Add((message, type)));
        var vm = new SetsViewModel(_service, _dialogs.Object, notifications.Object, library: library.Object);
        await vm.OpenSetAsync(set.Id);
        await vm.PendingSlotLoad;

        await vm.StartFromPlaylistCommand.Execute();

        Assert.Equal(new[] { "a", "b" }, Order(vm));
        Assert.Contains(_toasts, t => t.Message.Contains("2 tracks") && t.Message.Contains("1 track skipped"));

        await vm.UndoCommand.Execute();
        Assert.Empty(vm.Slots);
        await vm.RedoCommand.Execute();
        Assert.Equal(new[] { "a", "b" }, Order(vm));
    }

    [Theory]
    [InlineData("8A", new[] { "8A", "9A", "7A", "8B" })]
    [InlineData("12B", new[] { "12B", "1B", "11B", "12A" })]
    [InlineData("1A", new[] { "1A", "2A", "12A", "1B" })]
    [InlineData("garbage", new[] { "garbage" })]
    public void CamelotNeighbours_WrapTheWheel(string key, string[] expected)
    {
        Assert.Equal(expected.OrderBy(k => k), SetPoolViewModel.CamelotNeighbours(key).OrderBy(k => k));
    }

    [Fact]
    public async Task FailedEdit_IsReportedAndStaysVisible()
    {
        var (vm, _) = await OpenSetWithTracksAsync("a", "b");
        vm.SelectSlot(vm.Slots[0]);
        await _service.RemoveSlotAsync(vm.Slots[0].SlotId); // gone behind the page's back

        await vm.MoveSlotRightCommand.Execute();

        Assert.True(vm.HasError);
        Assert.Contains(_toasts, t => t.Type == NotificationType.Error);
        Assert.False(vm.CanUndo); // nothing was recorded for the failed move
    }

    /// <summary>
    /// The rail and slot list must be filled on the caller's (UI) context even when the service
    /// completes on the thread pool — the cross-thread update Phase 0 fixed (F5).
    /// </summary>
    [Fact]
    public async Task Load_FillsCollectionsOnCallersContext()
    {
        var setId = Guid.NewGuid();
        var pooled = new Mock<ISetProjectService>();
        pooled.Setup(s => s.GetSetsAsync(It.IsAny<bool>())).Returns(async () =>
        {
            await Task.Run(() => System.Threading.Thread.Sleep(5)).ConfigureAwait(false);
            return (IReadOnlyList<SetSummary>)new[] { new SetSummary(setId, "Friday", SLSKDONET.Data.Entities.SetProjectStatus.Draft, 1, DateTime.UtcNow) };
        });
        pooled.Setup(s => s.GetSlotsAsync(setId)).Returns(async () =>
        {
            await Task.Run(() => System.Threading.Thread.Sleep(5)).ConfigureAwait(false);
            return (IReadOnlyList<SetSlotInfo>)new[] { new SetSlotInfo(Guid.NewGuid(), 0, "h", "T", "A", true, SLSKDONET.Data.Entities.SetSlotRole.Builder, null, null, null, null) };
        });
        var threads = new System.Collections.Concurrent.ConcurrentBag<int>();

        var contextThread = await SingleThreadContext.RunAsync(async () =>
        {
            var vm = Create(pooled.Object);
            vm.Sets.CollectionChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
            vm.Slots.CollectionChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
            await vm.OpenSetAsync(setId);
            await vm.PendingSlotLoad;
        });

        Assert.NotEmpty(threads);
        Assert.All(threads, id => Assert.Equal(contextThread, id));
    }

    [Fact]
    public async Task LoadFailure_IsShownAndToasted_NotSwallowed()
    {
        var failing = new Mock<ISetProjectService>();
        failing.Setup(s => s.GetSetsAsync(It.IsAny<bool>())).ThrowsAsync(new InvalidOperationException("db locked"));
        var vm = Create(failing.Object);

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.Contains("db locked", vm.ErrorMessage);
        Assert.Contains(_toasts, t => t.Type == NotificationType.Error && t.Message.Contains("db locked"));
        Assert.False(vm.HasNoSets); // an error is not "you have no sets"
    }
}
