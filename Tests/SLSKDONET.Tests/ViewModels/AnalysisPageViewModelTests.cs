using System;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Tests for issue #43 additions to <see cref="AnalysisPageViewModel"/>
/// and <see cref="AnalysisTrackItem"/>:
///   - per-track status indicators (StemsReady, IsInPlaylist)
///   - error surfaces (AnalysisError, StemError, HasAnalysisError, HasStemError)
///   - tooltip properties (LastAnalyzedAt, ModelVersion, LastAnalyzedDisplay)
///   - automix flow (TogglePlaylist, CreateAutomixPlaylist, AutomixConstraints)
/// </summary>
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

    // ── AnalysisTrackItem status indicators ───────────────────────────────

    [Fact]
    public void StemsReady_DefaultsFalse()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.StemsReady);
    }

    [Fact]
    public void IsInPlaylist_DefaultsFalse()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.IsInPlaylist);
    }

    [Fact]
    public void SetStemsReady_RaisesPropertyChanged()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        bool changed = false;
        item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AnalysisTrackItem.StemsReady)) changed = true; };
        item.StemsReady = true;
        Assert.True(changed);
    }

    // ── Error surfaces ────────────────────────────────────────────────────

    [Fact]
    public void HasAnalysisError_FalseWhenNoError()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.False(item.HasAnalysisError);
    }

    [Fact]
    public void HasAnalysisError_TrueWhenErrorSet()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.AnalysisError = "Essentia failed: codec not found";
        Assert.True(item.HasAnalysisError);
    }

    [Fact]
    public void HasStemError_TrueWhenStemErrorSet()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.StemError = "ONNX runtime OOM";
        Assert.True(item.HasStemError);
    }

    [Fact]
    public void HasStemError_FalseForNullOrEmpty()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.StemError = string.Empty;
        Assert.False(item.HasStemError);
    }

    // ── Tooltip / timestamp properties ────────────────────────────────────

    [Fact]
    public void LastAnalyzedDisplay_ReturnsNotYetAnalyzedWhenNull()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        Assert.Equal("Not yet analyzed", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsJustNowForRecentTimestamp()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddSeconds(-10);
        Assert.Equal("Just now", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsMinutesAgo()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddMinutes(-5);
        Assert.Contains("min ago", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void LastAnalyzedDisplay_ReturnsDaysAgo()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.LastAnalyzedAt = DateTime.UtcNow.AddDays(-3);
        Assert.Contains("day", item.LastAnalyzedDisplay);
    }

    [Fact]
    public void ModelVersion_RoundTrips()
    {
        var item = new AnalysisTrackItem("t1", "Artist", "Title");
        item.ModelVersion = "essentia-2.1-b6";
        Assert.Equal("essentia-2.1-b6", item.ModelVersion);
    }

    // ── TogglePlaylist command ────────────────────────────────────────────

    [Fact]
    public void TogglePlaylist_AddsAnalysedTrackToPlaylistTracks()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);
        Assert.True(track.IsInPlaylist);
        Assert.Contains(track, _vm.PlaylistTracks);
    }

    [Fact]
    public void TogglePlaylist_RemovesTrackWhenAlreadyInPlaylist()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);   // add
        _vm.TogglePlaylist(track);   // remove
        Assert.False(track.IsInPlaylist);
        Assert.DoesNotContain(track, _vm.PlaylistTracks);
    }

    [Fact]
    public void TogglePlaylist_IgnoresTrackWithoutAnalysis()
    {
        var track = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.TogglePlaylist(track);
        Assert.False(track.IsInPlaylist);
        Assert.Empty(_vm.PlaylistTracks);
    }

    // ── CreateAutomixPlaylist command ─────────────────────────────────────

    [Fact]
    public void CreateAutomixPlaylist_RequiresAtLeastTwoTracks()
    {
        var track = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(track);  // only 1

        _vm.CreateAutomixPlaylist();

        Assert.NotNull(_vm.AutomixStatusMessage);
        Assert.Contains("at least 2", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateAutomixPlaylist_SortsByBpm()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        _vm.AutomixConstraints.MinBpm = 0;
        _vm.AutomixConstraints.MaxBpm = 300;

        _vm.CreateAutomixPlaylist();

        var bpms = _vm.PlaylistTracks
            .Select(t => t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0)
            .ToList();

        for (int i = 1; i < bpms.Count; i++)
            Assert.True(bpms[i] >= bpms[i - 1], $"Track {i} BPM {bpms[i]} is less than previous {bpms[i-1]}");
    }

    [Fact]
    public void CreateAutomixPlaylist_FiltersOutOfRangeBpm()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        // An extremely narrow BPM range that should match no tracks
        _vm.AutomixConstraints.MinBpm = 999;
        _vm.AutomixConstraints.MaxBpm = 1000;

        _vm.CreateAutomixPlaylist();

        Assert.Contains("Not enough tracks", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateAutomixPlaylist_SetsSuccessStatusMessage()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        _vm.AutomixConstraints.MinBpm = 0;
        _vm.AutomixConstraints.MaxBpm = 300;

        _vm.CreateAutomixPlaylist();

        Assert.NotNull(_vm.AutomixStatusMessage);
        Assert.Contains("BPM", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAutomixPlaylistAsync_SortsByBpm_WhenOptimizerUnavailable()
    {
        foreach (var t in _vm.LibraryTracks.Where(t => t.HasAnalysis))
            _vm.TogglePlaylist(t);

        _vm.AutomixConstraints.MinBpm = 0;
        _vm.AutomixConstraints.MaxBpm = 300;

        await _vm.CreateAutomixPlaylistAsync();

        var bpms = _vm.PlaylistTracks
            .Select(t => t.AnalysisData?.Mechanics.Bpm ?? t.Bpm ?? 0)
            .ToList();

        for (int i = 1; i < bpms.Count; i++)
            Assert.True(bpms[i] >= bpms[i - 1], $"Track {i} BPM {bpms[i]} is less than previous {bpms[i - 1]}");
    }

    // ── AutomixConstraints ────────────────────────────────────────────────

    [Fact]
    public void AutomixConstraints_DefaultsAreReasonable()
    {
        var c = new AutomixConstraints();
        Assert.True(c.MinBpm >= 80 && c.MinBpm <= 120);
        Assert.True(c.MaxBpm > c.MinBpm);
        Assert.True(c.MaxTracks >= 2);
    }

    [Fact]
    public void DashboardCounts_ReflectLibraryQueueAndPlaylistState()
    {
        Assert.True(_vm.TotalTrackCount >= 10);
        Assert.True(_vm.AnalyzedTrackCount >= 3);

        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);
        _vm.AddToQueue(pending);

        Assert.Equal(1, _vm.QueueTrackCount);
        Assert.Contains("queued", _vm.QueueMetricsSummary, StringComparison.OrdinalIgnoreCase);

        var analyzed = _vm.LibraryTracks.First(t => t.HasAnalysis);
        _vm.TogglePlaylist(analyzed);

        Assert.Equal(1, _vm.PlaylistTrackCount);
    }

    [Fact]
    public void TrackAnalysisRequestedEvent_AddsMatchingLibraryTrackToAnalysisQueue()
    {
        var pending = _vm.LibraryTracks.First(t => !t.HasAnalysis);

        _bus.Publish(new TrackAnalysisRequestedEvent(pending.TrackId));

        Assert.True(pending.IsInQueue);
        Assert.Contains(pending, _vm.AnalysisQueue);
        Assert.Equal(AnalysisRunStatus.Queued, pending.AnalysisStatus);
    }

    [Fact]
    public void FileIngestionQueuedEvent_IncrementsIngestionBacklog()
    {
        var before = _vm.IngestionBacklogCount;

        _bus.Publish(new FileIngestionQueuedEvent("hash-1", Guid.NewGuid(), @"C:\\music\\queued.flac", DateTime.UtcNow));

        Assert.Equal(before + 1, _vm.IngestionBacklogCount);
        Assert.Contains("Ingestion pending", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileIngestionCompletedEvent_DecrementsBacklogAndIncrementsCatalog()
    {
        _bus.Publish(new FileIngestionQueuedEvent("hash-2", Guid.NewGuid(), @"C:\\music\\queued.flac", DateTime.UtcNow));
        var backlogBeforeComplete = _vm.IngestionBacklogCount;
        var catalogBeforeComplete = _vm.IndexedCatalogCount;

        _bus.Publish(new FileIngestionCompletedEvent("hash-2", Guid.NewGuid(), @"C:\\music\\queued.flac", DateTime.UtcNow));

        Assert.Equal(Math.Max(0, backlogBeforeComplete - 1), _vm.IngestionBacklogCount);
        Assert.Equal(catalogBeforeComplete + 1, _vm.IndexedCatalogCount);
        Assert.Contains("Indexed", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileMissingDetectedEvent_IncrementsStaleIndexedCount()
    {
        InvokeHandler(_vm, "OnFileIngestionCompleted", new FileIngestionCompletedEvent("hash-indexed", Guid.NewGuid(), @"C:\\music\\indexed.flac", DateTime.UtcNow));

        InvokeHandler(_vm, "OnFileMissingDetected", new FileMissingDetectedEvent("hash-missing", @"C:\\music\\missing.flac", DateTime.UtcNow, "test"));

        Assert.True(_vm.StaleIndexedCount >= 0);
        Assert.Contains("Stale", _vm.AutomixStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reanalyze_RequeuesCompletedTrack_AndClearsPreviousResults()
    {
        var analyzed = _vm.LibraryTracks.First(t => t.HasAnalysis);

        _vm.Reanalyze(analyzed);

        Assert.False(analyzed.HasAnalysis);
        Assert.True(analyzed.IsInQueue);
        Assert.Equal(AnalysisRunStatus.Queued, analyzed.AnalysisStatus);
        Assert.Contains(analyzed, _vm.AnalysisQueue);
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

        InvokeHandler(_vm, "OnTrackAnalysisFailed", new TrackAnalysisFailedEvent(pending.TrackId, "decoder error"));
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
    public void AutomixConstraints_RaisesPropertyChangedOnMatchKeyChange()
    {
        var c = new AutomixConstraints();
        bool changed = false;
        c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AutomixConstraints.MatchKey)) changed = true; };
        c.MatchKey = !c.MatchKey;
        Assert.True(changed);
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
