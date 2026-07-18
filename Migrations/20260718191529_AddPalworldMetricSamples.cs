using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPalworldMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PalworldMetricSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ServerFps = table.Column<int>(type: "INTEGER", nullable: false),
                    FrameTimeMs = table.Column<double>(type: "REAL", nullable: false),
                    PlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UptimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseCampCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InGameDay = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalworldMetricSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PalworldMetricSamples_SampledUtc",
                table: "PalworldMetricSamples",
                column: "SampledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PalworldMetricSamples");
        }
    }
}
