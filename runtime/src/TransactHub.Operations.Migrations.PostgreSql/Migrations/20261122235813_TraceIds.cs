using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Operations.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class TraceIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "ops_timeline",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "ops_timeline");
        }
    }
}
