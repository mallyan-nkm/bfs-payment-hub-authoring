using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EventStreamGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GapsJson",
                table: "stream_position",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GapsJson",
                table: "stream_position");
        }
    }
}
