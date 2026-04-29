using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddAwolKickAuditRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AwolKickAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    RolesAtKick = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InvokerId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    InvokerName = table.Column<string>(type: "TEXT", nullable: false),
                    WasDryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwolKickAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AwolKickAudits_GuildId_ProcessedAt",
                table: "AwolKickAudits",
                columns: new[] { "GuildId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AwolKickAudits_GuildId_UserId_ProcessedAt",
                table: "AwolKickAudits",
                columns: new[] { "GuildId", "UserId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AwolKickAudits_InvokerId_ProcessedAt",
                table: "AwolKickAudits",
                columns: new[] { "InvokerId", "ProcessedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AwolKickAudits");
        }
    }
}
