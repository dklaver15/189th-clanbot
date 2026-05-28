using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingRecordings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DiscordMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    MeetingTitle = table.Column<string>(type: "TEXT", nullable: false),
                    MeetingStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MeetingEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JoinAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StateUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordingStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecordingStoppedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AudioDirPath = table.Column<string>(type: "TEXT", nullable: true),
                    TranscriptPath = table.Column<string>(type: "TEXT", nullable: true),
                    MinutesMessageId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRecordings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordings_DiscordMessageId",
                table: "MeetingRecordings",
                column: "DiscordMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordings_State",
                table: "MeetingRecordings",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingRecordings");
        }
    }
}
