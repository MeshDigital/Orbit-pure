using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Tests.Services.Sets;
using SLSKDONET.ViewModels.Maintenance;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Maintenance;

/// <summary>GA10 (#255): stale genres are re-fused from stored data, no audio (isolated DB).</summary>
public sealed class GenreRefreshServiceTests : IDisposable
{
    private const string CurrentStyle = "discogs-effnet-bsdynamic-1|ABCDEF12|mel3|pool=energy1";
    private static readonly string Rhythm = JsonSerializer.Serialize(new RhythmSignature(0.1, 0.9, 0.1, 0.6, 87, 174, 32, 0.9));
    private static readonly byte[] EmbeddingBlob = Enumerable.Range(0, 1280 * 4).Select(i => (byte)(i % 251)).ToArray();
    private static readonly byte[] StyleBlob = Enumerable.Range(0, 400 * 4).Select(i => (byte)(i % 13)).ToArray();

    private readonly SetsTestDatabase _db = new("genre-refresh");
    private readonly EventBusService _bus = new();
    private readonly ITestOutputHelper _output;

    public GenreRefreshServiceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _bus.Dispose();
        _db.Dispose();
    }

    private sealed class FakeJamendo : IJamendoGenreHead
    {
        public bool IsAvailable { get; init; } = true;
        public int Calls { get; private set; }

        public IReadOnlyList<GenreLabelScore>? PredictJamendo(float[] embedding)
        {
            Calls++;
            return new[] { new GenreLabelScore("drumnbass", 0.95f), new GenreLabelScore("techno", 0.1f) };
        }
    }

    private GenreRefreshService Service(IJamendoGenreHead? head = null) => new(
        _db.Factory,
        new StyleBackfillService(_db.Factory, _bus, NullLogger<StyleBackfillService>.Instance, () => CurrentStyle),
        _bus,
        NullLogger<GenreRefreshService>.Instance,
        head ?? new FakeJamendo());

    private static string Evidence(int fusionVersion, string? genres, string? source) => new GenreEvidenceRecord
    {
        FusionVersion = fusionVersion,
        MetadataText = genres,
        MetadataSource = source,
    }.ToJson();

    private AudioFeaturesEntity Row(string hash, string? evidence, string? styleTag = CurrentStyle, bool rhythm = true) => new()
    {
        TrackUniqueHash = hash,
        Bpm = 174,
        TopStyle = "Electronic---Techno",
        StyleModelTag = styleTag,
        EmbeddingModelTag = StyleBackfillService.EmbeddingTagOf(CurrentStyle),
        RhythmSignatureJson = rhythm ? Rhythm : null,
        EmbeddingBlob = EmbeddingBlob,
        StyleScoresBlob = StyleBlob,
        DetectedSubGenre = "Techno",
        SubGenreConfidence = 0.3f,
        GenreEvidenceJson = evidence,
    };

    private async Task SeedClassesAsync()
    {
        var hashes = new[] { "fresh", "oldfusion", "noevidence", "genrechanged", "sourcechanged", "badjson", "stalemodel", "norhythm", "nofeatures" };
        _db.SeedLibrary(hashes.Select(h => (h, "Artist", h)).ToArray());
        await using var db = _db.Factory.CreateDbContext();
        foreach (var h in hashes)
            db.LibraryEntries.Find(h)!.Genres = "Drum & Bass";
        db.LibraryEntries.Find("genrechanged")!.Genres = "Techno";
        db.MetadataProvenance.Add(new MetadataProvenanceEntity { TrackUniqueHash = "sourcechanged", Field = "genre", Source = "user", Confidence = 1 });
        db.MetadataProvenance.Add(new MetadataProvenanceEntity { TrackUniqueHash = "genrechanged", Field = "genre", Source = "tags", Confidence = 1 });

        var v = GenreFusionEngine.FusionVersion;
        db.AudioFeatures.AddRange(
            Row("fresh", Evidence(v, "Drum & Bass", null)),
            Row("oldfusion", Evidence(v - 1, "Drum & Bass", null)),
            Row("noevidence", null),
            Row("genrechanged", Evidence(v, "Drum & Bass", null)),
            Row("sourcechanged", Evidence(v, "Drum & Bass", null)),
            Row("badjson", "{not json"),
            Row("stalemodel", Evidence(1, "Drum & Bass", null), styleTag: "discogs-effnet-bsdynamic-1|ABCDEF12|mel2"),
            Row("norhythm", Evidence(1, "Drum & Bass", null), rhythm: false));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Scan_SortsEveryClass()
    {
        await SeedClassesAsync();
        var scan = await Service().ScanAsync();

        Assert.Equal(new[] { "badjson", "genrechanged", "noevidence", "oldfusion", "sourcechanged" }, scan.Refuse.OrderBy(h => h).ToArray());
        Assert.Equal(3, scan.NeedsInference); // stalemodel, norhythm, nofeatures
        Assert.Equal(1, scan.Fresh);
    }

    [Fact]
    public async Task Refuse_WritesOnlyGenreColumns_PublishesChanges_AndASecondRunIsANoOp()
    {
        await SeedClassesAsync();
        var updated = new List<string>();
        using var sub = _bus.GetEvent<TrackAnalysisUpdatedEvent>().Subscribe(e => updated.Add(e.TrackGlobalId));
        var head = new FakeJamendo();
        var service = Service(head);

        var scan = await service.ScanAsync();
        var progress = new List<(int, int)>();
        var report = await service.RefuseAsync(scan.Refuse, new SyncProgress(progress));

        Assert.Equal(5, report.Refused);
        Assert.Equal(0, report.Failed);
        Assert.False(report.Cancelled);
        Assert.Equal(5, head.Calls);
        Assert.Equal((5, 5), progress[^1]);

        await using (var db = _db.Factory.CreateDbContext())
        {
            var row = db.AudioFeatures.Single(f => f.TrackUniqueHash == "oldfusion");
            Assert.Equal("Drum and Bass", row.DetectedSubGenre);              // the audio opinion, re-fused
            var evidence = GenreEvidenceRecord.TryParse(row.GenreEvidenceJson)!;
            Assert.Equal(GenreFusionEngine.FusionVersion, evidence.FusionVersion);
            Assert.Equal("Drum & Bass", evidence.MetadataText);
            Assert.Equal(EmbeddingBlob, row.EmbeddingBlob);                     // inputs untouched
            Assert.Equal(StyleBlob, row.StyleScoresBlob);
            Assert.Equal("Electronic---Techno", row.TopStyle);
            Assert.Equal(174, row.Bpm);
            Assert.Equal("Drum & Bass", db.LibraryEntries.Find("oldfusion")!.Genres); // tags untouched

            // Not in the refresh list: left alone.
            Assert.Equal("Techno", db.AudioFeatures.Single(f => f.TrackUniqueHash == "stalemodel").DetectedSubGenre);

            var techno = GenreEvidenceRecord.TryParse(db.AudioFeatures.Single(f => f.TrackUniqueHash == "genrechanged").GenreEvidenceJson)!;
            Assert.Equal("Techno", techno.MetadataText);
            Assert.Equal("Techno", techno.Bucket); // a clean file tag beats the audio
        }

        Assert.Contains("oldfusion", updated);
        Assert.DoesNotContain("fresh", updated);

        var again = await service.ScanAsync();
        Assert.Empty(again.Refuse);
        Assert.Equal(6, again.Fresh);
    }

    [Fact]
    public async Task WithoutTheJamendoHead_StillRefuses_FromWhatIsStored()
    {
        await SeedClassesAsync();
        var service = Service(new FakeJamendo { IsAvailable = false });
        var scan = await service.ScanAsync();
        var report = await service.RefuseAsync(scan.Refuse);

        Assert.Equal(5, report.Refused);
        Assert.Equal(0, report.Failed);
        Assert.Empty((await service.ScanAsync()).Refuse); // still idempotent: the evidence carries this version
    }

    [Fact]
    public async Task Cancel_StopsBetweenChunks_AndSaysSo()
    {
        await SeedClassesAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await Service().RefuseAsync(new[] { "oldfusion" }, ct: cts.Token);
        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Refused);
        Assert.Contains("cancelled", LibraryMaintenanceViewModel.DescribeGenreRefresh(report));
    }

    [Fact]
    public async Task MissingRows_AreSkipped_NotFailed()
    {
        var report = await Service().RefuseAsync(new[] { "nobody" });
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Failed);
    }

    [Theory]
    [InlineData(3, "House", null, "House", null, GenreStaleness.Fresh)]
    [InlineData(2, "House", null, "House", null, GenreStaleness.Refuse)]
    [InlineData(3, "House", null, "Techno", null, GenreStaleness.Refuse)]
    [InlineData(3, "House", null, "House", "user", GenreStaleness.Refuse)]
    [InlineData(3, null, null, "  ", "user", GenreStaleness.Fresh)] // a blank genre carries no source
    [InlineData(3, "House", null, " House ", null, GenreStaleness.Fresh)]
    public void Classify_ComparesVersionGenreAndSource(int version, string? evidenceGenres, string? evidenceSource,
        string? genres, string? source, GenreStaleness expected)
    {
        var row = new GenreRefreshService.StalenessRow
        {
            Hash = "h", FusionVersion = version, EvidenceGenres = evidenceGenres, EvidenceSource = evidenceSource, HasEvidence = true,
        };
        Assert.Equal(expected, GenreRefreshService.Classify(row, new TrackGenreContext(genres, source), currentFusionVersion: 3));
    }

    [Fact]
    public void Summary_FormatsDurationsAndFailures()
    {
        Assert.Equal("3 s", LibraryMaintenanceViewModel.FormatDuration(TimeSpan.FromSeconds(2.2)));
        Assert.Equal("5 min", LibraryMaintenanceViewModel.FormatDuration(TimeSpan.FromMinutes(4.5)));
        Assert.Equal("2.5 h", LibraryMaintenanceViewModel.FormatDuration(TimeSpan.FromHours(2.5))); // UI numbers are culture-neutral
        var text = LibraryMaintenanceViewModel.DescribeGenreRefresh(new GenreRefreshReport(2, 8, 0, 1, false, TimeSpan.Zero));
        Assert.Contains("10 tracks", text);
        Assert.Contains("1 failed", text);
    }

    [Fact, Trait("Category", "Perf")]
    public async Task Refuse_1000Rows_UnderTenSeconds()
    {
        var hashes = Enumerable.Range(0, 1000).Select(i => $"perf-{i:D4}").ToArray();
        _db.SeedLibrary(hashes.Select(h => (h, "Artist", h)).ToArray());
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.AudioFeatures.AddRange(hashes.Select(h => Row(h, null)));
            await db.SaveChangesAsync();
        }

        var service = Service();
        var sw = Stopwatch.StartNew();
        var scan = await service.ScanAsync();
        var report = await service.RefuseAsync(scan.Refuse);
        sw.Stop();

        Assert.Equal(1000, report.Refused);
        _output.WriteLine($"scan + re-fuse of 1,000 rows: {sw.ElapsedMilliseconds} ms");
        // A budget, not a CI gate (#255): shared CI runners are several times slower than a desktop and vary
        // run to run (13 s seen on one), so the 10 s bound is only enforced locally.
        if (Environment.GetEnvironmentVariable("CI") is null)
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; this records synchronously.</summary>
    private sealed class SyncProgress(List<(int, int)> sink) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => sink.Add(value);
    }
}
