using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class RemoveApolloPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dropping columns rebuilds CalendarEvents, which resets its
            // AUTOINCREMENT counter to the highest surviving Id. Attendance rows
            // keep the Ids of deleted events, so the counter is saved here and
            // restored by RestoreCalendarEventsSequence (EF runs the rebuild last,
            // after any SQL in this migration).
            migrationBuilder.Sql(
                "CREATE TABLE \"ef_CalendarEvents_seq\" AS SELECT \"seq\" FROM \"sqlite_sequence\" WHERE \"name\" = 'CalendarEvents';");

            migrationBuilder.DropTable(
                name: "ApolloEvents");

            migrationBuilder.DropTable(
                name: "ApolloMessageLog");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_GuildId_ContentHash",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_PendingCancelUntil",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "DeleteOnCancelTimeout",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "PendingCancelUntil",
                table: "CalendarEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DeleteOnCancelTimeout",
                table: "CalendarEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingCancelUntil",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApolloEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ParsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParsedDescription = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParsedOrganizerId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ParsedOrganizerName = table.Column<string>(type: "TEXT", nullable: true),
                    ParsedStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParsedTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SourceLogId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
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
                    AuthorId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ParseError = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApolloMessageLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_GuildId_ContentHash",
                table: "CalendarEvents",
                columns: new[] { "GuildId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_PendingCancelUntil",
                table: "CalendarEvents",
                column: "PendingCancelUntil");

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
    }
}
