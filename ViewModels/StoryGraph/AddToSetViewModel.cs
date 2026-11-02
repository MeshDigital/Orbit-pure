using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>A set shown in the "Add to Set" picker.</summary>
public sealed record AddToSetItem(Guid Id, string Name, SetProjectStatus Status);

/// <summary>
/// Lightweight picker for the Library "Add to Set" flow. Lists the most recent non-archived sets
/// plus a "New Set…" action; choosing one appends the track to that set and raises
/// <see cref="Completed"/> with the set name. Failures are toasted, never swallowed.
/// </summary>
public sealed class AddToSetViewModel : ReactiveObject, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISetProjectService _sets;
    private readonly INotificationService? _notifications;
    private readonly string _trackUniqueHash;

    public ObservableCollection<AddToSetItem> Items { get; } = new();

    private AddToSetItem? _selectedItem;
    public AddToSetItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> NewSetCommand { get; }

    /// <summary>Raised with the set name the track was added to (the window closes on this).</summary>
    public event EventHandler<string>? Completed;

    public AddToSetViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ISetProjectService sets,
        string trackUniqueHash,
        INotificationService? notifications = null)
    {
        _dbFactory = dbFactory;
        _sets = sets;
        _trackUniqueHash = trackUniqueHash;
        _notifications = notifications;
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync);
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
    }

    public async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var sets = await db.SetLists
                .AsNoTracking()
                .Where(s => s.Status != SetProjectStatus.Archived)
                .OrderByDescending(s => s.LastModifiedAt)
                .Take(8)
                .ToListAsync();

            Items.Clear();
            foreach (var set in sets)
                Items.Add(new AddToSetItem(set.Id, set.Name, set.Status));
        }
        catch (Exception ex)
        {
            Fail("Couldn't load your sets", ex);
        }
    }

    private async Task AddAsync()
    {
        if (SelectedItem is not { } target)
            return;
        try
        {
            if (await _sets.AppendTrackAsync(target.Id, _trackUniqueHash) is null)
            {
                _notifications?.Show("Add to Set", $"\"{target.Name}\" no longer exists.", NotificationType.Warning);
                return;
            }
            Completed?.Invoke(this, target.Name);
        }
        catch (Exception ex)
        {
            Fail("Couldn't add the track", ex);
        }
    }

    private async Task NewSetAsync()
    {
        try
        {
            var set = await _sets.CreateSetAsync();
            await _sets.AppendTrackAsync(set.Id, _trackUniqueHash);
            Completed?.Invoke(this, set.Name);
        }
        catch (Exception ex)
        {
            Fail("Couldn't create the set", ex);
        }
    }

    private void Fail(string message, Exception ex)
        => _notifications?.Show("Add to Set", $"{message}: {ex.Message}", NotificationType.Error);

    public void Dispose()
    {
        Completed = null;
        AddCommand.Dispose();
        NewSetCommand.Dispose();
    }
}
