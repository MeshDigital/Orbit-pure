using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

public class MetadataMergerTests
{
    private static EnrichmentTrack Track() => new() { Hash = "h", Artist = "Calibre", Title = "Even If", FilePath = @"C:\m\x.flac" };

    [Fact]
    public void NeverWritesAnalysisBpmKey_AndWebHintsAreDropped()
    {
        var track = Track();
        var patch = new MetadataPatch("h");
        var c = new MetadataCandidate { BpmHint = 87, KeyHint = "3A", Album = "Overflow", Confidence = 0.9 };

        MetadataMerger.Apply(track, new Dictionary<string, MetadataProvenanceEntity>(), c, "beatport", patch);

        Assert.Null(patch.TagBpm);       // a web BPM is never stored anywhere
        Assert.Equal("Overflow", patch.Album);
        Assert.DoesNotContain(MetadataMerger.Fields.TagBpm, patch.Provenance.Keys);
        // MetadataPatch has no BPM/Key/Energy fields at all: the writer cannot touch analysis data.
        Assert.Null(typeof(MetadataPatch).GetProperty("BPM"));
        Assert.Null(typeof(MetadataPatch).GetProperty("MusicalKey"));
        Assert.Null(typeof(MetadataPatch).GetProperty("Energy"));
    }

    [Fact]
    public void FileTagBpm_FillsOnlyTheEmptyTagBpmSideColumn()
    {
        var track = Track();
        var patch = new MetadataPatch("h");
        MetadataMerger.Apply(track, new Dictionary<string, MetadataProvenanceEntity>(), new MetadataCandidate { BpmHint = 174 }, MetadataMerger.TagsSource, patch);
        Assert.Equal(174, patch.TagBpm);

        var already = Track();
        already.TagBpm = 172;
        var patch2 = new MetadataPatch("h");
        MetadataMerger.Apply(already, new Dictionary<string, MetadataProvenanceEntity>(), new MetadataCandidate { BpmHint = 174 }, MetadataMerger.TagsSource, patch2);
        Assert.Null(patch2.TagBpm);
    }

