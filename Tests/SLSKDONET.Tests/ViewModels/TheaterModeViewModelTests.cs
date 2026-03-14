using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Moq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Tagging; // For IUniversalCueService
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Tests.ViewModels;

public class TheaterModeViewModelTests
{
    private readonly Mock<INavigationService> _mockNavigationService;
    private readonly Mock<ILibraryService> _mockLibraryService;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<ArtworkCacheService> _mockArtworkCache; // Assume we can mock it or pass null if not strictly used in VM logic

    public TheaterModeViewModelTests()
    {
        _mockNavigationService = new Mock<INavigationService>();
        _mockLibraryService = new Mock<ILibraryService>();
        _mockEventBus = new Mock<IEventBus>();
        
        // Setup RxApp to use immediate schedulers for testing to avoid threading issues
        RxApp.MainThreadScheduler = Scheduler.Immediate;
        RxApp.TaskpoolScheduler = Scheduler.Immediate;
        
        // Mock ArtworkCacheService (it's a class, assuming virtuals or we might need valid instance)
        // Since it's passed to PlaylistTrackViewModel, we can mock it if methods are virtual, 
        // or just pass null if PTVM handles null (check PTVM). 
        // For now, let's try Mock.
        _mockArtworkCache = new Mock<ArtworkCacheService>(null, null); 
    }

    private TheaterModeViewModel CreateViewModel()
    {
        // We pass null for PlayerViewModel as it is only used for property exposure, not internal logic
        return new TheaterModeViewModel(
            null!, 
            _mockNavigationService.Object,
            _mockLibraryService.Object,
            _mockEventBus.Object,
            _mockArtworkCache.Object,
            null!, // StemSeparationService
            null!, // RealTimeStemEngine
            new Mock<IUniversalCueService>().Object,
            null!  // WaveformAnalysisService
        );
    }

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsLibraryVisible);
        Assert.True(vm.IsTechnicalVisible);
        Assert.Empty(vm.SearchResults);
        Assert.Equal(0, vm.LibraryTabIndex);
    }

    [Fact]
    public void ToggleLibraryCommand_TogglesVisibility()
    {
        var vm = CreateViewModel();

        vm.ToggleLibraryCommand.Execute(null);
        Assert.False(vm.IsLibraryVisible);

        vm.ToggleLibraryCommand.Execute(null);
        Assert.True(vm.IsLibraryVisible);
    }

    [Fact]
    public void ToggleTechnicalCommand_TogglesVisibility()
    {
        var vm = CreateViewModel();

        vm.ToggleTechnicalCommand.Execute(null);
        Assert.False(vm.IsTechnicalVisible);

        vm.ToggleTechnicalCommand.Execute(null);
        Assert.True(vm.IsTechnicalVisible);
    }

    [Fact]
    public void CloseTheaterCommand_NavigatesBack()
    {
        var vm = CreateViewModel();

        vm.CloseTheaterCommand.Execute(null);

        _mockNavigationService.Verify(x => x.GoBack(), Times.Once);
    }

    [Fact]
    public async Task SearchText_TriggersSearch_AndSwitchesTab()
    {
        var vm = CreateViewModel();
        var searchResults = new List<LibraryEntry>
        {
            new LibraryEntry { UniqueHash = "abc", Title = "Test Track", FilePath = "C:/Music/Test.mp3" }
        };

        // Setup LibraryService Mock
        _mockLibraryService
            .Setup(x => x.SearchLibraryEntriesWithStatusAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(searchResults);

        // Act
        vm.SearchText = "Test";

        // Allow throttle time (VM uses 300ms throttle)
        // Since we replaced Scheduler with Immediate, Throttle might behave differently or we need to wait.
        // ReactiveUI Throttle uses the scheduler. 
        // With Scheduler.Immediate, it might still need a delay or manual tick if using TestScheduler.
        // But Scheduler.Immediate executes immediately? 
        // Actually Throttle with Immediate might not work as expected without virtual time.
        // Instead of fighting Throttle, let's call the private PerformSearchAsync logic via reflection 
        // OR relying on the fact that we can't easily test Throttle execution without TestScheduler.
        
        // Wait loop for simplicity in this context
        await Task.Delay(400); 

        // Assert
        Assert.Single(vm.SearchResults);
        Assert.Equal("Test Track", vm.SearchResults[0].Title);
        Assert.Equal(1, vm.LibraryTabIndex); // Verifies tab switch
    }

    [Fact]
    public void PlayTrack_PublishesEvent()
    {
        var vm = CreateViewModel();
        var trackVm = new PlaylistTrackViewModel(
            new PlaylistTrack { Title = "Song", ResolvedFilePath = "path.mp3" }, 
            _mockEventBus.Object,
            _mockLibraryService.Object,
            _mockArtworkCache.Object);

        vm.PlayTrackCommand.Execute(trackVm);

        _mockEventBus.Verify(x => x.Publish(It.Is<PlayTrackRequestEvent>(e => e.Track == trackVm)), Times.Once);
    }
    
    [Fact]
    public void AddToQueue_PublishesEvent()
    {
        var vm = CreateViewModel();
        var trackVm = new PlaylistTrackViewModel(
            new PlaylistTrack { Title = "Song", ResolvedFilePath = "path.mp3" }, 
            _mockEventBus.Object,
            _mockLibraryService.Object,
            _mockArtworkCache.Object);

        vm.AddToQueueCommand.Execute(trackVm);

        _mockEventBus.Verify(x => x.Publish(It.Is<AddToQueueRequestEvent>(e => e.Track == trackVm)), Times.Once);
    }
}
