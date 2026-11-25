using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;
using SLSKDONET.Utils;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Views;

namespace SLSKDONET.Services;


/// <summary>
/// Entity/domain model conversions, JSON helpers, and library reconciliation (#307).
/// </summary>
public partial class LibraryService
{
    // ===== Helper Conversion Methods =====

    private PlaylistJob EntityToPlaylistJob(PlaylistJobEntity entity)
    {
        var playlistTracks = entity.Tracks?.Select(EntityToPlaylistTrack).ToList() ?? new List<PlaylistTrack>();

        var originalTracks = new ObservableCollection<Track>(playlistTracks.Select(pt => new Track {
            Artist = pt.Artist,
            Title = pt.Title,
            Album = pt.Album,
        }));

        var job = new PlaylistJob
        {
            Id = entity.Id,
            SourceTitle = entity.SourceTitle,
            SourceType = entity.SourceType,
            DestinationFolder = entity.DestinationFolder,
            CreatedAt = entity.CreatedAt,
            OriginalTracks = originalTracks,
            PlaylistTracks = playlistTracks,
            SuccessfulCount = entity.SuccessfulCount,
            FailedCount = entity.FailedCount,

            AlbumArtUrl = entity.AlbumArtUrl,
            SourceUrl = entity.SourceUrl,
            
            // Phase 2.5
            IsUserPaused = entity.IsUserPaused,
            DateStarted = entity.DateStarted,
            DateUpdated = entity.DateUpdated,
            IsDeleted = entity.IsDeleted,
            DeletedAt = entity.DeletedAt,

            // Phase 20
            IsSmartPlaylist = entity.IsSmartPlaylist,
            SmartCriteriaJson = entity.SmartCriteriaJson,

            FolderId = entity.FolderId,
            ManualSortOrder = entity.ManualSortOrder
        };

        job.MissingCount = entity.TotalTracks - entity.SuccessfulCount - entity.FailedCount;
        job.RefreshStatusCounts();

        return job;
    }