    [Fact]
    public void UserEditedFields_AreNeverOverwritten_EvenWhenEmpty()
    {
        var track = Track();
        track.Label = null;
        var provenance = new Dictionary<string, MetadataProvenanceEntity>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataMerger.Fields.Label] = new() { TrackUniqueHash = "h", Field = "label", Source = MetadataMerger.UserSource },
            [MetadataMerger.Fields.Genre] = new() { TrackUniqueHash = "h", Field = "genre", Source = MetadataMerger.UserSource },
        };
        track.Genres = "My Own Genre";
        var patch = new MetadataPatch("h");
        var c = new MetadataCandidate { Label = "Signature", Confidence = 1.0 };
        c.Styles.Add("Liquid Funk");

        MetadataMerger.Apply(track, provenance, c, "discogs", patch);

        Assert.Null(patch.Label);
        Assert.Null(patch.Genres);
        Assert.Equal("My Own Genre", track.Genres);
    }

    [Fact]
    public void FilledFields_AreKept_FirstProviderWins()
    {
        var track = Track();
        track.Album = "From the file";
        var provenance = new Dictionary<string, MetadataProvenanceEntity>(StringComparer.OrdinalIgnoreCase);
        var patch = new MetadataPatch("h");

        MetadataMerger.Apply(track, provenance, new MetadataCandidate { Album = "Deezer album", Label = "Deezer label", Confidence = 0.9 }, "deezer", patch);
        MetadataMerger.Apply(track, provenance, new MetadataCandidate { Label = "MB label", Confidence = 0.99 }, "musicbrainz", patch);

        Assert.Null(patch.Album);
        Assert.Equal("Deezer label", patch.Label);
        Assert.Equal("deezer", patch.Provenance["label"].Source);
    }

    [Fact]
    public void DiscogsStyles_ReplaceOnlyABroadGenreAnEarlierProviderWrote()
    {
        var provenance = new Dictionary<string, MetadataProvenanceEntity>(StringComparer.OrdinalIgnoreCase);
        var track = Track();
        var patch = new MetadataPatch("h");
        var deezer = new MetadataCandidate { Confidence = 0.9 };
        deezer.Genres.Add("Dance");
        MetadataMerger.Apply(track, provenance, deezer, "deezer", patch);
        Assert.True(MetadataMerger.IsStyleMissing(track, provenance));

        var discogs = new MetadataCandidate { Confidence = 0.8 };
        discogs.Styles.Add("Liquid Funk");
        discogs.Genres.Add("Electronic");
        MetadataMerger.Apply(track, provenance, discogs, "discogs", patch);
        Assert.Equal("Liquid Funk, Electronic", patch.Genres);

        // A genre from the file's own tags is never replaced.
        var tagged = Track();
        tagged.Genres = "Drum & Bass";
        var tagProv = new Dictionary<string, MetadataProvenanceEntity>(StringComparer.OrdinalIgnoreCase)
        {
            ["genre"] = new() { TrackUniqueHash = "h", Field = "genre", Source = MetadataMerger.TagsSource },
        };
        Assert.False(MetadataMerger.IsStyleMissing(tagged, tagProv));
        var p2 = new MetadataPatch("h");
        MetadataMerger.Apply(tagged, tagProv, discogs, "discogs", p2);
        Assert.Null(p2.Genres);
    }

    [Fact]
    public void Identity_ReplacesOnlyPlaceholders_AtHighConfidence()
    {
        var weak = new EnrichmentTrack { Hash = "h", Artist = "Unknown Artist", Title = "track01", FilePath = @"C:\m\track01.mp3" };
        var patch = new MetadataPatch("h");
        MetadataMerger.Apply(weak, new Dictionary<string, MetadataProvenanceEntity>(),
            new MetadataCandidate { Artist = "Calibre", Title = "Even If", Confidence = 0.8 }, "acoustid", patch);
        Assert.Null(patch.Artist); // below 0.85

        MetadataMerger.Apply(weak, new Dictionary<string, MetadataProvenanceEntity>(),
            new MetadataCandidate { Artist = "Calibre", Title = "Even If", Confidence = 0.93 }, "acoustid", patch);
        Assert.Equal("Calibre", patch.Artist);
        Assert.Equal("Even If", patch.Title);

        var named = Track();
        var p2 = new MetadataPatch("h");
        MetadataMerger.Apply(named, new Dictionary<string, MetadataProvenanceEntity>(),
            new MetadataCandidate { Artist = "Someone", Title = "Else", Confidence = 0.99 }, "acoustid", p2);
        Assert.Null(p2.Artist);
        Assert.Null(p2.Title);
    }
}

[Collection(NonParallelCollection.Name)]
public sealed class MetadataEnrichmentOrchestratorTests : IDisposable
{
    private readonly TempLibraryDb _db = new();
    private readonly EventBusService _bus = new();
    private readonly ConcurrentQueue<TracksEnrichedEvent> _enriched = new();
    private readonly IDisposable _sub;

    public MetadataEnrichmentOrchestratorTests()
    {
        _sub = _bus.GetEvent<TracksEnrichedEvent>().Subscribe(e => _enriched.Enqueue(e));
    }

    public void Dispose()
    {
        _sub.Dispose();
        _bus.Dispose();
        _db.Dispose();
    }

    private EnrichmentStore Store() => new(_db.Factory, NullLogger<EnrichmentStore>.Instance);

    private MetadataEnrichmentOrchestrator Create(EnrichmentStore store, IEnumerable<IMetadataProvider> providers, ArtworkCacheService? artwork = null,
        Func<bool>? busy = null) =>
        new(_db.Factory, store, providers, _bus, NullLogger<MetadataEnrichmentOrchestrator>.Instance,
            () => EnrichmentSettingsFor.Providers(providers.Select(p => p.Name).ToArray()), artwork,
            busy ?? (() => false), () => false, workers: 2, busyPoll: TimeSpan.FromMilliseconds(20), startupResumeDelay: null);

    private static MetadataCandidate Full(string label = "Signature")
    {
        var c = new MetadataCandidate { Album = "Overflow", Label = label, ReleaseDate = new DateTime(2007, 1, 1), Isrc = "GBCPZ0700001", ArtworkUrl = "C:/art.jpg", Confidence = 0.95 };
        c.Genres.Add("Drum & Bass");
        return c;
    }

