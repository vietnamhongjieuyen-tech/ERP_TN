using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hak.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase7Workflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "analysis_code_id",
                table: "voucher_lines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "analysis_code_id",
                table: "journal_lines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "approval_limit",
                table: "companies",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "analysis_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    company_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analysis_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_analysis_codes_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "voucher_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    company_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    voucher_type = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    lines_json = table.Column<string>(type: "TEXT", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voucher_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_voucher_templates_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analysis_codes_company_id_code",
                table: "analysis_codes",
                columns: new[] { "company_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_voucher_templates_company_id_name",
                table: "voucher_templates",
                columns: new[] { "company_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_codes");

            migrationBuilder.DropTable(
                name: "voucher_templates");

            migrationBuilder.DropColumn(
                name: "analysis_code_id",
                table: "voucher_lines");

            migrationBuilder.DropColumn(
                name: "analysis_code_id",
                table: "journal_lines");

            migrationBuilder.DropColumn(
                name: "approval_limit",
                table: "companies");
        }
    }
}
