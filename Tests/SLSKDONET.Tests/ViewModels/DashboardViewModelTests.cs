using System;
using System.IO;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="DashboardViewModel"/>.
/// Verifies that the three-column layout state is correctly coordinated and
/// that layout persistence round-trips cleanly through <see cref="AppConfig"/>
/// and <see cref="ConfigManager"/>.
/// </summary>
public class DashboardViewModelTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static (DashboardViewModel sut, AppConfig config, ConfigManager configManager, IRightPanelService service)
        BuildSut(AppConfig? config = null, IRightPanelService? service = null)
    {
        config ??= new AppConfig
        {
            DashboardRightPanelWidth = 320,
            DashboardIsNavigationCollapsed = false,
            DashboardIsRightPanelOpen = true
        };

        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-dashboard-test-{Guid.NewGuid():N}.ini");
        var configManager = new ConfigManager(tempPath);
        var rightPanelService = service ?? new RightPanelService();
        var sut = new DashboardViewModel(rightPanelService, config, configManager);
        return (sut, config, configManager, rightPanelService);
    }

    // ── Constructor / RestoreLayout ────────────────────────────────────────

    [Fact]
    public void Constructor_RestoresRightPanelWidthFromConfig()
    {
        var config = new AppConfig { DashboardRightPanelWidth = 400 };
        var (sut, _, _, _) = BuildSut(config);

        Assert.Equal(400, sut.RightPanelWidth);
        sut.Dispose();
    }

    [Fact]
    public void Constructor_RestoresNavigationCollapsedFromConfig()
    {
        var config = new AppConfig { DashboardIsNavigationCollapsed = true };
        var (sut, _, _, _) = BuildSut(config);

        Assert.True(sut.IsNavigationCollapsed);
        sut.Dispose();
    }

    [Fact]
    public void Constructor_RestoresRightPanelOpenFromConfig()
    {
        var config = new AppConfig { DashboardIsRightPanelOpen = false };
        var (sut, _, _, _) = BuildSut(config);

        Assert.False(sut.IsRightPanelOpen);
        sut.Dispose();
    }

    [Fact]
    public void Constructor_ClampsRightPanelWidthToMinimum()
    {
        var config = new AppConfig { DashboardRightPanelWidth = 50 }; // below the 200 floor
        var (sut, _, _, _) = BuildSut(config);

        Assert.Equal(200, sut.RightPanelWidth);
        sut.Dispose();
    }

    // ── Property setters ───────────────────────────────────────────────────

    [Fact]
    public void IsNavigationCollapsed_Setter_UpdatesProperty()
    {
        var (sut, _, _, _) = BuildSut();

        sut.IsNavigationCollapsed = true;

        Assert.True(sut.IsNavigationCollapsed);
        sut.Dispose();
    }

    [Fact]
    public void IsRightPanelOpen_Setter_DelegatesToRightPanelService()
    {
        var serviceMock = new Mock<IRightPanelService>();
        serviceMock.SetupProperty(s => s.IsPanelOpen, true);
        var (sut, _, _, _) = BuildSut(service: serviceMock.Object);

        sut.IsRightPanelOpen = false;

        serviceMock.VerifySet(s => s.IsPanelOpen = false, Times.Once);
        sut.Dispose();
    }

    [Fact]
    public void RightPanelWidth_Setter_ClampsToMinimum()
    {
        var (sut, _, _, _) = BuildSut();

        sut.RightPanelWidth = 10;

        Assert.Equal(200, sut.RightPanelWidth);
        sut.Dispose();
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [Fact]
    public void ToggleNavigationCommand_TogglesIsNavigationCollapsed()
    {
        var (sut, _, _, _) = BuildSut();
        Assert.False(sut.IsNavigationCollapsed);

        sut.ToggleNavigationCommand.Execute(null);

        Assert.True(sut.IsNavigationCollapsed);
        sut.Dispose();
    }

    [Fact]
    public void ToggleNavigationCommand_TogglesBackWhenCalledTwice()
    {
        var (sut, _, _, _) = BuildSut();

        sut.ToggleNavigationCommand.Execute(null);
        sut.ToggleNavigationCommand.Execute(null);

        Assert.False(sut.IsNavigationCollapsed);
        sut.Dispose();
    }

    [Fact]
    public void ToggleRightPanelCommand_TogglesIsRightPanelOpen()
    {
        var config = new AppConfig { DashboardIsRightPanelOpen = true };
        var (sut, _, _, _) = BuildSut(config);

        sut.ToggleRightPanelCommand.Execute(null);

        Assert.False(sut.IsRightPanelOpen);
        sut.Dispose();
    }

    // ── Layout persistence (SaveLayout / RestoreLayout) ────────────────────

    [Fact]
    public void SaveLayout_WritesCurrentStateToConfig()
    {
        var (sut, config, _, _) = BuildSut();
        sut.IsNavigationCollapsed = true;
        sut.RightPanelWidth = 500;
        sut.IsRightPanelOpen = false;

        sut.SaveLayout();

        Assert.True(config.DashboardIsNavigationCollapsed);
        Assert.Equal(500, config.DashboardRightPanelWidth);
        Assert.False(config.DashboardIsRightPanelOpen);
        sut.Dispose();
    }

    [Fact]
    public void SaveLayout_PersistsToDiskAndCanBeReadBack()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-dashboard-roundtrip-{Guid.NewGuid():N}.ini");
        try
        {
            var config = new AppConfig
            {
                DashboardRightPanelWidth = 380,
                DashboardIsNavigationCollapsed = true,
                DashboardIsRightPanelOpen = false
            };
            var configManager = new ConfigManager(tempPath);
            var service = new RightPanelService();
            var sut = new DashboardViewModel(service, config, configManager);

            sut.SaveLayout();
            sut.Dispose();

            // Load the saved config back
            var restoredConfig = configManager.Load();
            Assert.Equal(380, restoredConfig.DashboardRightPanelWidth);
            Assert.True(restoredConfig.DashboardIsNavigationCollapsed);
            Assert.False(restoredConfig.DashboardIsRightPanelOpen);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void RestoreLayout_ReloadsFromConfig()
    {
        var config = new AppConfig
        {
            DashboardRightPanelWidth = 300,
            DashboardIsNavigationCollapsed = false,
            DashboardIsRightPanelOpen = true
        };
        var (sut, _, _, _) = BuildSut(config);

        // Change state in memory
        sut.IsNavigationCollapsed = true;
        sut.RightPanelWidth = 999;

        // Restore from the original config values
        sut.RestoreLayout();

        Assert.Equal(300, sut.RightPanelWidth);
        Assert.False(sut.IsNavigationCollapsed);
        Assert.True(sut.IsRightPanelOpen);
        sut.Dispose();
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var (sut, _, _, _) = BuildSut();
        var ex = Record.Exception(() => sut.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_ThrowsOnNullRightPanelService()
    {
        var config = new AppConfig();
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-null-test-{Guid.NewGuid():N}.ini");
        var cm = new ConfigManager(tempPath);

        Assert.Throws<ArgumentNullException>(() => new DashboardViewModel(null!, config, cm));
    }

    [Fact]
    public void Constructor_ThrowsOnNullConfig()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbit-null-test-{Guid.NewGuid():N}.ini");
        var cm = new ConfigManager(tempPath);

        Assert.Throws<ArgumentNullException>(() => new DashboardViewModel(new RightPanelService(), null!, cm));
    }

    [Fact]
    public void Constructor_ThrowsOnNullConfigManager()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DashboardViewModel(new RightPanelService(), new AppConfig(), null!));
    }
}
