using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityAuditRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Feature = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditRecords_GuildId_Feature_OccurredAt",
                table: "SecurityAuditRecords",
                columns: new[] { "GuildId", "Feature", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditRecords_GuildId_OccurredAt",
                table: "SecurityAuditRecords",
                columns: new[] { "GuildId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAuditRecords_GuildId_UserId_OccurredAt",
                table: "SecurityAuditRecords",
                columns: new[] { "GuildId", "UserId", "OccurredAt" },
                filter: "\"UserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityAuditRecords");
        }
    }
}
