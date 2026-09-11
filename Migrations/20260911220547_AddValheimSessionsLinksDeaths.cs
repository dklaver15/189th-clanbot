using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddValheimSessionsLinksDeaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ValheimDeaths",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ValheimPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    DiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValheimDeaths", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValheimLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ValheimPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    ValheimName = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValheimLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValheimSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ValheimPlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValheimSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ValheimDeaths_DiedUtc",
                table: "ValheimDeaths",
                column: "DiedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ValheimDeaths_ValheimPlayerId_DiedUtc",
                table: "ValheimDeaths",
                columns: new[] { "ValheimPlayerId", "DiedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ValheimLinks_GuildId_DiscordUserId",
                table: "ValheimLinks",
                columns: new[] { "GuildId", "DiscordUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValheimLinks_GuildId_ValheimPlayerId",
                table: "ValheimLinks",
                columns: new[] { "GuildId", "ValheimPlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ValheimSessions_EndedUtc",
                table: "ValheimSessions",
                column: "EndedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ValheimSessions_PlayerName_StartedUtc",
                table: "ValheimSessions",
                columns: new[] { "PlayerName", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ValheimSessions_ValheimPlayerId_StartedUtc",
                table: "ValheimSessions",
                columns: new[] { "ValheimPlayerId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ValheimDeaths");

            migrationBuilder.DropTable(
                name: "ValheimLinks");

            migrationBuilder.DropTable(
                name: "ValheimSessions");
        }
    }
}
