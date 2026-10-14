using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClanGuardBot.Migrations
{
    /// <inheritdoc />
    public partial class UnifyVideoUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Change 2 kept one row per button (Video + Short). Uploads are one per
            // clip again, so its per-button Short rows go; they only ever existed in
            // dev databases (that version never reached production).
            migrationBuilder.Sql("DELETE FROM VideoSubmissions WHERE Kind = 1;");

            migrationBuilder.DropIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex_Kind",
                table: "VideoSubmissions");

            migrationBuilder.RenameColumn(name: "ShortStartSeconds", table: "VideoSubmissions", newName: "TrimStartSeconds");
            migrationBuilder.RenameColumn(name: "ShortEndSeconds",   table: "VideoSubmissions", newName: "TrimEndSeconds");
            migrationBuilder.RenameColumn(name: "ShortTitle",        table: "VideoSubmissions", newName: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex",
                table: "VideoSubmissions",
                columns: new[] { "MessageId", "AttachmentIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex",
                table: "VideoSubmissions");

            migrationBuilder.RenameColumn(name: "TrimStartSeconds", table: "VideoSubmissions", newName: "ShortStartSeconds");
            migrationBuilder.RenameColumn(name: "TrimEndSeconds",   table: "VideoSubmissions", newName: "ShortEndSeconds");
            migrationBuilder.RenameColumn(name: "Title",            table: "VideoSubmissions", newName: "ShortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_VideoSubmissions_MessageId_AttachmentIndex_Kind",
                table: "VideoSubmissions",
                columns: new[] { "MessageId", "AttachmentIndex", "Kind" },
                unique: true);
        }
    }
}
