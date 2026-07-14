using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPalworldSessionsAndLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PalworldLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PalworldName = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PalworldSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PalworldPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    AccountName = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    BuildingCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PalworldLinks_GuildId_DiscordUserId",
                table: "PalworldLinks",
                columns: new[] { "GuildId", "DiscordUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PalworldLinks_GuildId_PalworldUserId",
                table: "PalworldLinks",
                columns: new[] { "GuildId", "PalworldUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PalworldSessions_EndedUtc",
                table: "PalworldSessions",
                column: "EndedUtc",
                filter: "\"EndedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PalworldSessions_PalworldUserId_StartedUtc",
                table: "PalworldSessions",
                columns: new[] { "PalworldUserId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PalworldSessions_PlayerName",
                table: "PalworldSessions",
                column: "PlayerName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PalworldLinks");

            migrationBuilder.DropTable(
                name: "PalworldSessions");
        }
    }
}
