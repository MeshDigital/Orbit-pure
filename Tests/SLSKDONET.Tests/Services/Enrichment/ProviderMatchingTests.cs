using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Enrichment.Providers;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

public class ProviderMatchingTests
{
    private const string IsrcTrack = """
        {"id":3135556,"title":"Even If","isrc":"GBCPZ0700001","duration":301,"track_position":4,"release_date":"2007-05-14",
         "artist":{"id":1,"name":"Calibre"},
         "album":{"id":302127,"title":"Overflow","cover_xl":"https://e-cdns-images.dzcdn.net/images/cover/abc/1000x1000-000000-80-0-0.jpg"}}
        """;
    private const string Album = """{"id":302127,"label":"Signature Records","release_date":"2007-05-14","genres":{"data":[{"id":106,"name":"Electro"}]}}""";

    private static DeezerProvider Deezer(FakeHttpHandler handler, IMetadataLookupCache? cache = null) =>
        new(new HttpClient(handler), cache ?? new RateLimitAndBreakerTests.NoCache(), NullLogger<DeezerProvider>.Instance,
            new ProviderRuntime(DeezerProvider.ProviderName, RatePolicy.Unlimited));

    [Fact]
    public async Task Deezer_UsesIsrcFirst_WithoutATextSearch()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/track/isrc:GBCPZ0700001" => FakeHttpHandler.Json(IsrcTrack),
            "/album/302127" => FakeHttpHandler.Json(Album),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var track = new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If", Isrc = "GB-CPZ-07-00001", DurationSeconds = 300 };

        var result = await Deezer(handler).LookupAsync(track, new KnownIds(), CancellationToken.None);

