using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactoryTrainSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryTrainSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SampledUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    TrainCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MovingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DerailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StuckCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadMass = table.Column<double>(type: "REAL", nullable: false),
                    MaxPayloadMass = table.Column<double>(type: "REAL", nullable: false),
                    StationCount = table.Column<int>(type: "INTEGER", nullable: true),
                    StarvedPlatforms = table.Column<int>(type: "INTEGER", nullable: true),
                    BackedUpPlatforms = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryTrainSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryTrainSamples_SampledUtc",
                table: "SatisfactoryTrainSamples",
                column: "SampledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryTrainSamples");
        }
    }
}
