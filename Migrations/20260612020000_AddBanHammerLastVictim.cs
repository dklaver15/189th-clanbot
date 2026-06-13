using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddBanHammerLastVictim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BanHammerLastBanReason",
                table: "BotStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BanHammerLastBanTarget",
                table: "BotStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BanHammerLastBanReason",
                table: "BotStates");

            migrationBuilder.DropColumn(
                name: "BanHammerLastBanTarget",
                table: "BotStates");
        }
    }
}
