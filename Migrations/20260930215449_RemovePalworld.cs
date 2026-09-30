using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class RemovePalworld : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PalworldLinks");

            migrationBuilder.DropTable(
                name: "PalworldMetricSamples");

            migrationBuilder.DropTable(
                name: "PalworldNameOverrides");

            migrationBuilder.DropTable(
                name: "PalworldSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PalworldLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    LinkedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PalworldName = table.Column<string>(type: "TEXT", nullable: false),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PalworldMetricSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BaseCampCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FrameTimeMs = table.Column<double>(type: "REAL", nullable: false),
                    InGameDay = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ServerFps = table.Column<int>(type: "INTEGER", nullable: false),
                    UptimeSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldMetricSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PalworldNameOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CanonicalName = table.Column<string>(type: "TEXT", nullable: false),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SetByUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldNameOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PalworldSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountName = table.Column<string>(type: "TEXT", nullable: false),
                    BuildingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    PalworldPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    PalworldUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                name: "IX_PalworldMetricSamples_SampledUtc",
                table: "PalworldMetricSamples",
                column: "SampledUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PalworldNameOverrides_PalworldUserId",
                table: "PalworldNameOverrides",
                column: "PalworldUserId",
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
    }
}
