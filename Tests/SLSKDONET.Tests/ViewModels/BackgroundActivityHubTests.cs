using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Activity;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class BackgroundActivityHubTests
{
    private sealed class FakeEnrichmentControl : IEnrichmentActivityControl
    {
        public int Pauses, Resumes;
        public EnrichmentProgress CurrentProgress() => EnrichmentProgress.Idle;
        public void Pause() => Pauses++;
        public void Resume() => Resumes++;
    }

    private static EnrichmentProgress Enrichment(bool running, bool paused, int done, int total) =>
        new(running, paused, false, total, done, done, total - done, Array.Empty<ProviderStatusSnapshot>());

    [Fact]
    public void Aggregates_EveryRunningJob_IntoOneChip()
    {
        using var bus = new EventBusService();
        var control = new FakeEnrichmentControl();
        var navigated = new List<string>();
        using var hub = new BackgroundActivityHubViewModel(bus, null, control, navigated.Add, postToUi: a => a(), throttle: TimeSpan.Zero);
        Assert.False(hub.IsVisible);

        bus.Publish(new EnrichmentProgressEvent(Enrichment(true, false, 25, 100)));
        Assert.True(hub.IsVisible);
        Assert.Equal(1, hub.ActiveCount);
        Assert.Equal("Enriching 25 / 100", hub.ChipText);

        // Analysis: ProcessedCount is cumulative for the session; the hub counts this run.
        bus.Publish(new AnalysisQueueStatusChangedEvent(QueuedCount: 50, ProcessedCount: 900, CurrentTrackHash: null, IsPaused: false, MaxConcurrency: 4));
        bus.Publish(new AnalysisQueueStatusChangedEvent(QueuedCount: 40, ProcessedCount: 910, CurrentTrackHash: null, IsPaused: false, MaxConcurrency: 4));
        Assert.Equal(10, hub.Analysis.Done);
        Assert.Equal(50, hub.Analysis.Total);

        bus.Publish(new LoudnessBackfillProgressEvent(5, 10, 0, 5, true, false));
        Assert.Equal(3, hub.ActiveCount);
        Assert.Equal((25 + 20 + 50) / 3.0, hub.ChipProgress, 3);
        Assert.Equal("3 jobs · 32%", hub.ChipText);

        hub.Enrichment.PauseCommand.Execute(null);
        Assert.Equal(1, control.Pauses);
        bus.Publish(new EnrichmentProgressEvent(Enrichment(false, true, 25, 100)));
        Assert.True(hub.Enrichment.IsActive);             // paused still counts as active
        Assert.True(hub.Enrichment.ShowResume);
        hub.Enrichment.ResumeCommand.Execute(null);
        Assert.Equal(1, control.Resumes);

        hub.Analysis.OpenCommand.Execute(null);
        Assert.Equal(new[] { "Analysis" }, navigated);
        Assert.False(hub.Analysis.SupportsPause);

        // Everything finishes: the chip goes away.
        bus.Publish(new EnrichmentProgressEvent(Enrichment(false, false, 100, 100)));
        bus.Publish(new AnalysisQueueStatusChangedEvent(0, 950, null, false));
        bus.Publish(new LoudnessBackfillProgressEvent(10, 10, 0, 10, false, false));
        Assert.False(hub.IsVisible);
        Assert.Equal(0, hub.ActiveCount);
    }

    [Fact]
    public async Task ImportProgress_IsShown_AndABackgroundSummaryStaysUntilOpened()
    {
        using var bus = new EventBusService();
        var gate = new TaskCompletionSource<LibraryImportResult>();
        IProgress<LibraryImportProgress>? reporter = null;
        var import = new LibraryImportViewModel(bus, NullLogger<LibraryImportViewModel>.Instance, null, null,
            (request, progress, ct) => { reporter = progress; return gate.Task; }, a => a());
        using var hub = new BackgroundActivityHubViewModel(bus, import, null, null, postToUi: a => a(), throttle: TimeSpan.Zero);

        await import.StartBackgroundImportAsync(null, "Importing your music");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (reporter == null && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.False(import.IsOpen);                        // the wizard keeps going: no locking sheet
        Assert.True(hub.Import.IsActive);

        reporter!.Report(new LibraryImportProgress(LibraryImportPhase.Adding, 3000, 1500, 3000, "x.flac", 1500, 0, 0, 0, 0, 0, 0));
        Assert.Equal(50, hub.Import.Progress);
        Assert.Equal("Importing 1,500 / 3,000", hub.ChipText);

        gate.SetResult(new LibraryImportResult { Added = 3000, FilesFound = 3000 });
        sw.Restart();
        while (import.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.True(hub.Import.IsFinished);
        Assert.Equal("Import done · 3,000 added", hub.ChipText);

        hub.Import.OpenCommand.Execute(null);                 // shows the summary sheet
        Assert.True(import.IsOpen);
        import.CloseCommand.Execute(null);
        Assert.False(hub.IsVisible);
    }

    [Fact]
    public async Task Throttles_UiUpdates_ToAFewPerSecond()
    {
        using var bus = new EventBusService();
        var posts = 0;
        using var hub = new BackgroundActivityHubViewModel(bus, null, null, null,
            postToUi: a => { Interlocked.Increment(ref posts); a(); }, throttle: TimeSpan.FromMilliseconds(100));
        var before = hub.ApplyCount;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
        {
            bus.Publish(new EnrichmentProgressEvent(Enrichment(true, false, i, 2000)));
            bus.Publish(new AnalysisQueueStatusChangedEvent(2000 - i, i, null, false));
            if (i % 200 == 0) await Task.Delay(20);
        }
        await Task.Delay(250);
        var applies = hub.ApplyCount - before;

        Assert.InRange(applies, 1, (int)(sw.Elapsed.TotalMilliseconds / 100) + 3);
        Assert.Equal(1999, hub.Enrichment.Done);            // the latest snapshot wins
    }
}

public class VirtualizedTrackCollectionBatchedRefreshTests
{
    [Fact]
    public async Task EnrichmentBursts_AreCoalescedIntoFewBatchedQueries_OffTheUiThread()
    {
        var playlistId = Guid.NewGuid();
        var library = new Mock<ILibraryService>();
        library.Setup(s => s.GetTrackCountAsync(playlistId, null, null, null, null, null)).ReturnsAsync(100);
        var pageQueries = 0;
        var refreshQueries = 0;
        library.Setup(s => s.GetPagedPlaylistTracksAsync(playlistId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<TrackSortColumn>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns((Guid _, int skip, int take, string? _, bool? _, IEnumerable<string>? hashes, string? _, TrackSortColumn _, bool _, string? _) =>
            {
                if (hashes != null) Interlocked.Increment(ref refreshQueries); else Interlocked.Increment(ref pageQueries);
                var rows = hashes != null
                    ? hashes.Select(h => new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = h, Artist = "A", Title = h, Album = "Enriched" }).ToList()
                    : Enumerable.Range(skip, take).Select(i => new PlaylistTrack { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = $"h{i}", Artist = "A", Title = $"T{i}" }).ToList();
                return Task.FromResult(rows);
            });

        using var bus = new EventBusService();
        var previous = VirtualizedTrackCollection.RowRefreshInterval;
        VirtualizedTrackCollection.RowRefreshInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            using var sut = new VirtualizedTrackCollection(NullLogger<VirtualizedTrackCollection>.Instance, library.Object, bus,
                new ArtworkCacheService(NullLogger<ArtworkCacheService>.Instance, new HttpClient()), playlistId, pageSize: 50);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sut.Count < 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
            await sut.LoadPageAsync(0);  // rows h0..h49 are materialised

            // 25 enrichment batches + per-track analysis updates for 40 rows, from background threads.
            await Task.WhenAll(Enumerable.Range(0, 25).Select(b => Task.Run(() =>
            {
                bus.Publish(new TracksEnrichedEvent(Enumerable.Range(0, 40).Select(i => $"h{i}").ToList()));
                bus.Publish(new TrackAnalysisUpdatedEvent($"h{b}", DateTime.UtcNow));
                bus.Publish(new TracksEnrichedEvent(new[] { "not-loaded-row" })); // ignored: not on screen
            })));
            await Task.Delay(400);

            Assert.InRange(sut.RowRefreshQueries, 1, 3);
            Assert.Equal(sut.RowRefreshQueries, refreshQueries);
        }
        finally
        {
            VirtualizedTrackCollection.RowRefreshInterval = previous;
        }
    }
}
