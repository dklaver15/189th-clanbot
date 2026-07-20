using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPalworldNameOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PalworldNameOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalName = table.Column<string>(type: "TEXT", nullable: false),
                    SetByUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldNameOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PalworldNameOverrides_PalworldUserId",
                table: "PalworldNameOverrides",
                column: "PalworldUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PalworldNameOverrides");
        }
    }
}
