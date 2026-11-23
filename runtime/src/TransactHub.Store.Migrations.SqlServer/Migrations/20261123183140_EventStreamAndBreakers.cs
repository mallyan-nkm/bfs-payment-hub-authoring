using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class EventStreamAndBreakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "breaker_change",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Tenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Port = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Adapter = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Failures = table.Column<int>(type: "int", nullable: false),
                    Why = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: true),
                    Host = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_breaker_change", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stream_position",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Stream = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Through = table.Column<long>(type: "bigint", nullable: false),
                    Events = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stream_position", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_breaker_change_Tenant_Port_Adapter_Target_Id",
                table: "breaker_change",
                columns: new[] { "Tenant", "Port", "Adapter", "Target", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_stream_position_Stream_Through",
                table: "stream_position",
                columns: new[] { "Stream", "Through" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "breaker_change");

            migrationBuilder.DropTable(
                name: "stream_position");
        }
    }
}
