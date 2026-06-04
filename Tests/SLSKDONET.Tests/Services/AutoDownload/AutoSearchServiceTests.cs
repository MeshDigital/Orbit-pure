using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;

namespace SLSKDONET.Tests.Services.AutoDownload;

/// <summary>
/// Unit tests for AutoSearchService.
/// Tests the exact-first, filtered-fallback pipeline for strict-mode automatic downloads.
/// </summary>
public class AutoSearchServiceTests
{
    private readonly Mock<ILogger<AutoSearchService>> _mockLogger;
    private readonly Mock<ILogger<SoulseekSearchHelper>> _mockSearchHelperLogger;
    private readonly Mock<AppConfig> _mockConfig;
    private readonly Mock<ISoulseekAdapter> _mockAdapter;
    private readonly AutoSearchService _service;

    public AutoSearchServiceTests()
    {
        _mockLogger = new Mock<ILogger<AutoSearchService>>();
        _mockSearchHelperLogger = new Mock<ILogger<SoulseekSearchHelper>>();
        _mockConfig = new Mock<AppConfig>();
        _mockAdapter = new Mock<ISoulseekAdapter>();

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());

        // Default config: feature enabled, conservative timeouts
        _mockConfig.Object.EnableAutoDownloadStrictMode = true;
        _mockConfig.Object.AutoDownloadInitialWaitMs = 4000;
        _mockConfig.Object.AutoDownloadExtendedWaitMs = 20000;
        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "flac", "wav" };
        _mockConfig.Object.AutoDownloadMinBitrateKbps = 320;
        _mockConfig.Object.AutoDownloadDiagnosticsEnabled = false;

        var helper = new SoulseekSearchHelper(
            _mockSearchHelperLogger.Object,
            _mockConfig.Object,
            _mockAdapter.Object);

        _service = new AutoSearchService(
            _mockLogger.Object,
            _mockConfig.Object,
            null!,
            null!,
            helper);
    }

    private static async IAsyncEnumerable<Track> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<Track> StreamFrom(params Track[] candidates)
    {
        foreach (var candidate in candidates)
        {
            await Task.Yield();
            yield return candidate;
        }
    }

    [Fact]
    public async Task FeatureDisabledReturnsQuietly()
    {
        _mockConfig.Object.EnableAutoDownloadStrictMode = false;

        var track = new PlaylistTrack { Id = Guid.NewGuid(), Artist = "Test", Title = "Track" };

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
        Assert.False(diag.IsEnabled);
        _mockAdapter.Verify(x => x.StreamResultsAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<(int? Min, int? Max)>(),
            It.IsAny<DownloadMode>(),
            It.IsAny<SearchExecutionProfile?>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnabledSearchPopulatesDiagnostics()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Daft Punk",
            Title = "One More Time",
            CanonicalDuration = 305 * 1000
        };

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
        Assert.True(diag.IsEnabled);
        Assert.Equal(track.Id, diag.TrackId);
        Assert.Equal(track.Artist, diag.TrackArtist);
        Assert.Equal(track.Title, diag.TrackTitle);
        Assert.Null(diag.MatchType);
        Assert.Equal(0, diag.CandidatesConsidered);
    }

    [Fact]
    public async Task EnabledSearchReturnsEmptyResultWhenNoCandidatesExist()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Miles Davis",
            Title = "Kind of Blue",
            CanonicalDuration = 420 * 1000
        };

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
        Assert.Null(diag.MatchType);
        Assert.Equal(0, diag.ExactFilenameResultsCount);
        Assert.Equal(0, diag.TemplateResultsCount);
        Assert.True(diag.ExactFilenameElapsedMs >= 0);
        Assert.True(diag.TemplateElapsedMs >= 0);
    }

    [Fact]
    public async Task EnabledSearchStreamsCandidatesAndSelectsDeterministicBestExactMatch()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Daft Punk",
            Title = "Harder Better Faster Stronger",
            CanonicalDuration = 224 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-deterministic",
                    Filename = "Daft Punk Harder Better Faster Stronger.flac",
                    Format = "flac",
                    Bitrate = 980,
                    Size = 28 * 1024 * 1024,
                    QueueLength = 0
                },
                new Track
                {
                    Username = "peer-high-queue",
                    Filename = "Daft Punk Harder Better Faster Stronger.flac",
                    Format = "flac",
                    Bitrate = 980,
                    Size = 28 * 1024 * 1024,
                    QueueLength = 12
                },
                new Track
                {
                    Username = "peer-noise",
                    Filename = "Daft Punk - Harder Better Faster Stronger (Live).flac",
                    Format = "flac",
                    Bitrate = 950,
                    Size = 26 * 1024 * 1024,
                    QueueLength = 1
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-deterministic", result!.Username);
        Assert.Equal("exact", diag.MatchType);
        Assert.Equal(2, diag.CandidatesConsidered);
        _mockAdapter.Verify(x => x.StreamResultsAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<(int? Min, int? Max)>(),
            It.IsAny<DownloadMode>(),
            It.IsAny<SearchExecutionProfile?>(),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnabledSearchFallsBackToTemplateWhenExactCandidatesAreRejectedByFilters()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Justice",
            Title = "Genesis",
            CanonicalDuration = 231 * 1000
        };

        var callCount = 0;
        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return StreamFrom(
                        new Track
                        {
                            Username = "peer-mp3",
                            Filename = "Justice Genesis.mp3",
                            Format = "mp3",
                            Bitrate = 320,
                            Size = 8 * 1024 * 1024,
                            QueueLength = 1
                        },
                        new Track
                        {
                            Username = "peer-aac",
                            Filename = "Justice Genesis.aac",
                            Format = "aac",
                            Bitrate = 512,
                            Size = 16 * 1024 * 1024,
                            QueueLength = 1
                        });
                }

                return StreamFrom(
                    new Track
                    {
                        Username = "peer-template-good",
                        Filename = "Justice Genesis Album Version.flac",
                        Format = "flac",
                        Bitrate = 980,
                        Size = 29 * 1024 * 1024,
                        QueueLength = 2
                    });
            });

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-template-good", result!.Username);
        Assert.Equal("filtered_template", diag.MatchType);
        Assert.True(diag.ExactFilenameResultsCount > 0);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task EnabledSearch_UsesPerTrackPreferredFormats_WhenPresent()
    {
        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "flac" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Noisia",
            Title = "Machine Gun",
            PreferredFormats = "wav",
            CanonicalDuration = 266 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-wav",
                    Filename = "Noisia Machine Gun.wav",
                    Format = "wav",
                    Bitrate = 1411,
                    Size = 45 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 266
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-wav", result!.Username);
        Assert.Equal("exact", diag.MatchType);
    }

    [Fact]
    public async Task EnabledSearch_NormalizesMimeStylePreferredFormats_BeforeAdapterSearch()
    {
        IEnumerable<string>? observedFormats = null;

        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "wav" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Rone",
            Title = "Brest",
            PreferredFormats = "audio/x-flac; charset=utf-8",
            CanonicalDuration = 245 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>?, (int? Min, int? Max), DownloadMode, SearchExecutionProfile?, CancellationToken>((_, formats, _, _, _, _) =>
            {
                observedFormats = formats;
            })
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-flac",
                    Filename = "Rone Brest.flac",
                    Format = "flac",
                    Bitrate = 980,
                    Size = 27 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 245
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-flac", result!.Username);
        Assert.Equal("exact", diag.MatchType);
        Assert.NotNull(observedFormats);
        Assert.Contains("flac", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("audio/x-flac", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-flac", observedFormats!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledSearch_CanonicalizesMimeMpegPreferredFormats_BeforeAdapterSearch()
    {
        IEnumerable<string>? observedFormats = null;

        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "flac" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Massive Attack",
            Title = "Teardrop",
            PreferredFormats = "audio/mpeg; charset=utf-8",
            CanonicalDuration = 330 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>?, (int? Min, int? Max), DownloadMode, SearchExecutionProfile?, CancellationToken>((_, formats, _, _, _, _) =>
            {
                observedFormats = formats;
            })
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-mp3",
                    Filename = "Massive Attack Teardrop.mp3",
                    Format = "mp3",
                    Bitrate = 320,
                    Size = 9 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 330
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-mp3", result!.Username);
        Assert.Equal("exact", diag.MatchType);
        Assert.NotNull(observedFormats);
        Assert.Contains("mp3", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("mpeg", observedFormats!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledSearch_DropsMimeParameterFragments_FromPreferredFormats_BeforeAdapterSearch()
    {
        IEnumerable<string>? observedFormats = null;

        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "wav" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Bonobo",
            Title = "Kerala",
            PreferredFormats = "audio/mpeg; codecs=mp3, audio/flac",
            CanonicalDuration = 230 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>?, (int? Min, int? Max), DownloadMode, SearchExecutionProfile?, CancellationToken>((_, formats, _, _, _, _) =>
            {
                observedFormats = formats;
            })
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-flac",
                    Filename = "Bonobo Kerala.flac",
                    Format = "flac",
                    Bitrate = 920,
                    Size = 26 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 230
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-flac", result!.Username);
        Assert.Equal("exact", diag.MatchType);
        Assert.NotNull(observedFormats);
        Assert.Contains("mp3", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("flac", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("codecs=mp3", observedFormats!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledSearch_CanonicalizesWhitespaceDelimitedMimeMpegPreferredFormats_BeforeAdapterSearch()
    {
        IEnumerable<string>? observedFormats = null;

        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string> { "flac" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Massive Attack",
            Title = "Teardrop",
            PreferredFormats = "[\"audio/mpeg\"] codecs=mp3",
            CanonicalDuration = 330 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>?, (int? Min, int? Max), DownloadMode, SearchExecutionProfile?, CancellationToken>((_, formats, _, _, _, _) =>
            {
                observedFormats = formats;
            })
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-mp3",
                    Filename = "Massive Attack Teardrop.mp3",
                    Format = "mp3",
                    Bitrate = 320,
                    Size = 9 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 330
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-mp3", result!.Username);
        Assert.Equal("exact", diag.MatchType);
        Assert.NotNull(observedFormats);
        Assert.Contains("mp3", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("mpeg codecs=mp3", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"mpeg\"", observedFormats!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("[\"mpeg\"]", observedFormats!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledSearch_FallsBackToConfigMinBitrate_WhenOverrideIsZero()
    {
        _mockConfig.Object.AutoDownloadMinBitrateKbps = 320;

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test Artist",
            Title = "Test Track",
            Status = TrackStatus.OnHold,
            MinBitrateOverride = 0,
            CanonicalDuration = 200 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-low-bitrate",
                    Filename = "Test Artist Test Track.mp3",
                    Format = "mp3",
                    Bitrate = 192,
                    Size = 6 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 200
                }));

        var (result, _) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnabledSearch_UsesPerTrackMinBitrateOverride_WhenPositive()
    {
        _mockConfig.Object.AutoDownloadMinBitrateKbps = 320;

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test Artist",
            Title = "Test Track",
            Status = TrackStatus.OnHold,
            MinBitrateOverride = 256,
            CanonicalDuration = 200 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-override",
                    Filename = "Test Artist Test Track.mp3",
                    Format = "mp3",
                    Bitrate = 256,
                    Size = 7 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 200
                }));

        var (result, diag) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("peer-override", result!.Username);
        Assert.Equal("exact", diag.MatchType);
    }

    [Fact]
    public async Task EnabledSearch_UsesPreferredFormatsFallback_WhenAutoDownloadExtensionsMissing()
    {
        _mockConfig.Object.AutoDownloadAllowedExtensions = new List<string>();
        _mockConfig.Object.PreferredFormats = new List<string> { "flac" };

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Boards of Canada",
            Title = "Dayvan Cowboy",
            CanonicalDuration = 305 * 1000
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-wav-only",
                    Filename = "Boards of Canada Dayvan Cowboy.wav",
                    Format = "wav",
                    Bitrate = 1411,
                    Size = 42 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 305
                }));

        var (result, _) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnabledSearch_NormalizesSecondsScaleCanonicalDuration_ForDurationGate()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Moderat",
            Title = "A New Error",
            CanonicalDuration = 300 // Upstream seconds-scale metadata
        };

        _mockAdapter
            .Setup(x => x.StreamResultsAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<(int? Min, int? Max)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<SearchExecutionProfile?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom(
                new Track
                {
                    Username = "peer-duration-mismatch",
                    Filename = "Moderat A New Error.flac",
                    Format = "flac",
                    Bitrate = 1000,
                    Size = 40 * 1024 * 1024,
                    QueueLength = 0,
                    Length = 450
                }));

        var (result, _) = await _service.FindBestMatchAsync(track, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PrivateSelectionRejectsCandidatesBelowMinimumScore()
    {
        _mockConfig.Object.AutoDownloadMinMatchScore = 100;

        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Justice",
            Title = "Genesis",
            CanonicalDuration = 231 * 1000
        };

        var candidates = new List<Track>
        {
            new()
            {
                Username = "peer-low-score",
                Filename = "Justice Genesis Album Version.flac",
                Format = "flac",
                Bitrate = 980,
                Size = 29 * 1024 * 1024,
                QueueLength = 2
            }
        };

        var result = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SelectBestCandidateAsync",
            track,
            candidates,
            "filtered_template",
            CancellationToken.None);

        Assert.Null(result.BestMatch);
        Assert.True(result.Score < 100);
    }

    [Fact]
    public async Task PrivateSelection_EnforcesSparseMetadataFloor_WhenThresholdLowered()
    {
        _mockConfig.Object.AutoDownloadMinMatchScore = 40;

        var sparseTrack = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = string.Empty,
            Title = "LowSignal",
            CanonicalDuration = null
        };

        var candidates = new List<Track>
        {
            new()
            {
                Username = "peer-sparse",
                Filename = "Unknown LowSignal.flac",
                Format = "flac",
                Bitrate = 1000,
                Size = 20 * 1024 * 1024,
                QueueLength = 0,
                Length = 220
            }
        };

        var result = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SelectBestCandidateAsync",
            sparseTrack,
            candidates,
            "filtered_template",
            CancellationToken.None);

        Assert.Null(result.BestMatch);
        Assert.True(result.Score < 75);
    }

    [Fact]
    public async Task PrivateSelection_DoesNotApplySparseFloor_ForCompleteMetadataTrack()
    {
        _mockConfig.Object.AutoDownloadMinMatchScore = 40;

        var completeTrack = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Bicep",
            Title = "Glue",
            CanonicalDuration = 267 * 1000
        };

        var candidates = new List<Track>
        {
            new()
            {
                Username = "peer-complete",
                Filename = "Bicep Glue.flac",
                Format = "flac",
                Bitrate = 1000,
                Size = 32 * 1024 * 1024,
                QueueLength = 0,
                Length = 267
            }
        };

        var result = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SelectBestCandidateAsync",
            completeTrack,
            candidates,
            "exact",
            CancellationToken.None);

        Assert.NotNull(result.BestMatch);
        Assert.True(result.Score >= 40);
    }

    [Fact]
    public void NormalizeQueryRemovesPunctuation()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "A!rtist",
            Title = "Tra@ck (Remix)?"
        };

        var normalized = InvokePrivate<string>("NormalizeQuery", track);

        Assert.Equal("artist track remix", normalized);
    }

    [Fact]
    public void NormalizeQueryCollapsesWhitespace()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "  Pink    Floyd  ",
            Title = "  Comfortably   Numb "
        };

        var normalized = InvokePrivate<string>("NormalizeQuery", track);

        Assert.Equal("pink floyd comfortably numb", normalized);
    }

    [Fact]
    public async Task PrivateExactSearchReturnsEmptyResult()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "The Beatles",
            Title = "Hey Jude",
            CanonicalDuration = 431 * 1000
        };

        var result = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SearchExactFilenameAsync",
            track,
            "the beatles hey jude",
            CancellationToken.None);

        Assert.Null(result.BestMatch);
        Assert.Equal(0, result.CandidatesCount);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task PrivateFilteredSearchReturnsEmptyResult()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Pink Floyd",
            Title = "Time",
            CanonicalDuration = 413 * 1000
        };

        var result = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SearchFilteredTemplateAsync",
            track,
            "pink floyd time",
            CancellationToken.None);

        Assert.Null(result.BestMatch);
        Assert.Equal(0, result.CandidatesCount);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task CancelledTokenStillReturnsCleanly()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test",
            Title = "Track"
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exactResult = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SearchExactFilenameAsync",
            track,
            "test track",
            cts.Token);

        var filteredResult = await InvokePrivateAsync<AutoSearchService.SearchResult>(
            "SearchFilteredTemplateAsync",
            track,
            "test track",
            cts.Token);

        Assert.Null(exactResult.BestMatch);
        Assert.Null(filteredResult.BestMatch);
    }

    private T InvokePrivate<T>(string methodName, params object[] args)
    {
        var method = typeof(AutoSearchService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find private method {methodName}.");

        return (T)(method.Invoke(_service, args) ?? throw new InvalidOperationException($"Private method {methodName} returned null."));
    }

    private async Task<T> InvokePrivateAsync<T>(string methodName, params object[] args)
    {
        var method = typeof(AutoSearchService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Could not find private method {methodName}.");

        var task = method.Invoke(_service, args) as Task
            ?? throw new InvalidOperationException($"Private method {methodName} did not return a Task.");

        await task;

        var result = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(task);

        if (result is null)
        {
            throw new InvalidOperationException($"Private method {methodName} returned null result.");
        }

        return (T)result;
    }
}