    [Fact]
    public async Task CheapestFirst_StopsEarly_OnceEverythingWantedIsFilled()
    {
        var tags = new FakeProvider("tags", MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc | MetadataField.Genre,
            (_, _) => ProviderLookup.Match(Full()));
        var deezer = new FakeProvider("deezer", MetadataField.Artwork | MetadataField.Label, (_, _) => ProviderLookup.Match(Full("Other")));
        var orchestrator = Create(Store(), new IMetadataProvider[] { tags, deezer });

        var outcome = await orchestrator.EnrichTrackAsync(new EnrichmentTrack { Hash = "h1", Artist = "Calibre", Title = "Even If" }, CancellationToken.None);

        Assert.Equal(1, tags.Calls);
        Assert.Equal(0, deezer.Calls); // never asked: nothing was missing any more
        Assert.Equal("Signature", outcome.Patch.Label);
        Assert.Equal(new[] { "tags" }, outcome.ProvidersAsked);
    }

    [Fact]
    public async Task LaterProviders_FillOnlyWhatIsStillMissing_AndSkipIrrelevantOnes()
    {
        var tags = new FakeProvider("tags", MetadataField.Album, (_, _) => ProviderLookup.Match(new MetadataCandidate { Album = "From tags" }));
        var genreOnly = new FakeProvider("genres", MetadataField.Genre, (_, _) => ProviderLookup.Match(new MetadataCandidate { Confidence = 0.9 }));
        var deezer = new FakeProvider("deezer", MetadataField.Artwork | MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc | MetadataField.Genre,
            (_, ids) => ProviderLookup.Match(Full()));
        var mb = new FakeProvider("musicbrainz", MetadataField.Label, (_, _) => ProviderLookup.Match(new MetadataCandidate { Label = "MB" }));
        var orchestrator = Create(Store(), new IMetadataProvider[] { tags, genreOnly, deezer, mb });

        var outcome = await orchestrator.EnrichTrackAsync(new EnrichmentTrack { Hash = "h2", Artist = "Calibre", Title = "Even If" }, CancellationToken.None);

        Assert.Equal("From tags", outcome.Patch.Album);
        Assert.Equal("Signature", outcome.Patch.Label);
        Assert.Equal(1, deezer.Calls);
        Assert.Equal(0, mb.Calls);
    }

    [Fact]
    public async Task RateLimitedProvider_DefersTheTrack_InsteadOfDroppingIt()
    {
        var deezer = new FakeProvider("deezer", MetadataField.Artwork | MetadataField.Label, (_, _) => ProviderLookup.Limited(TimeSpan.FromMinutes(2)));
        var orchestrator = Create(Store(), new IMetadataProvider[] { deezer });

        var outcome = await orchestrator.EnrichTrackAsync(new EnrichmentTrack { Hash = "h3", Artist = "A", Title = "B" }, CancellationToken.None);

        Assert.NotNull(outcome.DeferUntil);
        Assert.InRange((outcome.DeferUntil!.Value - DateTime.UtcNow).TotalSeconds, 100, 121);
    }

    [Fact]
    public async Task Queue_ResumesAfterARestart()
    {
        var store = Store();
        Assert.Equal(3, await store.EnqueueAsync(new[] { "a", "b", "c" }, priority: 0));
        Assert.Equal(0, await store.EnqueueAsync(new[] { "a", "b" }, priority: 0)); // idempotent
        var claimed = await store.ClaimAsync(2, DateTime.UtcNow);
        Assert.Equal(2, claimed.Count);
        // ...the app dies here with two tasks in Processing.

        var afterRestart = Store();
        Assert.Equal(2, await afterRestart.ResetInterruptedAsync());
        Assert.Equal(3, await afterRestart.CountPendingAsync());
        Assert.Equal(3, (await afterRestart.ClaimAsync(10, DateTime.UtcNow)).Count);
    }

