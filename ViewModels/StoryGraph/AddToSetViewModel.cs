using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>A set shown in the "Add to Set" picker.</summary>
public sealed record AddToSetItem(Guid Id, string Name, SetProjectStatus Status);

/// <summary>
/// Lightweight picker for the Library "Add to Set" flow. Lists the most recent non-archived sets
/// plus a "New Set…" action; choosing one appends the track to that set and raises
/// <see cref="Completed"/> with the set name.
/// </summary>
public sealed class AddToSetViewModel : ReactiveObject, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
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

    public AddToSetViewModel(IDbContextFactory<AppDbContext> dbFactory, string trackUniqueHash)
    {
        _dbFactory = dbFactory;
        _trackUniqueHash = trackUniqueHash;
        AddCommand = ReactiveCommand.CreateFromTask(AddAsync);
        NewSetCommand = ReactiveCommand.CreateFromTask(NewSetAsync);
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var sets = await db.SetLists
            .Where(s => s.Status != SetProjectStatus.Archived)
            .OrderByDescending(s => s.LastModifiedAt)
            .Take(8)
            .ToListAsync()
            .ConfigureAwait(false);

        Items.Clear();
        foreach (var set in sets)
            Items.Add(new AddToSetItem(set.Id, set.Name, set.Status));
    }

    private async Task AddAsync()
    {
        if (SelectedItem is null)
            return;
        await AppendTrackAsync(SelectedItem.Id).ConfigureAwait(false);
        Completed?.Invoke(this, SelectedItem.Name);
    }

    private async Task NewSetAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = new SetListEntity
        {
            Name = $"Set {DateTime.Now:MMM d HH:mm}",
            Status = SetProjectStatus.Draft,
        };
        db.SetLists.Add(set);
        await db.SaveChangesAsync().ConfigureAwait(false);

        await AppendTrackAsync(set.Id).ConfigureAwait(false);
        Completed?.Invoke(this, set.Name);
    }

    private async Task AppendTrackAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);

        if (set is null)
            return;

        var position = set.Tracks.Count == 0 ? 0 : set.Tracks.Max(t => t.Position) + 1;
        db.SetTracks.Add(new SetTrackEntity
        {
            SetListId = setId,
            TrackUniqueHash = _trackUniqueHash,
            Position = position,
        });
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        Completed = null;
        AddCommand.Dispose();
        NewSetCommand.Dispose();
    }
}
