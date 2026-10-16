using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReactiveUI;
using System.Reactive.Concurrency;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Network;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

[Collection(NonParallelCollection.Name)]
public class SearchViewModelTests
{
    /// <summary>
    /// Points RxApp.MainThreadScheduler at <paramref name="scheduler"/> for one test and restores the
    /// previous scheduler on dispose. These tests used to replace the process-wide scheduler with an
    /// EventLoopScheduler and then dispose it, leaving every later ReactiveUI test in the run on a
    /// dead scheduler (ObjectDisposedException "EventLoopScheduler" — the source of the random
    /// AnalysisPageViewModel/WorkstationDeck/DownloadCenter failures). Declare it after the scheduler
    /// so it restores before the scheduler is disposed.
    /// </summary>
    /// <summary>
    /// A background-thread event loop that is deliberately never disposed. In unit-test mode
    /// ReactiveUI can keep the first MainThreadScheduler it was given as its permanent fallback,
    /// so a disposed one resurfaced in later tests (ObjectDisposedException in WorkstationDeck /
    /// AnalysisPage tests, depending on test order). An idle background thread costs nothing.
    /// </summary>
    private static EventLoopScheduler UndisposedEventLoop() =>
        new(start => new System.Threading.Thread(start) { IsBackground = true, Name = "SearchVmTest loop" });

    private static IDisposable UseMainThreadScheduler(IScheduler scheduler)
    {
        var previous = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = scheduler;
        return System.Reactive.Disposables.Disposable.Create(() => RxApp.MainThreadScheduler = previous);
    }

    [Fact]
    public async Task ExecuteUnifiedSearchAsync_CancelSearch_ShouldStopListeningWithoutAddingFurtherResults()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

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
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

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

    [Fact]
    public async Task AddToPlaylistCommand_PublishesSelectedResultsToProjectFlow()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var (vm, eventBus) = CreateViewModelWithBus((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        var searchResult = new AnalyzedSearchResultViewModel(new SLSKDONET.ViewModels.SearchResult(CreateTrack("mix-peer")));
        vm.SelectedResults.Add(searchResult);

        AddToProjectRequestEvent? captured = null;
        eventBus.GetEvent<AddToProjectRequestEvent>().Subscribe(evt => captured = evt);

        vm.AddToPlaylistCommand.Execute(null);
        await Task.Delay(50);

        Assert.NotNull(captured);
        Assert.Single(captured!.Tracks);
        Assert.True(searchResult.RawResult.IsAddedToProject);
        Assert.Contains("Add to Mix", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConvertToPlaylistTrack_MapsSearchTrackMetadata()
    {
        var track = CreateTrack("peer-map");
        track.BPM = 128;
        track.MusicalKey = "8A";
        track.Energy = 0.74;
        track.AlbumArtUrl = "https://example.com/art.jpg";

        var mapped = SearchViewModel.ConvertToPlaylistTrack(track);

        Assert.NotNull(mapped);
        Assert.Equal("Artist", mapped!.Artist);
        Assert.Equal("Track", mapped.Title);
        Assert.Equal(128, mapped.BPM);
        Assert.Equal("8A", mapped.MusicalKey);
        Assert.Equal(0.74, mapped.Energy);
        Assert.Equal("https://example.com/art.jpg", mapped.AlbumArtUrl);
    }

    [Fact]
    public async Task Search_KeepsStripFilters_AndRecordsRecentSearch()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(new[] { CreateTrack("peer-1") }, token));
        vm.FilterViewModel.MinBitrate = 192;
        vm.FilterViewModel.HideSuspects = false;
        vm.SearchQuery = "Artist Track";

        await WaitForTaskAsync(InvokeUnifiedSearchAsync(vm), TimeSpan.FromSeconds(5));

        // Searching used to call FilterViewModel.Reset(), silently undoing the strip.
        Assert.Equal(192, vm.FilterViewModel.MinBitrate);
        Assert.Equal(192, vm.MinBitrate);
        Assert.False(vm.FilterViewModel.HideSuspects);
        Assert.Equal("Artist Track", vm.RecentSearches.First());
        Assert.True(vm.HasSearched);
        Assert.Equal(1, vm.PeersResponded);
    }

    [Fact]
    public async Task Search_FormatTokens_UpdateChipsAndAreStrippedFromQuery()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        var flacChipRaised = false;
        vm.PropertyChanged += (_, e) => flacChipRaised |= e.PropertyName == nameof(SearchViewModel.IsMp3Enabled);
        vm.SearchQuery = "Artist Track flac";

        await WaitForTaskAsync(InvokeUnifiedSearchAsync(vm), TimeSpan.FromSeconds(5));

        Assert.Equal("Artist Track", vm.SearchQuery);
        Assert.True(vm.IsFlacEnabled);
        Assert.False(vm.IsMp3Enabled);
        Assert.True(flacChipRaised, "The MP3 chip must be notified when a query token turns it off.");
    }

