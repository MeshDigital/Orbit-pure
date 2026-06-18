using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class AddCuePointLoopAndSlot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLoop",
                table: "CuePoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "LoopEndSeconds",
                table: "CuePoints",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SlotIndex",
                table: "CuePoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsLoop",         table: "CuePoints");
            migrationBuilder.DropColumn(name: "LoopEndSeconds", table: "CuePoints");
            migrationBuilder.DropColumn(name: "SlotIndex",      table: "CuePoints");
        }
    }
}
