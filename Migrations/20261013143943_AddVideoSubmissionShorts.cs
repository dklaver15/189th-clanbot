using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoSubmissionShorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex",
                table: "VideoSubmissions");

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "VideoSubmissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "VideoSubmissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "ShortEndSeconds",
                table: "VideoSubmissions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShortLayout",
                table: "VideoSubmissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ShortStartSeconds",
                table: "VideoSubmissions",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortTitle",
                table: "VideoSubmissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "VideoSubmissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex_Kind",
                table: "VideoSubmissions",
                columns: new[] { "MessageId", "AttachmentIndex", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex_Kind",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "ShortEndSeconds",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "ShortLayout",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "ShortStartSeconds",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "ShortTitle",
                table: "VideoSubmissions");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "VideoSubmissions");

            migrationBuilder.CreateIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex",
                table: "VideoSubmissions",
                columns: new[] { "MessageId", "AttachmentIndex" },
                unique: true);
        }
    }
}
