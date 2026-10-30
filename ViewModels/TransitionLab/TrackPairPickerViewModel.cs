using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.TransitionLab;

/// <summary>A library track shown in the track-pair picker.</summary>
public sealed record TrackPickerItem(string TrackUniqueHash, string Title, string Artist);

/// <summary>
/// Slice K6.1: lightweight modal for choosing the outgoing (source) and incoming (target) tracks for
/// the Transition Lab. On confirm it publishes <see cref="OpenTransitionLabRequestedEvent"/>.
/// </summary>
public sealed class TrackPairPickerViewModel : ReactiveObject, IDisposable
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    public ObservableCollection<TrackPickerItem> AvailableTracks { get; } = new();

    private TrackPickerItem? _selectedSource;
    public TrackPickerItem? SelectedSource { get => _selectedSource; set => this.RaiseAndSetIfChanged(ref _selectedSource, value); }

    private TrackPickerItem? _selectedTarget;
    public TrackPickerItem? SelectedTarget { get => _selectedTarget; set => this.RaiseAndSetIfChanged(ref _selectedTarget, value); }

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }

    /// <summary>Raised with (source, target) when confirmed; the window closes on this.</summary>
    public event EventHandler<(string Source, string Target)>? Confirmed;

    public TrackPairPickerViewModel(IDbContextFactory<AppDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        ConfirmCommand = ReactiveCommand.Create(Confirm);
    }

    public async Task LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var tracks = await db.LibraryEntries
            .OrderBy(e => e.Title)
            .Take(500)
            .ToListAsync()
            .ConfigureAwait(false);

        AvailableTracks.Clear();
        foreach (var track in tracks)
            AvailableTracks.Add(new TrackPickerItem(track.UniqueHash, track.Title, track.Artist));
    }

    private void Confirm()
    {
        if (SelectedSource is null || SelectedTarget is null)
            return;

        _eventBus.Publish(new OpenTransitionLabRequestedEvent(
            SelectedSource.TrackUniqueHash,
            SelectedTarget.TrackUniqueHash));
        Confirmed?.Invoke(this, (SelectedSource.TrackUniqueHash, SelectedTarget.TrackUniqueHash));
    }

    public void Dispose()
    {
        Confirmed = null;
        ConfirmCommand.Dispose();
    }
}
