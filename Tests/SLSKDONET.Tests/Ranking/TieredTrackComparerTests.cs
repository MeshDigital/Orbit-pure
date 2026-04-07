using SLSKDONET.Models;
using SLSKDONET.Services.Ranking;
using System.Collections.Generic;
using Xunit;

namespace SLSKDONET.Tests.Ranking;

public class TieredTrackComparerTests
{
    private readonly Track _searchTrack;

    public TieredTrackComparerTests()
    {
        _searchTrack = new Track 
        { 
            Artist = "Test Artist",
            Title = "Test Title",
            Length = 300, 
            BPM = 120 
        };
    }

    [Fact]
    public void DiamondTier_ShouldBeat_GoldTier()
    {
        // Arrange
        var policy = SearchPolicy.QualityFirst();
        var comparer = new TieredTrackComparer(policy, _searchTrack);

        var diamondTrack = new Track 
        { 
            Bitrate = 320, 
            Length = 300, 
            HasFreeUploadSlot = true
        };

        var goldTrack = new Track 
        { 
            Bitrate = 320, 
            Length = 300, 
            HasFreeUploadSlot = false, // Queue makes it Gold
            QueueLength = 5
        };

        // Act
        // Compare returns < 0 if x is less than y.
        // In sorting, "less than" means "comes first".
        // So Diamond < Gold means Diamond comes first.
        int result = comparer.Compare(diamondTrack, goldTrack);

        // Assert
        Assert.True(result < 0, "Diamond track should be ranked higher (smaller value) than Gold track");
    }

    [Fact]
    public void DjMode_ShouldPrioritize_BpmMatch()
    {
        // Arrange
        var policy = SearchPolicy.DjReady(); // DJ Mode
        var comparer = new TieredTrackComparer(policy, _searchTrack);

        var highQualityMismatch = new Track 
        { 
            Bitrate = 320, 
            Length = 300, 
            BPM = 140, // BPM Mismatch
            HasFreeUploadSlot = true
        };

        var lowerQualityMatch = new Track 
        { 
            Bitrate = 192, 
            Length = 300, 
            BPM = 120, // Perfect BPM Match
            HasFreeUploadSlot = true
        };

        // Act
        int result = comparer.Compare(lowerQualityMatch, highQualityMismatch);

        // Assert
        // In DJ Mode, the BPM match might push it to a higher tier or sort order
        // Depending on exact implementation:
        // Tier 1 (Diamond) might require 320kbps AND BPM match?
        // Let's assume matching BPM is weighted heavily in DJ Mode.
        Assert.True(result < 0, "In DJ Mode, BPM match should win");
    }

    [Fact]
    public void QualityFirst_ShouldPrioritize_Bitrate()
    {
        // Arrange
        var policy = SearchPolicy.QualityFirst();
        var comparer = new TieredTrackComparer(policy, _searchTrack);

        var flacTrack = new Track 
        { 
            Bitrate = 1000, 
            Format = "FLAC",
            Length = 300, 
            HasFreeUploadSlot = true 
        };

        var mp3Track = new Track 
        { 
            Bitrate = 128, 
            Length = 300, 
            HasFreeUploadSlot = true 
        };

        // Act
        int result = comparer.Compare(flacTrack, mp3Track);

        // Assert
        Assert.True(result < 0, "FLAC should beat 128kbps MP3 in Quality Mode");
    }

    [Fact]
    public void DjMode_ShouldPrioritize_KeyMatch()
    {
        // Arrange
        var policy = SearchPolicy.DjReady();
        var searchWithKey = new Track { Artist = "A", Title = "T", BPM = 120, MusicalKey = "8A", Energy = 0.5 };
        var comparer = new TieredTrackComparer(policy, searchWithKey);

        var keyMatch = new Track { Bitrate = 320, BPM = 120, MusicalKey = "8A", Energy = 0.5, HasFreeUploadSlot = true };
        var keyMismatch = new Track { Bitrate = 320, BPM = 120, MusicalKey = "1A", Energy = 0.5, HasFreeUploadSlot = true };

        // Act
        int result = comparer.Compare(keyMatch, keyMismatch);

        // Assert
        Assert.True(result < 0, "Key match should be ranked higher than mismatch in DJ Mode");
        Assert.Equal(TrackTier.Diamond, (TrackTier)typeof(TieredTrackComparer).GetMethod("CalculateTier", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(comparer, new object[] { keyMatch }));
    }

    [Fact]
    public void DjMode_ShouldPrioritize_EnergyMatch()
    {
        // Arrange
        var policy = SearchPolicy.DjReady();
        var searchWithEnergy = new Track { Artist = "A", Title = "T", BPM = 120, Energy = 0.8 };
        var comparer = new TieredTrackComparer(policy, searchWithEnergy);

        var energyMatch = new Track { Bitrate = 320, BPM = 120, Energy = 0.8, HasFreeUploadSlot = true };
        var energyMismatch = new Track { Bitrate = 320, BPM = 120, Energy = 0.2, HasFreeUploadSlot = true };

        // Act
        int result = comparer.Compare(energyMatch, energyMismatch);

        // Assert
        Assert.True(result < 0, "Energy match should be ranked higher than mismatch in DJ Mode");
    }

    [Fact]
    public void Leniency_MissingCandidateMetadata_ShouldNotBeTrash()
    {
        // Arrange
        var policy = SearchPolicy.DjReady();
        var searchTrack = new Track { Artist = "A", Title = "T", BPM = 120, MusicalKey = "8A", Energy = 0.8 };
        var comparer = new TieredTrackComparer(policy, searchTrack);

        // Candidate has NO sonic metadata (common for search results)
        var bareCandidate = new Track 
        { 
            Bitrate = 320, 
            Length = 300, 
            HasFreeUploadSlot = true,
            BPM = null,
            MusicalKey = null,
            Energy = null
        };

        // Act
        var score = comparer.CalculateRankScore(bareCandidate);

        // Assert
        // Should be at least Gold or Silver, definitely NOT Trash (0.1)
        Assert.True(score >= 0.6, "Bare candidates should be treated leniently and not dumped to Trash");
    }

    [Fact]
    public void Integrity_SuspiciousFile_ShouldBeDemoted()
    {
        // Arrange
        var policy = SearchPolicy.QualityFirst();
        var comparer = new TieredTrackComparer(policy, _searchTrack);

        var suspiciousTrack = new Track 
        { 
            Bitrate = 320, 
            Length = 60, // Way too short vs 300s
            HasFreeUploadSlot = true 
        };

        var normalTrack = new Track 
        { 
            Bitrate = 320, 
            Length = 300, 
            HasFreeUploadSlot = true 
        };

        // Act
        int result = comparer.Compare(normalTrack, suspiciousTrack);

        // Assert
        Assert.True(result < 0, "Normal track should beat suspicious track");
    }
}
