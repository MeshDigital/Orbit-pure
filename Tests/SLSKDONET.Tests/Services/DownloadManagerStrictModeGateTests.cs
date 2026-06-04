using System;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class DownloadManagerStrictModeGateTests
{
    [Fact]
    public async Task ResolveDiscoveryWithStrictGateAsync_StrictOn_UsesStrictResult_WhenBestMatchExists()
    {
        var strictCalls = 0;
        var legacyCalls = 0;
        var callbackCalls = 0;

        var strictBest = new Track
        {
            Username = "strict-peer",
            Filename = "Artist Track.flac",
            Format = "flac",
            Bitrate = 980,
            Size = 25 * 1024 * 1024
        };

        var diagnostics = new AutoSearchDiagnostics
        {
            IsEnabled = true,
            MatchType = "exact",
            StartedAtUtc = DateTime.UtcNow
        };

        var result = await DownloadManager.ResolveDiscoveryWithStrictGateAsync(
            strictModeEnabled: true,
            allowFuzzyFallback: true,
            strictSearch: () =>
            {
                strictCalls++;
                return Task.FromResult<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)>((strictBest, diagnostics));
            },
            legacyDiscovery: () =>
            {
                legacyCalls++;
                return Task.FromResult(new DownloadDiscoveryService.DiscoveryResult(null, null, null));
            },
            onStrictMatch: _ => callbackCalls++);

        Assert.Equal(1, strictCalls);
        Assert.Equal(0, legacyCalls);
        Assert.Equal(1, callbackCalls);
        Assert.Same(strictBest, result.BestMatch);
    }

    [Fact]
    public async Task ResolveDiscoveryWithStrictGateAsync_StrictOn_FallsBackToLegacy_WhenStrictHasNoMatch()
    {
        var strictCalls = 0;
        var legacyCalls = 0;

        var fallback = new Track
        {
            Username = "legacy-peer",
            Filename = "Artist Track.flac",
            Format = "flac",
            Bitrate = 950,
            Size = 24 * 1024 * 1024
        };

        var result = await DownloadManager.ResolveDiscoveryWithStrictGateAsync(
            strictModeEnabled: true,
            allowFuzzyFallback: true,
            strictSearch: () =>
            {
                strictCalls++;
                return Task.FromResult<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)>((null, new AutoSearchDiagnostics
                {
                    IsEnabled = true,
                    MatchType = null,
                    StartedAtUtc = DateTime.UtcNow
                }));
            },
            legacyDiscovery: () =>
            {
                legacyCalls++;
                return Task.FromResult(new DownloadDiscoveryService.DiscoveryResult(fallback, null, null));
            });

        Assert.Equal(1, strictCalls);
        Assert.Equal(1, legacyCalls);
        Assert.Same(fallback, result.BestMatch);
    }

    [Fact]
    public async Task ResolveDiscoveryWithStrictGateAsync_StrictOff_UsesLegacyOnly()
    {
        var strictCalls = 0;
        var legacyCalls = 0;

        var legacy = new Track
        {
            Username = "legacy-only-peer",
            Filename = "Artist Track.flac",
            Format = "flac",
            Bitrate = 920,
            Size = 23 * 1024 * 1024
        };

        var result = await DownloadManager.ResolveDiscoveryWithStrictGateAsync(
            strictModeEnabled: false,
            allowFuzzyFallback: true,
            strictSearch: () =>
            {
                strictCalls++;
                return Task.FromResult<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)>((null, new AutoSearchDiagnostics()));
            },
            legacyDiscovery: () =>
            {
                legacyCalls++;
                return Task.FromResult(new DownloadDiscoveryService.DiscoveryResult(legacy, null, null));
            });

        Assert.Equal(0, strictCalls);
        Assert.Equal(1, legacyCalls);
        Assert.Same(legacy, result.BestMatch);
    }

    [Fact]
    public async Task ResolveDiscoveryWithStrictGateAsync_StrictOn_BlocksLegacyFallback_WhenDisabled()
    {
        var strictCalls = 0;
        var legacyCalls = 0;

        var result = await DownloadManager.ResolveDiscoveryWithStrictGateAsync(
            strictModeEnabled: true,
            allowFuzzyFallback: false,
            strictSearch: () =>
            {
                strictCalls++;
                return Task.FromResult<(Track? BestMatch, AutoSearchDiagnostics Diagnostics)>((null, new AutoSearchDiagnostics
                {
                    IsEnabled = true,
                    MatchType = null,
                    StartedAtUtc = DateTime.UtcNow
                }));
            },
            legacyDiscovery: () =>
            {
                legacyCalls++;
                return Task.FromResult(new DownloadDiscoveryService.DiscoveryResult(new Track
                {
                    Username = "legacy-peer",
                    Filename = "ShouldNotBeUsed.flac",
                    Format = "flac"
                }, null, null));
            });

        Assert.Equal(1, strictCalls);
        Assert.Equal(0, legacyCalls);
        Assert.Null(result.BestMatch);
    }
}
