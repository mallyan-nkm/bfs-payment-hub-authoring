using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TransactHub.Store.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exception_item",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exception_item", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "message",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Topic = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "party",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    IsOrganisation = table.Column<bool>(type: "boolean", nullable: false),
                    Department = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    StreetName = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    BuildingNumber = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PostCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TownName = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    CountrySubDivision = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    AddressLinesJson = table.Column<string>(type: "text", nullable: false),
                    AnyBic = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    Lei = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OtherIdsJson = table.Column<string>(type: "text", nullable: false),
                    CountryOfResidence = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BulkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    InstructionId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    EndToEndId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    TransactionId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    Uetr = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SettlementAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    SettlementCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    SettlementDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ChargeBearer = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    UltimateDebtorPartyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DebtorPartyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    debtor_account_iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    debtor_account_other_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    debtor_account_other_scheme = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    debtor_account_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    debtor_account_name = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    debtor_agent_bicfi = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    debtor_agent_lei = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    debtor_agent_clearing_system = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    debtor_agent_member_id = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    debtor_agent_name = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    creditor_agent_bicfi = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    creditor_agent_lei = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    creditor_agent_clearing_system = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    creditor_agent_member_id = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    creditor_agent_name = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    CreditorPartyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    creditor_account_iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    creditor_account_other_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    creditor_account_other_scheme = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    creditor_account_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    creditor_account_name = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    UltimateCreditorPartyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PurposeCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    CategoryPurposeCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    RemittanceJson = table.Column<string>(type: "text", nullable: false),
                    SupplementaryJson = table.Column<string>(type: "text", nullable: false),
                    ReleaseId = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_bulk",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    PaymentInformationId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedExecutionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DebtorPartyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    debtor_account_iban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    debtor_account_other_id = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    debtor_account_other_scheme = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    debtor_account_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    debtor_account_name = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                    debtor_agent_bicfi = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: true),
                    debtor_agent_lei = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    debtor_agent_clearing_system = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    debtor_agent_member_id = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    debtor_agent_name = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    CategoryPurposeCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    NumberOfTransactions = table.Column<int>(type: "integer", nullable: false),
                    ControlSum = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    SupplementaryJson = table.Column<string>(type: "text", nullable: false),
                    ReleaseId = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_bulk", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_fact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleaseId = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Actor = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    PrevHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_fact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "release",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Release = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    Package = table.Column<byte[]>(type: "bytea", nullable: false),
                    StoredBy = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    StoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "release_activation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Environment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Release = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_activation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "versioned_value",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BodyJson = table.Column<string>(type: "text", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_versioned_value", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exception_item_Tenant_Code",
                table: "exception_item",
                columns: new[] { "Tenant", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_message_Tenant_Scheme_Kind_ContentHash",
                table: "message",
                columns: new[] { "Tenant", "Scheme", "Kind", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_message_Tenant_Scheme_Kind_FileName",
                table: "message",
                columns: new[] { "Tenant", "Scheme", "Kind", "FileName" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_BulkId_LineNo",
                table: "payment",
                columns: new[] { "BulkId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_Tenant_Scheme_EndToEndId",
                table: "payment",
                columns: new[] { "Tenant", "Scheme", "EndToEndId" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_Uetr",
                table: "payment",
                column: "Uetr");

            migrationBuilder.CreateIndex(
                name: "IX_payment_bulk_Tenant_Scheme_MessageId",
                table: "payment_bulk",
                columns: new[] { "Tenant", "Scheme", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_fact_Kind_Code",
                table: "payment_fact",
                columns: new[] { "Kind", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_fact_SubjectId_Seq",
                table: "payment_fact",
                columns: new[] { "SubjectId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_Scope_Release",
                table: "release",
                columns: new[] { "Scope", "Release" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_activation_Environment_Scope_EffectiveFrom",
                table: "release_activation",
                columns: new[] { "Environment", "Scope", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_versioned_value_Kind_Tenant_Name_VersionNo",
                table: "versioned_value",
                columns: new[] { "Kind", "Tenant", "Name", "VersionNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exception_item");

            migrationBuilder.DropTable(
                name: "message");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "party");

            migrationBuilder.DropTable(
                name: "payment");

            migrationBuilder.DropTable(
                name: "payment_bulk");

            migrationBuilder.DropTable(
                name: "payment_fact");

            migrationBuilder.DropTable(
                name: "release");

            migrationBuilder.DropTable(
                name: "release_activation");

            migrationBuilder.DropTable(
                name: "versioned_value");
        }
    }
}
