using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddApolloCapturePhase12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_DiscordMessageId",
                table: "CalendarEvents");

            migrationBuilder.CreateTable(
                name: "ApolloEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ParsedTitle = table.Column<string>(type: "TEXT", nullable: false),
                    ParsedStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParsedEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParsedDescription = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedOrganizerId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ParsedOrganizerName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    ParsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SourceLogId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApolloEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApolloMessageLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AuthorId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParseError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApolloMessageLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_DiscordMessageId_Unique",
                table: "CalendarEvents",
                column: "DiscordMessageId",
                unique: true,
                filter: "\"DiscordMessageId\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_ApolloEvents_DiscordMessageId",
                table: "ApolloEvents",
                column: "DiscordMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApolloEvents_GuildId_Status_ParsedStartUtc",
                table: "ApolloEvents",
                columns: new[] { "GuildId", "Status", "ParsedStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApolloMessageLog_CapturedAt",
                table: "ApolloMessageLog",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApolloMessageLog_DiscordMessageId_RevisionNumber",
                table: "ApolloMessageLog",
                columns: new[] { "DiscordMessageId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApolloMessageLog_ProcessedAt_CapturedAt",
                table: "ApolloMessageLog",
                columns: new[] { "ProcessedAt", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApolloEvents");

            migrationBuilder.DropTable(
                name: "ApolloMessageLog");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_DiscordMessageId_Unique",
                table: "CalendarEvents");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_DiscordMessageId",
                table: "CalendarEvents",
                column: "DiscordMessageId");
        }
    }
}
