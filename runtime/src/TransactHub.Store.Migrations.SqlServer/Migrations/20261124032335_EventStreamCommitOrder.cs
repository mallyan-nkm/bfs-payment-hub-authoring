using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EventStreamCommitOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ThroughId",
                table: "stream_position",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThroughVersion",
                table: "stream_position",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVer",
                table: "outbox",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CommitVersion",
                table: "outbox",
                type: "bigint",
                nullable: false,
                computedColumnSql: "CAST([RowVer] AS bigint)",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_CommitVersion_Id",
                table: "outbox",
                columns: new[] { "CommitVersion", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_CommitVersion_Id",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "CommitVersion",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "ThroughId",
                table: "stream_position");

            migrationBuilder.DropColumn(
                name: "ThroughVersion",
                table: "stream_position");

            migrationBuilder.DropColumn(
                name: "RowVer",
                table: "outbox");
        }
    }
}
