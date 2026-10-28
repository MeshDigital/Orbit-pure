using System;
using System.Linq;
using System.Text.Json;
using SLSKDONET.Models;
using SLSKDONET.Services.Discovery;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Services.Discovery;

public class PlaylistDiscoveryTests
{
    // Trimmed copies of the two real Beatport __NEXT_DATA__ track shapes (2026-09-28).
    private const string CatalogPage = """
        <html><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"dehydratedState":{"queries":[
          {"queryKey":["tracks"],"state":{"data":{"results":[
            {"id":18712345,"name":"Tesla","mix_name":"Original Mix","slug":"tesla","bpm":174,
             "artists":[{"id":80565,"name":"Atomik Tags"}],
             "genre":{"id":1,"name":"Drum & Bass","slug":"drum-bass"},
             "key":{"camelot_number":9,"camelot_letter":"A"},
             "sample_url":"https://geo-samples.beatport.com/track/x.LOFI.mp3",
             "new_release_date":"2024-04-05","length_ms":323720,
             "release":{"name":"10 Years","image":{"dynamic_uri":"https://img/{w}x{h}/a.jpg"},"label":{"name":"GTA"}},
             "price":{"display":"€1.49"}}
          ]}}}]}}}}</script></html>
        """;

    private const string SearchPage = """
        <html><script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"dehydratedState":{"queries":[
          {"queryKey":["search-tracks"],"state":{"data":{"data":[
            {"track_id":4504190,"track_name":"DIRTY (Metrik Remix)","mix_name":"Metrik Remix","bpm":87,
             "key_name":"G Minor","length":253793,"release_date":"2013-07-08",
             "artists":[{"artist_id":40108,"artist_name":"Metrik","artist_type_name":"Remixer"},
                        {"artist_id":92488,"artist_name":"Dirtyphonics","artist_type_name":"Artist"}],
             "genre":[{"genre_id":1,"genre_name":"Drum & Bass"}],
             "label":{"label_name":"Dim Mak Records"},
             "release":{"release_name":"Irreverence"}}
          ]}}}]}}}}</script></html>
        """;

    [Fact]
    public void Beatport_CatalogShape_IsParsed()
    {
        var (c, refs) = BeatportCatalogClient.ParseTracksWithRefs(CatalogPage).Single();

        Assert.Equal("Tesla", c.Title);
        Assert.Equal("Atomik Tags", c.Artist);
        Assert.Equal(174, c.Bpm);
        Assert.Equal("9A", c.CamelotKey);
        Assert.Equal("Drum & Bass", c.Genre);
        Assert.Equal("GTA", c.Label);
        Assert.Equal("https://img/200x200/a.jpg", c.ImageUrl);
        Assert.Equal("https://www.beatport.com/track/tesla/18712345", c.BeatportUrl);
        Assert.NotNull(c.PreviewUrl);
        Assert.Equal(323.72, c.DurationSeconds!.Value, 2);
        Assert.Equal((80565, "Atomik Tags"), refs.Artists.Single());
        Assert.Equal(1, refs.GenreId);
        Assert.Equal("drum-bass", refs.GenreSlug);
    }

    [Fact]
    public void Beatport_SearchShape_IsParsed_AndRemixerIsNotTheArtist()
    {
        var (c, refs) = BeatportCatalogClient.ParseTracksWithRefs(SearchPage).Single();

        Assert.Equal("DIRTY", c.Title);                // mix moved out of the title
        Assert.Equal("Metrik Remix", c.MixName);
        Assert.Equal("DIRTY (Metrik Remix)", c.SearchTitle);
        Assert.Equal("Dirtyphonics", c.Artist);         // remixer credit stays in the title only
        Assert.Equal("6A", c.CamelotKey);               // G minor
        Assert.Equal(2, refs.Artists.Count);            // both credits kept for id lookups
        Assert.Equal("Drum & Bass", c.Genre);
    }

    [Fact]
    public void Beatport_ChangedPage_ReturnsNothingInsteadOfThrowing()
    {
        Assert.Empty(BeatportCatalogClient.ParseTracks("<html>no data</html>"));
        Assert.Empty(BeatportCatalogClient.ParseTracks("<script id=\"__NEXT_DATA__\" type=\"application/json\">{broken</script>"));
    }

