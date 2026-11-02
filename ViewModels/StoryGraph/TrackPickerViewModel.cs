using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.ViewModels.TransitionLab;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>
/// Lightweight single-track picker for the Story Graph "Add Track" flow. Lists library tracks
/// (title/artist/hash) and raises <see cref="Selected"/> with the chosen hash on confirm.
/// </summary>
public sealed class TrackPickerViewModel : ReactiveObject, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ObservableCollection<TrackPickerItem> Items { get; } = new();

    private TrackPickerItem? _selectedItem;
    public TrackPickerItem? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }

    /// <summary>Raised with the chosen track's UniqueHash; the window closes on this.</summary>
    public event EventHandler<string>? Selected;

    public TrackPickerViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        AddCommand = ReactiveCommand.Create(Add);
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var tracks = await db.LibraryEntries
            .OrderBy(e => e.Title)
            .Take(500)
            .ToListAsync()
            .ConfigureAwait(false);

        Items.Clear();
        foreach (var track in tracks)
            Items.Add(new TrackPickerItem(track.UniqueHash, track.Title, track.Artist));
    }

    private void Add()
    {
        if (SelectedItem is null)
            return;
        Selected?.Invoke(this, SelectedItem.TrackUniqueHash);
    }

    public void Dispose()
    {
        Selected = null;
        AddCommand.Dispose();
    }
}
