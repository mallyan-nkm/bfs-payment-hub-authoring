using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Registers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "register_change",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MadeBy = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    MadeAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_register_change", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "register_decision",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChangeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_register_decision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "register_index",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VersionId = table.Column<long>(type: "bigint", nullable: false),
                    Tenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_register_index", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "register_version",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChangeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_register_version", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_register_change_Tenant_Kind_MadeAt",
                table: "register_change",
                columns: new[] { "Tenant", "Kind", "MadeAt" });

            migrationBuilder.CreateIndex(
                name: "IX_register_change_Tenant_Kind_Reference",
                table: "register_change",
                columns: new[] { "Tenant", "Kind", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_register_decision_ChangeId",
                table: "register_decision",
                column: "ChangeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_register_index_Tenant_Kind_Field_Value",
                table: "register_index",
                columns: new[] { "Tenant", "Kind", "Field", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_register_index_VersionId",
                table: "register_index",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_register_version_ChangeId",
                table: "register_version",
                column: "ChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_register_version_Tenant_Kind_Key_VersionNo",
                table: "register_version",
                columns: new[] { "Tenant", "Kind", "Key", "VersionNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "register_change");

            migrationBuilder.DropTable(
                name: "register_decision");

            migrationBuilder.DropTable(
                name: "register_index");

            migrationBuilder.DropTable(
                name: "register_version");
        }
    }
}