    [Theory]
    [InlineData("A Minor", "8A")]
    [InlineData("Ab Major", "4B")]
    [InlineData("F♯ Minor", "11A")]
    [InlineData("nonsense", null)]
    public void Beatport_KeyNames_ToCamelot(string name, string? expected) =>
        Assert.Equal(expected, BeatportCatalogClient.KeyNameToCamelot(name));

    [Fact]
    public void Deezer_Tracks_AreParsed_WithVersionAsMix()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[{"title":"Miracle (VIP)","title_short":"Miracle","title_version":"(VIP)","link":"https://www.deezer.com/track/1",
                      "duration":300,"preview":"https://cdn/p.mp3","artist":{"name":"Sub Focus"},
                      "album":{"title":"Torus","cover_medium":"https://cdn/c.jpg"}}]}
            """);
        var c = DeezerCatalogClient.ParseTracks(doc.RootElement).Single();

        Assert.Equal("Miracle", c.Title);
        Assert.Equal("VIP", c.MixName);
        Assert.Equal("Miracle (VIP)", c.SearchTitle);
        Assert.Equal("Sub Focus", c.Artist);
        Assert.Equal(300, c.DurationSeconds);
        Assert.Equal("https://cdn/c.jpg", c.ImageUrl);
    }

    // ── Merge / cross-source ────────────────────────────────────────────────

    private static PlaylistDiscoveryProfile DnbProfile()
    {
        var p = new PlaylistDiscoveryProfile { Genre = "Drum & Bass" };
        PlaylistDiscoveryService.ApplyBpmRange(p, new double[] { 172, 174, 174, 175 });
        p.CamelotKeys.Add("8A");
        return p;
    }

    [Fact]
    public void Merge_FoldsTheSameTrackFromBothSources_AndCountsHits()
    {
        var beatport = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", MixName = "Original Mix", Bpm = 174, CamelotKey = "8A", Sources = DiscoverySource.BeatportArtist, SourceStrength = 0.9 };
        var deezer = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", Sources = DiscoverySource.DeezerRelated, SourceStrength = 0.5, PreviewUrl = "p" };

        var merged = PlaylistDiscoveryService.Merge(new[] { beatport, deezer }).Single();

        Assert.True(merged.IsCrossSource);
        Assert.Equal(2, merged.HitCount);
        Assert.Equal(0.9, merged.SourceStrength);
        Assert.Equal("p", merged.PreviewUrl);
        Assert.Equal(174, merged.Bpm);
    }

    [Fact]
    public void Merge_KeepsDifferentVersionsApart()
    {
        var original = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity" };
        var vip = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", MixName = "VIP" };
        Assert.Equal(2, PlaylistDiscoveryService.Merge(new[] { original, vip }).Count);
    }

    [Fact]
    public void Score_PromotesTracksBothSourcesAgreeOn()
    {
        var profile = DnbProfile();
        DiscoveryCandidate Make(DiscoverySource s) => new()
        {
            Artist = "X", Title = "Y", Bpm = 174, CamelotKey = "8A", Genre = "Drum & Bass",
            Sources = s, SourceStrength = 0.7, ViaArtist = "Metrik",
        };
        var single = Make(DiscoverySource.BeatportArtist);
        var agreed = Make(DiscoverySource.BeatportArtist | DiscoverySource.DeezerRelated);

        PlaylistDiscoveryService.Score(single, profile);
        PlaylistDiscoveryService.Score(agreed, profile);

        Assert.True(agreed.Score > single.Score + 0.1, $"{agreed.Score} vs {single.Score}");
        Assert.Equal("Beatport + Deezer agree", agreed.Reasons[0]);
        Assert.DoesNotContain("Beatport + Deezer agree", single.Reasons);
    }

    [Fact]
    public void Score_BeatportLookupAloneIsNotAgreement()
    {
        var c = new DiscoveryCandidate { Artist = "X", Title = "Y", Sources = DiscoverySource.DeezerRelated | DiscoverySource.BeatportMatch, ViaArtist = "Metrik" };
        Assert.False(c.IsCrossSource);
        PlaylistDiscoveryService.Score(c, DnbProfile());
        Assert.DoesNotContain("Beatport + Deezer agree", c.Reasons);
    }

    [Fact]
    public void Score_RepeatedHitsRankHigher()
    {
        var once = new DiscoveryCandidate { Artist = "X", Title = "Y", Sources = DiscoverySource.DeezerRelated, SourceStrength = 0.5, ViaArtist = "A" };
        var thrice = new DiscoveryCandidate { Artist = "X", Title = "Y", Sources = DiscoverySource.DeezerRelated, SourceStrength = 0.5, ViaArtist = "A", HitCount = 3 };
        PlaylistDiscoveryService.Score(once, DnbProfile());
        PlaylistDiscoveryService.Score(thrice, DnbProfile());
        Assert.True(thrice.Score > once.Score);
        Assert.Contains("Suggested 3×", thrice.Reasons);
    }

    [Fact]
    public void Score_HalfTimeBpmCountsAsInRange_AndOffTempoRanksLower()
    {
        var profile = DnbProfile();
        var halfTime = new DiscoveryCandidate { Artist = "X", Title = "Y", Bpm = 87, Genre = "Drum & Bass", Sources = DiscoverySource.BeatportChart, SourceStrength = 0.5 };
        var house = new DiscoveryCandidate { Artist = "X", Title = "Z", Bpm = 124, Genre = "House", Sources = DiscoverySource.BeatportChart, SourceStrength = 0.5 };

        PlaylistDiscoveryService.Score(halfTime, profile);
        PlaylistDiscoveryService.Score(house, profile);

        Assert.Contains("174 BPM", halfTime.Reasons);
        Assert.True(halfTime.Score > house.Score + 0.3);
        Assert.InRange(halfTime.Score, 0, 1);
    }

    [Fact]
    public void Owned_MatchesAcrossDefaultMixTagsAndFeatures_ButNotOtherVersions()
    {
        var owned = new OwnedTrackIndex();
        owned.Add("Sub Focus, Wilkinson", "Illuminate (Original Mix)");

        Assert.True(owned.Contains(new DiscoveryCandidate { Artist = "Sub Focus", Title = "Illuminate" }));
        Assert.True(owned.Contains(new DiscoveryCandidate { Artist = "Wilkinson", Title = "Illuminate", MixName = "Original Mix" }));
        Assert.True(owned.Contains(new DiscoveryCandidate { Artist = "Sub Focus", Title = "Illuminate", MixName = "Extended Mix" }));
        Assert.False(owned.Contains(new DiscoveryCandidate { Artist = "Sub Focus", Title = "Illuminate", MixName = "VIP" }));
    }

    // Real shapes from the library (2026-09-28 sample of 3,485 files / 7,567 playlist rows).
    [Theory]
    [InlineData("Billain", "Feed For Speed - Original Mix", "Billain", "Feed For Speed", null)]
    [InlineData("Rafa Barrios", "Suazer - Extended Mix", "Rafa Barrios", "Suazer", "Original Mix")]
    [InlineData("WING", "Dopamine - DnB Remix", "WING", "Dopamine", "DnB Remix")]
    [InlineData("Natty Lou", "Voltaic - Involver Remix", "Natty Lou", "Voltaic", "Involver Remix")]
    [InlineData("Cloonee & Prospa", "Free Your Mind (Sub Focus Remix)", "Prospa, Cloonee", "Free Your Mind", "Sub Focus Remix")]
    [InlineData("Sub Focus & Wilkinson", "Illuminate", "Sub Focus", "Illuminate", null)]
    [InlineData("Camo & Krooked & Mefjus", "Sientelo", "Camo & Krooked, Mefjus", "Sientelo", "Original Mix")]
    [InlineData("Vintage Culture, Maverick Sabre & Tom Breu", "Weak", "Vintage Culture", "Weak", "Extended Mix")]
    [InlineData("Unknown Artist", "Metrik - Gravity", "Metrik", "Gravity", null)]
    [InlineData("Durdenhauer", "Praise The Lord (Da Shine) (feat. Skepta) - Durdenhauer Edit", "Durdenhauer", "Praise The Lord (Da Shine)", "Durdenhauer Edit")]
    // Misses found by the real-library check (OwnedTrackIndexLibraryCheck):
    [InlineData("Sub Focus", "Elevate (SOTA Remix)", "Sub Focus, Sota", "Elevate", "SOTA Extended Mix")]
    [InlineData("Hannah Boleyn", "Teardrop - Friction & Subsonic Remix", "Hannah Boleyn", "Teardrop", "Friction & Subsonic Extended Remix")]
    [InlineData("Sub Focus & Dimension", "Desire", "Sub Focus", "Desire", "Sub Focus x Dimension")]
    [InlineData("Sub Focus & Dimension", "Ready to Fly", "Sub Focus", "Ready To Fly", "Sub Focus & Dimension")]
    public void Owned_RecognisesLibraryFormats(string libArtist, string libTitle, string artist, string title, string? mix)
    {
        var owned = new OwnedTrackIndex();
        owned.Add(libArtist, libTitle);
        Assert.True(owned.Contains(new DiscoveryCandidate { Artist = artist, Title = title, MixName = mix }));
    }

    [Theory]
    [InlineData("Metrik", "Gravity", "Metrik", "Gravity", "VIP")]          // other version
    [InlineData("Metrik", "Gravity", "Hybrid Minds", "Gravity", null)]     // same title, other artist
    [InlineData("Metrik", "Dopamine - DnB Remix", "Metrik", "Dopamine", null)] // remix owned, original isn't
    [InlineData("Sub Focus", "Elevate (SOTA Remix)", "Sub Focus", "Elevate", "Alok Extended Mix")]   // other remixer
    [InlineData("Camo & Krooked", "Aurora", "Camo & Krooked", "Aurora", "Red Bull Symphonic 2025")] // a live/orchestral version
    [InlineData("Kneecap & Sub Focus", "No Comment (Instrumental)", "Sub Focus", "No Comment", null)]  // vocal vs instrumental
    public void Owned_DoesNotHideDifferentTracks(string libArtist, string libTitle, string artist, string title, string? mix)
    {
        var owned = new OwnedTrackIndex();
        owned.Add(libArtist, libTitle);
        Assert.False(owned.Contains(new DiscoveryCandidate { Artist = artist, Title = title, MixName = mix }));
    }

    [Fact]
    public void FindSameTrack_AndApplyBeatportMatch_EnrichWithoutClaimingASuggestion()
    {
        var deezer = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", Sources = DiscoverySource.DeezerRelated };
        var results = new[]
        {
            new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", MixName = "VIP", Bpm = 170 },
            new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", MixName = "Original Mix", Bpm = 174, CamelotKey = "8A", BeatportUrl = "u" },
        };

        var match = PlaylistDiscoveryService.FindSameTrack(deezer, results);
        Assert.NotNull(match);
        PlaylistDiscoveryService.ApplyBeatportMatch(deezer, match!);

        Assert.Equal(174, deezer.Bpm);
        Assert.Equal("u", deezer.BeatportUrl);
        Assert.True(deezer.Sources.HasFlag(DiscoverySource.BeatportMatch));
        Assert.False(deezer.IsCrossSource);
    }

    [Fact]
    public void UnwantedVersions_AreFiltered()
    {
        Assert.True(PlaylistDiscoveryService.IsUnwantedVersion(new DiscoveryCandidate { MixName = "DJ Mix" }));
        Assert.True(PlaylistDiscoveryService.IsUnwantedVersion(new DiscoveryCandidate { DurationSeconds = 3600 }));
        Assert.False(PlaylistDiscoveryService.IsUnwantedVersion(new DiscoveryCandidate { MixName = "Extended Mix", DurationSeconds = 360 }));
    }

    [Fact]
    public void BuildTrack_MakesAMissingPlaylistRowReadyForSoulseek()
    {
        var id = Guid.NewGuid();
        var c = new DiscoveryCandidate { Artist = "Metrik", Title = "Gravity", MixName = "VIP", Bpm = 174, CamelotKey = "8A", DurationSeconds = 301.5, Label = "Hospital" };

        var t = PlaylistDiscoveryViewModel.BuildTrack(c, id, "Liquid");

        Assert.Equal(id, t.PlaylistId);
        Assert.Equal("Gravity (VIP)", t.Title);
        Assert.Equal(SLSKDONET.Utils.TrackHashUtil.Compute("Metrik", "Gravity (VIP)"), t.TrackUniqueHash);
        Assert.Equal(TrackStatus.Missing, t.Status);
        Assert.Equal(301500, t.CanonicalDuration);
        Assert.Equal(0, t.Priority);
        Assert.Equal("Liquid", t.SourcePlaylistName);
    }
}
