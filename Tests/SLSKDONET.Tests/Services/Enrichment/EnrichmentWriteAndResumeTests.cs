using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

/// <summary>Write-time fill-empty and resume after a restart, on a throwaway database.</summary>
[Collection(NonParallelCollection.Name)]
public sealed class EnrichmentWriteAndResumeTests : IDisposable
{
    private readonly TempLibraryDb _db = new();
    private readonly EventBusService _bus = new();

    public void Dispose()
    {
        _bus.Dispose();
        _db.Dispose();
    }

    private MetadataEnrichmentOrchestrator Create(EnrichmentStore store, IMetadataProvider provider) =>
        new(_db.Factory, store, new[] { provider }, _bus, NullLogger<MetadataEnrichmentOrchestrator>.Instance,
            () => EnrichmentSettingsFor.Providers(provider.Name), null, () => false, () => false,
            workers: 2, busyPoll: TimeSpan.FromMilliseconds(20), startupResumeDelay: null);

    [Fact]
    public async Task Write_KeepsAFieldSomebodyFilledAfterTheSnapshot_AndRecordsNoProvenanceForIt()
    {
        _db.AddEntry("lu1", "Calibre", "Even If");
        var store = new EnrichmentStore(_db.Factory, NullLogger<EnrichmentStore>.Instance);
        var provider = new FakeProvider("deezer", MetadataField.Album | MetadataField.Label | MetadataField.Genre,
            (_, _) =>
            {
                var c = new MetadataCandidate { Album = "Overflow", Label = "Signature", Confidence = 0.95 };
                c.Genres.Add("Drum & Bass");
                return ProviderLookup.Match(c);
            });
        var orchestrator = Create(store, provider);

        // The pass reads the row (label empty) ...
        var outcome = await orchestrator.EnrichTrackAsync(new EnrichmentTrack { Hash = "lu1", Artist = "Calibre", Title = "Even If", Album = "" }, CancellationToken.None);
        Assert.Equal("Signature", outcome.Patch.Label);
        // ... meanwhile the tag editor / analysis writes the row ...
        await using (var db = _db.Factory.CreateDbContext())
        {
            var row = db.LibraryEntries.Single(e => e.UniqueHash == "lu1");
            row.Label = "Typed By User";
            row.BPM = 172;
            await db.SaveChangesAsync();
        }
        // ... and then the patch is written.
        await orchestrator.WriteAsync(outcome.Patch, CancellationToken.None);

        await using var check = _db.Factory.CreateDbContext();
        var e = check.LibraryEntries.Single(x => x.UniqueHash == "lu1");
        Assert.Equal("Typed By User", e.Label);    // used to be overwritten with the stale patch
        Assert.Equal("Overflow", e.Album);
        Assert.Equal("Drum & Bass", e.Genres);
        Assert.Equal(172, e.BPM);
        var provenance = check.MetadataProvenance.Where(p => p.TrackUniqueHash == "lu1").Select(p => p.Field).ToList();
        Assert.Contains("album", provenance);
        Assert.DoesNotContain("label", provenance);
        orchestrator.Dispose();
    }

    [Fact]
    public async Task Queue_ResumesAfterARestartMidRun_WithoutDuplicatesOrHalfWrittenRows()
    {
        const int n = 40;
        for (var i = 0; i < n; i++) _db.AddEntry("r" + i, "Artist", "Song " + i);
        var store = new EnrichmentStore(_db.Factory, NullLogger<EnrichmentStore>.Instance);
        var slow = new SlowFlakyProvider("deezer", limitEvery: 0, latency: TimeSpan.FromMilliseconds(40));
        var first = Create(store, slow);
        await first.EnrichLibraryAsync();
        var wait = Stopwatch.StartNew();
        while (slow.Calls < 8 && wait.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);

        // The app quits mid-run: the run is cancelled with tracks in flight (left in Processing).
        var run = first.WaitForIdleAsync();
        first.Dispose();
        try { await run; }
        catch (OperationCanceledException) { /* the cancelled run */ }
        await using (var db = _db.Factory.CreateDbContext())
        {
            var done = db.LibraryEntries.Count(e => e.Album == "Provider Album");
            Assert.InRange(done, 1, n - 1);
            // Every row is either untouched or fully written (one transaction per row).
            Assert.All(db.LibraryEntries.ToList(), e =>
                Assert.Equal(e.Album == "Provider Album", e.Label == "Provider Label" && e.LastEnrichmentAttempt != null));
        }

        // Next start: a new orchestrator over the same database resumes the leftover queue.
        var afterRestart = new EnrichmentStore(_db.Factory, NullLogger<EnrichmentStore>.Instance);
        var second = Create(afterRestart, new SlowFlakyProvider("deezer", 0, TimeSpan.FromMilliseconds(1)));
        second.Start();
        var sw = Stopwatch.StartNew();
        while ((second.IsRunning || await afterRestart.CountPendingAsync() > 0) && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);

        await using var check = _db.Factory.CreateDbContext();
        Assert.All(check.LibraryEntries.ToList(), e => Assert.Equal("Provider Album", e.Album));
        var tasks = check.EnrichmentTasks.ToList();
        Assert.Equal(n, tasks.Count);                                  // one task per track
        Assert.All(tasks, t => Assert.Equal(EnrichmentStatus.Completed, t.Status));
        second.Dispose();
    }
}