    private PlaylistTrack EntityToPlaylistTrack(PlaylistTrackEntity entity)
    {
        // TechnicalDetails.WaveformData/RmsData/LowData/MidData/HighData were dropped (dead columns,
        // never populated) — the real path is always AudioFeaturesEntity.WaveformBlob below.
        var playlistWaveform = ResolveWaveformBands(
            null,
            null,
            null,
            null,
            null,
            entity.AudioFeatures?.WaveformBlob,
            entity.AudioFeatures?.WaveformBlobSampleCount ?? 0);

        return new PlaylistTrack
        {
            Id = entity.Id,
            PlaylistId = entity.PlaylistId,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            TrackUniqueHash = entity.TrackUniqueHash,
            Status = entity.Status,
            IsClearedFromDownloadCenter = entity.IsClearedFromDownloadCenter,
            ResolvedFilePath = entity.ResolvedFilePath ?? "",
            TrackNumber = entity.TrackNumber,
            Rating = entity.Rating,
            IsLiked = entity.IsLiked,
            PlayCount = entity.PlayCount,
            LastPlayedAt = entity.LastPlayedAt,
            AddedAt = entity.AddedAt,
            SortOrder = entity.SortOrder,
            PreferredFormats = entity.PreferredFormats,
            MinBitrateOverride = entity.MinBitrateOverride,
            Format = SLSKDONET.Models.AudioFileFormat.Resolve(entity.Format, entity.ResolvedFilePath),
            AvailabilityState = entity.AvailabilityState,
            SpotifyPlaylistId = entity.SpotifyPlaylistId,
            SpotifyUri = entity.SpotifyUri,
            
            // Spotify Metadata
            SpotifyTrackId = entity.SpotifyTrackId,
            ISRC = entity.ISRC,
            MusicBrainzId = entity.MusicBrainzId,
            SpotifyAlbumId = entity.SpotifyAlbumId,
            SpotifyArtistId = entity.SpotifyArtistId,
            AlbumArtUrl = entity.AlbumArtUrl,
            // Load waveform bands from TechnicalDetails (Lazy loaded via Include in Repository)
            WaveformData = playlistWaveform.PeakData,
            RmsData = playlistWaveform.RmsData,
            LowData = playlistWaveform.LowData,
            MidData = playlistWaveform.MidData,
            HighData = playlistWaveform.HighData,
            ArtistImageUrl = entity.ArtistImageUrl,
            Genres = entity.Genres,
            Popularity = entity.Popularity,
            CanonicalDuration = entity.CanonicalDuration,
            ActualDurationSeconds = entity.ActualDurationSeconds,
            ReleaseDate = entity.ReleaseDate,

            // Musical Intelligence (Fallback to AudioFeatures if direct columns are not populated)
            MusicalKey = !string.IsNullOrEmpty(entity.MusicalKey) ? entity.MusicalKey : entity.AudioFeatures?.Key,
            BPM = entity.BPM > 0 ? entity.BPM : (entity.AudioFeatures?.Bpm > 0 ? (double?)entity.AudioFeatures.Bpm : null),
            CuePointsJson = !string.IsNullOrWhiteSpace(entity.TechnicalDetails?.CuePointsJson)
                ? entity.TechnicalDetails!.CuePointsJson
                : entity.CuePointsJson,
            CuePointCount = entity.CuePointCount,
            Energy = entity.Energy > 0 ? entity.Energy : (entity.AudioFeatures?.Energy > 0 ? (double?)entity.AudioFeatures.Energy : null),
            Danceability = entity.Danceability > 0 ? entity.Danceability : (entity.AudioFeatures?.Danceability > 0 ? (double?)entity.AudioFeatures.Danceability : null),
            Valence = entity.Valence > 0 ? entity.Valence : (entity.AudioFeatures?.Valence > 0 ? (double?)entity.AudioFeatures.Valence : null),
            Label = entity.Label,
            Comments = entity.Comments,
            ColorTag = entity.ColorTag,
            MoodTag = !string.IsNullOrEmpty(entity.MoodTag) ? entity.MoodTag : entity.AudioFeatures?.MoodTag,
            MoodConfidence = entity.AudioFeatures?.MoodConfidence,
            PrimaryGenre = entity.PrimaryGenre,

            // Phase 21: AI Brain - Mapped below via AudioFeatures
            // Sadness = entity.Sadness, // Removed
            // VectorEmbedding = entity.VectorEmbedding, // Removed

            AnalysisOffset = entity.AnalysisOffset,
            BitrateScore = entity.BitrateScore,
            Bitrate = entity.Bitrate,
            
            // Dual-Truth
            SpotifyBPM = entity.SpotifyBPM,
            SpotifyKey = entity.SpotifyKey,
            ManualBPM = entity.ManualBPM,
            ManualKey = entity.ManualKey,
            TagBPM = entity.TagBPM,

            IsEnriched = entity.IsEnriched,
            
            // Sonic Integrity
            Integrity = entity.Integrity,
            IsTrustworthy = entity.IsTrustworthy,
            QualityConfidence = entity.QualityConfidence,
            FrequencyCutoff = entity.FrequencyCutoff,
            QualityDetails = entity.QualityDetails,
            // Derive IsTranscoded and SpectralVerdictText from stored Integrity level + QualityDetails
            IsTranscoded = entity.Integrity == SLSKDONET.Data.IntegrityLevel.Suspicious,
            SpectralVerdictText = !string.IsNullOrEmpty(entity.QualityDetails)
                ? entity.QualityDetails.Split('|')[0].Trim()
                : null,

            // Extended spectral forensics (populated by PostDownloadSpectralScanService)
            SpectralSampleRateHz   = entity.SpectralSampleRateHz,
            SpectralBitDepth       = entity.SpectralBitDepth,
            SpectralRolloffSteepness = entity.SpectralRolloffSteepness,
            SpectralMidBandEnergy  = entity.SpectralMidBandEnergy,
            SpectralHighBandEnergy = entity.SpectralHighBandEnergy,
            SpectralRmsDbfs        = entity.SpectralRmsDbfs,
            SpectralCrestFactorDb  = entity.SpectralCrestFactorDb,
            SpectralNoiseFloorDbfs = entity.SpectralNoiseFloorDbfs,
            
            // Phase 17: Technical Audio Analysis
            Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(entity.Loudness),
            TruePeak = entity.TruePeak,
            DynamicRange = entity.DynamicRange,
            
            // Phase 15
            DetectedSubGenre = !string.IsNullOrEmpty(entity.DetectedSubGenre) ? entity.DetectedSubGenre : entity.AudioFeatures?.DetectedSubGenre,
            SubGenreConfidence = entity.SubGenreConfidence > 0 ? entity.SubGenreConfidence : entity.AudioFeatures?.SubGenreConfidence,
            InstrumentalProbability = entity.InstrumentalProbability ?? (entity.AudioFeatures?.InstrumentalProbability > 0 ? (double?)entity.AudioFeatures.InstrumentalProbability : null), // Phase 18.2

            // Phase 21: AI Brain (Mapped from AudioFeatures)
            Sadness = entity.AudioFeatures?.Sadness,
            VectorEmbedding = entity.AudioFeatures?.VectorEmbedding,
            MoodHappy = entity.AudioFeatures?.MoodHappy,
            MoodRelaxed = entity.AudioFeatures?.MoodRelaxed,
            MoodParty = entity.AudioFeatures?.MoodParty,
            MoodAggressive = entity.AudioFeatures?.MoodAggressive,
            GenreDistributionJson = entity.AudioFeatures?.GenreDistributionJson,

            BpmStability = entity.AudioFeatures?.BpmStability,
            TimeTillDropSeconds = entity.AudioFeatures?.TimeTillDropSeconds,

            GridConfidence = entity.AudioFeatures?.GridConfidence,
            GridVerified = entity.AudioFeatures?.GridVerified ?? false,

            SourcePlaylistId   = entity.SourcePlaylistId,
            SourcePlaylistName = entity.SourcePlaylistName,
        };
    }

