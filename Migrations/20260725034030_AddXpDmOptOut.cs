using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddXpDmOptOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "XpDmOptOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    OptedOutAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpDmOptOuts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XpDmOptOuts_GuildId_UserId",
                table: "XpDmOptOuts",
                columns: new[] { "GuildId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XpDmOptOuts");
        }
    }
}
