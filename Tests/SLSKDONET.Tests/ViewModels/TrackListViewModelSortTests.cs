using System.Collections.Generic;
using System.Reflection;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Covers TrackListViewModel.ApplyInMemorySort — the in-memory sort path used for smart
/// playlists (the DB-backed VirtualizedTrackCollection path applies the equivalent ORDER BY in
/// SQL instead, covered by TrackRepositorySortTests).
/// </summary>
public class TrackListViewModelSortTests
{
    private static TrackListViewModel CreateSut(TrackSortColumn column, bool descending)
    {
        var sut = (TrackListViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TrackListViewModel));
        SetField(sut, "_sortColumn", column);
        SetField(sut, "_sortDescending", descending);
        return sut;
    }

    private static PlaylistTrackViewModel CreateTrack(string artist, string title, double? bpm, int? durationMs)
        => new(new PlaylistTrack
        {
            Artist = artist,
            Title = title,
            BPM = bpm,
            CanonicalDuration = durationMs
        });

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }

    [Fact]
    public void ApplyInMemorySort_Default_LeavesOrderUnchanged()
    {
        var sut = CreateSut(TrackSortColumn.Default, descending: false);
        var tracks = new List<PlaylistTrackViewModel>
        {
            CreateTrack("Zeta", "Z Track", 128, 200000),
            CreateTrack("Alpha", "A Track", 100, 180000)
        };

        var result = sut.ApplyInMemorySort(tracks);

        Assert.Equal("Zeta", result[0].Artist);
        Assert.Equal("Alpha", result[1].Artist);
    }

    [Fact]
    public void ApplyInMemorySort_Artist_SortsAscendingByArtistThenTitle()
    {
        var sut = CreateSut(TrackSortColumn.Artist, descending: false);
        var tracks = new List<PlaylistTrackViewModel>
        {
            CreateTrack("Zeta", "Z Track", 128, 200000),
            CreateTrack("Alpha", "B Track", 100, 180000),
            CreateTrack("Alpha", "A Track", 100, 180000)
        };

        var result = sut.ApplyInMemorySort(tracks);

        Assert.Equal(new[] { "A Track", "B Track", "Z Track" }, new[] { result[0].Title, result[1].Title, result[2].Title });
    }

    [Fact]
    public void ApplyInMemorySort_ArtistDescending_ReversesOrder()
    {
        var sut = CreateSut(TrackSortColumn.Artist, descending: true);
        var tracks = new List<PlaylistTrackViewModel>
        {
            CreateTrack("Alpha", "A Track", 100, 180000),
            CreateTrack("Zeta", "Z Track", 128, 200000)
        };

        var result = sut.ApplyInMemorySort(tracks);

        Assert.Equal("Zeta", result[0].Artist);
        Assert.Equal("Alpha", result[1].Artist);
    }

    [Fact]
    public void ApplyInMemorySort_Bpm_SortsNumericallyNotAlphabetically()
    {
        var sut = CreateSut(TrackSortColumn.Bpm, descending: false);
        var tracks = new List<PlaylistTrackViewModel>
        {
            CreateTrack("A", "T1", 140, 180000),
            CreateTrack("B", "T2", 90, 180000),
            CreateTrack("C", "T3", 128, 180000)
        };

        var result = sut.ApplyInMemorySort(tracks);

        Assert.Equal(new[] { 90.0, 128.0, 140.0 }, new[] { result[0].BPM, result[1].BPM, result[2].BPM });
    }

    [Fact]
    public void ApplyInMemorySort_Duration_SortsByCanonicalDuration()
    {
        var sut = CreateSut(TrackSortColumn.Duration, descending: false);
        var tracks = new List<PlaylistTrackViewModel>
        {
            CreateTrack("A", "Long", 128, 300000),
            CreateTrack("B", "Short", 128, 90000)
        };

        var result = sut.ApplyInMemorySort(tracks);

        Assert.Equal("Short", result[0].Title);
        Assert.Equal("Long", result[1].Title);
    }
}
