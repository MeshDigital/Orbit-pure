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
            migrationBuilder.AddColumn<bool>(
                name: "IsLiked",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPlayedAt",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlayCount",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Tracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StalledReason",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "SubGenreConfidence",
                table: "Tracks",
                type: "REAL",
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
                name: "IsLiked",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "LastPlayedAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "PlayCount",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "StalledReason",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SubGenreConfidence",
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
