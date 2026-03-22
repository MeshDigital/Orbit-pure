using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class AddVocalDensityToAudioFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StalledReason",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StalledReason",
                table: "PlaylistTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "VocalDensity",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StalledReason",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "StalledReason",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "VocalDensity",
                table: "audio_features");
        }
    }
}
