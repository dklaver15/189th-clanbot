using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastMeetingSnapshotAttemptUtc",
                table: "CalendarEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingAttendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    EventStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttendedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAttendances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttendances_CalendarEventId",
                table: "MeetingAttendances",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttendances_GuildId_UserId_CalendarEventId",
                table: "MeetingAttendances",
                columns: new[] { "GuildId", "UserId", "CalendarEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAttendances_GuildId_UserId_EventEndUtc",
                table: "MeetingAttendances",
                columns: new[] { "GuildId", "UserId", "EventEndUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingAttendances");

            migrationBuilder.DropColumn(
                name: "LastMeetingSnapshotAttemptUtc",
                table: "CalendarEvents");
        }
    }
}
