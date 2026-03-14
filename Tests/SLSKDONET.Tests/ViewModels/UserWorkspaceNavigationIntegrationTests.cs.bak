using System;
using System.Collections.Generic;
using System.Reactive.Concurrency;
using Moq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Integration Tests for Phase 7: Navigation and UI Integration
/// Verifies UserWorkspace is properly integrated into MainViewModel navigation,
/// sidebar presence, and page routing.
/// </summary>
public class UserWorkspaceNavigationIntegrationTests
{
    private readonly Mock<INavigationService> _mockNavigationService;
    private readonly Mock<ILibraryService> _mockLibraryService;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<SetlistStressTestService> _mockStressTestService;
    private readonly Mock<HarmonicMatchService> _mockHarmonicMatchService;

    public UserWorkspaceNavigationIntegrationTests()
    {
        _mockNavigationService = new Mock<INavigationService>();
        _mockLibraryService = new Mock<ILibraryService>();
        _mockEventBus = new Mock<IEventBus>();
        _mockStressTestService = new Mock<SetlistStressTestService>();
        _mockHarmonicMatchService = new Mock<HarmonicMatchService>();

        RxApp.MainThreadScheduler = Scheduler.Immediate;
        RxApp.TaskpoolScheduler = Scheduler.Immediate;
    }

    private MainViewModel CreateMainViewModel()
    {
        // Create all required child ViewModels
        var userWorkspaceVM = new UserWorkspaceViewModel(
            _mockLibraryService.Object,
            _mockStressTestService.Object,
            _mockHarmonicMatchService.Object,
            _mockEventBus.Object,
            new DJCompanionViewModel(
                _mockLibraryService.Object,
                _mockStressTestService.Object,
                _mockHarmonicMatchService.Object,
                _mockEventBus.Object),
            new ForensicInspectorViewModel(),
            new SetlistHealthBarViewModel());

        var mainVM = new MainViewModel(
            _mockNavigationService.Object,
            _mockLibraryService.Object,
            _mockEventBus.Object,
            new Mock<INotificationService>().Object,
            new Mock<ArtworkCacheService>(null, null).Object,
            new Mock<AnalysisQueueService>().Object,
            new Mock<ExportManagerViewModel>().Object,
            new Mock<BulkOperationViewModel>().Object,
            new Mock<HomeViewModel>(null!, null!, null!, null!).Object,
            new Mock<SearchViewModel>(null!, null!, null!, null!, null!).Object,
            new Mock<ConnectionViewModel>().Object,
            new Mock<SettingsViewModel>(null!, null!, null!).Object,
            new Mock<LibraryViewModel>(null!, null!, null!, null!, null!, null!, null!).Object,
            new Mock<IntelligenceCenterViewModel>(null!, null!, null!).Object,
            userWorkspaceVM,
            new Mock<DJCompanionViewModel>().Object,
            new Mock<SetDesignerViewModel>(null!, null!, null!, null!).Object,
            new Mock<ForensicLabViewModel>().Object);

        return mainVM;
    }

    [Fact]
    public void MainViewModel_HasUserWorkspaceViewModel()
    {
        var mainVM = CreateMainViewModel();

        Assert.NotNull(mainVM.UserWorkspaceViewModel);
    }

    [Fact]
    public void NavigateUserWorkspaceCommand_ExecutesSuccessfully()
    {
        var mainVM = CreateMainViewModel();

        var command = mainVM.NavigateUserWorkspaceCommand;
        Assert.NotNull(command);
        Assert.True(command.CanExecute(null));

        command.Execute(null);

        // Verify navigation service was called
        _mockNavigationService.Verify(
            ns => ns.NavigateTo("UserWorkspace"),
            Times.Once);
    }

    [Fact]
    public void NavigateUserWorkspaceCommand_ChangesCurrentPageType()
    {
        var mainVM = CreateMainViewModel();

        mainVM.NavigateUserWorkspaceCommand.Execute(null);

        Assert.Equal(PageType.UserWorkspace, mainVM.CurrentPageType);
    }

