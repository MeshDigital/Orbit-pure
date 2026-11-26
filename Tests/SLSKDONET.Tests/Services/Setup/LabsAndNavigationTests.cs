using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Setup;
using Xunit;

namespace SLSKDONET.Tests.Services.Setup;

/// <summary>
/// Covers Epic #517 Phase 4 — #535 (sidebar regroup), #536 (LabsEnabled default off),
/// #537 (existing-user auto-enable + one-time note + SetupVersion bump), and
/// #538 (deep links / shortcuts into Labs pages raise LabsNavigationBlocked without navigating).
/// </summary>
public class LabsAndNavigationTests
{
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void Sidebar_HasCoreGroups_TransitionsLabel_AndLabsGroup()
    {
        var xaml = ReadRepoFile("Views/Avalonia/MainWindow.axaml");
        Assert.Contains("Text=\"ACQUIRE\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"LIBRARY &amp; PREP\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"PLAY &amp; MIX\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"LABS\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Transitions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Now Playing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Downloads\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LabsMigration_DataPresent_EnablesLabsAndShowsNoteOnce()
    {
        var config = new AppConfig { LabsEnabled = false, LabsMigrationNoteShown = false };

        var first = LabsMigrationService.EvaluateAndApply(config, hasSavedLabsData: true);
        Assert.True(first.EnabledLabs);
        Assert.True(first.ShouldShowNote);
        Assert.True(config.LabsEnabled);
        Assert.True(config.LabsMigrationNoteShown);

        // Second launch: no note shown again
        var second = LabsMigrationService.EvaluateAndApply(config, hasSavedLabsData: true);
        Assert.False(second.EnabledLabs);
        Assert.False(second.ShouldShowNote);
    }

    [Fact]
    public void LabsMigration_NoSavedData_KeepsLabsOffAndNoNote()
    {
        var config = new AppConfig { LabsEnabled = false, LabsMigrationNoteShown = false };

        var result = LabsMigrationService.EvaluateAndApply(config, hasSavedLabsData: false);
        Assert.False(result.EnabledLabs);
        Assert.False(result.ShouldShowNote);
        Assert.False(config.LabsEnabled);
    }

    [Theory]
    [InlineData("Sets")]
    [InlineData("Workstation")]
    [InlineData("EditLab")]
    public void NavigationService_LabsPageWhileLabsOff_RaisesBlockedEventAndDoesNotNavigate(string pageKey)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var nav = new NavigationService(services, NullLogger<NavigationService>.Instance, new PerformanceTracker());
        nav.RegisterPage(pageKey, typeof(object));
        nav.IsLabsEnabled = () => false;

        var blocked = new List<string>();
        bool navigated = false;
        nav.LabsNavigationBlocked += (_, key) => blocked.Add(key);
        nav.Navigated += (_, _) => navigated = true;

        nav.NavigateTo(pageKey);

        Assert.Single(blocked, pageKey);
        Assert.False(navigated);
        Assert.Null(nav.CurrentPage);
    }

    [Fact]
    public void FirstRunDetector_OlderSetupVersion_ReturnsUpgradeWizard()
    {
        var result = FirstRunDetector.Evaluate(
            new FirstRunSignals(
                SetupCompleted: true,
                SetupVersion: FirstRunDetector.CurrentSetupVersion - 1,
                Username: "dj",
                HasStoredCredentials: true,
                DownloadDirectory: "C:/Music",
                SharedFolderPath: null,
                TrackCount: 10,
                LibraryFolderCount: 1),
            fallbackDownloadDirectory: "C:/Default");

        Assert.Equal(FirstRunDecision.UpgradeWizard, result.Decision);
    }
}
