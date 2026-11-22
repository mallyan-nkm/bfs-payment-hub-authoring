using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class StoredFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_content",
                columns: table => new
                {
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_content", x => x.ContentHash);
                });

            migrationBuilder.CreateTable(
                name: "stored_file",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Signature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SigningKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Leg = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Activity = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stored_file", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stored_file_Tenant_ContentHash",
                table: "stored_file",
                columns: new[] { "Tenant", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_stored_file_Tenant_SubjectId",
                table: "stored_file",
                columns: new[] { "Tenant", "SubjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_content");

            migrationBuilder.DropTable(
                name: "stored_file");
        }
    }
}
