using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddClanEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClanEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    OrganizerId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    OrganizerName = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RemindersSentCsv = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClanEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClanEventSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    OrganizerId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    OrganizerName = table.Column<string>(type: "TEXT", nullable: false),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstStartLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClanEventSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventRsvps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClanEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRsvps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTimeZones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    IanaId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTimeZones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_CalendarEventId",
                table: "ClanEvents",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_GuildId_StartUtc",
                table: "ClanEvents",
                columns: new[] { "GuildId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_MessageId",
                table: "ClanEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_SeriesId_StartUtc",
                table: "ClanEvents",
                columns: new[] { "SeriesId", "StartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClanEvents_Status_StartUtc",
                table: "ClanEvents",
                columns: new[] { "Status", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClanEventSeries_GuildId_Active",
                table: "ClanEventSeries",
                columns: new[] { "GuildId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_EventRsvps_ClanEventId",
                table: "EventRsvps",
                column: "ClanEventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventRsvps_ClanEventId_UserId",
                table: "EventRsvps",
                columns: new[] { "ClanEventId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTimeZones_UserId",
                table: "UserTimeZones",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClanEvents");

            migrationBuilder.DropTable(
                name: "ClanEventSeries");

            migrationBuilder.DropTable(
                name: "EventRsvps");

            migrationBuilder.DropTable(
                name: "UserTimeZones");
        }
    }
}
