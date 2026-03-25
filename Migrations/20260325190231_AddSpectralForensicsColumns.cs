using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class AddSpectralForensicsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlacklistedItems");

            migrationBuilder.DropTable(
                name: "ForensicLogs");

            migrationBuilder.AddColumn<int>(
                name: "NotFoundRestartCount",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SearchRetryCount",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsClearedFromDownloadCenter",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NotFoundRestartCount",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SearchRetryCount",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SpectralBitDepth",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralCrestFactorDb",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralHighBandEnergy",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralMidBandEnergy",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralNoiseFloorDbfs",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralRmsDbfs",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpectralRolloffSteepness",
                table: "PlaylistTracks",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpectralSampleRateHz",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EnergyRatio",
                table: "LibraryEntries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTranscoded",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "DeepTextureEmbedding",
                table: "audio_features",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DownloadHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SearchAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SearchStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SearchEndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SearchOutcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UsedMp3Fallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FilteredCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PeerUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DownloadedFilename = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    DownloadedFormat = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DownloadedBitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    FinalState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerReliability",
                columns: table => new
                {
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    SearchCandidates = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadStarts = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadCompletions = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadFailures = table.Column<long>(type: "INTEGER", nullable: false),
                    StallFailures = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesTransferred = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerReliability", x => x.Username);
                });

            migrationBuilder.CreateTable(
                name: "StemPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    AlwaysMutedJson = table.Column<string>(type: "TEXT", nullable: false),
                    AlwaysSoloJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StemPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StemPreferences_LibraryEntries_TrackUniqueHash",
                        column: x => x.TrackUniqueHash,
                        principalTable: "LibraryEntries",
                        principalColumn: "UniqueHash",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StemPreferences_TrackUniqueHash",
                table: "StemPreferences",
                column: "TrackUniqueHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadHistory");

            migrationBuilder.DropTable(
                name: "PeerReliability");

            migrationBuilder.DropTable(
                name: "StemPreferences");

            migrationBuilder.DropColumn(
                name: "NotFoundRestartCount",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SearchRetryCount",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsClearedFromDownloadCenter",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "NotFoundRestartCount",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SearchRetryCount",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralBitDepth",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralCrestFactorDb",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralHighBandEnergy",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralMidBandEnergy",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralNoiseFloorDbfs",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralRmsDbfs",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralRolloffSteepness",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SpectralSampleRateHz",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "EnergyRatio",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "IsTranscoded",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "DeepTextureEmbedding",
                table: "audio_features");

            migrationBuilder.CreateTable(
                name: "BlacklistedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlacklistedItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForensicLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrackIdentifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Blacklist_Hash",
                table: "BlacklistedItems",
                column: "Hash",
                unique: true);
        }
    }
}
