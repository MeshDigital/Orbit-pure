using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.Setup;
using SLSKDONET.ViewModels.Setup;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Regression tests for Epic #508 Wave 1 & Wave 2 (#512 SP1, #513 SP2, #514 SP3, #516 SP5).
/// </summary>
public sealed class SpotifyWave1And2Tests
{
    [Fact]
    public void Migration_KeepsBuiltInOnlyWithVerifiedSession_ClearsLegacyOtherwise_LeavesCustomUntouched()
    {
        var saved = false;

        // 1. Built-in ID with verified session -> kept
        var verifiedConfig = new AppConfig { SpotifyClientId = SpotifyAuthService.DefaultClientId };
        var kept = SpotifyClientIdMigrationService.MigrateIfNeeded(verifiedConfig, hasVerifiedBuiltInSession: true, _ => saved = true);
        Assert.Equal(SpotifyClientIdMigrationOutcome.KeptBuiltInWithActiveSession, kept);
        Assert.Equal(SpotifyAuthService.DefaultClientId, verifiedConfig.SpotifyClientId);
        Assert.False(saved);

        // 2. Built-in ID without verified session -> cleared
        var legacyConfig = new AppConfig { SpotifyClientId = SpotifyAuthService.DefaultClientId };
        var cleared = SpotifyClientIdMigrationService.MigrateIfNeeded(legacyConfig, hasVerifiedBuiltInSession: false, _ => saved = true);
        Assert.Equal(SpotifyClientIdMigrationOutcome.ClearedLegacyBuiltIn, cleared);
        Assert.Equal(string.Empty, legacyConfig.SpotifyClientId);
        Assert.True(saved);

        // 3. Custom ID -> untouched
        var customId = "0123456789abcdef0123456789abcdef";
        var customConfig = new AppConfig { SpotifyClientId = customId };
        var customOutcome = SpotifyClientIdMigrationService.MigrateIfNeeded(customConfig, hasVerifiedBuiltInSession: false);
        Assert.Equal(SpotifyClientIdMigrationOutcome.UnchangedCustom, customOutcome);
        Assert.Equal(customId, customConfig.SpotifyClientId);

        // 4. Fresh install (empty) -> unchanged
        var freshConfig = new AppConfig();
        Assert.Equal(SpotifyClientIdMigrationOutcome.UnchangedNone,
            SpotifyClientIdMigrationService.MigrateIfNeeded(freshConfig, hasVerifiedBuiltInSession: false));
        Assert.Equal("none configured", SpotifyClientIdMigrationService.DescribeKind(freshConfig.SpotifyClientId));
    }

    [Fact]
    public void ScrapedBatch_SetsTruncationNotice_WhenAt100TracksOrTotalExceedsCount()
    {
        var hundred = Enumerable.Range(1, 100)
            .Select(i => new SearchQuery { Artist = "A", Title = $"T{i}", SourceTitle = "Big Mix", TotalTracks = 123 })
            .ToList();

        var batch = SpotifyImportProvider.ScrapedBatch(hundred);

        Assert.NotNull(batch.Notice);
        Assert.Contains("100", batch.Notice);
        Assert.Contains("123", batch.Notice);
        Assert.Equal(123, batch.TotalEstimated);

        var small = new List<SearchQuery>
        {
            new() { Artist = "A", Title = "T1", SourceTitle = "Small", TotalTracks = 1 }
        };
        Assert.Null(SpotifyImportProvider.ScrapedBatch(small).Notice);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("ABCDEF0123456789abcdef0123456789", true)]
    [InlineData("too-short", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)] // non-hex 'g'
    public void OwnSpotifyApp_Validates32HexClientId(string input, bool expectedValid)
    {
        var error = OwnSpotifyAppViewModel.ValidateClientId(input);
        Assert.Equal(expectedValid, error == null);
    }

    [Fact]
    public void OwnSpotifyApp_WritesValidClientId_AndRejectsInvalidWithoutOverwriting()
    {
        string stored = string.Empty;
        var vm = new OwnSpotifyAppViewModel(
            () => stored,
            v => stored = v,
            "http://127.0.0.1:5000/callback",
            () => { },
            () => { });

        Assert.True(vm.IsNoneConfigured);

        vm.ClientIdInput = "not-a-valid-id";
        Assert.True(vm.HasValidationError);
        Assert.Equal(string.Empty, stored);

        var valid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        vm.ClientIdInput = valid;
        Assert.False(vm.HasValidationError);
        Assert.Equal(valid, stored);
        Assert.True(vm.IsUsingOwnApp);

        vm.UseBuiltInTesterAppCommand.Execute(null);
        Assert.Equal(SpotifyAuthService.DefaultClientId, stored);
        Assert.True(vm.IsUsingTesterApp);

        vm.ClearClientIdCommand.Execute(null);
        Assert.Equal(string.Empty, stored);
        Assert.True(vm.IsNoneConfigured);
    }

    [Fact]
    public void Spotify403Diagnostics_ClassifiesBodies_AndScrubsTokens()
    {
        Assert.Equal(Spotify403Kind.NotAllowlisted,
            Spotify403Diagnostics.Classify("{\"error\":{\"status\":403,\"message\":\"Check settings, user may not be registered\"}}"));
        Assert.Equal(Spotify403Kind.OwnerPremiumRequired,
            Spotify403Diagnostics.Classify("{\"error\":{\"status\":403,\"message\":\"Active Premium subscription required for app owner\"}}"));
        Assert.Equal(Spotify403Kind.PlaylistNotOwned,
            Spotify403Diagnostics.Classify("{\"error\":{\"status\":403,\"message\":\"Forbidden for non-collaborative playlist\"}}"));

        var scrubbed = Spotify403Diagnostics.Scrub("Authorization: Bearer secret_token_12345 {\"access_token\":\"abc.def\",\"refresh_token\":\"xyz\"}");
        Assert.DoesNotContain("secret_token_12345", scrubbed);
        Assert.DoesNotContain("abc.def", scrubbed);
        Assert.DoesNotContain("xyz", scrubbed);
        Assert.Contains("[REDACTED]", scrubbed);
    }

    [Fact]
    public void SharedOwnSpotifyAppControl_IsUsedInBothSettingsAndWizard_AndDeadSettingIsGone()
    {
        var root = FindRepoRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "SettingsPage.axaml"));
        var wizardXaml = File.ReadAllText(Path.Combine(root, "Views", "Avalonia", "Setup", "SetupWizardView.axaml"));
        var appConfigCs = File.ReadAllText(Path.Combine(root, "Configuration", "AppConfig.cs"));

        Assert.Contains("OwnSpotifyAppSetupControl", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("OwnSpotifyAppSetupControl", wizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SpotifyUsePublicOnly", appConfigCs, StringComparison.Ordinal);
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
