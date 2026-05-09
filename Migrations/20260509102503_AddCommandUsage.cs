using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommandUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    CommandName = table.Column<string>(type: "TEXT", nullable: false),
                    SubcommandPath = table.Column<string>(type: "TEXT", nullable: true),
                    Parameters = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandUsages_ExecutedAt",
                table: "CommandUsages",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CommandUsages_GuildId_CommandName_ExecutedAt",
                table: "CommandUsages",
                columns: new[] { "GuildId", "CommandName", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandUsages_GuildId_UserId_ExecutedAt",
                table: "CommandUsages",
                columns: new[] { "GuildId", "UserId", "ExecutedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandUsages");
        }
    }
}
