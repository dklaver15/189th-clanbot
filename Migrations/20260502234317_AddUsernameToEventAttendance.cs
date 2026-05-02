using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddUsernameToEventAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "EventAttendances",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // One-shot backfill for rows written before this column existed. Pulls
            // whatever Username we have on UserActivity for the same (GuildId, UserId)
            // — that table is kept current by ActivityTrackingHandler and keeps rows
            // around even after a member leaves the guild, so it's the best fallback
            // for legacy attendance rows whose live SocketGuildUser is gone.
            //
            // Rows where no UserActivity match exists keep the empty-string default;
            // /attendance falls back to the raw mention rendering for those.
            migrationBuilder.Sql(@"
                UPDATE EventAttendances
                SET Username = COALESCE((
                    SELECT ua.Username
                    FROM UserActivities ua
                    WHERE ua.GuildId = EventAttendances.GuildId
                      AND ua.UserId  = EventAttendances.UserId
                ), '')
                WHERE Username = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Username",
                table: "EventAttendances");
        }
    }
}
