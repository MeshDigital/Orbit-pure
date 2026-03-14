using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audio_analysis",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: false),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LoudnessLufs = table.Column<double>(type: "REAL", nullable: false),
                    TruePeakDb = table.Column<double>(type: "REAL", nullable: false),
                    DynamicRange = table.Column<double>(type: "REAL", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsUpscaled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpectralHash = table.Column<string>(type: "TEXT", nullable: false),
                    FrequencyCutoff = table.Column<int>(type: "INTEGER", nullable: false),
                    QualityConfidence = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_analysis", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audio_features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Bpm = table.Column<float>(type: "REAL", nullable: false),
                    BpmConfidence = table.Column<float>(type: "REAL", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Scale = table.Column<string>(type: "TEXT", nullable: false),
                    KeyConfidence = table.Column<float>(type: "REAL", nullable: false),
                    CamelotKey = table.Column<string>(type: "TEXT", nullable: false),
                    Energy = table.Column<float>(type: "REAL", nullable: false),
                    Danceability = table.Column<float>(type: "REAL", nullable: false),
                    Valence = table.Column<float>(type: "REAL", nullable: false),
                    SpectralCentroid = table.Column<float>(type: "REAL", nullable: false),
                    SpectralComplexity = table.Column<float>(type: "REAL", nullable: false),
                    OnsetRate = table.Column<float>(type: "REAL", nullable: false),
                    DynamicComplexity = table.Column<float>(type: "REAL", nullable: false),
                    LoudnessLUFS = table.Column<float>(type: "REAL", nullable: false),
                    DropTimeSeconds = table.Column<float>(type: "REAL", nullable: true),
                    DropConfidence = table.Column<float>(type: "REAL", nullable: false),
                    CueIntro = table.Column<float>(type: "REAL", nullable: false),
                    CueBuild = table.Column<float>(type: "REAL", nullable: true),
                    CueDrop = table.Column<float>(type: "REAL", nullable: true),
                    CuePhraseStart = table.Column<float>(type: "REAL", nullable: true),
                    BpmStability = table.Column<float>(type: "REAL", nullable: false),
                    IsDynamicCompressed = table.Column<bool>(type: "INTEGER", nullable: false),
                    InstrumentalProbability = table.Column<float>(type: "REAL", nullable: false),
                    MoodTag = table.Column<string>(type: "TEXT", nullable: false),
                    MoodConfidence = table.Column<float>(type: "REAL", nullable: false),
                    ChordProgression = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    AnalysisVersion = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetectedSubGenre = table.Column<string>(type: "TEXT", nullable: false),
                    SubGenreConfidence = table.Column<float>(type: "REAL", nullable: false),
                    GenreDistributionJson = table.Column<string>(type: "TEXT", nullable: false),
                    AiEmbeddingJson = table.Column<string>(type: "TEXT", nullable: false),
                    PredictedVibe = table.Column<string>(type: "TEXT", nullable: false),
                    PredictionConfidence = table.Column<float>(type: "REAL", nullable: false),
                    EmbeddingMagnitude = table.Column<float>(type: "REAL", nullable: false),
                    CurationConfidence = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    ProvenanceJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_features", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlacklistedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    BlockedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlacklistedItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrichmentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForensicLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TrackIdentifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryActionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionType = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePath = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationPath = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrackArtist = table.Column<string>(type: "TEXT", nullable: true),
                    TrackTitle = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryActionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    UniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Album = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FilePathUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    ISRC = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WaveformData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RmsData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LowData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    MidData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    HighData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MusicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    BPM = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyBPM = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyKey = table.Column<string>(type: "TEXT", nullable: true),
                    ManualBPM = table.Column<double>(type: "REAL", nullable: true),
                    ManualKey = table.Column<string>(type: "TEXT", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    Valence = table.Column<double>(type: "REAL", nullable: true),
                    Danceability = table.Column<double>(type: "REAL", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    Integrity = table.Column<int>(type: "INTEGER", nullable: false),
                    Loudness = table.Column<double>(type: "REAL", nullable: true),
                    TruePeak = table.Column<double>(type: "REAL", nullable: true),
                    DynamicRange = table.Column<double>(type: "REAL", nullable: true),
                    IsEnriched = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrepared = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrimaryGenre = table.Column<string>(type: "TEXT", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DetectedSubGenre = table.Column<string>(type: "TEXT", nullable: true),
                    SubGenreConfidence = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => x.UniqueHash);
                });

            migrationBuilder.CreateTable(
                name: "LibraryFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TracksFound = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryHealth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TotalTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    HqTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingUpdates = table.Column<int>(type: "INTEGER", nullable: false),
                    GoldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SilverCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BronzeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalStorageBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FreeStorageBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastScanDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TopGenresJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryHealth", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingOrchestrations",
                columns: table => new
                {
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingOrchestrations", x => x.TrackUniqueHash);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationFolder = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MissingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsUserPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    DateStarted = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DateUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistTrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsCurrentTrack = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpotifyMetadataCache",
                columns: table => new
                {
                    SpotifyId = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CachedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotifyMetadataCache", x => x.SpotifyId);
                });

            migrationBuilder.CreateTable(
                name: "style_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", nullable: false),
                    ParentGenre = table.Column<string>(type: "TEXT", nullable: false),
                    CentroidJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReferenceTrackHashesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_style_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    GlobalId = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    SoulseekUsername = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CoverArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    ISRC = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MusicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    BPM = table.Column<double>(type: "REAL", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    Valence = table.Column<double>(type: "REAL", nullable: true),
                    Danceability = table.Column<double>(type: "REAL", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AnalysisOffset = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyBPM = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyKey = table.Column<string>(type: "TEXT", nullable: true),
                    ManualBPM = table.Column<double>(type: "REAL", nullable: true),
                    ManualKey = table.Column<string>(type: "TEXT", nullable: true),
                    Integrity = table.Column<int>(type: "INTEGER", nullable: false),
                    SpectralHash = table.Column<string>(type: "TEXT", nullable: true),
                    QualityConfidence = table.Column<double>(type: "REAL", nullable: true),
                    FrequencyCutoff = table.Column<int>(type: "INTEGER", nullable: true),
                    IsTrustworthy = table.Column<bool>(type: "INTEGER", nullable: true),
                    QualityDetails = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnriched = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUpgradeScanAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUpgradeAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRetryTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpgradeSource = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousBitrate = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePlaylistId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePlaylistName = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredFormats = table.Column<string>(type: "TEXT", nullable: true),
                    MinBitrateOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedSubGenre = table.Column<string>(type: "TEXT", nullable: true),
                    SubGenreConfidence = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.GlobalId);
                });

            migrationBuilder.CreateTable(
                name: "ActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityLogs_Projects_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Album = table.Column<string>(type: "TEXT", nullable: false),
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedFilePath = table.Column<string>(type: "TEXT", nullable: false),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLiked = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    ISRC = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MusicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    BPM = table.Column<double>(type: "REAL", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AnalysisOffset = table.Column<double>(type: "REAL", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    Danceability = table.Column<double>(type: "REAL", nullable: true),
                    Valence = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyBPM = table.Column<double>(type: "REAL", nullable: true),
                    SpotifyKey = table.Column<string>(type: "TEXT", nullable: true),
                    ManualBPM = table.Column<double>(type: "REAL", nullable: true),
                    ManualKey = table.Column<string>(type: "TEXT", nullable: true),
                    SpectralHash = table.Column<string>(type: "TEXT", nullable: true),
                    QualityConfidence = table.Column<double>(type: "REAL", nullable: true),
                    FrequencyCutoff = table.Column<int>(type: "INTEGER", nullable: true),
                    IsTrustworthy = table.Column<bool>(type: "INTEGER", nullable: true),
                    Integrity = table.Column<int>(type: "INTEGER", nullable: false),
                    QualityDetails = table.Column<string>(type: "TEXT", nullable: true),
                    Loudness = table.Column<double>(type: "REAL", nullable: true),
                    TruePeak = table.Column<double>(type: "REAL", nullable: true),
                    DynamicRange = table.Column<double>(type: "REAL", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePlaylistId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePlaylistName = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnriched = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrepared = table.Column<bool>(type: "INTEGER", nullable: false),
                    DetectedSubGenre = table.Column<string>(type: "TEXT", nullable: true),
                    SubGenreConfidence = table.Column<float>(type: "REAL", nullable: true),
                    PrimaryGenre = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredFormats = table.Column<string>(type: "TEXT", nullable: true),
                    MinBitrateOverride = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistTracks_Projects_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TechnicalDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistTrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WaveformData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RmsData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LowData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    MidData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    HighData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AiEmbeddingJson = table.Column<string>(type: "TEXT", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    SpectralHash = table.Column<string>(type: "TEXT", nullable: true),
                    IsPrepared = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrimaryGenre = table.Column<string>(type: "TEXT", nullable: true),
                    CurationConfidence = table.Column<int>(type: "INTEGER", nullable: false),
                    ProvenanceJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsReviewNeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TechnicalDetails_PlaylistTracks_PlaylistTrackId",
                        column: x => x.PlaylistTrackId,
                        principalTable: "PlaylistTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_PlaylistId",
                table: "ActivityLogs",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_Blacklist_Hash",
                table: "BlacklistedItems",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentTasks_Status",
                table: "EnrichmentTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentTasks_Status_CreatedAt",
                table: "EnrichmentTasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTrack_PlaylistId",
                table: "PlaylistTracks",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTrack_Status",
                table: "PlaylistTracks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTracks_TrackUniqueHash",
                table: "PlaylistTracks",
                column: "TrackUniqueHash");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistJob_CreatedAt",
                table: "Projects",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalDetails_PlaylistTrackId",
                table: "TechnicalDetails",
                column: "PlaylistTrackId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityLogs");

            migrationBuilder.DropTable(
                name: "audio_analysis");

            migrationBuilder.DropTable(
                name: "audio_features");

            migrationBuilder.DropTable(
                name: "BlacklistedItems");

            migrationBuilder.DropTable(
                name: "EnrichmentTasks");

            migrationBuilder.DropTable(
                name: "ForensicLogs");

            migrationBuilder.DropTable(
                name: "LibraryActionLogs");

            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "LibraryFolders");

            migrationBuilder.DropTable(
                name: "LibraryHealth");

            migrationBuilder.DropTable(
                name: "PendingOrchestrations");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "SpotifyMetadataCache");

            migrationBuilder.DropTable(
                name: "style_definitions");

            migrationBuilder.DropTable(
                name: "TechnicalDetails");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "PlaylistTracks");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
