using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services
{
    public interface ISmartPlaylistService
    {
        bool EvaluateTrack(TrackEntity track, SmartPlaylistCriteria criteria);
        bool EvaluateTrack(LibraryEntryEntity track, SmartPlaylistCriteria criteria);
        bool EvaluateTrack(PlaylistTrack track, SmartPlaylistCriteria criteria);
        Task<List<string>> FindMatchesAsync(SmartPlaylistCriteria criteria);
    }

    public class SmartPlaylistService : ISmartPlaylistService
    {
        private readonly ILogger<SmartPlaylistService> _logger;
        private readonly DatabaseService _db;

        public SmartPlaylistService(ILogger<SmartPlaylistService> logger, DatabaseService db)
        {
            _logger = logger;
            _db = db;
        }

        public bool EvaluateTrack(LibraryEntryEntity track, SmartPlaylistCriteria criteria)
        {
            // Energy
            if (criteria.MinEnergy.HasValue && (track.Energy ?? 0) < criteria.MinEnergy.Value) return false;
            if (criteria.MaxEnergy.HasValue && (track.Energy ?? 0) > criteria.MaxEnergy.Value) return false;

            // Mood (Valence)
            if (criteria.MinValence.HasValue && (track.Valence ?? 0) < criteria.MinValence.Value) return false;
            if (criteria.MaxValence.HasValue && (track.Valence ?? 0) > criteria.MaxValence.Value) return false;

            // Danceability
            if (criteria.MinDanceability.HasValue && (track.Danceability ?? 0) < criteria.MinDanceability.Value) return false;
            if (criteria.MaxDanceability.HasValue && (track.Danceability ?? 0) > criteria.MaxDanceability.Value) return false;

            // BPM
            var bpm = track.BPM ?? 0;
            if (criteria.MinBPM.HasValue && bpm < criteria.MinBPM.Value) return false;
            if (criteria.MaxBPM.HasValue && bpm > criteria.MaxBPM.Value) return false;

            // Genre (Text Contains)
            if (!string.IsNullOrWhiteSpace(criteria.Genre))
            {
                var genreMatch = (track.Genres ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase) ||
                                 (track.DetectedSubGenre ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase) ||
                                 (track.PrimaryGenre ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase);
                
                if (!genreMatch) return false;
            }

            // Rating
            if (criteria.MinRating.HasValue && track.Rating < criteria.MinRating.Value) return false;

            // Liked
            if (criteria.IsLiked.HasValue && track.IsLiked != criteria.IsLiked.Value) return false;

            return true;
        }

        public bool EvaluateTrack(TrackEntity track, SmartPlaylistCriteria criteria)
        {
            // Energy
            if (criteria.MinEnergy.HasValue && (track.Energy ?? 0) < criteria.MinEnergy.Value) return false;
            if (criteria.MaxEnergy.HasValue && (track.Energy ?? 0) > criteria.MaxEnergy.Value) return false;

            // Mood (Valence)
            if (criteria.MinValence.HasValue && (track.Valence ?? 0) < criteria.MinValence.Value) return false;
            if (criteria.MaxValence.HasValue && (track.Valence ?? 0) > criteria.MaxValence.Value) return false;

            // Danceability
            if (criteria.MinDanceability.HasValue && (track.Danceability ?? 0) < criteria.MinDanceability.Value) return false;
            if (criteria.MaxDanceability.HasValue && (track.Danceability ?? 0) > criteria.MaxDanceability.Value) return false;

            // BPM
            var bpm = track.BPM ?? 0;
            if (criteria.MinBPM.HasValue && bpm < criteria.MinBPM.Value) return false;
            if (criteria.MaxBPM.HasValue && bpm > criteria.MaxBPM.Value) return false;

            // Genre
            if (!string.IsNullOrWhiteSpace(criteria.Genre))
            {
                var genreMatch = (track.Genres ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase) ||
                                 (track.DetectedSubGenre ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase);
                if (!genreMatch) return false;
            }

            // Rating & Liked (TrackEntity doesn't have these directly)
            
            return true;
        }

        public bool EvaluateTrack(PlaylistTrack track, SmartPlaylistCriteria criteria)
        {
            // Energy
            if (criteria.MinEnergy.HasValue && (track.Energy ?? 0) < criteria.MinEnergy.Value) return false;
            if (criteria.MaxEnergy.HasValue && (track.Energy ?? 0) > criteria.MaxEnergy.Value) return false;

            // Mood (Valence)
            if (criteria.MinValence.HasValue && (track.Valence ?? 0) < criteria.MinValence.Value) return false;
            if (criteria.MaxValence.HasValue && (track.Valence ?? 0) > criteria.MaxValence.Value) return false;

            // Danceability
            if (criteria.MinDanceability.HasValue && (track.Danceability ?? 0) < criteria.MinDanceability.Value) return false;
            if (criteria.MaxDanceability.HasValue && (track.Danceability ?? 0) > criteria.MaxDanceability.Value) return false;

            // BPM
            var bpm = track.BPM ?? 0;
            if (criteria.MinBPM.HasValue && bpm < criteria.MinBPM.Value) return false;
            if (criteria.MaxBPM.HasValue && bpm > criteria.MaxBPM.Value) return false;

            // Genre
            if (!string.IsNullOrWhiteSpace(criteria.Genre))
            {
                var genreMatch = (track.Genres ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase) ||
                                 (track.DetectedSubGenre ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase) ||
                                 (track.PrimaryGenre ?? "").Contains(criteria.Genre, StringComparison.OrdinalIgnoreCase);
                if (!genreMatch) return false;
            }

            // Rating
            if (criteria.MinRating.HasValue && track.Rating < criteria.MinRating.Value) return false;

            // Liked
            if (criteria.IsLiked.HasValue && track.IsLiked != criteria.IsLiked.Value) return false;

            return true;
        }

        public async Task<List<string>> FindMatchesAsync(SmartPlaylistCriteria criteria)
        {
            // Load all library entries (Optimize later with LINQ to SQL if performance is bad)
            // For now, in-memory filtering is safer for JSON/Nullable complexity
            var allTracks = await _db.GetAllLibraryEntriesAsync();
            
            return allTracks
                .Where(t => EvaluateTrack(t, criteria))
                .Select(t => t.UniqueHash)
                .ToList();
        }
    }
}
