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
            // ── Note ──────────────────────────────────────────────────────
            // EF auto-generated a DropIndex("IX_CalendarEvents_DiscordMessageId")
            // + CreateIndex("IX_CalendarEvents_DiscordMessageId_Unique") pair
            // here due to model-snapshot drift: production already had the
            // unique-filtered index (applied manually during the 2026-05-04/05
            // duplicate-calendar-events cleanup), but the snapshot still
            // recorded the older non-unique form. The auto-generated DROP
            // failed against prod ("no such index") and put the bot into a
            // restart loop. Both ops have been removed — the running schema
            // already matches the model. The model snapshot was updated by
            // this migration's `migrations add` and now correctly reflects
            // the unique-filtered index, so future migrations will be clean.
            // ──────────────────────────────────────────────────────────────

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
            // Inverse of Up(). Note: we deliberately do not recreate the
            // older non-unique CalendarEvents index here — see comment in
            // Up() for the snapshot-drift backstory.
            migrationBuilder.DropTable(
                name: "ApolloEvents");

            migrationBuilder.DropTable(
                name: "ApolloMessageLog");
        }
    }
}