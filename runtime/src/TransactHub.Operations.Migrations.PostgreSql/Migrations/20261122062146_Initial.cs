using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TransactHub.Operations.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ops_action",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HoldId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SecondApprover = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_action", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_checkpoint",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Through = table.Column<long>(type: "bigint", nullable: false),
                    Applied = table.Column<long>(type: "bigint", nullable: false),
                    LastFactAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProjectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RebuiltAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_checkpoint", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "ops_document",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_document", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_field_value",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Field = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Number = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_field_value", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_hold",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Activity = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Reason = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_hold", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_leg",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instance = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Leg = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Title = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastStep = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_leg", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_line",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BulkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_line", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_stream",
                columns: table => new
                {
                    StreamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSeq = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PendingJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_stream", x => x.StreamId);
                });

            migrationBuilder.CreateTable(
                name: "ops_subject",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scheme = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BulkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReleaseId = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    EndToEndId = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastStep = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Hold = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    HoldReason = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    View = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    FieldsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_subject", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ops_timeline",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StreamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Instance = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Step = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Activity = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    Actor = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ops_timeline", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ops_action_SubjectId",
                table: "ops_action",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_action_Tenant_Status",
                table: "ops_action",
                columns: new[] { "Tenant", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_document_FileName",
                table: "ops_document",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_ops_document_SubjectId",
                table: "ops_document",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_field_value_Field_Number",
                table: "ops_field_value",
                columns: new[] { "Field", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_field_value_Field_Text",
                table: "ops_field_value",
                columns: new[] { "Field", "Text" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_field_value_SubjectId",
                table: "ops_field_value",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_hold_SubjectId",
                table: "ops_hold",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_hold_Tenant_Status",
                table: "ops_hold",
                columns: new[] { "Tenant", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_leg_Leg_LastOutcome",
                table: "ops_leg",
                columns: new[] { "Leg", "LastOutcome" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_leg_SubjectId",
                table: "ops_leg",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_line_BulkId_LineNo",
                table: "ops_line",
                columns: new[] { "BulkId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_stream_SubjectId",
                table: "ops_stream",
                column: "SubjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_subject_EndToEndId",
                table: "ops_subject",
                column: "EndToEndId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_subject_MessageId",
                table: "ops_subject",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ops_subject_Tenant_Hold",
                table: "ops_subject",
                columns: new[] { "Tenant", "Hold" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_subject_Tenant_Scheme_AcceptedAt",
                table: "ops_subject",
                columns: new[] { "Tenant", "Scheme", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ops_timeline_StreamId_Seq",
                table: "ops_timeline",
                columns: new[] { "StreamId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ops_timeline_SubjectId_At",
                table: "ops_timeline",
                columns: new[] { "SubjectId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ops_action");

            migrationBuilder.DropTable(
                name: "ops_checkpoint");

            migrationBuilder.DropTable(
                name: "ops_document");

            migrationBuilder.DropTable(
                name: "ops_field_value");

            migrationBuilder.DropTable(
                name: "ops_hold");

            migrationBuilder.DropTable(
                name: "ops_leg");

            migrationBuilder.DropTable(
                name: "ops_line");

            migrationBuilder.DropTable(
                name: "ops_stream");

            migrationBuilder.DropTable(
                name: "ops_subject");

            migrationBuilder.DropTable(
                name: "ops_timeline");
        }
    }
}
