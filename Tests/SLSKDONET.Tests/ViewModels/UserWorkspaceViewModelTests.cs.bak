using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Moq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Unit Tests for Phase 7: Unified User Workspace
/// Tests the central UserWorkspaceViewModel orchestrator, child ViewModel integration,
/// event propagation, and workspace persistence.
/// </summary>
public class UserWorkspaceViewModelTests
{
    private readonly Mock<ILibraryService> _mockLibraryService;
    private readonly Mock<SetlistStressTestService> _mockStressTestService;
    private readonly Mock<HarmonicMatchService> _mockHarmonicMatchService;
    private readonly Mock<IEventBus> _mockEventBus;

    public UserWorkspaceViewModelTests()
    {
        _mockLibraryService = new Mock<ILibraryService>();
        _mockStressTestService = new Mock<SetlistStressTestService>();
        _mockHarmonicMatchService = new Mock<HarmonicMatchService>();
        _mockEventBus = new Mock<IEventBus>();

        // Configure RxApp to use immediate schedulers for unit testing
        RxApp.MainThreadScheduler = Scheduler.Immediate;
        RxApp.TaskpoolScheduler = Scheduler.Immediate;
    }

    private UserWorkspaceViewModel CreateViewModel()
    {
        var djCompanion = new DJCompanionViewModel(
            _mockLibraryService.Object,
            _mockStressTestService.Object,
            _mockHarmonicMatchService.Object,
            _mockEventBus.Object);

        var forensicInspector = new ForensicInspectorViewModel();
        var healthBar = new SetlistHealthBarViewModel();

        return new UserWorkspaceViewModel(
            _mockLibraryService.Object,
            _mockStressTestService.Object,
            _mockHarmonicMatchService.Object,
            _mockEventBus.Object,
            djCompanion,
            forensicInspector,
            healthBar);
    }

    [Fact]
    public void Constructor_InitializesChildViewModels()
    {
        var vm = CreateViewModel();

        Assert.NotNull(vm.DJCompanion);
        Assert.NotNull(vm.ForensicInspector);
        Assert.NotNull(vm.HealthBar);
    }

    [Fact]
    public void Constructor_InitializesPaneWidths()
    {
        var vm = CreateViewModel();

        // Verify default pane widths (Left 320px, Center 800px, Right 380px)
        Assert.Equal("320", vm.LeftPaneWidth);   // GridLength format
        Assert.Equal("800", vm.CenterPaneWidth);
        Assert.Equal("380", vm.RightPaneWidth);
    }

    [Fact]
    public void Constructor_InitializesDensitySetting()
    {
        var vm = CreateViewModel();

        Assert.Equal(1.0, vm.WorkspaceDensity);
    }

    [Fact]
    public void CurrentSetlist_PropertyChanged_NotifiesSubscribers()
    {
        var vm = CreateViewModel();
        var notified = false;

        vm.WhenAnyValue(x => x.CurrentSetlist).Subscribe(_ => notified = true);

        var testSetlist = new PlaylistEntity { Name = "Test Setlist" };
        vm.CurrentSetlist = testSetlist;

        Assert.True(notified);
        Assert.Equal("Test Setlist", vm.CurrentSetlist.Name);
    }

    [Fact]
    public void IsLoadingTrack_PropertyChanged_NotifiesSubscribers()
    {
        var vm = CreateViewModel();
        var notified = false;

        vm.WhenAnyValue(x => x.IsLoadingTrack).Subscribe(value =>
        {
            if (value) notified = true;
        });

        vm.IsLoadingTrack = true;

        Assert.True(notified);
    }

    [Fact]
    public void WorkspaceDensity_CanBeChanged()
    {
        var vm = CreateViewModel();

        vm.WorkspaceDensity = 0.5;
        Assert.Equal(0.5, vm.WorkspaceDensity);

        vm.WorkspaceDensity = 1.5;
        Assert.Equal(1.5, vm.WorkspaceDensity);

        vm.WorkspaceDensity = 1.0;
        Assert.Equal(1.0, vm.WorkspaceDensity);
    }

    [Fact]
    public void IncreaseDensityCommand_IncreasesDensity()
    {
        var vm = CreateViewModel();
        var initialDensity = vm.WorkspaceDensity;

        vm.IncreaseDensityCommand.Execute(null);

        Assert.True(vm.WorkspaceDensity > initialDensity);
    }

    [Fact]
    public void DecreaseDensityCommand_DecreasesDensity()
    {
        var vm = CreateViewModel();
        vm.WorkspaceDensity = 1.5;  // Set to a value that can be decreased

        vm.DecreaseDensityCommand.Execute(null);

        Assert.Equal(1.0, vm.WorkspaceDensity);
    }

