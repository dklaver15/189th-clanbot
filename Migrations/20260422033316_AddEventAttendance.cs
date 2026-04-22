using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddEventAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventAttendances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttendedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventAttendances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendances_CalendarEventId",
                table: "EventAttendances",
                column: "CalendarEventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendances_GuildId_UserId_CalendarEventId",
                table: "EventAttendances",
                columns: new[] { "GuildId", "UserId", "CalendarEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendances_GuildId_UserId_EventEndUtc",
                table: "EventAttendances",
                columns: new[] { "GuildId", "UserId", "EventEndUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventAttendances");
        }
    }
}
