using System;
using System.Collections.Generic;
using Xunit;
using SLSKDONET.Models;
using SLSKDONET.Services.AutoDownload;

namespace SLSKDONET.Tests.Services.AutoDownload;

/// <summary>
/// Unit tests for MatchScorer.
/// Tests deterministic scoring of download candidates with weighted components.
/// </summary>
public class MatchScorerTests
{
    /// <summary>
    /// ARRANGE: Create a perfect match candidate (exact filename, high bitrate, lossless)
    /// ACT: Score the candidate
    /// ASSERT: Score is 100 or very close (>95)
    /// </summary>
    [Fact]
    public void ScoresExactMatchHighest()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "The Beatles",
            Title = "Hey Jude",
            CanonicalDuration = 431 * 1000,
            MinBitrateOverride = null
        };

        var perfectCandidate = new Track
        {
            Artist = "The Beatles",
            Title = "Hey Jude",
            Filename = "The Beatles - Hey Jude.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 431,
            Username = "trusted_source",
            QueueLength = 0,
            Size = 54_000_000
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024,
            AllowMp3Fallback = false
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, perfectCandidate, options);

        // Assert: Perfect match should score >= 95
        Assert.True(score >= 95, $"Expected score >= 95, got {score}");
    }

    /// <summary>
    /// ARRANGE: Candidate has low bitrate (< 320kbps)
    /// ACT: Score the candidate
    /// ASSERT: Score is significantly reduced (<50)
    /// </summary>
    [Fact]
    public void PenalizesLowBitrateAndSmallSize()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test",
            Title = "Song",
            CanonicalDuration = 200 * 1000
        };

        var poorCandidate = new Track
        {
            Artist = "Test",
            Title = "Song",
            Filename = "test_song.flac",
            Format = "flac",
            Bitrate = 128, // Low bitrate
            Length = 200,
            Size = 3_200_000 // Small file
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, poorCandidate, options);

        // Assert: Low bitrate candidate should score lower than a clean match
        Assert.True(score < 85, $"Expected score < 85 for low-bitrate candidate, got {score}");
    }

    /// <summary>
    /// ARRANGE: Candidate is wrong format (MP3 when lossless required)
    /// ACT: Score the candidate
    /// ASSERT: Score is 0 or very low, depending on config
    /// </summary>
    [Fact]
    public void RejectsWrongFormat()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Artist",
            Title = "Song",
            CanonicalDuration = 200 * 1000
        };

        var mp3Candidate = new Track
        {
            Artist = "Artist",
            Title = "Song",
            Filename = "artist_song.mp3",
            Format = "mp3",
            Bitrate = 320,
            Length = 200,
            Size = 8_000_000
        };

        var strictOptions = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            AllowMp3Fallback = false
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, mp3Candidate, strictOptions);

        // Assert: MP3 should be penalized under strict mode
        Assert.True(score < 80, $"Expected score < 80 for MP3 in strict mode, got {score}");
    }

    /// <summary>
    /// ARRANGE: Candidate has very long queue (50 items)
    /// ACT: Score the candidate
    /// ASSERT: Queue penalty is applied (score reduced)
    /// </summary>
    [Fact]
    public void PenalizesLongPeerQueue()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Artist",
            Title = "Song",
            CanonicalDuration = 200 * 1000
        };

        var longQueueCandidate = new Track
        {
            Artist = "Artist",
            Title = "Song",
            Filename = "artist_song.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 200,
            Username = "busy_peer",
            QueueLength = 50, // Very long queue
            Size = 25_000_000
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac" },
            MinBitrateKbps = 320
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, longQueueCandidate, options);

        // Assert: Long queue should reduce score vs. idle peer
        var idleQueueCandidate = new Track
        {
            Artist = "Artist",
            Title = "Song",
            Filename = "artist_song.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 200,
            Username = "idle_peer",
            QueueLength = 0, // Idle peer
            Size = 25_000_000
        };

        var idleScore = MatchScorer.ScoreCandidate(track, idleQueueCandidate, options);

        Assert.True(score < idleScore, $"Queue penalty not applied: busy={score}, idle={idleScore}");
    }

    /// <summary>
    /// ARRANGE: Candidate filename has extra metadata (not exact)
    /// ACT: Score the candidate
    /// ASSERT: Score is reduced but still acceptable (>70)
    /// </summary>
    [Fact]
    public void AllowsPartialMatchWithMetadata()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Pink Floyd",
            Title = "Comfortably Numb",
            CanonicalDuration = 384 * 1000
        };

        var partialMatch = new Track
        {
            Artist = "Pink Floyd",
            Title = "Comfortably Numb",
            Filename = "pink_floyd-comfortably_numb_REMASTERED_FLAC_2015.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 384,
            Username = "music_source",
            Size = 48_000_000
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, partialMatch, options);

        // Assert: Partial match should still be acceptable
        Assert.True(score >= 70, $"Expected score >= 70 for partial match, got {score}");
    }

    /// <summary>
    /// ARRANGE: Candidate is suspected FLAC transcode (FLAC format but <400kbps bitrate)
    /// ACT: Score the candidate
    /// ASSERT: Score is 0 (hard fail) or very low
    /// </summary>
    [Fact]
    public void RejectsSuspiciousFLACTranscode()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Artist",
            Title = "Track",
            CanonicalDuration = 200 * 1000
        };

        var fakeFlac = new Track
        {
            Artist = "Artist",
            Title = "Track",
            Filename = "artist_track.flac",
            Format = "flac",
            Bitrate = 128, // Suspiciously low for FLAC!
            Length = 200,
            Size = 3_200_000
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, fakeFlac, options);

        // Assert: Fake FLAC should be penalized hard relative to a perfect match
        Assert.True(score < 85, $"Expected score < 85 for fake FLAC, got {score}");
    }

    /// <summary>
    /// ARRANGE: Two identical candidates, run scoring twice
    /// ACT: Score both runs
    /// ASSERT: Scores are identical (determinism test)
    /// </summary>
    [Fact]
    public void ScoringIsDeterministic()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Test",
            Title = "Determinism",
            CanonicalDuration = 200 * 1000
        };

        var candidate = new Track
        {
            Artist = "Test",
            Title = "Determinism",
            Filename = "test_determinism.flac",
            Format = "flac",
            Bitrate = 800,
            Length = 200,
            Username = "peer",
            QueueLength = 5,
            Size = 20_000_000
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320
        };

        // Act
        var score1 = MatchScorer.ScoreCandidate(track, candidate, options);
        var score2 = MatchScorer.ScoreCandidate(track, candidate, options);

        // Assert: Same inputs should produce same score
        Assert.Equal(score1, score2);
    }

    /// <summary>
    /// ARRANGE: File size is below minimum (100 bytes)
    /// ACT: Score the candidate
    /// ASSERT: Score is 0 or very low (stub file rejection)
    /// </summary>
    [Fact]
    public void RejectsStubFile()
    {
        // Arrange
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Stub",
            Title = "Empty",
            CanonicalDuration = 200 * 1000
        };

        var stubFile = new Track
        {
            Artist = "Stub",
            Title = "Empty",
            Filename = "stub.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 200,
            Size = 100 // Way too small!
        };

        var options = new MatchScoringOptions
        {
            MinFileSizeBytes = 500 * 1024
        };

        // Act
        var score = MatchScorer.ScoreCandidate(track, stubFile, options);

        // Assert: Stub should be strongly penalized
        Assert.True(score < 70, $"Expected score < 70 for stub file, got {score}");
    }

    [Fact]
    public void ScoresSparseMetadataLowerThanCompleteMetadata()
    {
        var candidate = new Track
        {
            Artist = "Bicep",
            Title = "Glue",
            Filename = "Bicep - Glue.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 267,
            Size = 32_000_000,
            QueueLength = 0
        };

        var completeTrack = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Bicep",
            Title = "Glue",
            CanonicalDuration = 267 * 1000
        };

        var sparseTrack = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = string.Empty,
            Title = string.Empty,
            CanonicalDuration = null
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var completeScore = MatchScorer.ScoreCandidate(completeTrack, candidate, options);
        var sparseScore = MatchScorer.ScoreCandidate(sparseTrack, candidate, options);

        Assert.True(sparseScore < completeScore, $"Expected sparse score < complete score, got sparse={sparseScore}, complete={completeScore}");
    }

    [Fact]
    public void HandlesNullOptionsWithoutThrowingAndReturnsBoundedScore()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Orbital",
            Title = "Halcyon",
            CanonicalDuration = 546 * 1000
        };

        var candidate = new Track
        {
            Artist = "Orbital",
            Title = "Halcyon",
            Filename = "Orbital - Halcyon.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 546,
            Size = 40_000_000,
            QueueLength = 1
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, null);

        Assert.InRange(score, 0, 100);
    }

    [Fact]
    public void AllowsMp3FallbackWhenEnabledWithReducedScore()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Boards of Canada",
            Title = "Dayvan Cowboy",
            CanonicalDuration = 336 * 1000
        };

        var candidate = new Track
        {
            Artist = "Boards of Canada",
            Title = "Dayvan Cowboy",
            Filename = "Boards of Canada - Dayvan Cowboy.mp3",
            Format = "mp3",
            Bitrate = 320,
            Length = 336,
            Size = 9_000_000,
            QueueLength = 0,
            Username = "peer-mp3"
        };

        var strictOptions = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024,
            AllowMp3Fallback = false
        };

        var fallbackOptions = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024,
            AllowMp3Fallback = true
        };

        var strictScore = MatchScorer.ScoreCandidate(track, candidate, strictOptions);
        var fallbackScore = MatchScorer.ScoreCandidate(track, candidate, fallbackOptions);

        Assert.True(fallbackScore > strictScore, $"Expected fallback score > strict score, got fallback={fallbackScore}, strict={strictScore}");
        Assert.True(fallbackScore < 95, $"Expected fallback score to remain below premium lossless ranges, got {fallbackScore}");
    }

    [Fact]
    public void NormalizesAllowedExtensionsWithDotsAndWhitespace()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Underworld",
            Title = "Born Slippy",
            CanonicalDuration = 690 * 1000
        };

        var candidate = new Track
        {
            Artist = "Underworld",
            Title = "Born Slippy",
            Filename = "Underworld - Born Slippy.FLAC",
            Format = " FLAC ",
            Bitrate = 980,
            Length = 690,
            Size = 65_000_000,
            QueueLength = 0,
            Username = "peer-lossless"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { " .FlAc ", " .Wav " },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 90, $"Expected high score when allowed extensions are normalized, got {score}");
    }

    [Fact]
    public void DetectsSuspiciousFlacByFilenameExtensionWhenFormatMissing()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            CanonicalDuration = 230 * 1000
        };

        var candidate = new Track
        {
            Artist = "Burial",
            Title = "Archangel",
            Filename = "Burial - Archangel.flac",
            Format = null,
            Bitrate = 192,
            Length = 230,
            Size = 7_000_000,
            QueueLength = 0,
            Username = "peer-transcode"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 192,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score < 85, $"Expected transcode-like FLAC candidate to be penalized, got {score}");
    }

    [Fact]
    public void UsesExtensionFallbackWhenCandidateFormatIsUnrecognized()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Nils Frahm",
            Title = "Says",
            CanonicalDuration = 520 * 1000
        };

        var candidate = new Track
        {
            Artist = "Nils Frahm",
            Title = "Says",
            Filename = "Nils Frahm - Says.FLAC",
            Format = "application/octet-stream",
            Bitrate = 980,
            Length = 520,
            Size = 58_000_000,
            QueueLength = 0,
            Username = "peer-lossless"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 90, $"Expected extension fallback to preserve high score for valid lossless candidate, got {score}");
    }

    [Fact]
    public void AcceptsMimeStyleFormatWhenFilenameHasNoExtension()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Rival Consoles",
            Title = "Recovery",
            CanonicalDuration = 312 * 1000
        };

        var candidate = new Track
        {
            Artist = "Rival Consoles",
            Title = "Recovery",
            Filename = "Rival Consoles - Recovery",
            Format = "audio/x-flac; charset=utf-8",
            Bitrate = 940,
            Length = 312,
            Size = 39_000_000,
            QueueLength = 0,
            Username = "peer-lossless"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac", "wav" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 90, $"Expected MIME-style format normalization to preserve high score, got {score}");
    }

    [Fact]
    public void CanonicalizesMimeMpegAliasWhenFilenameHasNoExtension()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            CanonicalDuration = 246 * 1000
        };

        var candidate = new Track
        {
            Artist = "Burial",
            Title = "Archangel",
            Filename = "Burial - Archangel",
            Format = "audio/mpeg; charset=utf-8",
            Bitrate = 320,
            Length = 246,
            Size = 8_000_000,
            QueueLength = 0,
            Username = "peer-mp3"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "mp3" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 85, $"Expected MIME mpeg alias canonicalization to preserve high score, got {score}");
    }

    [Fact]
    public void CanonicalizesCommaDelimitedMimeMpegAliasWhenFilenameHasNoExtension()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            CanonicalDuration = 246 * 1000
        };

        var candidate = new Track
        {
            Artist = "Burial",
            Title = "Archangel",
            Filename = "Burial - Archangel",
            Format = "audio/mpeg,codecs=mp3",
            Bitrate = 320,
            Length = 246,
            Size = 8_000_000,
            QueueLength = 0,
            Username = "peer-mp3"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "mp3" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 85, $"Expected comma-delimited MIME mpeg alias canonicalization to preserve high score, got {score}");
    }

    [Fact]
    public void CanonicalizesWhitespaceDelimitedMimeMpegAliasWhenFilenameHasNoExtension()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            CanonicalDuration = 246 * 1000
        };

        var candidate = new Track
        {
            Artist = "Burial",
            Title = "Archangel",
            Filename = "Burial - Archangel",
            Format = "audio/mpeg codecs=mp3",
            Bitrate = 320,
            Length = 246,
            Size = 8_000_000,
            QueueLength = 0,
            Username = "peer-mp3"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "mp3" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 85, $"Expected whitespace-delimited MIME mpeg alias canonicalization to preserve high score, got {score}");
    }

    [Fact]
    public void CanonicalizesQuotedMimeMpegAliasWhenFilenameHasNoExtension()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Burial",
            Title = "Archangel",
            CanonicalDuration = 246 * 1000
        };

        var candidate = new Track
        {
            Artist = "Burial",
            Title = "Archangel",
            Filename = "Burial - Archangel",
            Format = "[\"audio/mpeg\"]; charset=utf-8",
            Bitrate = 320,
            Length = 246,
            Size = 8_000_000,
            QueueLength = 0,
            Username = "peer-mp3"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "mp3" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 85, $"Expected quoted MIME mpeg alias canonicalization to preserve high score, got {score}");
    }

    [Fact]
    public void FallsBackToDefaultAllowlistWhenConfiguredAllowlistIsMalformed()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Tycho",
            Title = "Awake",
            CanonicalDuration = 270 * 1000
        };

        var candidate = new Track
        {
            Artist = "Tycho",
            Title = "Awake",
            Filename = "Tycho - Awake.flac",
            Format = "flac",
            Bitrate = 920,
            Length = 270,
            Size = 32_000_000,
            QueueLength = 0,
            Username = "peer-lossless"
        };

        var options = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "codecs=mp3" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024
        };

        var score = MatchScorer.ScoreCandidate(track, candidate, options);

        Assert.True(score >= 90, $"Expected fallback to default allowlist for malformed tokens, got {score}");
    }

    [Fact]
    public void TreatsRepeatedSourcesAsCaseInsensitiveAndTrimmed()
    {
        var track = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            Artist = "Orbital",
            Title = "Halcyon",
            CanonicalDuration = 546 * 1000
        };

        var candidate = new Track
        {
            Artist = "Orbital",
            Title = "Halcyon",
            Filename = "Orbital - Halcyon.flac",
            Format = "flac",
            Bitrate = 1000,
            Length = 546,
            Size = 40_000_000,
            QueueLength = 30,
            Username = "Trusted_Source"
        };

        var untrustedOptions = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024,
            RepeatedSources = new HashSet<string> { "another_peer" }
        };

        var trustedOptions = new MatchScoringOptions
        {
            AllowedExtensions = new List<string> { "flac" },
            MinBitrateKbps = 320,
            MinFileSizeBytes = 500 * 1024,
            RepeatedSources = new HashSet<string> { "  trusted_source  " }
        };

        var untrustedScore = MatchScorer.ScoreCandidate(track, candidate, untrustedOptions);
        var trustedScore = MatchScorer.ScoreCandidate(track, candidate, trustedOptions);

        Assert.True(trustedScore > untrustedScore, $"Expected trusted score > untrusted score, got trusted={trustedScore}, untrusted={untrustedScore}");
    }
}
