using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // TrackRepository.ApplyMetadata — dual-truth metadata/analysis separation.
    // Metadata enrichment (Spotify/MusicBrainz) must never overwrite analysis-owned
    // BPM/Key/Energy/Danceability/Valence; both sources are kept side by side.
    // ─────────────────────────────────────────────────────────────────────────

    public class TrackRepositoryMetadataTests
    {
        private readonly TrackRepository _sut = new(NullLogger<TrackRepository>.Instance);

        [Fact]
        public void ApplyMetadata_LibraryEntry_DoesNotOverwriteExistingBpmAndKey()
        {
            var entity = new LibraryEntryEntity { BPM = 174, MusicalKey = "8B" };
            var result = new TrackEnrichmentResult { Success = true, Bpm = 88, MusicalKey = "3A" };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(174, entity.BPM);
            Assert.Equal("8B", entity.MusicalKey);
            Assert.Equal(88, entity.SpotifyBPM);
            Assert.Equal("3A", entity.SpotifyKey);
        }

        [Fact]
        public void ApplyMetadata_LibraryEntry_FillsEmptyBpmAndKeyFromMetadata()
        {
            var entity = new LibraryEntryEntity { BPM = null, MusicalKey = null };
            var result = new TrackEnrichmentResult { Success = true, Bpm = 88, MusicalKey = "3A" };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(88, entity.BPM);
            Assert.Equal("3A", entity.MusicalKey);
            Assert.Equal(88, entity.SpotifyBPM);
            Assert.Equal("3A", entity.SpotifyKey);
        }

        [Fact]
        public void ApplyMetadata_LibraryEntry_DoesNotOverwriteExistingEnergyDanceabilityValence()
        {
            var entity = new LibraryEntryEntity { Energy = 0.9, Danceability = 0.8, Valence = 0.7 };
            var result = new TrackEnrichmentResult { Success = true, Energy = 0.1, Danceability = 0.2, Valence = 0.3 };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(0.9, entity.Energy);
            Assert.Equal(0.8, entity.Danceability);
            Assert.Equal(0.7, entity.Valence);
        }

        [Fact]
        public void ApplyMetadata_LibraryEntry_FillsEmptyEnergyDanceabilityValence()
        {
            var entity = new LibraryEntryEntity();
            var result = new TrackEnrichmentResult { Success = true, Energy = 0.1, Danceability = 0.2, Valence = 0.3 };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(0.1, entity.Energy);
            Assert.Equal(0.2, entity.Danceability);
            Assert.Equal(0.3, entity.Valence);
        }

        [Fact]
        public void ApplyMetadata_PlaylistTrack_DoesNotOverwriteExistingBpmAndKey()
        {
            var entity = new PlaylistTrackEntity { BPM = 174, MusicalKey = "8B" };
            var result = new TrackEnrichmentResult { Success = true, Bpm = 88, MusicalKey = "3A" };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(174, entity.BPM);
            Assert.Equal("8B", entity.MusicalKey);
            Assert.Equal(88, entity.SpotifyBPM);
            Assert.Equal("3A", entity.SpotifyKey);
        }

        [Fact]
        public void ApplyMetadata_Track_DoesNotOverwriteExistingBpmAndKey()
        {
            var entity = new TrackEntity { BPM = 174, MusicalKey = "8B" };
            var result = new TrackEnrichmentResult { Success = true, Bpm = 88, MusicalKey = "3A" };

            _sut.ApplyMetadata(entity, result);

            Assert.Equal(174, entity.BPM);
            Assert.Equal("8B", entity.MusicalKey);
            Assert.Equal(88, entity.SpotifyBPM);
            Assert.Equal("3A", entity.SpotifyKey);
        }
    }
}
