using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordStatusIncidentUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordStatusIncidentUpdates",
                columns: table => new
                {
                    UpdateId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IncidentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seeded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordStatusIncidentUpdates", x => x.UpdateId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordStatusIncidentUpdates_IncidentId",
                table: "DiscordStatusIncidentUpdates",
                column: "IncidentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordStatusIncidentUpdates");
        }
    }
}
