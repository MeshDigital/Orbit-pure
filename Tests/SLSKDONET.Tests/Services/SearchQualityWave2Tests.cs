using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression tests for Epic #543 Wave 2 (#546 SQ2):
/// legacy RankingProfile compatibility, Safety Gates preserved, no placebo controls in SettingsPage.
/// </summary>
public sealed class SearchQualityWave2Tests
{
    [Theory]
    [InlineData("Quality First")]
    [InlineData("Balanced")]
    [InlineData("DJ Mode")]
    [InlineData("Data Saver")]
    [InlineData("Custom")]
    [InlineData("Migrated_DJ")]
    [InlineData("")]
    [InlineData("garbage_value")]
    public void LegacyRankingProfile_LoadsToQualityFirst_AndPreservesSavedSafetyGates(string legacyProfile)
    {
        var config = new AppConfig
        {
            RankingProfile = legacyProfile,
            SearchPolicy = new SearchPolicy
            {
                Priority = SearchPriority.QualityFirst,
                EnforceFileIntegrity = false,
                EnforceStrictTitleMatch = false,
                EnforceDurationMatch = true
            }
        };

        var migration = new ConfigMigrationService(NullLogger<ConfigMigrationService>.Instance);
        migration.Migrate(config);

        Assert.Equal(SearchPriority.QualityFirst, config.SearchPolicy.Priority);
        Assert.False(config.SearchPolicy.EnforceFileIntegrity);
        Assert.False(config.SearchPolicy.EnforceStrictTitleMatch);
        Assert.True(config.SearchPolicy.EnforceDurationMatch);
    }

    [Fact]
    public void SettingsPage_HasNoPlaceboRankingStrategyOrFuzzyToggle_AndHasUnifiedFileChecksAndCustomChip()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "SettingsPage.axaml"));

        Assert.DoesNotContain("RANKING STRATEGY", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectStrategyCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableFuzzyNormalization", xaml, StringComparison.Ordinal);
        Assert.Contains("FILE CHECKS", xaml, StringComparison.Ordinal);
        Assert.Contains("Before download — File integrity", xaml, StringComparison.Ordinal);
        Assert.Contains("After download — Upscale detection", xaml, StringComparison.Ordinal);
        Assert.Contains("SearchProfileCustom", xaml, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root.");
    }
}
