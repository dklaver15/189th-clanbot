using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRedditLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RedditLeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedditPostId = table.Column<string>(type: "TEXT", nullable: false),
                    Subreddit = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorUsername = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorAccountAgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorKarma = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Excerpt = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimedByDiscordUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ClaimedByUsername = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ContactedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OutcomeAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MatchedKeywords = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedditLeads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RedditLeads_DiscoveredAtUtc",
                table: "RedditLeads",
                column: "DiscoveredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RedditLeads_RedditPostId",
                table: "RedditLeads",
                column: "RedditPostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RedditLeads_Status",
                table: "RedditLeads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RedditLeads_Subreddit",
                table: "RedditLeads",
                column: "Subreddit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RedditLeads");
        }
    }
}
