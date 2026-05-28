using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranscriptText",
                table: "MeetingRecordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinutesText",
                table: "MeetingRecordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionItemsJson",
                table: "MeetingRecordings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TranscriptText", table: "MeetingRecordings");
            migrationBuilder.DropColumn(name: "MinutesText", table: "MeetingRecordings");
            migrationBuilder.DropColumn(name: "ActionItemsJson", table: "MeetingRecordings");
        }
    }
}
