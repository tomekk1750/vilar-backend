using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VilarDriverApi.Migrations
{
    /// <inheritdoc />
    public partial class RenameEpodPdfRelativePathToBlobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PdfRelativePath",
                table: "EpodFiles",
                newName: "BlobName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BlobName",
                table: "EpodFiles",
                newName: "PdfRelativePath");
        }
    }
}