    private PlaylistTrackEntity PlaylistTrackToEntity(PlaylistTrack track)
    {
        return new PlaylistTrackEntity
        {
            Id = track.Id,
            PlaylistId = track.PlaylistId,
            Artist = track.Artist,
            Title = track.Title,
            Album = track.Album,
            TrackUniqueHash = track.TrackUniqueHash,
            Status = track.Status,
            IsClearedFromDownloadCenter = track.IsClearedFromDownloadCenter,
            ResolvedFilePath = track.ResolvedFilePath,
            TrackNumber = track.TrackNumber,
            Rating = track.Rating,
            IsLiked = track.IsLiked,
            PlayCount = track.PlayCount,
            LastPlayedAt = track.LastPlayedAt,
            AddedAt = track.AddedAt,
            SortOrder = track.SortOrder,
            PreferredFormats = track.PreferredFormats,
            MinBitrateOverride = track.MinBitrateOverride,
            Format = track.Format,
            AvailabilityState = track.AvailabilityState,
            SpotifyPlaylistId = track.SpotifyPlaylistId,
            SpotifyUri = track.SpotifyUri,
            
            // Spotify Metadata
            SpotifyTrackId = track.SpotifyTrackId,
            ISRC = track.ISRC,
            MusicBrainzId = track.MusicBrainzId,
            SpotifyAlbumId = track.SpotifyAlbumId,
            SpotifyArtistId = track.SpotifyArtistId,
            AlbumArtUrl = track.AlbumArtUrl,
            // HEAVY DATA REFACTOR: Managed via TechnicalDetails
            ArtistImageUrl = track.ArtistImageUrl,
            Genres = track.Genres,
            Popularity = track.Popularity,
            CanonicalDuration = track.CanonicalDuration,
            ActualDurationSeconds = track.ActualDurationSeconds,
            ReleaseDate = track.ReleaseDate,

            // Musical Intelligence
            MusicalKey = track.MusicalKey,
            BPM = track.BPM,
            Energy = track.Energy,
            DetectedSubGenre = track.DetectedSubGenre,
            Valence = track.Valence,
            Label = track.Label,
            Comments = track.Comments,
            ColorTag = track.ColorTag,
            MoodTag = track.MoodTag,
            PrimaryGenre = track.PrimaryGenre,
            AnalysisOffset = track.AnalysisOffset,
            BitrateScore = track.BitrateScore,
            Bitrate = track.Bitrate ?? 0,
            
            // Dual-Truth
            SpotifyBPM = track.SpotifyBPM,
            SpotifyKey = track.SpotifyKey,
            ManualBPM = track.ManualBPM,
            ManualKey = track.ManualKey,
            TagBPM = track.TagBPM,

            IsEnriched = track.IsEnriched,
            
            // Sonic Integrity
            Integrity = track.Integrity,
            IsTrustworthy = track.IsTrustworthy,
            QualityConfidence = track.QualityConfidence,
            FrequencyCutoff = track.FrequencyCutoff,

            QualityDetails = track.QualityDetails,
            
            // Phase 17: Technical Audio Analysis
            Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(track.Loudness),
            TruePeak = track.TruePeak,
            DynamicRange = track.DynamicRange,
            InstrumentalProbability = track.InstrumentalProbability, // Phase 18.2

            // ── Columns previously omitted from this mapping ─────────────────────────────
            // These are real PlaylistTracks columns populated outside this converter (the
            // post-download spectral scan writes the Spectral* forensics via raw SQL, and the
            // enrichment/vocal pipelines write the rest). SavePlaylistTrackAsync and
            // SavePlaylistTracksAsync upsert the whole row with CurrentValues.SetValues(...), so
            // any column left unmapped here was reset to its CLR default on every save. Map every
            // one of them so the round-trip never blanks analysis data.
            CompletedAt = track.CompletedAt,
            Priority = track.Priority,
            IsUserPaused = track.IsUserPaused,
            StalledReason = track.StalledReason,
            IsPrepared = track.IsPrepared,
            AnalysisStatus = track.AnalysisStatus,
            EnrichmentAttempts = track.EnrichmentAttempts,
            LastEnrichmentAttempt = track.LastEnrichmentAttempt,
            DropTimestamp = track.DropTimestamp,
            ManualEnergy = track.ManualEnergy,
            SourceProvenance = track.SourceProvenance,
            SearchRetryCount = track.SearchRetryCount,
            NotFoundRestartCount = track.NotFoundRestartCount,
            CuePointsJson = track.CuePointsJson,
            AudioFingerprint = track.AudioFingerprint,
            Danceability = track.Danceability,
            Arousal = track.Arousal,
            IsDjTool = track.IsDjTool,
            SubGenreConfidence = track.SubGenreConfidence,
            SpectralHash = track.SpectralHash,
            SpectralSampleRateHz = track.SpectralSampleRateHz,
            SpectralBitDepth = track.SpectralBitDepth,
            SpectralRolloffSteepness = track.SpectralRolloffSteepness,
            SpectralMidBandEnergy = track.SpectralMidBandEnergy,
            SpectralHighBandEnergy = track.SpectralHighBandEnergy,
            SpectralRmsDbfs = track.SpectralRmsDbfs,
            SpectralCrestFactorDb = track.SpectralCrestFactorDb,
            SpectralNoiseFloorDbfs = track.SpectralNoiseFloorDbfs,
            VocalType = track.VocalType,
            VocalIntensity = track.VocalIntensity,
            VocalStartSeconds = track.VocalStartSeconds,
            VocalEndSeconds = track.VocalEndSeconds
            
            // Phase 21: AI Brain - READ ONLY via AudioFeatures link
            // We do not set Sadness/Vector on the PlaylistTrackEntity directly.
            // They are stored in AudioFeaturesEntity linked by TrackUniqueHash.
        };
    }

    // ===== Private Helper Methods (JSON - LibraryEntry only) =====
    
