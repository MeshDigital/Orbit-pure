using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class Phase13CognitiveLibrarian : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MoodTag",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "AggressiveConfidence",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<float>(
                name: "HappyConfidence",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MoodTag",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "AggressiveConfidence",
                table: "audio_features");

            migrationBuilder.DropColumn(
                name: "HappyConfidence",
                table: "audio_features");
        }
    }
}