    [Fact]
    public async Task FullRun_WritesEntries_RecordsProvenance_AndPublishesOneBatchedEvent()
    {
        for (int i = 0; i < 6; i++)
            _db.AddEntry("t" + i, "Calibre", "Song " + i, e => { e.BPM = 174; e.MusicalKey = "8A"; e.Energy = 0.7; });
        var deezer = new FakeProvider("deezer", MetadataField.Album | MetadataField.Label | MetadataField.ReleaseDate | MetadataField.Isrc | MetadataField.Genre,
            (t, _) =>
            {
                var c = Full();
                c.ArtworkUrl = null;
                c.BpmHint = 87; // half-time web BPM: must not touch the analysed 174
                return ProviderLookup.Match(c);
            });
        var store = Store();
        var orchestrator = Create(store, new IMetadataProvider[] { deezer });

        var queued = await orchestrator.EnrichLibraryAsync();
        await orchestrator.WaitForIdleAsync();

        Assert.Equal(6, queued);
        Assert.Equal(6, deezer.Calls);
        using (var db = _db.Factory.CreateDbContext())
        {
            var rows = db.LibraryEntries.ToList();
            Assert.All(rows, r =>
            {
                Assert.Equal("Signature", r.Label);
                Assert.Equal("Overflow", r.Album);
                Assert.Equal(174, r.BPM);      // analysis-owned, untouched
                Assert.Equal("8A", r.MusicalKey);
                Assert.Equal(0.7, r.Energy);
                Assert.True(r.IsEnriched);
                Assert.NotNull(r.LastEnrichmentAttempt);
            });
            Assert.Equal("deezer", db.MetadataProvenance.First(p => p.TrackUniqueHash == "t0" && p.Field == "label").Source);
            Assert.Equal(0, db.EnrichmentTasks.Count(t => t.Status == EnrichmentStatus.Queued || t.Status == EnrichmentStatus.Processing));
        }
        var hashes = _enriched.SelectMany(e => e.TrackHashes).ToList();
        Assert.Equal(6, hashes.Distinct().Count());
        Assert.True(_enriched.Count < 6, "track-updated events are batched, not one per track");

        // Re-running right away finds nothing new: the 7-day cooldown applies.
        Assert.Equal(0, await orchestrator.CountTracksNeedingMetadataAsync());
    }

    [Fact]
    public async Task WaitsWhileTheAnalysisQueueIsBusy()
    {
        _db.AddEntry("w1", "A", "B");
        var deezer = new FakeProvider("deezer", MetadataField.Label, (_, _) => ProviderLookup.Match(new MetadataCandidate { Label = "L" }));
        var busy = true;
        var orchestrator = Create(Store(), new IMetadataProvider[] { deezer }, busy: () => Volatile.Read(ref busy));

        await orchestrator.EnrichLibraryAsync();
        await Task.Delay(200);
        Assert.Equal(0, deezer.Calls);
        Assert.True(orchestrator.CurrentProgress().IsWaitingForAnalysis);

        Volatile.Write(ref busy, false);
        await orchestrator.WaitForIdleAsync();
        Assert.Equal(1, deezer.Calls);
    }

    [Fact]
    public async Task Artwork_IsDownloadedIntoTheArtworkCache_AndDeadUrlsAreNotStored()
    {
        var artDir = Path.Combine(_db.Directory, "artwork");
        var jpeg = Enumerable.Repeat((byte)0xAB, 2048).ToArray();
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath.Contains("dead")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) });
        var artwork = new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient(handler), artDir);

        _db.AddEntry("a1", "Calibre", "Live");
        _db.AddEntry("a2", "Calibre", "Dead");
        var provider = new FakeProvider("deezer", MetadataField.Artwork, (t, _) => ProviderLookup.Match(new MetadataCandidate
        {
            ArtworkUrl = t.Title == "Live" ? "https://cdn.example/cover/live.jpg" : "https://cdn.example/cover/dead.jpg",
        }));
        var orchestrator = Create(Store(), new IMetadataProvider[] { provider }, artwork);

        await orchestrator.EnrichLibraryAsync();
        await orchestrator.WaitForIdleAsync();

        using var db = _db.Factory.CreateDbContext();
        Assert.Equal("https://cdn.example/cover/live.jpg", db.LibraryEntries.Single(e => e.UniqueHash == "a1").AlbumArtUrl);
        Assert.True(string.IsNullOrEmpty(db.LibraryEntries.Single(e => e.UniqueHash == "a2").AlbumArtUrl));
        Assert.Single(Directory.GetFiles(artDir, "*.cache")); // the live cover is on disk for the Library
        Assert.True(await artwork.PrefetchAsync("https://cdn.example/cover/live.jpg")); // cached: no second request
        Assert.Equal(1, handler.Requests.Count(u => u.Contains("live")));

        // Embedded covers are content-addressed files the Library reads as a local path.
        var path = await artwork.StoreEmbeddedArtworkAsync(jpeg);
        Assert.NotNull(path);
        Assert.Equal(path, await artwork.StoreEmbeddedArtworkAsync(jpeg));
        Assert.Equal(jpeg, await File.ReadAllBytesAsync(path!));
    }
}
