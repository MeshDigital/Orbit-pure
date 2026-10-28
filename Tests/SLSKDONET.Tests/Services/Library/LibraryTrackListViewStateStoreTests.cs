using System;
using System.IO;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.Services.Library;

public sealed class LibraryTrackListViewStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "orbit-library-view-state-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsOnePlaylistWithoutLeakingIntoAnother()
    {
        var store = new LibraryTrackListViewStateStore(rootDirectory: _root);
        var first = new LibraryTrackListViewState
        {
            SearchText = "honey",
            FilterArtist = "dj",
            IsFilterAll = false,
            IsFilterDownloaded = true,
            SortColumn = TrackSortColumn.Bpm,
            SortDescending = true,
            ArtistColumnWidth = 218,
            PreparationColumnWidth = 144,
            ScrollOffset = 1260,
        };
        first.SelectedStyles.Add("Drum & Bass");
        first.DuplicateHashes.Add("hash-a");

        await store.SaveAsync("playlist-first", first);
        await store.SaveAsync("playlist-second", new LibraryTrackListViewState());

        var restored = store.Load("playlist-first");
        var other = store.Load("playlist-second");

        Assert.NotNull(restored);
        Assert.Equal("honey", restored.SearchText);
        Assert.Equal("dj", restored.FilterArtist);
        Assert.True(restored.IsFilterDownloaded);
        Assert.Equal(TrackSortColumn.Bpm, restored.SortColumn);
        Assert.True(restored.SortDescending);
        Assert.Equal(218, restored.ArtistColumnWidth);
        Assert.Equal(144, restored.PreparationColumnWidth);
        Assert.Equal(1260, restored.ScrollOffset);
        Assert.Equal(new[] { "Drum & Bass" }, restored.SelectedStyles);
        Assert.Equal(new[] { "hash-a" }, restored.DuplicateHashes);

        Assert.NotNull(other);
        Assert.Equal(string.Empty, other.SearchText);
        Assert.Equal(TrackSortColumn.Default, other.SortColumn);
        Assert.Equal(0, other.ScrollOffset);
        Assert.False(File.Exists(Path.Combine(_root, "playlist-first.json.tmp")));
    }

    [Fact]
    public async Task Load_CorruptJson_UsesDefaultsAndCanBeRepairedByNextSave()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "playlist-broken.json");
        await File.WriteAllTextAsync(path, "{ definitely-not-valid-json");
        var store = new LibraryTrackListViewStateStore(rootDirectory: _root);

        Assert.Null(store.Load("playlist-broken"));

        await store.SaveAsync("playlist-broken", new LibraryTrackListViewState { SearchText = "repaired" });
        Assert.Equal("repaired", store.Load("playlist-broken")?.SearchText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
