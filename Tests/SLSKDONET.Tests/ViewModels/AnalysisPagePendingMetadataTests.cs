using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
/// Analysis requests for tracks the page hasn't loaded (a just-imported folder) used to add rows
/// literally titled "Selected Artist — Selected Track". They now show "Loading track details…" and
/// are filled in from one batched library lookup.
/// </summary>
[Collection(NonParallelCollection.Name)]
public class AnalysisPagePendingMetadataTests : IDisposable
{
    private readonly EventBusService _bus = new();

    public void Dispose() => _bus.Dispose();

    [Fact]
    public async Task UnknownTrackRequested_GetsRealMetadataFromOneBatchedLookup()
    {
        var library = new Mock<ILibraryService>();
        var lookups = new List<List<string>>();
        library.Setup(s => s.GetLibraryEntriesByHashesAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> hashes) =>
            {
                lock (lookups) lookups.Add(hashes.ToList());
                return hashes.Select(h => new LibraryEntry { UniqueHash = h, Artist = "Real Artist", Title = "Title " + h, FilePath = @"C:\m\" + h + ".flac" }).ToList();
            });

        using var vm = new AnalysisPageViewModel(_bus,
            lifecycleProjectionService: new LifecycleProjectionService(library.Object),
            libraryService: library.Object);
        await WaitUntil(() => !vm.IsLibraryLoading);

        _bus.Publish(new TrackAnalysisRequestedEvent("imported-1"));
        _bus.Publish(new TrackAnalysisRequestedEvent("imported-2"));

        await WaitUntil(() => vm.LibraryTracks.Count(t => t.Artist == "Real Artist") == 2);

        var rows = vm.LibraryTracks.Where(t => t.TrackId.StartsWith("imported-")).OrderBy(t => t.TrackId).ToList();
        Assert.Equal(new[] { "Title imported-1", "Title imported-2" }, rows.Select(r => r.Title));
        Assert.DoesNotContain(vm.LibraryTracks, t => t.Artist == "Selected Artist" || t.Title == "Selected Track");
        Assert.Single(lookups); // both rows resolved by one query
        Assert.All(rows, r => Assert.Equal(AnalysisRunStatus.Queued, r.AnalysisStatus));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
    }
}
