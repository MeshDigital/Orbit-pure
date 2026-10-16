using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>Pure presentation logic behind the Search page (no scheduler / DI needed).</summary>
public class SearchPagePresentationTests
{
    [Fact]
    public void TokenParser_ExtractsCombinedFormatsAndBitrate()
    {
        var parsed = SearchQueryTokenParser.Parse("Bicep Glue flac wav 256+");

        Assert.Equal("Bicep Glue", parsed.Query);
        Assert.NotNull(parsed.Formats);
        Assert.True(parsed.Formats!.SetEquals(new[] { "FLAC", "WAV" }));
        Assert.Equal(256, parsed.MinBitrate);
    }

    [Fact]
    public void TokenParser_LeavesPlainQueryAlone()
    {
        var parsed = SearchQueryTokenParser.Parse("Artist - Title (Extended Mix)");

        Assert.Equal("Artist - Title (Extended Mix)", parsed.Query);
        Assert.Null(parsed.Formats);
        Assert.Null(parsed.MinBitrate);
    }

    [Fact]
    public void TokenParser_GreaterThanSyntax_SetsMinBitrate()
    {
        var parsed = SearchQueryTokenParser.Parse("track >320");
        Assert.Equal("track", parsed.Query);
        Assert.Equal(320, parsed.MinBitrate);
    }

    [Fact]
    public void FilterViewModel_DefaultsHaveNoActiveFilters_AndClearRestoresThem()
    {
        var filters = new SearchFilterViewModel();
        Assert.False(filters.HasActiveFilters);

        filters.FilterMp3 = false;
        filters.MinBitrate = 192;
        filters.IsStrict = true;
        filters.HideSuspects = false;
        filters.UseHighReliability = true;

        Assert.Equal(5, filters.ActiveFilterCount);
        Assert.True(filters.HasActiveFilters);

        filters.ResetToDefaults();

        Assert.False(filters.HasActiveFilters);
        Assert.True(filters.FilterMp3);
        Assert.Equal(SearchFilterViewModel.DefaultMinBitrate, filters.MinBitrate);
        Assert.Equal(BouncerMode.Standard, filters.BouncerMode);
        Assert.True(filters.HideSuspects);
    }

    [Fact]
    public void FilterViewModel_MinBitrateCanBeLoweredBelow320()
    {
        // Used to be clamped to >= 320, so "Relax filters" (MinBitrate = 0) silently did nothing.
        var filters = new SearchFilterViewModel { MinBitrate = 0 };
        var lowBitrate = new SearchResult(new Track { Filename = "a - b.mp3", Bitrate = 128, Format = "mp3" });

        Assert.Equal(0, filters.MinBitrate);
        Assert.True(filters.GetFilterPredicate()(lowBitrate));
    }

    [Fact]
    public void FilterViewModel_HiddenReasonAgreesWithPredicate()
    {
        var filters = new SearchFilterViewModel { UseHighReliability = true };
        var deepQueue = new SearchResult(new Track { Filename = "a - b.mp3", Bitrate = 320, Format = "mp3", QueueLength = 80 });

        Assert.False(filters.GetFilterPredicate()(deepQueue));
        Assert.False(filters.IsMatch(deepQueue));
        Assert.Contains("queue", filters.GetHiddenReason(deepQueue), System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(256_000, "250 KB/s")]
    [InlineData(3 * 1024 * 1024, "3.0 MB/s")]
    public void FormatSpeed_TreatsSoulseekSpeedAsBytesPerSecond(int bytesPerSecond, string expected)
    {
        Assert.Equal(expected, AnalyzedSearchResultViewModel.FormatSpeed(bytesPerSecond));
    }

    [Fact]
    public void ResultRow_QualityTierFlags()
    {
        var flac = new AnalyzedSearchResultViewModel(new SearchResult(new Track { Filename = "x.flac", Bitrate = 900 }));
        var lowMp3 = new AnalyzedSearchResultViewModel(new SearchResult(new Track { Filename = "x.mp3", Bitrate = 192 }));
        var unknown = new AnalyzedSearchResultViewModel(new SearchResult(new Track { Filename = "x.mp3", Bitrate = 0 }));

        Assert.True(flac.IsLossless);
        Assert.False(flac.IsLowQuality);
        Assert.True(lowMp3.IsLowQuality);
        Assert.False(unknown.IsLowQuality);
        Assert.Equal("—", unknown.BitrateDisplay);
    }

    [Fact]
    public void ResultRow_RaisesMatchChangesWhenRankIsRefreshed()
    {
        var track = new Track { Filename = "x.mp3", CurrentRank = 40 };
        var row = new AnalyzedSearchResultViewModel(new SearchResult(track));
        var raised = false;
        row.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(AnalyzedSearchResultViewModel.MatchConfidence);

        track.CurrentRank = 90;
        row.RawResult.RefreshRank();

        Assert.True(raised);
        Assert.Equal(90, row.MatchConfidence);
    }

    [Theory]
    [InlineData(PlaylistTrackState.Queued, TrackStatus.Pending)]
    [InlineData(PlaylistTrackState.Downloading, TrackStatus.Pending)]
    [InlineData(PlaylistTrackState.Completed, TrackStatus.Downloaded)]
    [InlineData(PlaylistTrackState.Failed, TrackStatus.Failed)]
    [InlineData(PlaylistTrackState.Cancelled, TrackStatus.Missing)]
    public void MapDownloadState_MapsInFlightStatesToQueued(PlaylistTrackState state, TrackStatus expected)
    {
        Assert.Equal(expected, SearchViewModel.MapDownloadState(state));
    }

    [Fact]
    public void Comparer_SortsByPeerAvailability()
    {
        var busy = Row("busy", slotFree: false, queue: 0, speed: 9_000_000);
        var freeLongQueue = Row("long", slotFree: true, queue: 30, speed: 100);
        var freeShortQueue = Row("short", slotFree: true, queue: 1, speed: 100);

        var ordered = new[] { busy, freeLongQueue, freeShortQueue }
            .OrderBy(r => r, SearchViewModel.BuildComparer(SearchSortColumn.Peer, descending: true))
            .Select(r => r.User)
            .ToArray();

        Assert.Equal(new[] { "short", "long", "busy" }, ordered);
    }

    [Fact]
    public void Comparer_SortsQualityLosslessFirst()
    {
        var mp3 = new AnalyzedSearchResultViewModel(new SearchResult(new Track { Filename = "a.mp3", Bitrate = 320 }));
        var flac = new AnalyzedSearchResultViewModel(new SearchResult(new Track { Filename = "b.flac", Bitrate = 0 }));

        var first = new[] { mp3, flac }
            .OrderBy(r => r, SearchViewModel.BuildComparer(SearchSortColumn.Quality, descending: true))
            .First();

        Assert.Same(flac, first);
    }

    private static AnalyzedSearchResultViewModel Row(string user, bool slotFree, int queue, int speed) =>
        new(new SearchResult(new Track
        {
            Filename = $"{user}.mp3",
            Username = user,
            HasFreeUploadSlot = slotFree,
            QueueLength = queue,
            UploadSpeed = speed
        }));
}