    private LibraryEntry EntityToLibraryEntry(LibraryEntryEntity entity)
    {
        var libraryWaveform = ResolveWaveformBands(
            entity.WaveformData,
            entity.RmsData,
            entity.LowData,
            entity.MidData,
            entity.HighData,
            entity.AudioFeatures?.WaveformBlob,
            entity.AudioFeatures?.WaveformBlobSampleCount ?? 0);

        return new LibraryEntry
        {
            Id = entity.Id,
            UniqueHash = entity.UniqueHash,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            FilePath = entity.FilePath,
            Bitrate = entity.Bitrate,
            DurationSeconds = entity.DurationSeconds,
            Format = SLSKDONET.Models.AudioFileFormat.Resolve(entity.Format, entity.FilePath) ?? "",
            AvailabilityState = entity.AvailabilityState,
            SpotifyPlaylistId = entity.SpotifyPlaylistId,
            SpotifyUri = entity.SpotifyUri,
            AddedAt = entity.AddedAt,
            
            // Scientific Fields
            SpotifyTrackId = entity.SpotifyTrackId,
            ISRC = entity.ISRC,
            MusicBrainzId = entity.MusicBrainzId,
            Energy = entity.Energy > 0 ? entity.Energy : (entity.AudioFeatures?.Energy > 0 ? (double?)entity.AudioFeatures.Energy : null),
            Danceability = entity.Danceability > 0 ? entity.Danceability : (entity.AudioFeatures?.Danceability > 0 ? (double?)entity.AudioFeatures.Danceability : null),
            Valence = entity.Valence > 0 ? entity.Valence : (entity.AudioFeatures?.Valence > 0 ? (double?)entity.AudioFeatures.Valence : null),
            BPM = entity.BPM > 0 ? entity.BPM : (entity.AudioFeatures?.Bpm > 0 ? (double?)entity.AudioFeatures.Bpm : null),
            MusicalKey = !string.IsNullOrEmpty(entity.MusicalKey) ? entity.MusicalKey : entity.AudioFeatures?.Key,
            Label = entity.Label,
            Comments = entity.Comments,
            MoodTag = !string.IsNullOrEmpty(entity.MoodTag) ? entity.MoodTag : entity.AudioFeatures?.MoodTag,
            PrimaryGenre = entity.PrimaryGenre,

            // Phase 21: AI Brain
            Sadness = entity.AudioFeatures?.Sadness,
            VectorEmbedding = entity.AudioFeatures?.VectorEmbedding,

            IsEnriched = entity.IsEnriched,
            
            // Phase 17: Technical Audio Analysis
            Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(entity.Loudness) ?? SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(entity.AudioFeatures?.LoudnessLUFS),
            TruePeak = entity.TruePeak,
            DynamicRange = entity.DynamicRange,

            // Sonic Integrity (post-import / post-download spectral scan)
            SpectralHash = entity.SpectralHash,
            QualityDetails = entity.QualityDetails,
            Integrity = entity.Integrity,
            IsTranscoded = entity.IsTranscoded ?? false,
            
            WaveformData = libraryWaveform.PeakData,
            RmsData = libraryWaveform.RmsData,
            LowData = libraryWaveform.LowData,
            MidData = libraryWaveform.MidData,
            HighData = libraryWaveform.HighData,

            // Dual-Truth
            SpotifyBPM = entity.SpotifyBPM,
            SpotifyKey = entity.SpotifyKey,
            ManualBPM = entity.ManualBPM,
            ManualKey = entity.ManualKey,
            TagBPM = entity.TagBPM,

            InstrumentalProbability = entity.InstrumentalProbability ?? (entity.AudioFeatures?.InstrumentalProbability > 0 ? (double?)entity.AudioFeatures.InstrumentalProbability : null) // Phase 18.2
            ,
            BpmConfidence = entity.AudioFeatures?.BpmConfidence,
            KeyConfidence = entity.AudioFeatures?.KeyConfidence,
            BpmStability = entity.AudioFeatures?.BpmStability,
            CamelotKey = entity.AudioFeatures?.CamelotKey,
            ChordProgression = entity.AudioFeatures?.ChordProgression,
            LoudnessLufs = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(entity.AudioFeatures?.LoudnessLUFS),
            DropTimeSeconds = entity.AudioFeatures?.DropTimeSeconds,
            DropConfidence = entity.AudioFeatures?.DropConfidence,
            TimeTillDropSeconds = entity.AudioFeatures?.TimeTillDropSeconds,
            EnergyCurveJson = entity.AudioFeatures?.EnergyCurveJson,
            SegmentedEnergyJson = entity.AudioFeatures?.SegmentedEnergyJson,
            GenreDistributionJson = entity.AudioFeatures?.GenreDistributionJson,
            VocalDensity = entity.AudioFeatures?.VocalDensity,
            VocalType = entity.AudioFeatures?.DetectedVocalType ?? entity.VocalType
        };
    }

    private static (byte[] PeakData, byte[] RmsData, byte[] LowData, byte[] MidData, byte[] HighData) ResolveWaveformBands(
        byte[]? peakData,
        byte[]? rmsData,
        byte[]? lowData,
        byte[]? midData,
        byte[]? highData,
        byte[]? packedWaveformBlob,
        int waveformBlobSampleCount = 0)
    {
        var resolvedPeak = peakData ?? Array.Empty<byte>();
        var resolvedRms = rmsData ?? Array.Empty<byte>();
        var resolvedLow = lowData ?? Array.Empty<byte>();
        var resolvedMid = midData ?? Array.Empty<byte>();
        var resolvedHigh = highData ?? Array.Empty<byte>();

        var needsBlobFallback = resolvedLow.Length == 0 || resolvedMid.Length == 0 || resolvedHigh.Length == 0;
        if (needsBlobFallback && TryUnpackWaveformBlob(packedWaveformBlob, waveformBlobSampleCount, out var unpackedLow, out var unpackedMid, out var unpackedHigh))
        {
            resolvedLow = unpackedLow;
            resolvedMid = unpackedMid;
            resolvedHigh = unpackedHigh;
        }

        if (resolvedPeak.Length == 0)
        {
            resolvedPeak = SynthesizePeakData(resolvedLow, resolvedMid, resolvedHigh);
        }

        if (resolvedRms.Length == 0)
        {
            resolvedRms = SynthesizeRmsData(resolvedLow, resolvedMid, resolvedHigh);
        }

        return (resolvedPeak, resolvedRms, resolvedLow, resolvedMid, resolvedHigh);
    }

