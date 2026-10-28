using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class TrackListSelectionUpdateTests
{
    [Fact]
    public void ReplaceSelectionContents_SuppressesPerRowHandlerAndRestoresIt()
    {
        var original = CreateTrack("Original");
        var replacement = new[] { CreateTrack("A"), CreateTrack("B"), CreateTrack("C") };
        var selected = new ObservableCollection<PlaylistTrackViewModel> { original };
        var notificationCount = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => notificationCount++;
        selected.CollectionChanged += handler;

        TrackListViewModel.ReplaceSelectionContents(selected, replacement, handler);

        Assert.Equal(0, notificationCount);
        Assert.Equal(replacement, selected);

        selected.Add(CreateTrack("Later"));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public void ReplaceSelectionContents_RestoresHandlerWhenReplacementFails()
    {
        var selected = new ObservableCollection<PlaylistTrackViewModel>();
        var notificationCount = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => notificationCount++;
        selected.CollectionChanged += handler;

        Assert.Throws<InvalidOperationException>(() =>
            TrackListViewModel.ReplaceSelectionContents(
                selected, ThrowAfterFirst(CreateTrack("A")), handler));

        selected.Add(CreateTrack("Later"));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public void ClearSelectionContents_ClearsRowFlagsAndDoesNotDuplicateHandler()
    {
        var first = CreateTrack("A");
        var second = CreateTrack("B");
        first.IsSelected = true;
        second.IsSelected = true;
        var selected = new ObservableCollection<PlaylistTrackViewModel> { first, second };
        var notificationCount = 0;
        NotifyCollectionChangedEventHandler handler = (_, _) => notificationCount++;
        selected.CollectionChanged += handler;

        TrackListViewModel.ClearSelectionContents(selected, handler);

        Assert.Empty(selected);
        Assert.False(first.IsSelected);
        Assert.False(second.IsSelected);

        selected.Add(CreateTrack("Later"));
        Assert.Equal(1, notificationCount);
    }

    private static IEnumerable<PlaylistTrackViewModel> ThrowAfterFirst(PlaylistTrackViewModel track)
    {
        yield return track;
        throw new InvalidOperationException("Simulated enumeration failure.");
    }

    private static PlaylistTrackViewModel CreateTrack(string title) => new(new PlaylistTrack
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Test"
    });
}
