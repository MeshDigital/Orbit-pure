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
            // Idempotent legacy cleanup.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"BlacklistedItems\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"ForensicLogs\";");

            // NOTE:
            // Multiple columns introduced in this migration are already patched by
            // SchemaMigratorService on upgraded installations.
            // We intentionally avoid ALTER TABLE ADD COLUMN here to prevent
            // duplicate-column failures during EF migration replay.

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""DownloadHistory"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_DownloadHistory"" PRIMARY KEY,
                    ""TrackHash"" TEXT NOT NULL,
                    ""Artist"" TEXT NOT NULL DEFAULT '',
                    ""Title"" TEXT NOT NULL DEFAULT '',
                    ""ProjectId"" TEXT NULL,
                    ""SearchAttemptCount"" INTEGER NOT NULL DEFAULT 0,
                    ""SearchStartedAt"" TEXT NULL,
                    ""SearchEndedAt"" TEXT NULL,
                    ""SearchOutcome"" TEXT NOT NULL DEFAULT 'Unknown',
                    ""UsedMp3Fallback"" INTEGER NOT NULL DEFAULT 0,
                    ""MatchedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""QueuedCount"" INTEGER NOT NULL DEFAULT 0,
                    ""FilteredCount"" INTEGER NOT NULL DEFAULT 0,
                    ""PeerUsername"" TEXT NULL,
                    ""DownloadedFilename"" TEXT NULL,
                    ""DownloadedFormat"" TEXT NULL,
                    ""DownloadedBitrateKbps"" INTEGER NULL,
                    ""FinalState"" TEXT NOT NULL DEFAULT '',
                    ""RecordedAt"" TEXT NOT NULL
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""PeerReliability"" (
                    ""Username"" TEXT NOT NULL CONSTRAINT ""PK_PeerReliability"" PRIMARY KEY,
                    ""SearchCandidates"" INTEGER NOT NULL,
                    ""DownloadStarts"" INTEGER NOT NULL,
                    ""DownloadCompletions"" INTEGER NOT NULL,
                    ""DownloadFailures"" INTEGER NOT NULL,
                    ""StallFailures"" INTEGER NOT NULL,
                    ""BytesTransferred"" INTEGER NOT NULL,
                    ""LastSeenTicks"" INTEGER NOT NULL,
                    ""LastUpdated"" TEXT NOT NULL
                );
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""StemPreferences"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_StemPreferences"" PRIMARY KEY,
                    ""TrackUniqueHash"" TEXT NOT NULL,
                    ""AlwaysMutedJson"" TEXT NOT NULL,
                    ""AlwaysSoloJson"" TEXT NOT NULL,
                    ""LastModified"" TEXT NOT NULL,
                    CONSTRAINT ""FK_StemPreferences_LibraryEntries_TrackUniqueHash""
                        FOREIGN KEY (""TrackUniqueHash"") REFERENCES ""LibraryEntries"" (""UniqueHash"") ON DELETE CASCADE
                );
            ");

            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_StemPreferences_TrackUniqueHash\" ON \"StemPreferences\" (\"TrackUniqueHash\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"DownloadHistory\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"PeerReliability\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"StemPreferences\";");

            // See Up() note: schema-patched columns are intentionally not dropped here.

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