    /// <summary>
    /// Unpacks a low|mid|high packed waveform blob (see WaveformExtractionService) using the real
    /// per-band sample count it was written with — the extractor's sample count scales with track
    /// duration (2000-12000, not a fixed number), so a hardcoded band length here silently sliced
    /// the wrong byte ranges for virtually every track, corrupting the mid/high bands.
    /// </summary>
    private static bool TryUnpackWaveformBlob(
        byte[]? packedWaveformBlob,
        int sampleCount,
        out byte[] lowBand,
        out byte[] midBand,
        out byte[] highBand)
    {
        lowBand = Array.Empty<byte>();
        midBand = Array.Empty<byte>();
        highBand = Array.Empty<byte>();

        if (packedWaveformBlob is null || packedWaveformBlob.Length < 3)
        {
            return false;
        }

        // Fall back to an even three-way split for rows analyzed before WaveformBlobSampleCount
        // was populated, or if the stored count doesn't actually fit the blob (corrupt/short data).
        int bandLength = sampleCount > 0 && packedWaveformBlob.Length >= sampleCount * 3
            ? sampleCount
            : packedWaveformBlob.Length / 3;

        if (bandLength <= 0)
        {
            return false;
        }

        lowBand = new byte[bandLength];
        midBand = new byte[bandLength];
        highBand = new byte[bandLength];

        Buffer.BlockCopy(packedWaveformBlob, 0, lowBand, 0, bandLength);
        Buffer.BlockCopy(packedWaveformBlob, bandLength, midBand, 0, bandLength);
        Buffer.BlockCopy(packedWaveformBlob, bandLength * 2, highBand, 0, bandLength);

        return true;
    }

    private static byte[] SynthesizePeakData(byte[] lowBand, byte[] midBand, byte[] highBand)
    {
        var length = Math.Min(lowBand.Length, Math.Min(midBand.Length, highBand.Length));
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var peakData = new byte[length];
        for (var i = 0; i < length; i++)
        {
            peakData[i] = Math.Max(lowBand[i], Math.Max(midBand[i], highBand[i]));
        }

        return peakData;
    }

    private static byte[] SynthesizeRmsData(byte[] lowBand, byte[] midBand, byte[] highBand)
    {
        var length = Math.Min(lowBand.Length, Math.Min(midBand.Length, highBand.Length));
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var rmsData = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var low = lowBand[i];
            var mid = midBand[i];
            var high = highBand[i];
            var rms = Math.Sqrt((low * low + mid * mid + high * high) / 3.0);
            rmsData[i] = (byte)Math.Clamp(rms, 0.0, 255.0);
        }

