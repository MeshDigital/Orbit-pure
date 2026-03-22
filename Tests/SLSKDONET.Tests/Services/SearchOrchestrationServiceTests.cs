using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task SearchAsync_RespectsVariationCap_WhenManyVariationsGenerated()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 2,
            SearchThrottleDelayMs = 10,
            PreferredFormats = new List<string> { "flac", "mp3" },
            PreferredMinBitrate = 320
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        var invocationCount = 0;
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                return StreamCandidates(Array.Empty<Track>(), token);
            });

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

        await foreach (var _ in sut.SearchAsync(
            query: "Artist - Track (Original Mix)",
            preferredFormats: "flac,mp3",
            minBitrate: 320,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
            // no-op
        }

        Assert.Equal(2, invocationCount);
    }

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
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
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
        Assert.Contains("Blend:", winner.ScoreBreakdown ?? string.Empty);
        Assert.NotNull(winner.Metadata);
        Assert.True(winner.Metadata!.ContainsKey("BlendMatchScore"));
        Assert.True(winner.Metadata!.ContainsKey("BlendFitScore"));
        Assert.True(winner.Metadata!.ContainsKey("BlendFinalScore"));
    }

    [Fact]
    public async Task SearchAsync_StrictFirstStopsRelaxed_WhenStrictThresholdReached()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 3,
            StrictSearchSufficientResultCount = 1,
            EnableStrictHighConfidenceShortCircuit = false,
            SearchThrottleDelayMs = 10,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var invocationCount = 0;
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                var firstVariationTrack = new Track
                {
                    Artist = "Artist",
                    Title = "Track",
                    Filename = "Artist - Track.mp3",
                    Format = "mp3",
                    Bitrate = 320,
                    QueueLength = 3,
                    UploadSpeed = 120000,
                    Username = "peer-a",
                    HasFreeUploadSlot = true
                };
                return StreamCandidates(new[] { firstVariationTrack }, token);
            });

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

        await foreach (var _ in sut.SearchAsync(
            query: "Artist - Track (Original Mix)",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
        }

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task SearchAsync_StrictFirstStopsRelaxed_WhenHighConfidenceWinnerFound()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 3,
            StrictSearchSufficientResultCount = 999,
            EnableStrictHighConfidenceShortCircuit = true,
            SearchThrottleDelayMs = 10,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var invocationCount = 0;
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                var strictWinner = new Track
                {
                    Artist = "Artist",
                    Title = "Track",
                    Filename = "Artist - Track.mp3",
                    Format = "mp3",
                    Bitrate = 320,
                    QueueLength = 0,
                    UploadSpeed = 250000,
                    Username = "peer-fast",
                    HasFreeUploadSlot = true
                };
                return StreamCandidates(new[] { strictWinner }, token);
            });

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

        await foreach (var _ in sut.SearchAsync(
            query: "Artist - Track (Original Mix)",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
        }

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task SearchAsync_ShouldEscalateToDesperateLane_WhenStrictAndStandardMiss()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 3,
            StrictSearchSufficientResultCount = 999,
            EnableStrictHighConfidenceShortCircuit = false,
            SearchThrottleDelayMs = 1,
            RelaxationTimeoutSeconds = 1,
            SearchAccumulatorWindowSeconds = 5,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var invocationCount = 0;
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                return invocationCount switch
                {
                    1 => StreamCandidates(Array.Empty<Track>(), token),
                    2 => StreamCandidates(Array.Empty<Track>(), token),
                    _ => StreamCandidates(new[]
                    {
                        new Track
                        {
                            Artist = "Artist",
                            Title = "Track",
                            Filename = "Artist - Track.mp3",
                            Format = "mp3",
                            Bitrate = 320,
                            QueueLength = 0,
                            UploadSpeed = 250000,
                            Username = "peer-desperate",
                            HasFreeUploadSlot = true
                        }
                    }, token)
                };
            });

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
            query: "Artist - Track (Original Mix)",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
            results.Add(track);
        }

        Assert.Equal(3, invocationCount);
        Assert.Contains(results, t => t.Username == "peer-desperate");
    }

    [Fact]
    public async Task SearchAsync_ShouldSkipDesperateLane_WhenEarlierLaneProducedAcceptedResult()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 3,
            StrictSearchSufficientResultCount = 999,
            EnableStrictHighConfidenceShortCircuit = false,
            SearchThrottleDelayMs = 1,
            RelaxationTimeoutSeconds = 1,
            SearchAccumulatorWindowSeconds = 5,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var invocationCount = 0;
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                return invocationCount switch
                {
                    1 => StreamCandidates(new[]
                    {
                        new Track
                        {
                            Artist = "Artist",
                            Title = "Track",
                            Filename = "Artist - Track.mp3",
                            Format = "mp3",
                            Bitrate = 320,
                            QueueLength = 1,
                            UploadSpeed = 120000,
                            Username = "peer-strict",
                            HasFreeUploadSlot = true
                        }
                    }, token),
                    _ => StreamCandidates(Array.Empty<Track>(), token)
                };
            });

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

        await foreach (var _ in sut.SearchAsync(
            query: "Artist - Track (Original Mix)",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
        }

        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public async Task SearchAsync_WithMetadataTarget_ShouldPrioritizeDurationAccurateWinner()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 1,
            SearchLengthToleranceSeconds = 3,
            StrictSearchSufficientResultCount = 5,
            EnableStrictHighConfidenceShortCircuit = false,
            SearchThrottleDelayMs = 1,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
                StreamCandidates(new[]
                {
                    new Track
                    {
                        Artist = "Artist",
                        Title = "Track",
                        Filename = "Artist - Track [Wrong Duration].mp3",
                        Format = "mp3",
                        Bitrate = 320,
                        Length = 210,
                        QueueLength = 0,
                        UploadSpeed = 220000,
                        Username = "peer-wrong-duration",
                        HasFreeUploadSlot = true
                    },
                    new Track
                    {
                        Artist = "Artist",
                        Title = "Track",
                        Filename = "Artist - Track [Correct Duration].mp3",
                        Format = "mp3",
                        Bitrate = 320,
                        Length = 180,
                        QueueLength = 0,
                        UploadSpeed = 180000,
                        Username = "peer-correct-duration",
                        HasFreeUploadSlot = true
                    }
                }, token));

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

        var target = new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track",
            CanonicalDuration = 180000
        };

        var results = new List<Track>();
        await foreach (var track in sut.SearchAsync(
            target,
            query: "Artist Track",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: false,
            cancellationToken: CancellationToken.None))
        {
            results.Add(track);
        }

        Assert.NotEmpty(results);
        Assert.Equal("peer-correct-duration", results.First().Username);
    }

    [Fact]
    public async Task SearchAsync_FastClearance_ShouldNotShortCircuitOnLowConfidenceFirstLane()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 2,
            MaxSearchVariations = 2,
            StrictSearchSufficientResultCount = 999,
            EnableStrictHighConfidenceShortCircuit = false,
            SearchThrottleDelayMs = 1,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var invocationCount = 0;
        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token) =>
            {
                invocationCount++;
                return invocationCount switch
                {
                    1 => StreamCandidates(new[]
                    {
                        new Track
                        {
                            Artist = "Other",
                            Title = "Completely Different",
                            Filename = "Other - Completely Different.mp3",
                            Format = "mp3",
                            Bitrate = 320,
                            QueueLength = 0,
                            UploadSpeed = 250000,
                            Username = "peer-low-confidence",
                            HasFreeUploadSlot = true
                        }
                    }, token),
                    _ => StreamCandidates(new[]
                    {
                        new Track
                        {
                            Artist = "Artist",
                            Title = "Track",
                            Filename = "Artist - Track.mp3",
                            Format = "mp3",
                            Bitrate = 320,
                            QueueLength = 0,
                            UploadSpeed = 250000,
                            Username = "peer-high-confidence",
                            HasFreeUploadSlot = true
                        }
                    }, token)
                };
            });

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
            query: "Artist - Track (Original Mix)",
            preferredFormats: "mp3",
            minBitrate: 192,
            maxBitrate: 0,
            isAlbumSearch: false,
            fastClearance: true,
            cancellationToken: CancellationToken.None))
        {
            results.Add(track);
        }

        Assert.Equal(2, invocationCount);
        Assert.Contains(results, t => t.Username == "peer-high-confidence");
    }

    [Fact]
    public async Task SearchAsync_ShouldPropagateHardCapException_FromAdapterStream()
    {
        var config = new AppConfig
        {
            MaxConcurrentSearches = 1,
            MaxSearchVariations = 1,
            SearchThrottleDelayMs = 1,
            PreferredFormats = new List<string> { "mp3" },
            PreferredMinBitrate = 192
        };

        var eventBus = new EventBusService();
        var hardening = new ProtocolHardeningService(
            NullLogger<ProtocolHardeningService>.Instance,
            config,
            eventBus);

        var soulseek = new Mock<ISoulseekAdapter>();
        soulseek.SetupGet(s => s.IsConnected).Returns(true);
        soulseek
            .Setup(s => s.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<string> _, (int? Min, int? Max) _, DownloadMode _, SearchExecutionProfile? _, CancellationToken token)
                => StreamThrows(new SearchLimitExceededException("cap reached", 10000, 50000), token));

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

        async Task ConsumeAsync()
        {
            await foreach (var _ in sut.SearchAsync(
                query: "Artist Track",
                preferredFormats: "mp3",
                minBitrate: 192,
                maxBitrate: 0,
                isAlbumSearch: false,
                fastClearance: false,
                cancellationToken: CancellationToken.None))
            {
            }
        }

        var ex = await Assert.ThrowsAsync<SearchLimitExceededException>(ConsumeAsync);
        Assert.Equal(10000, ex.HardResultCap);
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

    private static async IAsyncEnumerable<Track> StreamThrows(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
