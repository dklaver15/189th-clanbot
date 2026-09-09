using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddValheimMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ValheimMetricSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    World = table.Column<string>(type: "TEXT", nullable: false),
                    RoundTripMs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValheimMetricSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ValheimMetricSamples_SampledUtc",
                table: "ValheimMetricSamples",
                column: "SampledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ValheimMetricSamples");
        }
    }
}