        return rmsData;
    }

    private LibraryEntryEntity LibraryEntryToEntity(LibraryEntry entry)
    {
        var entity = new LibraryEntryEntity();
        entity.UniqueHash = entry.UniqueHash;
        entity.Artist = entry.Artist;
        entity.Title = entry.Title;
        entity.Album = entry.Album;
        entity.FilePath = entry.FilePath;
        entity.Bitrate = entry.Bitrate;
        entity.DurationSeconds = entry.DurationSeconds;
        entity.Format = entry.Format;
        entity.AvailabilityState = entry.AvailabilityState;
        entity.SpotifyPlaylistId = entry.SpotifyPlaylistId;
        entity.SpotifyUri = entry.SpotifyUri;
        
        // Scientific Fields
        entity.SpotifyTrackId = entry.SpotifyTrackId;
        entity.ISRC = entry.ISRC;
        entity.MusicBrainzId = entry.MusicBrainzId;
        entity.Energy = entry.Energy;
        entity.Danceability = entry.Danceability;
        entity.Valence = entry.Valence;
        entity.BPM = entry.BPM;
        entity.MusicalKey = entry.MusicalKey;
        entity.IsEnriched = entry.IsEnriched;
        entity.Label = entry.Label;
        entity.Comments = entry.Comments;
        entity.AudioContentHash = entry.AudioContentHash;
        entity.MoodTag = entry.MoodTag;
        entity.PrimaryGenre = entry.PrimaryGenre;
        
        // Phase 17: Technical Audio Analysis
        entity.Loudness = SLSKDONET.Services.AudioAnalysis.IntegratedLoudnessMeter.AsLufs(entry.Loudness);
        entity.TruePeak = entry.TruePeak;
        entity.DynamicRange = entry.DynamicRange;
        
        entity.WaveformData = entry.WaveformData;
        entity.RmsData = entry.RmsData;
        entity.LowData = entry.LowData;
        entity.MidData = entry.MidData;
        entity.HighData = entry.HighData;
        
        // Dual-Truth
        entity.SpotifyBPM = entry.SpotifyBPM;
        entity.SpotifyKey = entry.SpotifyKey;
        entity.ManualBPM = entry.ManualBPM;
        entity.ManualKey = entry.ManualKey;
        entity.TagBPM = entry.TagBPM;
        
        entity.InstrumentalProbability = entry.InstrumentalProbability; // Phase 18.2
        
        // Phase 21: AI Brain - Read Only from AudioFeatures
        // We do not set entity.Sadness directly as it lives in AudioFeaturesEntity
        // entity.Sadness = entry.Sadness; 
        // entity.VectorEmbedding = entry.VectorEmbedding;

        if (entry.AddedAt == default)
        {
            entity.AddedAt = DateTime.UtcNow;
        }
        return entity;
    }




    /// <summary>
    /// Updates the file path for a library entry and persists the change.
    /// </summary>
    public async Task UpdateLibraryEntryPathAsync(string uniqueHash, string newPath)
    {
        try
        {
            var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash);
            if (entity != null)
            {
                entity.FilePath = newPath;
                await _databaseService.SaveLibraryEntryAsync(entity);
                _logger.LogInformation("Updated file path for {Hash}: {NewPath}", uniqueHash, newPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update library entry path");
            throw;
        }
    }

    // Phase 16.2: Vibe Match
    public async Task<List<AudioFeaturesEntity>> GetAllAudioFeaturesAsync()
    {
        return await _databaseService.LoadAllAudioFeaturesAsync().ConfigureAwait(false);
    }

    // Phase 15
    public async Task<List<StyleDefinitionEntity>> GetStyleDefinitionsAsync()
    {
        try
        {
            return await _databaseService.LoadAllStyleDefinitionsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load style definitions");
            return new List<StyleDefinitionEntity>();
        }
    }

    // Phase 11.5: Verification logic
    public async Task MarkTrackAsVerifiedAsync(string trackHash)
    {
        try
        {
            await _databaseService.MarkTrackAsVerifiedAsync(trackHash).ConfigureAwait(false);
            _logger.LogInformation("Marked track verified: {Hash}", trackHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify track");
            throw;
        }
    }
    /// <summary>
    /// Creates a physical clone of a track, duplicating its database entry and decoupling its identity.
    /// This allows for multiple versions of the same file (e.g., Radio Edit vs Extended) with independent cues.
    /// </summary>
    public async Task<PlaylistTrack> CreatePhysicalCloneAsync(PlaylistTrack source, string newPath)
    {
        try
        {
            // 1. Create a deep copy of the track model
            var clone = EntityToPlaylistTrack(PlaylistTrackToEntity(source));
            
            // 2. Assign a new unique identity
            clone.Id = Guid.NewGuid();
            clone.ResolvedFilePath = newPath;
            
            // 3. Decouple the Hash (Critical to avoid collision in the "All Tracks" view)
            // We append a clone suffix to ensure it doesn't merge with the original
            clone.TrackUniqueHash = $"{source.TrackUniqueHash}_CLONE_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            
            // 4. Reset preparation state for the new copy
            clone.IsPrepared = false;
            clone.CuePointsJson = null; // Fresh start for cues
            clone.Status = TrackStatus.Downloaded; // Immediately ready
            // Explicit, not inherited from source — EntityToPlaylistTrack copies AvailabilityState
            // from the source track above, which would leave the clone stuck showing "FILE MISSING"
            // if the source ever happened to be Ghost, even though the clone's file is confirmed
            // present at newPath right now.
            clone.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
            clone.AddedAt = DateTime.UtcNow;

            // 5. Persist to PlaylistTracks table
            await SavePlaylistTrackAsync(clone).ConfigureAwait(false);

            // 6. Index in the global LibraryEntry table
            await AddTrackToLibraryIndexAsync(clone, newPath).ConfigureAwait(false);

            // 7. Initialize fresh Technical Details
            var tech = new TrackTechnicalEntity
            {
                PlaylistTrackId = clone.Id,
                IsPrepared = false,
                LastUpdated = DateTime.UtcNow
            };
            await SaveTechnicalDetailsAsync(tech).ConfigureAwait(false);

            _logger.LogInformation("Physical clone created: {Hash} at {Path}", clone.TrackUniqueHash, newPath);
            return clone;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create physical clone for {Title}", source.Title);
            throw;
        }
    }

    public async Task<AudioFeaturesEntity?> GetAudioFeaturesByHashAsync(string uniqueHash)
    {
        return await _databaseService.GetAudioFeaturesByHashAsync(uniqueHash);
    }

    public async Task<List<LibraryEntry>> GetTracksAddedSinceAsync(DateTime since)
    {
        var all = await LoadAllLibraryEntriesAsync();
        return all.Where(e => e.AddedAt >= since).ToList();
    }

    public async Task AddTracksToProjectAsync(IEnumerable<PlaylistTrack> tracks, Guid targetProjectId)
    {
        try
        {
            var project = await FindPlaylistJobAsync(targetProjectId);
            if (project == null) throw new InvalidOperationException("Target project not found");

            // "ID" is DJ-tracklist shorthand for an unidentified track (see
            // CommentTracklistParser, which already drops these at tracklist-paste import time).
            // This is the single chokepoint every "add to playlist" path funnels through
            // (drag-drop, Smart Insert, the context-menu action, Flow Builder) — filtering here
            // catches ID tracks regardless of how they ended up in the source list.
            var incoming = tracks.ToList();
            var skippedIdTracks = incoming.Where(t => CommentTracklistParser.IsUnidentifiedTitle(t.Title)).ToList();
            if (skippedIdTracks.Count > 0)
            {
                incoming = incoming.Except(skippedIdTracks).ToList();
                _logger.LogWarning("Skipped {Count} unidentified 'ID' track(s) when adding to project {Title}", skippedIdTracks.Count, project.SourceTitle);
                _eventBus.Publish(new NotificationEvent(
                    "Skipped unidentified tracks",
                    skippedIdTracks.Count == 1
                        ? "1 track titled \"ID\" (unidentified) was not added."
                        : $"{skippedIdTracks.Count} tracks titled \"ID\" (unidentified) were not added.",
                    NotificationType.Warning));
            }

            // Dedup guard: this method is the single chokepoint every "add to playlist" path
            // funnels through (drag-drop, Smart Insert, Flow Builder, batch add, cross-playlist
            // add) — without this, calling it more than once for the same track+playlist (a
            // resumed action, a retry, pulling the same bridge track twice) silently created
            // another row instead of being a no-op.
            var existingHashes = (await LoadPlaylistTracksAsync(targetProjectId))
                .Select(t => t.TrackUniqueHash)
                .Where(h => !string.IsNullOrEmpty(h))
                .ToHashSet(StringComparer.Ordinal);

            var skippedDuplicateTracks = incoming
                .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash) && existingHashes.Contains(t.TrackUniqueHash))
                .ToList();
            if (skippedDuplicateTracks.Count > 0)
            {
                incoming = incoming.Except(skippedDuplicateTracks).ToList();
                _logger.LogInformation("Skipped {Count} track(s) already in project {Title}", skippedDuplicateTracks.Count, project.SourceTitle);
            }

            var newTracks = new List<PlaylistTrack>();
            foreach (var track in incoming)
            {
                // Create a clone of the relational entry for the new project
                var newTrack = new PlaylistTrack
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = targetProjectId,
                    Artist = track.Artist,
                    Title = track.Title,
                    Album = track.Album,
                    TrackUniqueHash = track.TrackUniqueHash,
                    Status = track.Status,
                    ResolvedFilePath = track.ResolvedFilePath,
                    TrackNumber = track.TrackNumber,
                    AddedAt = DateTime.UtcNow,
                    
                    // Copy metadata
                    SpotifyTrackId = track.SpotifyTrackId,
                    ISRC = track.ISRC,
                    SpotifyAlbumId = track.SpotifyAlbumId,
                    SpotifyArtistId = track.SpotifyArtistId,
                    AlbumArtUrl = track.AlbumArtUrl,
                    ArtistImageUrl = track.ArtistImageUrl,
                    Genres = track.Genres,
                    Popularity = track.Popularity,
                    CanonicalDuration = track.CanonicalDuration,
                    ReleaseDate = track.ReleaseDate,
                    MusicalKey = track.MusicalKey,
                    BPM = track.BPM,
                    Bitrate = track.Bitrate,
                    Format = track.Format,
                    IsEnriched = track.IsEnriched,
                    IsPrepared = track.IsPrepared, // Phase 10
                    PrimaryGenre = track.PrimaryGenre,
                    DetectedSubGenre = track.DetectedSubGenre,
                    Label = track.Label,
                    Comments = track.Comments
                };
                newTracks.Add(newTrack);
            }

            if (newTracks.Any())
            {
                await SavePlaylistTracksAsync(newTracks);
                
                // Update project counts
                project.SuccessfulCount += newTracks.Count(t => t.Status == TrackStatus.Downloaded);
                project.FailedCount += newTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
                project.MissingCount += newTracks.Count(t => t.Status == TrackStatus.Missing);
                project.TotalTracks += newTracks.Count;
                
                await SavePlaylistJobAsync(project);

                _logger.LogInformation("Added {Count} tracks to project {Title}", newTracks.Count, project.SourceTitle);
                var fireAndForget = _diagnostics?.LogTracksAddedToPlaylistAsync(targetProjectId, newTracks.Count, project.SourceTitle);

                // Publish event so UI can refresh
                _eventBus.Publish(new ProjectUpdatedEvent(targetProjectId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add tracks to project {Id}", targetProjectId);
            throw;
        }
    }

    /// <summary>
    /// Removes tracks from one playlist only. Deletes the PlaylistTrack relational row(s) —
    /// the LibraryEntry (and the file on disk) is untouched, so the track stays in All Tracks
    /// and any other playlist it's also part of. Contrast with DownloadManager's
    /// DeleteTrackFromDiskAndHistoryAsync, which is a global, permanent delete.
    /// </summary>
    public async Task RemoveTracksFromPlaylistAsync(Guid playlistId, List<Guid> playlistTrackIds)
    {
        if (playlistTrackIds.Count == 0) return;

        await _databaseService.BatchDeletePlaylistTracksAsync(playlistTrackIds);

        _cache.InvalidateProject(playlistId);
        _eventBus.Publish(new ProjectUpdatedEvent(playlistId));

        _logger.LogInformation("Removed {Count} track(s) from playlist {PlaylistId} (library entries untouched)", playlistTrackIds.Count, playlistId);
    }

    /// <summary>
    /// Deletes the PlaylistTrack row(s) for this hash out of every playlist that contains it —
    /// see ILibraryService.RemoveTrackFromAllPlaylistsAsync's doc for why a permanent delete must
    /// do this instead of marking those rows TrackStatus.Missing.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetPlaylistIdsContainingTrackAsync(string trackHash)
    {
        using var db = new AppDbContext();
        return await db.PlaylistTracks.AsNoTracking()
            .Where(t => t.TrackUniqueHash == trackHash)
            .Select(t => t.PlaylistId)
            .Distinct()
            .ToListAsync();
    }

    public async Task RemoveTrackFromAllPlaylistsAsync(string trackHash)
    {
        using var db = new AppDbContext();
        var playlistTracks = await db.PlaylistTracks
            .Where(t => t.TrackUniqueHash == trackHash)
            .ToListAsync();

        if (playlistTracks.Count == 0) return;

        var affectedPlaylistIds = playlistTracks.Select(t => t.PlaylistId).Distinct().ToList();
        db.PlaylistTracks.RemoveRange(playlistTracks);
        await db.SaveChangesAsync();

        foreach (var playlistId in affectedPlaylistIds)
        {
            _cache.InvalidateProject(playlistId);
            _eventBus.Publish(new ProjectUpdatedEvent(playlistId));
        }

        _logger.LogInformation("Removed track {Hash} from {Count} playlist(s)", trackHash, affectedPlaylistIds.Count);
    }

    public async Task UpdateTrackCuePointsAsync(string trackHash, string cuePointsJson)
    {
        using var db = new AppDbContext();
        
        // 1. Update Library Entry
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash);
        if (entry != null)
        {
            entry.CuePointsJson = cuePointsJson;
        }

        // 2. Update all Playlist Tracks
        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == trackHash).ToListAsync();

        // Batched instead of one TechnicalDetails query per playlist track (N+1) — matters most
        // for tracks duplicated across many playlists, a common case for DJs reusing tracks.
        var trackIds = playlistTracks.Select(t => t.Id).ToList();
        var technicalDetailsById = await db.TechnicalDetails
            .Where(td => trackIds.Contains(td.PlaylistTrackId))
            .ToDictionaryAsync(td => td.PlaylistTrackId);

        foreach (var track in playlistTracks)
        {
            track.CuePointsJson = cuePointsJson;

            if (technicalDetailsById.TryGetValue(track.Id, out var tech))
            {
                tech.CuePointsJson = cuePointsJson;
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task UpdateTrackFilePathAsync(string trackHash, string newFilePath)
    {
        using var db = new AppDbContext();

        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash);

        // Update every PlaylistTrack sharing this hash: each playlist denormalizes its own path.
        // Relinking also restores local availability; otherwise the row remains NotDownloaded even
        // though its file is present again. Existing preparation and Ready state are preserved.
        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == trackHash).ToListAsync();
        ApplyRelinkedFilePath(entry, playlistTracks, newFilePath);

        await db.SaveChangesAsync();
        _cache.InvalidateGlobalLibrary();
    }

    internal static void ApplyRelinkedFilePath(
        LibraryEntryEntity? entry,
        IEnumerable<PlaylistTrackEntity> playlistTracks,
        string newFilePath)
    {
        if (entry != null)
        {
            entry.FilePath = newFilePath;
            entry.FilePathUpdatedAt = DateTime.UtcNow;
            AdvanceRelinkedAvailability(entry);
        }

        foreach (var track in playlistTracks)
        {
            track.ResolvedFilePath = newFilePath;
            track.Status = TrackStatus.Downloaded;
            AdvanceRelinkedAvailability(track);
        }
    }

    private static void AdvanceRelinkedAvailability(LibraryEntryEntity entry)
    {
        if (entry.AvailabilityState is TrackAvailabilityState.Ghost
            or TrackAvailabilityState.QueuedForDownload
            or TrackAvailabilityState.Downloading)
        {
            entry.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
        }
    }

    private static void AdvanceRelinkedAvailability(PlaylistTrackEntity track)
    {
        if (track.AvailabilityState is TrackAvailabilityState.Ghost
            or TrackAvailabilityState.QueuedForDownload
            or TrackAvailabilityState.Downloading)
        {
            track.AvailabilityState = TrackAvailabilityState.LocalUnanalyzed;
        }
    }

    public async Task UpdateAudioFeaturesAsync(AudioFeaturesEntity entity)
    {
        // Use the complete persistence path. The previous partial field list silently discarded
        // manual downbeat/grid corrections even though CueForge reported a successful save.
        await _databaseService.UpdateAudioFeaturesAsync(entity).ConfigureAwait(false);
    }

    public async Task<List<PlaylistTrack>> SearchAllPlaylists(string query, int limit = 50)
    {
        try
        {
            var entities = await _databaseService.SearchPlaylistTracksAsync(query, limit).ConfigureAwait(false);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search all playlists for {Query}", query);
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<PlaylistTrack>> FindTrackInOtherProjectsAsync(string artist, string title, Guid currentProjectId)
    {
        try
        {
            var entities = await _databaseService
                .FindTracksInOtherProjectsAsync(artist, title, currentProjectId)
                .ConfigureAwait(false);

            return entities.Select(m => EntityToPlaylistTrack(m)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding cross-references for {Artist} - {Title}", artist, title);
            return new List<PlaylistTrack>();
        }
    }

    public Task ReplacePhrasesForSourceAsync(string trackHash, string source, IReadOnlyList<TrackPhraseEntity> rows) =>
        _databaseService.ReplacePhrasesForSourceAsync(trackHash, source, rows);

    public Task ReplaceAllPhrasesAsync(string trackHash, IReadOnlyList<TrackPhraseEntity> rows) =>
        _databaseService.ReplaceAllPhrasesAsync(trackHash, rows);

    public async Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash)
    {
        return await _databaseService.GetPhrasesByHashAsync(trackHash);
    }

    public async Task SavePhrasesAsync(List<TrackPhraseEntity> phrases)
    {
        await _databaseService.SavePhrasesAsync(phrases);
    }

    private async Task InitializeDefaultPlaylistsAsync()
    {
        try
        {
            var likedJob = await FindPlaylistJobBySourceTypeAsync("Smart:Liked");
            if (likedJob == null)
            {
                _logger.LogInformation("Creating default 'Liked Songs' smart playlist...");
                var criteria = new SmartPlaylistCriteria { IsLiked = true };
                var job = new PlaylistJob
                {
                    Id = Guid.NewGuid(),
                    SourceTitle = "Liked Songs",
                    SourceType = "Smart:Liked",
                    CreatedAt = DateTime.UtcNow,
                    IsSmartPlaylist = true,
                    SmartCriteriaJson = System.Text.Json.JsonSerializer.Serialize(criteria)
                };
                await SavePlaylistJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize default playlists");
        }
    }

    public async Task<(int Reset, int Checked, int Relinked)> ReconcileLibraryAsync()
    {
        _logger.LogInformation("Library reconciliation started — cross-referencing PlaylistTrack DB rows against physical files.");

        var (reset, checked_, relinked) = await _databaseService.ReconcilePhysicalFilesAsync();

        if (reset > 0 || relinked > 0)
        {
            // Invalidate the global cache so the next load reflects the corrected states.
            _cache.InvalidateGlobalLibrary();

            _logger.LogWarning(
                "Library reconciliation: reset {Reset} missing files to Missing state, relinked {Relinked} to a moved/renamed path (checked {Checked} total).",
                reset, relinked, checked_);
        }
        else
        {
            _logger.LogInformation("Library reconciliation complete — all {Checked} files verified present.", checked_);
        }

        return (reset, checked_, relinked);
    }
}
