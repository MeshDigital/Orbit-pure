using System;
using System.IO;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Services.Discovery;
using Xunit;

namespace SLSKDONET.Tests.Services.Discovery;

public class DiscoveryCacheTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static string[] Tracks(int from, int count) => Enumerable.Range(from, count).Select(i => $"t{i}").ToArray();

    [Fact]
    public void UnchangedPlaylist_IsNotReevaluated()
    {
        var drift = DiscoveryCache.Evaluate(Tracks(0, 40), Tracks(0, 40), Now.AddDays(-1), Now);
        Assert.False(drift.Refresh);
    }

    [Fact]
    public void AFewTracksAddedToABigPlaylist_IsNotReevaluated()
    {
        // 2 of 40 changed (5%) — suggestions still fit.
        var drift = DiscoveryCache.Evaluate(Tracks(0, 40), Tracks(0, 42), Now.AddDays(-1), Now);
        Assert.False(drift.Refresh);
        Assert.Equal(2, drift.Added);
    }

    [Fact]
    public void AQuarterOfThePlaylistChanged_IsReevaluated()
    {
        // 30 → 30 with 8 swapped out: 16 changes, well over 20 %.
        var drift = DiscoveryCache.Evaluate(Tracks(0, 30), Tracks(8, 30), Now.AddDays(-1), Now);
        Assert.True(drift.Refresh);
        Assert.Contains("+8", drift.Reason);
        Assert.Contains("−8", drift.Reason);
    }

    [Fact]
    public void SmallPlaylist_NeedsAtLeastThreeChanges()
    {
        // 1 of 4 added is 25 %, but a single track isn't a direction change.
        Assert.False(DiscoveryCache.Evaluate(Tracks(0, 4), Tracks(0, 5), Now, Now).Refresh);
        Assert.True(DiscoveryCache.Evaluate(Tracks(0, 4), Tracks(0, 7), Now, Now).Refresh);
    }

    [Fact]
    public void TenChangesOnAHugePlaylist_IsReevaluated()
    {
        var drift = DiscoveryCache.Evaluate(Tracks(0, 300), Tracks(0, 310), Now, Now);
        Assert.True(drift.Refresh);
    }

    [Fact]
    public void OldResults_AreReevaluated()
    {
        var drift = DiscoveryCache.Evaluate(Tracks(0, 20), Tracks(0, 20), Now.AddDays(-15), Now);
        Assert.True(drift.Refresh);
        Assert.Contains("15 days", drift.Reason);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSuggestionsReasonsAndDismissals()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"discover-cache-{Guid.NewGuid():N}");
        try
        {
            var cache = new DiscoveryCache(directoryOverride: dir);
            var id = Guid.NewGuid();
            var candidate = new DiscoveryCandidate
            {
                Artist = "Metrik", Title = "Gravity", MixName = "VIP", Bpm = 174, CamelotKey = "8A",
                Sources = DiscoverySource.BeatportArtist | DiscoverySource.DeezerRelated, Score = 0.91, HitCount = 2,
            };
            candidate.Reasons.Add("Beatport + Deezer agree");
            cache.Save(new DiscoveryCacheEntry
            {
                PlaylistId = id, CreatedUtc = Now, Fingerprint = { "a", "b" }, Summary = "Drum & Bass",
                Suggestions = { candidate }, Dismissed = { "x|y" },
            });

            var loaded = cache.Load(id);

            Assert.NotNull(loaded);
            var c = Assert.Single(loaded!.Suggestions);
            Assert.Equal("Gravity (VIP)", c.SearchTitle);
            Assert.True(c.IsCrossSource);
            Assert.Equal(2, c.HitCount);
            Assert.Equal(new[] { "Beatport + Deezer agree" }, c.Reasons);
            Assert.Equal(new[] { "a", "b" }, loaded.Fingerprint);
            Assert.Equal(new[] { "x|y" }, loaded.Dismissed);
            Assert.Null(cache.Load(Guid.NewGuid()));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DamagedFile_IsTreatedAsNoCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"discover-cache-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var id = Guid.NewGuid();
            File.WriteAllText(Path.Combine(dir, $"{id:N}.json"), "{ not json");
            Assert.Null(new DiscoveryCache(directoryOverride: dir).Load(id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SuggestionKey_IgnoresDefaultMixAndFeaturing_ButKeepsVersions()
    {
        var a = DiscoveryCache.SuggestionKey(new DiscoveryCandidate { Artist = "Sub Focus, Wilkinson", Title = "Illuminate", MixName = "Original Mix" });
        var b = DiscoveryCache.SuggestionKey(new DiscoveryCandidate { Artist = "Sub Focus", Title = "Illuminate" });
        var vip = DiscoveryCache.SuggestionKey(new DiscoveryCandidate { Artist = "Sub Focus", Title = "Illuminate", MixName = "VIP" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, vip);
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent_AndFallsBackToArtistTitle()
    {
        var one = new[] { new PlaylistTrack { TrackUniqueHash = "h2" }, new PlaylistTrack { Artist = "A", Title = "T" } };
        var two = new[] { new PlaylistTrack { Artist = "A", Title = "T" }, new PlaylistTrack { TrackUniqueHash = "h2" } };
        Assert.Equal(PlaylistDiscoveryService.Fingerprint(one), PlaylistDiscoveryService.Fingerprint(two));
        Assert.Contains("a-t", PlaylistDiscoveryService.Fingerprint(one));
    }
}
