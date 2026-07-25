using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactorySessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SatisfactoryPlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SatisfactorySessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactorySessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryLinks_GuildId_DiscordUserId",
                table: "SatisfactoryLinks",
                columns: new[] { "GuildId", "DiscordUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryLinks_GuildId_SatisfactoryPlayerName",
                table: "SatisfactoryLinks",
                columns: new[] { "GuildId", "SatisfactoryPlayerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactorySessions_EndedUtc",
                table: "SatisfactorySessions",
                column: "EndedUtc",
                filter: "\"EndedUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactorySessions_PlayerName_StartedUtc",
                table: "SatisfactorySessions",
                columns: new[] { "PlayerName", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryLinks");

            migrationBuilder.DropTable(
                name: "SatisfactorySessions");
        }
    }
}
