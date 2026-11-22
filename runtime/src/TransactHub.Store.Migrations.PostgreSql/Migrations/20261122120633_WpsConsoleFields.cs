using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class WpsConsoleFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitiatingPartyId",
                table: "payment_bulk",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "debtor_account_type",
                table: "payment_bulk",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "creditor_account_type",
                table: "payment",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "debtor_account_type",
                table: "payment",
                type: "character varying(35)",
                maxLength: 35,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatingPartyId",
                table: "payment_bulk");

            migrationBuilder.DropColumn(
                name: "debtor_account_type",
                table: "payment_bulk");

            migrationBuilder.DropColumn(
                name: "creditor_account_type",
                table: "payment");

            migrationBuilder.DropColumn(
                name: "debtor_account_type",
                table: "payment");
        }
    }
}