        Assert.Equal(LookupStatus.Matched, result.Status);
        var c = result.Candidate!;
        Assert.Equal("Overflow", c.Album);
        Assert.Equal("Signature Records", c.Label);
        Assert.Equal(2007, c.ReleaseDate!.Value.Year);
        Assert.Contains("Electro", c.Genres);
        Assert.StartsWith("https://e-cdns-images.dzcdn.net/", c.ArtworkUrl);
        Assert.True(c.Confidence >= 0.95);
        Assert.DoesNotContain(handler.Requests, u => u.Contains("/search/"));
    }

    [Fact]
    public async Task Deezer_RejectsSearchHitsForADifferentVersionOrLength()
    {
        // Same artist, but the only hits are the VIP and a 40 s shorter radio edit: no match.
        const string search = """
            {"data":[
              {"id":1,"title":"Even If (VIP)","duration":300,"artist":{"name":"Calibre"},"album":{"id":9,"title":"X"}},
              {"id":2,"title":"Even If","duration":260,"artist":{"name":"Calibre"},"album":{"id":9,"title":"X"}},
              {"id":3,"title":"Even If","duration":300,"artist":{"name":"Someone Else Entirely"},"album":{"id":9,"title":"X"}}
            ]}
            """;
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/search")
            ? FakeHttpHandler.Json(search) : new HttpResponseMessage(HttpStatusCode.NotFound));
        var track = new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If", DurationSeconds = 301 };

        var result = await Deezer(handler).LookupAsync(track, new KnownIds(), CancellationToken.None);

        Assert.Equal(LookupStatus.NoMatch, result.Status);
        Assert.Single(handler.Requests); // no track/album follow-up for a rejected hit
    }

    [Fact]
    public async Task Deezer_SecondRun_IsServedFromTheLookupCache()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/track/isrc:GBCPZ0700001" => FakeHttpHandler.Json(IsrcTrack),
            "/album/302127" => FakeHttpHandler.Json(Album),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var cache = new RateLimitAndBreakerTests.MemoryCache();
        var deezer = Deezer(handler, cache);
        var track = new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If", Isrc = "GBCPZ0700001" };

        await deezer.LookupAsync(track, new KnownIds(), CancellationToken.None);
        var afterFirst = handler.Requests.Count;
        var again = await deezer.LookupAsync(track, new KnownIds(), CancellationToken.None);

        Assert.Equal(LookupStatus.Matched, again.Status);
        Assert.Equal(afterFirst, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Calibre", "Even If", 300, "Calibre", "Even If (Original Mix)", 302, true)]
    [InlineData("Calibre feat. DRS", "Even If", 300, "Calibre", "Even If", 300, true)]
    [InlineData("Calibre", "Even If", 300, "Calibre", "Even If (VIP)", 300, false)]
    [InlineData("Calibre", "Even If", 300, "Calibre", "Even If", 330, false)]
    [InlineData("Calibre", "Even If", 300, "dBridge", "Even If", 300, false)]
    public void MatchScore_IsStrict(string wa, string wt, double ws, string ga, string gt, double gs, bool accept)
    {
        var score = EnrichmentMatch.Score(wa, wt, ws, ga, gt, gs);
        Assert.Equal(accept, EnrichmentMatch.Accept(score));
    }

    [Fact]
    public async Task MusicBrainz_SendsTheApplicationUserAgent_AndReadsLabelFromRelease()
    {
        string? userAgent = null;
        var handler = new FakeHttpHandler(req =>
        {
            userAgent = req.Headers.UserAgent.ToString();
            return req.RequestUri!.AbsolutePath switch
            {
                "/ws/2/isrc/GBCPZ0700001" => FakeHttpHandler.Json("""
                    {"recordings":[{"id":"rec-1","title":"Even If","length":301000,
                      "artist-credit":[{"name":"Calibre"}],
                      "releases":[{"id":"rel-1","title":"Overflow","date":"2007-05-14","status":"Official"}]}]}
                    """),
                "/ws/2/release/rel-1" => FakeHttpHandler.Json("""
                    {"id":"rel-1","label-info":[{"catalog-number":"SIGLP001","label":{"name":"Signature Records"}}],
                     "cover-art-archive":{"front":true},"genres":[{"name":"drum and bass","count":3}]}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var mb = new MusicBrainzProvider(new HttpClient(handler), new RateLimitAndBreakerTests.NoCache(),
            NullLogger<MusicBrainzProvider>.Instance, new ProviderRuntime(MusicBrainzProvider.ProviderName, RatePolicy.Unlimited));

        var result = await mb.LookupAsync(new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If", Isrc = "GBCPZ0700001" },
            new KnownIds(), CancellationToken.None);

        Assert.Equal(LookupStatus.Matched, result.Status);
        Assert.Equal("Signature Records", result.Candidate!.Label);
        Assert.Equal("SIGLP001", result.Candidate.CatalogNumber);
        Assert.Equal("rec-1", result.Candidate.MusicBrainzRecordingId);
        Assert.Equal(MusicBrainzProvider.CoverArtUrl("rel-1"), result.Candidate.ArtworkUrl);
        Assert.StartsWith("ORBIT/", userAgent);
        Assert.Contains("github.com/MeshDigital/ORBIT", userAgent);
    }

    [Fact]
    public void Discogs_ParsesStylesAndRejectsOtherArtists()
    {
        const string body = """
            {"results":[
              {"title":"Somebody Else - Overflow","style":["House"],"genre":["Electronic"]},
              {"title":"Calibre - Overflow","year":"2007","label":["Signature Records"],"catno":"SIGLP001",
               "style":["Drum n Bass","Liquid Funk"],"genre":["Electronic"],"cover_image":"https://i.discogs.com/x.jpg"}
            ]}
            """;
        var c = DiscogsProvider.Parse(body, new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If" })!;
        Assert.Equal(new[] { "Drum n Bass", "Liquid Funk" }, c.Styles);
        Assert.Equal("Signature Records", c.Label);
        Assert.Equal("Drum n Bass, Liquid Funk, Electronic", MetadataMerger.GenreText(c));
    }

    [Fact]
    public void AcoustId_AcceptsOnlyHighScores()
    {
        const string body = """
            {"status":"ok","results":[
              {"score":0.62,"recordings":[{"id":"r-low","title":"Wrong","artists":[{"name":"Nobody"}]}]},
              {"score":0.93,"recordings":[{"id":"r-hi","title":"Even If","artists":[{"name":"Calibre"}],"releasegroups":[{"title":"Overflow"}]}]}
            ]}
            """;
        var c = AcoustIdProvider.Parse(body)!;
        Assert.Equal("Calibre", c.Artist);
        Assert.Equal("r-hi", c.MusicBrainzRecordingId);
        Assert.Null(AcoustIdProvider.Parse("""{"status":"ok","results":[{"score":0.5,"recordings":[{"title":"x","artists":[{"name":"y"}]}]}]}"""));
    }

    [Fact]
    public void AcoustId_WithoutFpcalc_ReportsNeedsTool()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "keys-" + Guid.NewGuid().ToString("N"));
        var keys = new MetadataCredentialStore(NullLogger<MetadataCredentialStore>.Instance, dir);
        var provider = new AcoustIdProvider(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            new RateLimitAndBreakerTests.NoCache(), keys, NullLogger<AcoustIdProvider>.Instance, locateFpcalc: () => null);
        Assert.Equal(ProviderAvailability.NeedsKey, provider.GetAvailability());
        if (keys.IsSecureStorageAvailable)
        {
            Assert.True(keys.Set(MetadataCredentialStore.AcoustIdKey, "abc123"));
            Assert.Equal(ProviderAvailability.NeedsTool, provider.GetAvailability());
            // The key is stored encrypted, not as plain text.
            var raw = System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, "metadata_keys.dat"));
            Assert.DoesNotContain("abc123", System.Text.Encoding.UTF8.GetString(raw));
            Assert.Equal("abc123", new MetadataCredentialStore(NullLogger<MetadataCredentialStore>.Instance, dir).Get(MetadataCredentialStore.AcoustIdKey));
        }
        try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { /* temp */ }
    }
}