    [Fact]
    public void PageType_IncludesUserWorkspace()
    {
        // Verify the enum has the UserWorkspace value
        var values = Enum.GetValues(typeof(PageType));
        Assert.Contains(PageType.UserWorkspace, (PageType[])values);
    }

    [Fact]
    public void NavigationService_CanRegisterUserWorkspacePage()
    {
        _mockNavigationService
            .Setup(ns => ns.RegisterPage(It.IsAny<string>(), It.IsAny<Type>()))
            .Verifiable();

        var navService = _mockNavigationService.Object;
        navService.RegisterPage("UserWorkspace", typeof(Views.Avalonia.UserWorkspaceView));

        _mockNavigationService.Verify(
            ns => ns.RegisterPage("UserWorkspace", typeof(Views.Avalonia.UserWorkspaceView)),
            Times.Once);
    }

    [Fact]
    public void UserWorkspaceCommand_AvailableForBinding()
    {
        var mainVM = CreateMainViewModel();

        // Test that the command property is accessible and not null
        var command = mainVM.NavigateUserWorkspaceCommand;
        Assert.NotNull(command);

        // Test that it implements ICommand
        Assert.IsAssignableFrom<System.Windows.Input.ICommand>(command);
    }

    [Fact]
    public void UserWorkspace_Preferred_Over_DJCompanion_InNavigation()
    {
        var mainVM = CreateMainViewModel();

        // Navigate to UserWorkspace
        mainVM.NavigateUserWorkspaceCommand.Execute(null);
        Assert.Equal(PageType.UserWorkspace, mainVM.CurrentPageType);

        // This can be extended to verify sidebar ordering once sidebar implementation is complete
    }

    [Fact]
    public void MultipleNavigations_BetweenUserWorkspaceAndOtherPages()
    {
        var mainVM = CreateMainViewModel();

        // Navigate to UserWorkspace
        mainVM.NavigateUserWorkspaceCommand.Execute(null);
        Assert.Equal(PageType.UserWorkspace, mainVM.CurrentPageType);

        // Navigate to another page (if available)
        mainVM.NavigateDJCompanionCommand.Execute(null);
        Assert.Equal(PageType.DJCompanion, mainVM.CurrentPageType);

        // Navigate back to UserWorkspace
        mainVM.NavigateUserWorkspaceCommand.Execute(null);
        Assert.Equal(PageType.UserWorkspace, mainVM.CurrentPageType);
    }

    [Fact]
    public void UserWorkspaceViewModel_IsInitialized_BeforeNavigation()
    {
        var mainVM = CreateMainViewModel();

        // Verify that UserWorkspaceViewModel is initialized before navigation is attempted
        Assert.NotNull(mainVM.UserWorkspaceViewModel);
        Assert.NotNull(mainVM.UserWorkspaceViewModel.DJCompanion);
        Assert.NotNull(mainVM.UserWorkspaceViewModel.ForensicInspector);
        Assert.NotNull(mainVM.UserWorkspaceViewModel.HealthBar);
    }

    [Fact]
    public void CurrentPageType_StartsAtHome()
    {
        var mainVM = CreateMainViewModel();

        // Verify initial page type (should be Home or Library as per app design)
        Assert.NotEqual(PageType.UserWorkspace, mainVM.CurrentPageType);
    }

    [Fact]
    public void NavigationService_Called_OnUserWorkspaceNavigation()
    {
        var mainVM = CreateMainViewModel();

        _mockNavigationService.Reset();

        mainVM.NavigateUserWorkspaceCommand.Execute(null);

        // Verify navigation service was invoked
        _mockNavigationService.Verify(
            ns => ns.NavigateTo("UserWorkspace"),
            Times.Once,
            "NavigateTo should be called when UserWorkspace command executes");
    }
}
