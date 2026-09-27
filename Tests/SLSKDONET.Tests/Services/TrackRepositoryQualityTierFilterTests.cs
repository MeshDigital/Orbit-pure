using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // TrackRepository.ApplyQualityTierFilter — boundary behavior for the Dashboard's
    // Gold/Silver/Bronze badges (HomeViewModel.NavigateLibraryCommand). Thresholds must
    // match DashboardService's Gold/Silver/Bronze counts exactly, or a badge's count and
    // the tracks it navigates to would disagree.
    // ─────────────────────────────────────────────────────────────────────────

    public class TrackRepositoryQualityTierFilterTests
    {
        private static List<LibraryEntryEntity> SampleLibraryEntries() => new()
        {
            new LibraryEntryEntity { UniqueHash = "flac-track", Format = "flac", Bitrate = 0 },
            new LibraryEntryEntity { UniqueHash = "wav-track", Format = "WAV", Bitrate = 1411 },
            new LibraryEntryEntity { UniqueHash = "mp3-320", Format = "mp3", Bitrate = 320 },
            new LibraryEntryEntity { UniqueHash = "mp3-319", Format = "mp3", Bitrate = 319 },
            new LibraryEntryEntity { UniqueHash = "mp3-128", Format = "mp3", Bitrate = 128 },
            new LibraryEntryEntity { UniqueHash = "zero-bitrate", Format = "mp3", Bitrate = 0 },
        };

        [Fact]
        public void LibraryEntry_Gold_MatchesOnlyFlacAndWav()
        {
            var result = TrackRepository.ApplyQualityTierFilter(SampleLibraryEntries().AsQueryable(), "Gold").ToList();

            Assert.Equal(new[] { "flac-track", "wav-track" }, result.Select(t => t.UniqueHash).OrderBy(h => h));
        }

        [Fact]
        public void LibraryEntry_Silver_MatchesExactly320AndAboveButNotLossless()
        {
            var result = TrackRepository.ApplyQualityTierFilter(SampleLibraryEntries().AsQueryable(), "Silver").ToList();

            Assert.Equal(new[] { "mp3-320" }, result.Select(t => t.UniqueHash));
        }

        [Fact]
        public void LibraryEntry_Bronze_Matches319AndBelowButExcludesZeroBitrate()
        {
            var result = TrackRepository.ApplyQualityTierFilter(SampleLibraryEntries().AsQueryable(), "Bronze").ToList();

            Assert.Equal(new[] { "mp3-128", "mp3-319" }, result.Select(t => t.UniqueHash).OrderBy(h => h));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Unknown")]
        public void LibraryEntry_NoOrUnknownTier_ReturnsAllTracksUnfiltered(string? tier)
        {
            var result = TrackRepository.ApplyQualityTierFilter(SampleLibraryEntries().AsQueryable(), tier).ToList();

            Assert.Equal(6, result.Count);
        }

        private static List<PlaylistTrackEntity> SamplePlaylistTracks() => new()
        {
            new PlaylistTrackEntity { TrackUniqueHash = "flac-track", Format = "flac", Bitrate = 0 },
            new PlaylistTrackEntity { TrackUniqueHash = "mp3-320", Format = "mp3", Bitrate = 320 },
            new PlaylistTrackEntity { TrackUniqueHash = "mp3-319", Format = "mp3", Bitrate = 319 },
        };

        [Fact]
        public void PlaylistTrack_SameThresholds_AppliedConsistentlyWithLibraryEntry()
        {
            Assert.Equal(new[] { "flac-track" },
                TrackRepository.ApplyQualityTierFilter(SamplePlaylistTracks().AsQueryable(), "Gold").Select(t => t.TrackUniqueHash));
            Assert.Equal(new[] { "mp3-320" },
                TrackRepository.ApplyQualityTierFilter(SamplePlaylistTracks().AsQueryable(), "Silver").Select(t => t.TrackUniqueHash));
            Assert.Equal(new[] { "mp3-319" },
                TrackRepository.ApplyQualityTierFilter(SamplePlaylistTracks().AsQueryable(), "Bronze").Select(t => t.TrackUniqueHash));
        }
    }
}
