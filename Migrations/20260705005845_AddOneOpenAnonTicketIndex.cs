using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddOneOpenAnonTicketIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_OneOpenAnonPerUser",
                table: "SupportTickets",
                columns: new[] { "GuildId", "OpenerUserId" },
                unique: true,
                filter: "\"IsAnonymous\" = 1 AND \"Status\" <> 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupportTickets_OneOpenAnonPerUser",
                table: "SupportTickets");
        }
    }
}
