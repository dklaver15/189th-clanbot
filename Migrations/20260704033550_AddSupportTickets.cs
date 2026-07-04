using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "TicketPanelMessageId",
                table: "BotStates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupportTicketMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    AuthorUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AuthorDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTicketMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CategoryKey = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryLabel = table.Column<string>(type: "TEXT", nullable: false),
                    OpenerUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    OpenerDisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    IsAnonymous = table.Column<bool>(type: "INTEGER", nullable: false),
                    AnonHandle = table.Column<string>(type: "TEXT", nullable: true),
                    RoutedRoleId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ThreadId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ControlMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedByUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ClaimedByUsername = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedByUserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    ClosedByUsername = table.Column<string>(type: "TEXT", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    EscalationLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaveStartUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaveEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReserveAssigned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTicketMessages_TicketId_SentUtc",
                table: "SupportTicketMessages",
                columns: new[] { "TicketId", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_ControlMessageId",
                table: "SupportTickets",
                column: "ControlMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_GuildId_OpenerUserId_Status",
                table: "SupportTickets",
                columns: new[] { "GuildId", "OpenerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_GuildId_Status_LastActivityUtc",
                table: "SupportTickets",
                columns: new[] { "GuildId", "Status", "LastActivityUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_Status_ReserveAssigned_LeaveEndUtc",
                table: "SupportTickets",
                columns: new[] { "Status", "ReserveAssigned", "LeaveEndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportTickets_ThreadId",
                table: "SupportTickets",
                column: "ThreadId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportTicketMessages");

            migrationBuilder.DropTable(
                name: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "TicketPanelMessageId",
                table: "BotStates");
        }
    }
}
