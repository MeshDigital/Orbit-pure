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
using SLSKDONET.Services.Sets;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>A row in the Set Project picker.</summary>
public sealed record SetProjectPickerItem(Guid Id, string Name, SetProjectStatus Status, string? EventProfileName);

/// <summary>
/// Slice K5.1: lightweight selection surface bridging the left-nav "Set Projects" button and
/// <see cref="OpenSetProjectsRequestedEvent"/>. Lists every <see cref="SetListEntity"/> with its
/// name, status and event profile; selecting one publishes the event and raises <see cref="Selected"/>.
/// </summary>
public sealed class SetProjectPickerViewModel : ReactiveObject, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISetProjectService _sets;
    private readonly IEventBus _eventBus;
    private readonly INotificationService? _notifications;

    public ObservableCollection<SetProjectPickerItem> Items { get; } = new();

    private SetProjectPickerItem? _selectedItem;
    public SetProjectPickerItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    private bool _isLoaded;
    /// <summary>True once loading finished and there are no sets (drives the empty state).</summary>
    public bool IsEmpty => _isLoaded && Items.Count == 0;

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateSetCommand { get; }

    /// <summary>Raised with the chosen set id (the window closes on this).</summary>
    public event EventHandler<Guid>? Selected;

    public SetProjectPickerViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ISetProjectService sets,
        IEventBus eventBus,
        INotificationService? notifications = null)
    {
        _dbFactory = dbFactory;
        _sets = sets;
        _eventBus = eventBus;
        _notifications = notifications;
        SelectCommand = ReactiveCommand.Create(Select);
        CreateSetCommand = ReactiveCommand.CreateFromTask(CreateSetAsync);
    }

    public async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var eventList = await db.EventProfiles.AsNoTracking().ToListAsync();
            var eventNames = eventList.ToDictionary(e => e.Id, e => e.Name);

            var sets = await db.SetLists
                .AsNoTracking()
                .OrderByDescending(s => s.LastModifiedAt)
                .ToListAsync();

            Items.Clear();
            foreach (var set in sets)
            {
                var eventName = set.EventProfileId is Guid epId && eventNames.TryGetValue(epId, out var name)
                    ? name
                    : null;
                Items.Add(new SetProjectPickerItem(set.Id, set.Name, set.Status, eventName));
            }
        }
        catch (Exception ex)
        {
            _notifications?.Show("Set Projects", $"Couldn't load your sets: {ex.Message}", NotificationType.Error);
        }
        finally
        {
            _isLoaded = true;
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    private void Select()
    {
        if (SelectedItem is null)
            return;
        _eventBus.Publish(new OpenSetProjectsRequestedEvent(SelectedItem.Id));
        Selected?.Invoke(this, SelectedItem.Id);
    }

    private async Task CreateSetAsync()
    {
        try
        {
            var set = await _sets.CreateSetAsync();
            _eventBus.Publish(new OpenSetProjectsRequestedEvent(set.Id));
            Selected?.Invoke(this, set.Id);
        }
        catch (Exception ex)
        {
            _notifications?.Show("Set Projects", $"Couldn't create the set: {ex.Message}", NotificationType.Error);
        }
    }

    public void Dispose()
    {
        Selected = null;
        SelectCommand.Dispose();
        CreateSetCommand.Dispose();
    }
}
