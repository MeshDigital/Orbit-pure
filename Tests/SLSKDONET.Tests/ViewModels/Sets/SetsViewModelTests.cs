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
