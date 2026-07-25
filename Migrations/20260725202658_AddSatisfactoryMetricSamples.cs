using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactoryMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryMetricSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    PowerConsumedMw = table.Column<double>(type: "REAL", nullable: false),
                    PowerCapacityMw = table.Column<double>(type: "REAL", nullable: false),
                    PowerProductionMw = table.Column<double>(type: "REAL", nullable: false),
                    CircuitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TrippedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BatteryPercent = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryMetricSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryMetricSamples_SampledUtc",
                table: "SatisfactoryMetricSamples",
                column: "SampledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryMetricSamples");
        }
    }
}