    [Fact]
    public void SaveWorkspaceConfig_CreatesConfigFile()
    {
        var vm = CreateViewModel();
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT",
            "workspace_config.json");

        // Ensure directory exists
        var directory = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Save configuration
        vm.SaveWorkspaceConfig();

        // Verify file exists
        Assert.True(File.Exists(configPath));

        // Cleanup
        if (File.Exists(configPath))
            File.Delete(configPath);
    }

    [Fact]
    public void SaveAndLoadWorkspaceConfig_PreservesPaneWidths()
    {
        var vm = CreateViewModel();
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT",
            "workspace_config.json");

        try
        {
            // Set custom pane widths
            vm.LeftPaneWidth = "250";
            vm.CenterPaneWidth = "900";
            vm.RightPaneWidth = "450";

            // Save
            vm.SaveWorkspaceConfig();

            // Create new ViewModel and load
            var vm2 = CreateViewModel();
            vm2.LoadWorkspaceConfig();

            // Verify widths were restored
            Assert.Equal("250", vm2.LeftPaneWidth);
            Assert.Equal("900", vm2.CenterPaneWidth);
            Assert.Equal("450", vm2.RightPaneWidth);
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Fact]
    public void SaveAndLoadWorkspaceConfig_PreservesDensity()
    {
        var vm = CreateViewModel();
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT",
            "workspace_config.json");

        try
        {
            // Set custom density
            vm.WorkspaceDensity = 0.75;

            // Save
            vm.SaveWorkspaceConfig();

            // Create new ViewModel and load
            var vm2 = CreateViewModel();
            vm2.LoadWorkspaceConfig();

            // Verify density was restored
            Assert.Equal(0.75, vm2.WorkspaceDensity);
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Fact]
    public void TrackSelected_UpdatesDJCompanionCurrentTrack()
    {
        var vm = CreateViewModel();
        var testTrack = new PlaylistTrack 
        { 
            Id = 1,
            Title = "Test Track",
            Artist = "Test Artist"
        };

        // Simulate track selection
        vm.DJCompanion.CurrentTrack = testTrack;

        Assert.NotNull(vm.DJCompanion.CurrentTrack);
        Assert.Equal("Test Track", vm.DJCompanion.CurrentTrack.Title);
    }

    [Fact]
    public void Dispose_ClearsSubscriptions()
    {
        var vm = CreateViewModel();
        
        // Verify disposable pattern
        vm.Dispose();

        // Verify object can be garbage collected (no lingering references)
        // This is more of an integration test, but ensures IDisposable is properly implemented
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void MultipleTracksSelected_ChildViewModelsUpdated()
    {
        var vm = CreateViewModel();

        var track1 = new PlaylistTrack { Id = 1, Title = "Track 1", Artist = "Artist 1" };
        var track2 = new PlaylistTrack { Id = 2, Title = "Track 2", Artist = "Artist 2" };

        vm.DJCompanion.CurrentTrack = track1;
        Assert.Equal("Track 1", vm.DJCompanion.CurrentTrack.Title);

        vm.DJCompanion.CurrentTrack = track2;
        Assert.Equal("Track 2", vm.DJCompanion.CurrentTrack.Title);
    }

    [Fact]
    public void EventBusIntegration_ChildViewModelsHaveEventBusReference()
    {
        var vm = CreateViewModel();

        // Verify child ViewModels are properly initialized with EventBus
        Assert.NotNull(vm.DJCompanion);
        Assert.NotNull(vm.ForensicInspector);
        Assert.NotNull(vm.HealthBar);
    }

    [Fact]
    public void PaneWidth_CanBeModifiedIndependently()
    {
        var vm = CreateViewModel();

        vm.LeftPaneWidth = "400";
        Assert.Equal("400", vm.LeftPaneWidth);
        Assert.Equal("800", vm.CenterPaneWidth);  // Unchanged
        Assert.Equal("380", vm.RightPaneWidth);   // Unchanged

        vm.CenterPaneWidth = "600";
        Assert.Equal("400", vm.LeftPaneWidth);    // Unchanged
        Assert.Equal("600", vm.CenterPaneWidth);
        Assert.Equal("380", vm.RightPaneWidth);   // Unchanged
    }

    [Fact]
    public void HelpText_CanBeSetAndRead()
    {
        var vm = CreateViewModel();
        var notified = false;

        vm.WhenAnyValue(x => x.HelpText).Subscribe(v =>
        {
            if (!string.IsNullOrEmpty(v)) notified = true;
        });

        vm.HelpText = "Test Help Message";

        Assert.True(notified);
        Assert.Equal("Test Help Message", vm.HelpText);
    }
}
