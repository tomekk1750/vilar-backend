using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VilarDriverApi.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractorName",
                table: "Orders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "InvoicePdfRelativePath",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentDueDate",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractorName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "InvoicePdfRelativePath",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaymentDueDate",
                table: "Orders");
        }
    }
}
