using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddSatisfactoryDailySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SatisfactoryDailySnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Seed = table.Column<long>(type: "INTEGER", nullable: false),
                    TakenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LocalDate = table.Column<string>(type: "TEXT", nullable: false),
                    PassedDays = table.Column<int>(type: "INTEGER", nullable: false),
                    PowerCapacityMw = table.Column<double>(type: "REAL", nullable: true),
                    PowerConsumedMw = table.Column<double>(type: "REAL", nullable: true),
                    CircuitCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SinkTotalPoints = table.Column<long>(type: "INTEGER", nullable: true),
                    SinkCoupons = table.Column<int>(type: "INTEGER", nullable: true),
                    ProducingItemCount = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalProductionPerMin = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatisfactoryDailySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryDailySnapshots_Seed_LocalDate",
                table: "SatisfactoryDailySnapshots",
                columns: new[] { "Seed", "LocalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SatisfactoryDailySnapshots_Seed_TakenUtc",
                table: "SatisfactoryDailySnapshots",
                columns: new[] { "Seed", "TakenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SatisfactoryDailySnapshots");
        }
    }
}
