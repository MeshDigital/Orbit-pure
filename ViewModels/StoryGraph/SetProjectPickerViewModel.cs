using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>A row in the Set Project picker.</summary>
public sealed record SetProjectPickerItem(Guid Id, string Name, SetProjectStatus Status, string? EventProfileName);

/// <summary>
/// Slice K5.1: lightweight selection surface bridging the left-nav "Set Projects" button and
/// <see cref="OpenSetProjectsRequestedEvent"/>. Lists every <see cref="SetListEntity"/> with its
/// name, status and event profile; selecting one publishes the event and raises <see cref="Selected"/>.
/// </summary>
public sealed class SetProjectPickerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    public ObservableCollection<SetProjectPickerItem> Items { get; } = new();

    private SetProjectPickerItem? _selectedItem;
    public SetProjectPickerItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }

    /// <summary>Raised with the chosen set id (the window closes on this).</summary>
    public event EventHandler<Guid>? Selected;

    public SetProjectPickerViewModel(IDbContextFactory<AppDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        SelectCommand = ReactiveCommand.Create(Select);
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var eventList = await db.EventProfiles.ToListAsync().ConfigureAwait(false);
        var eventNames = eventList.ToDictionary(e => e.Id, e => e.Name);

        var sets = await db.SetLists
            .OrderByDescending(s => s.LastModifiedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        Items.Clear();
        foreach (var set in sets)
        {
            var eventName = set.EventProfileId is Guid epId && eventNames.TryGetValue(epId, out var name)
                ? name
                : null;
            Items.Add(new SetProjectPickerItem(set.Id, set.Name, set.Status, eventName));
        }
    }

    private void Select()
    {
        if (SelectedItem is null)
            return;
        _eventBus.Publish(new OpenSetProjectsRequestedEvent(SelectedItem.Id));
        Selected?.Invoke(this, SelectedItem.Id);
    }
}
