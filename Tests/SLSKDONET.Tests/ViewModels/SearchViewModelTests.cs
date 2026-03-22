using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReactiveUI;
using System.Reactive.Concurrency;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Network;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class SearchViewModelTests
{
    [Fact]
    public async Task ExecuteUnifiedSearchAsync_CancelSearch_ShouldStopListeningWithoutAddingFurtherResults()
    {
        using var scheduler = new EventLoopScheduler();
        RxApp.MainThreadScheduler = scheduler;

        var vm = CreateViewModel((_, token) => InfiniteTrackStream(token));
        vm.SearchQuery = "Artist Track";

        var searchTask = InvokeUnifiedSearchAsync(vm);
        await Task.Delay(150);

        vm.CancelSearchCommand.Execute(null);
        await WaitForTaskAsync(searchTask, TimeSpan.FromSeconds(5));

        var countAfterCancel = vm.SearchResultsView.Count;
        await Task.Delay(300);

        Assert.False(vm.IsSearching);
        Assert.False(vm.IsListening);
        Assert.Equal("Stopped listening", vm.StatusText);
        Assert.Equal(countAfterCancel, vm.SearchResultsView.Count);
    }

    [Fact]
    public async Task ExecuteUnifiedSearchAsync_ShouldBatchUiUpdates_AndReportIdleTelemetryAfterCompletion()
    {
        using var scheduler = new EventLoopScheduler();
        RxApp.MainThreadScheduler = scheduler;

        var tracks = new[]
        {
            CreateTrack("peer-1"),
            CreateTrack("peer-2"),
            CreateTrack("peer-3")
        };

        var vm = CreateViewModel((_, token) => FiniteTrackStream(tracks, token));
        vm.SearchQuery = "Artist Track";

        var collectionEvents = 0;
        ((INotifyCollectionChanged)vm.SearchResultsView).CollectionChanged += (_, __) => collectionEvents++;

        await WaitForTaskAsync(InvokeUnifiedSearchAsync(vm), TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.False(vm.IsSearching);
        Assert.False(vm.IsListening);
        Assert.True(vm.TotalResultsReceived > 0);
        Assert.Equal(vm.TotalResultsReceived, vm.SearchResultsView.Count);
        Assert.Equal(0, vm.ResultsPerSecond);
        Assert.Contains("stream idle", vm.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(collectionEvents <= vm.TotalResultsReceived, $"Expected batched UI updates, got {collectionEvents} collection change events for {vm.TotalResultsReceived} result(s).");
    }

    private static SearchViewModel CreateViewModel(Func<string, CancellationToken, IAsyncEnumerable<Track>> streamFactory)
    {
        var config = new AppConfig
        {
            SearchThrottleDelayMs = 1,
            MaxSearchVariations = 1,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 320,
            SearchTimeout = 5000,
            MinSearchDurationSeconds = 9,
            SearchAccumulatorWindowSeconds = 5,
            RelaxationTimeoutSeconds = 1
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var adapter = new Mock<ISoulseekAdapter>();
        adapter.SetupGet(x => x.IsConnected).Returns(true);
        adapter.SetupGet(x => x.IsLoggedIn).Returns(true);
        adapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string query, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token)
                => streamFactory(query, token));

        var safety = new Mock<ISafetyFilterService>();
        safety.Setup(x => x.EvaluateSafety(It.IsAny<Track>(), It.IsAny<string>()));

        var library = new Mock<ILibraryService>();

        var orchestration = new SearchOrchestrationService(
            NullLogger<SearchOrchestrationService>.Instance,
            adapter.Object,
            new SearchQueryNormalizer(),
            new SearchNormalizationService(NullLogger<SearchNormalizationService>.Instance),
            safety.Object,
            config,
            hardening,
            library.Object);

        var bulkCoordinator = new Mock<IBulkOperationCoordinator>();
        bulkCoordinator.SetupGet(x => x.IsRunning).Returns(false);

        return new SearchViewModel(
            NullLogger<SearchViewModel>.Instance,
            soulseek: null!,
            config,
            configManager: null!,
            importOrchestrator: null!,
            importProviders: Array.Empty<IImportProvider>(),
            importPreviewViewModel: null!,
            userCollectionBrowser: null!,
            downloadManager: null!,
            navigationService: null!,
            fileInteractionService: null!,
            clipboardService: null!,
            searchOrchestration: orchestration,
            fileNameFormatter: null!,
            eventBus,
            bulkCoordinator: bulkCoordinator.Object);
    }

    private static async Task InvokeUnifiedSearchAsync(SearchViewModel vm)
    {
        var method = typeof(SearchViewModel).GetMethod("ExecuteUnifiedSearchAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(vm, null) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.Same(task, completed);
        await task;
    }

    private static Track CreateTrack(string peer) => new()
    {
        Artist = "Artist",
        Title = "Track",
        Filename = $"Artist - Track - {peer}.mp3",
        Format = "mp3",
        Bitrate = 320,
        QueueLength = 0,
        UploadSpeed = 256000,
        Username = peer,
        HasFreeUploadSlot = true,
        Length = 180,
        Size = 7_500_000
    };

    private static async IAsyncEnumerable<Track> FiniteTrackStream(
        IEnumerable<Track> tracks,
        [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            yield return track;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Track> InfiniteTrackStream(
        [EnumeratorCancellation] CancellationToken token)
    {
        var counter = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(50, token);
            yield return CreateTrack($"peer-{++counter}");
        }
    }
}
