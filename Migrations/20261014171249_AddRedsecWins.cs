using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRedsecWins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RedsecWins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GuildId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    WonOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedByUserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedsecWins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RedsecWinPlayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RedsecWinId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedsecWinPlayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RedsecWinPlayers_RedsecWins_RedsecWinId",
                        column: x => x.RedsecWinId,
                        principalTable: "RedsecWins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RedsecWinPlayers_RedsecWinId_UserId",
                table: "RedsecWinPlayers",
                columns: new[] { "RedsecWinId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RedsecWins_GuildId_Season_WonOn",
                table: "RedsecWins",
                columns: new[] { "GuildId", "Season", "WonOn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RedsecWinPlayers");

            migrationBuilder.DropTable(
                name: "RedsecWins");
        }
    }
}
