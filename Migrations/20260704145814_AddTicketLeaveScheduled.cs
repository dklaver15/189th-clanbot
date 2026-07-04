using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketLeaveScheduled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LeaveScheduled",
                table: "SupportTickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_LeaveScheduled",
                table: "SupportTickets",
                column: "LeaveScheduled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportTickets_LeaveScheduled",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "LeaveScheduled",
                table: "SupportTickets");
        }
    }
}
