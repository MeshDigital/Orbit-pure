using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Network;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchOrchestrationServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldPreferLeastBadPeer_WhenHighQualityPeerHasExtremeQueue()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            SearchThrottleDelayMs = 50,
            PreferredFormats = new List<string> { "flac", "wav", "aiff", "aif", "mp3" },
            PreferredMinBitrate = 320
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var candidates = new List<Track>
        {
            new()
            {
                Artist = "Artist",
                Title = "Track",
                Filename = "Artist - Track.flac",
                Format = "flac",
                Bitrate = 980,
                QueueLength = 500,
                UploadSpeed = 10_000,
                Username = "slow-peer",
                HasFreeUploadSlot = false
            },
            new()
            {
                Artist = "Artist",
                Title = "Track",
                Filename = "Artist - Track.mp3",
                Format = "mp3",
                Bitrate = 320,
                QueueLength = 0,
                UploadSpeed = 5_000_000,
                Username = "fast-peer",
                HasFreeUploadSlot = true
            }
        };

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, CancellationToken token) =>
                StreamCandidates(candidates, token));

        var safety = new Mock<ISafetyFilterService>();
        safety.Setup(s => s.EvaluateSafety(It.IsAny<Track>(), It.IsAny<string>()));

        var library = new Mock<ILibraryService>();

        var sut = new SearchOrchestrationService(
            NullLogger<SearchOrchestrationService>.Instance,
            soulseek.Object,
            new SearchQueryNormalizer(),
            new SearchNormalizationService(NullLogger<SearchNormalizationService>.Instance),
            safety.Object,
            config,
            hardening,
            library.Object);

        var results = new List<Track>();
        await foreach (var track in sut.SearchAsync(
            query: "Artist Track",
            preferredFormats: "flac,wav,aiff,aif,mp3",
            minBitrate: 320,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
            results.Add(track);
        }

        Assert.NotEmpty(results);
        var winner = results.First();

        Assert.Equal("fast-peer", winner.Username);
        Assert.True(winner.QueueLength <= 1, "Expected queue-aware winner selection.");
        Assert.True(winner.CurrentRank > 0, "Winner should be scored by ranking matrix.");
    }

    private static async IAsyncEnumerable<Track> StreamCandidates(
        IEnumerable<Track> tracks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return track;
        }
    }
}
