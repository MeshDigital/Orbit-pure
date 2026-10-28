using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Data.Entities;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

[Collection(NonParallelCollection.Name)]
public class AnalysisPageViewModelTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly AnalysisPageViewModel _vm;

    public AnalysisPageViewModelTests()
    {
        var projectionService = new LifecycleProjectionService(new Mock<ILibraryService>().Object);
        _vm = new AnalysisPageViewModel(_bus, lifecycleProjectionService: projectionService);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _vm.Dispose();
    }

    [Fact]
    public void HasAnalysisError_TrueWhenErrorSet()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title")
        {
            AnalysisError = "decoder error"
        };

        Assert.True(item.HasAnalysisError);
    }

    [Fact]
    public void IsLikelyCorruptFile_TrueOnlyForCorruptionScannerMessages()
    {
        var corrupt = new AnalysisTrackItem("t1", "Artist", "Title")
        {
            AnalysisError = "Corrupt file — analysis blocked: FFmpeg: [flac] decode_frame() failed"
        };
        var transientFailure = new AnalysisTrackItem("t2", "Artist", "Title")
        {
            AnalysisError = "Audio analysis returned no result for track.flac"
        };
        var noError = new AnalysisTrackItem("t3", "Artist", "Title");

        Assert.True(corrupt.IsLikelyCorruptFile);
        Assert.False(transientFailure.IsLikelyCorruptFile);
        Assert.False(noError.IsLikelyCorruptFile);
    }

    [Fact]
    public void IncompleteAnalysisSummary_SurfacesReDownloadGuidance_ForCorruptFiles_NotGenericFailure()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title")
        {
            AnalysisStatus = AnalysisRunStatus.Failed,
            AnalysisError = "Corrupt file — analysis blocked: FFmpeg: invalid sync code"
        };

        // Previously this just said "analysis failed" regardless of cause, indistinguishable from
        // a transient failure a retry might fix — re-analyzing a genuinely corrupt file fails
        // identically every time, so the summary should point at re-acquiring the file instead.
        Assert.Contains("re-download", item.IncompleteAnalysisSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("analysis failed:", item.IncompleteAnalysisSummary);
    }

    [Fact]
    public void IncompleteAnalysisSummary_SurfacesSpecificError_ForNonCorruptFailures()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title")
        {
            AnalysisStatus = AnalysisRunStatus.Failed,
            AnalysisError = "Audio analysis returned no result for track.flac"
        };

        Assert.Contains("analysis failed:", item.IncompleteAnalysisSummary);
        Assert.Contains("Audio analysis returned no result", item.IncompleteAnalysisSummary);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsNotYetAnalyzedWhenNull()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.Equal("Not yet analyzed", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void DashboardCounts_ReflectLibraryAndQueueState()
    {
        Assert.True(_vm.TotalTrackCount >= 10);
        Assert.True(_vm.AnalyzedTrackCount >= 3);

        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.AddToQueue(pending);

        Assert.Equal(1, _vm.QueueTrackCount);
        Assert.Contains("queued", _vm.QueueMetricsSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrackAnalysisRequestedEvent_AddsMatchingLibraryTrackToQueue()
    {
        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);

        _bus.Publish(new TrackAnalysisRequestedEvent(pending.TrackId));

        Assert.True(pending.IsInQueue);
        Assert.Contains(pending, _vm.AnalysisQueue);
        Assert.Equal(AnalysisRunStatus.Queued, pending.AnalysisStatus);
    }

    [Fact]
    public void FileIngestionQueuedEvent_UpdatesAnalysisStatusMessage()
    {
        _bus.Publish(new FileIngestionQueuedEvent("hash-1", Guid.NewGuid(), @"C:\\music\\queued.flac", DateTime.UtcNow));

        Assert.Contains("Ingestion pending", _vm.AnalysisStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reanalyze_RequeuesCompletedTrack_AndPublishesHighPriorityRequest()
    {
        var analyzed = _vm.LibraryTracks.First(t => t.HasAnalysis);
        TrackAnalysisRequestedEvent? published = null;
        using var sub = _bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(evt => published = evt);

        _vm.Reanalyze(analyzed);

        Assert.False(analyzed.HasAnalysis);
        Assert.True(analyzed.IsInQueue);
        Assert.Equal(AnalysisRunStatus.Queued, analyzed.AnalysisStatus);
        Assert.Contains(analyzed, _vm.AnalysisQueue);
        Assert.NotNull(published);
        Assert.True(published!.IsHighPriority);
    }

    [Fact]
    public async Task ReanalyzeAllTracksAsync_QueuesEveryLibraryTrack_EvenAlreadyCompleteOnes()
    {
        int totalBefore = _vm.LibraryTracks.Count;
        Assert.True(totalBefore > 0);

        await _vm.ReanalyzeAllTracksAsync();

        Assert.Equal(totalBefore, _vm.QueueTrackCount);
        Assert.All(_vm.LibraryTracks, t => Assert.True(t.IsInQueue));
        Assert.All(_vm.LibraryTracks, t => Assert.Equal(AnalysisRunStatus.Queued, t.AnalysisStatus));
    }

    [Fact]
    public async Task TrackAnalysisCompletedEvent_Success_RemovesTrackFromQueueAndMarksCompleted()
    {
        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.AddToQueue(pending);

        InvokeHandler(_vm, "OnTrackAnalysisStarted", new TrackAnalysisStartedEvent(pending.TrackId, "pending.flac"));
        await InvokeAsyncHandler(_vm, "OnTrackAnalysisCompletedAsync", new TrackAnalysisCompletedEvent(pending.TrackId, true));

        Assert.False(pending.IsInQueue);
        Assert.DoesNotContain(pending, _vm.AnalysisQueue);
        Assert.Equal(AnalysisRunStatus.Completed, pending.AnalysisStatus);
    }

    [Fact]
    public async Task TrackAnalysisCompletedEvent_Failure_RemovesTrackFromQueueAndMarksFailed()
    {
        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.AddToQueue(pending);

        await InvokeAsyncHandler(_vm, "OnTrackAnalysisFailedAsync", new TrackAnalysisFailedEvent(pending.TrackId, "decoder error"));
        await InvokeAsyncHandler(_vm, "OnTrackAnalysisCompletedAsync", new TrackAnalysisCompletedEvent(pending.TrackId, false, "decoder error"));

        Assert.False(pending.IsInQueue);
        Assert.DoesNotContain(pending, _vm.AnalysisQueue);
        Assert.Equal(AnalysisRunStatus.Failed, pending.AnalysisStatus);
        Assert.Contains("decoder error", pending.AnalysisError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAnalysisAsync_PublishesTrackAnalysisRequestsForQueuedTracks()
    {
        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.AddToQueue(pending);

        var publishedRequests = 0;
        using var sub = _bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(_ => publishedRequests++);

        await _vm.StartAnalysisAsync();

        Assert.True(publishedRequests >= 1);
    }

    [Fact]
    public async Task RunMissingForPlaylist_UsesCoveragePopulationAndPerTrackMasks()
    {
        var selected = new AnalysisTrackItem("selected", "Selected", "Track");
        var unrelated = new AnalysisTrackItem("unrelated", "Other", "Track");
        _vm.LibraryTracks.Add(selected);
        _vm.LibraryTracks.Add(unrelated);

        var missingKey = new TrackAnalysisCoverage(
            AnalysisEngine.Pipeline,
            AnalysisEngine.Pipeline & ~AnalysisEngine.Key);
        var coverage = new PlaylistAnalysisCoverage(Guid.NewGuid(), "Selected set", new[]
        {
            new TrackCoverageEntry("selected", "Selected", "Track", missingKey),
            new TrackCoverageEntry("not-loaded", "Unlisted", "Track", missingKey),
        });
        SetCoverage(coverage);

        var published = new System.Collections.Generic.List<TrackAnalysisRequestedEvent>();
        using var sub = _bus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(published.Add);

        var method = typeof(AnalysisPageViewModel).GetMethod(
            "RunMissingForPlaylistAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(_vm, null)!;

        Assert.Equal(2, published.Count);
        Assert.Contains(published, e => e.TrackGlobalId == "selected" && e.Engines == AnalysisEngine.Key);
        Assert.Contains(published, e => e.TrackGlobalId == "not-loaded" && e.Engines == AnalysisEngine.Key);
        Assert.DoesNotContain(published, e => e.TrackGlobalId == "unrelated");
        Assert.DoesNotContain(unrelated, _vm.AnalysisQueue);
    }

    [Fact]
    public void MapLibraryEntryToTrack_UnmeasuredEntry_DoesNotFabricateAnalysisData()
    {
        var method = typeof(AnalysisPageViewModel).GetMethod(
            "MapLibraryEntryToTrack", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var item = (AnalysisTrackItem)method.Invoke(_vm, new object[]
        {
            new LibraryEntry { UniqueHash = "empty", Artist = "Artist", Title = "Title" },
        })!;

        Assert.Null(item.AnalysisData);
        Assert.False(item.HasAnalysis);
    }

    [Fact]
    public void EmptyCoverage_RemainsVisibleAndExplainsTheEmptySelection()
    {
        SetCoverage(new PlaylistAnalysisCoverage(Guid.NewGuid(), "Empty set", Array.Empty<TrackCoverageEntry>()));

        Assert.True(_vm.HasCoverageData);
        Assert.Contains("No tracks with audio on disk", _vm.CoverageSummary);
    }

    private void SetCoverage(PlaylistAnalysisCoverage coverage)
    {
        typeof(AnalysisPageViewModel).GetField("_coverage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_vm, coverage);
    }

    private static void InvokeHandler(object instance, string methodName, object evt)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");
        method.Invoke(instance, new[] { evt });
    }

    private static async Task InvokeAsyncHandler(object instance, string methodName, object evt)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        var task = method.Invoke(instance, new[] { evt }) as Task;
        if (task is not null)
        {
            await task;
        }
    }
}
