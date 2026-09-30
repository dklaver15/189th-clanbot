using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class RestoreCalendarEventsSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Puts back the CalendarEvents AUTOINCREMENT counter saved by
            // RemoveApolloPipeline before its table rebuild reset it.
            migrationBuilder.Sql(
                "UPDATE \"sqlite_sequence\" SET \"seq\" = (SELECT MAX(\"seq\") FROM \"ef_CalendarEvents_seq\") " +
                "WHERE \"name\" = 'CalendarEvents' AND \"seq\" < (SELECT MAX(\"seq\") FROM \"ef_CalendarEvents_seq\");");
            migrationBuilder.Sql("DROP TABLE \"ef_CalendarEvents_seq\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