    [Fact]
    public async Task Search_WithNoResults_ShowsNoResultsState()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        Assert.True(vm.ShowNeverSearchedEmptyState);
        vm.SearchQuery = "nothing matches this";

        await WaitForTaskAsync(InvokeUnifiedSearchAsync(vm), TimeSpan.FromSeconds(5));

        Assert.False(vm.ShowNeverSearchedEmptyState);
        Assert.True(vm.ShowNoResultsState);
        Assert.False(vm.ShowErrorState);
        Assert.Equal("No results found", vm.StatusText);
    }

    [Fact]
    public async Task ApplyDownloadState_MarksMatchingResultsQueuedThenDownloaded()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(new[] { CreateTrack("peer-1") }, token));
        vm.SearchQuery = "Artist Track";
        await WaitForTaskAsync(InvokeUnifiedSearchAsync(vm), TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        var row = vm.SearchResults.Single();
        var hash = row.RawResult.Model.UniqueHash;

        vm.ApplyDownloadState(hash, PlaylistTrackState.Downloading);
        Assert.True(row.IsQueued);

        vm.ApplyDownloadState(hash, PlaylistTrackState.Completed);
        Assert.True(row.IsDownloaded);
        Assert.True(row.HasDownloadStatus);
    }

    [Fact]
    public void PeerScope_RequiresUsernameToSearch()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        vm.SearchQuery = "Artist Track";
        vm.IsPeerScope = true;

        Assert.False(vm.IsNetworkScope);
        Assert.False(vm.UnifiedSearchCommand.CanExecute(null));

        vm.PeerUsername = "someone";
        Assert.True(vm.UnifiedSearchCommand.CanExecute(null));
    }

    [Fact]
    public void SortBy_SameColumnFlipsDirection_NewColumnUsesNaturalDirection()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        Assert.True(vm.IsSortedByMatch);
        Assert.True(vm.SortDescending);

        vm.SortBy(SearchSortColumn.Match);
        Assert.False(vm.SortDescending);

        vm.SortBy(SearchSortColumn.Title);
        Assert.True(vm.IsSortedByTitle);
        Assert.False(vm.SortDescending); // A→Z first

        vm.SortBy(SearchSortColumn.Size);
        Assert.True(vm.SortDescending); // largest first
    }

    [Fact]
    public void RecentSearches_AreDeduplicatedAndCapped()
    {
        var scheduler = UndisposedEventLoop();
        using var _ = UseMainThreadScheduler(scheduler);

        var vm = CreateViewModel((_, token) => FiniteTrackStream(Array.Empty<Track>(), token));
        for (var i = 0; i < 12; i++) vm.AddRecentSearch($"query {i}");
        vm.AddRecentSearch("QUERY 5");

        Assert.Equal(8, vm.RecentSearches.Count);
        Assert.Equal("QUERY 5", vm.RecentSearches[0]);
        Assert.Single(vm.RecentSearches, q => q.Equals("query 5", StringComparison.OrdinalIgnoreCase));
    }

    private static SearchViewModel CreateViewModel(Func<string, CancellationToken, IAsyncEnumerable<Track>> streamFactory)
    {
        return CreateViewModelWithBus(streamFactory).vm;
    }

    private static (SearchViewModel vm, EventBusService eventBus) CreateViewModelWithBus(Func<string, CancellationToken, IAsyncEnumerable<Track>> streamFactory)
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
                It.IsAny<CancellationToken>(),
                It.IsAny<SearchScopeKind>()))
            .Returns((string query, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token, SearchScopeKind _)
                => streamFactory(query, token));

        var safety = new Mock<ISafetyFilterService>();
        safety.Setup(x => x.EvaluateSafety(It.IsAny<Track>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<SearchPolicy>()));

        var library = new Mock<ILibraryService>();

        var orchestration = new SearchOrchestrationService(
            NullLogger<SearchOrchestrationService>.Instance,
            adapter.Object,
            new SearchQueryNormalizer(),
            new SearchNormalizationService(NullLogger<SearchNormalizationService>.Instance),
            safety.Object,
            config,
            hardening,
            library.Object,
            eventBus,
            new EngineDiagnosticsService(
                Mock.Of<IDbContextFactory<AppDbContext>>(),
                eventBus,
                NullLogger<EngineDiagnosticsService>.Instance));

        var bulkCoordinator = new Mock<IBulkOperationCoordinator>();
        bulkCoordinator.SetupGet(x => x.IsRunning).Returns(false);

        var vm = new SearchViewModel(
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

        return (vm, eventBus);
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
