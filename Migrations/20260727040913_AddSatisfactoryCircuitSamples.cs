using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactoryCircuitSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryCircuitSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    CircuitGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedMw = table.Column<double>(type: "REAL", nullable: false),
                    CapacityMw = table.Column<double>(type: "REAL", nullable: false),
                    MaxConsumedMw = table.Column<double>(type: "REAL", nullable: false),
                    ProductionMw = table.Column<double>(type: "REAL", nullable: false),
                    FuseTriggered = table.Column<bool>(type: "INTEGER", nullable: false),
                    BatteryPercent = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryCircuitSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryCircuitSamples_CircuitGroupId_SampledUtc",
                table: "SatisfactoryCircuitSamples",
                columns: new[] { "CircuitGroupId", "SampledUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryCircuitSamples_SampledUtc",
                table: "SatisfactoryCircuitSamples",
                column: "SampledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryCircuitSamples");
        }
    }
}
