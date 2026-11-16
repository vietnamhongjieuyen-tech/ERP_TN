using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hak.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase7Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "voucher_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    company_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    voucher_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    extension = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    removed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    removed_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    remove_reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voucher_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_voucher_attachments_vouchers_voucher_id",
                        column: x => x.voucher_id,
                        principalTable: "vouchers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voucher_attachments_company_id_voucher_id",
                table: "voucher_attachments",
                columns: new[] { "company_id", "voucher_id" });

            migrationBuilder.CreateIndex(
                name: "ix_voucher_attachments_voucher_id",
                table: "voucher_attachments",
                column: "voucher_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voucher_attachments");
        }
    }
}
