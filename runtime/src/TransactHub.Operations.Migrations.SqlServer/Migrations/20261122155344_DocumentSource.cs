using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Operations.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DocumentSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Leg",
                table: "ops_document",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SigningKeyId",
                table: "ops_document",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "ops_document",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Leg",
                table: "ops_document");

            migrationBuilder.DropColumn(
                name: "SigningKeyId",
                table: "ops_document");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ops_document");
        }
    }
}
