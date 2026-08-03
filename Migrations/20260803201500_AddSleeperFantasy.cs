using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSleeperFantasy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SleeperLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SleeperUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SleeperUsername = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SleeperLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SleeperWeekPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Season = table.Column<string>(type: "TEXT", nullable: false),
                    Week = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviewMessageId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ScoreboardMessageId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    RecapMessageId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ScoreboardHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SleeperWeekPosts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SleeperLinks_GuildId_DiscordUserId",
                table: "SleeperLinks",
                columns: new[] { "GuildId", "DiscordUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SleeperLinks_GuildId_SleeperUserId",
                table: "SleeperLinks",
                columns: new[] { "GuildId", "SleeperUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SleeperWeekPosts_GuildId_Season_Week",
                table: "SleeperWeekPosts",
                columns: new[] { "GuildId", "Season", "Week" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SleeperLinks");

            migrationBuilder.DropTable(
                name: "SleeperWeekPosts");
        }
    }
}
