using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactHub.Store.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class ActivationSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "release_activation",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // The activations recorded before, numbered in the order they were recorded (R-09-137), as the database owner.
            migrationBuilder.Sql(@"UPDATE release_activation a SET ""Sequence"" = r.n FROM (SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""Environment"", ""Scope"" ORDER BY ""Id"") AS n FROM release_activation) r WHERE a.""Id"" = r.""Id"";");

            migrationBuilder.CreateIndex(
                name: "IX_release_activation_Environment_Scope_Sequence",
                table: "release_activation",
                columns: new[] { "Environment", "Scope", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_release_activation_Environment_Scope_Sequence",
                table: "release_activation");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "release_activation");
        }
    }
}
