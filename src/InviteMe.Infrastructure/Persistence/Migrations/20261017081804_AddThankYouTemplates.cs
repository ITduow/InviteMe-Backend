using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThankYouTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "thank_you_templates",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    tone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    used_fallback = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("thank_you_templates_pkey", x => x.id);
                    table.CheckConstraint("ck_thank_you_approval", "(status = 'DRAFT' AND approved_by IS NULL AND approved_at IS NULL) OR (status = 'APPROVED' AND approved_by IS NOT NULL AND approved_at IS NOT NULL)");
                    table.CheckConstraint("ck_thank_you_content", "length(btrim(content)) > 0 AND length(content) <= 3000 AND strpos(content, '{{GuestName}}') > 0");
                    table.CheckConstraint("ck_thank_you_language", "language = 'vi'");
                    table.CheckConstraint("ck_thank_you_status", "status IN ('DRAFT','APPROVED')");
                    table.CheckConstraint("ck_thank_you_tone", "tone IN ('FORMAL','WARM','FRIENDLY')");
                    table.CheckConstraint("ck_thank_you_usage", "(input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0)");
                    table.CheckConstraint("ck_thank_you_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_thank_you_approved_by",
                        column: x => x.approved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_created_by",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_thank_you_wedding_created",
                schema: "inviteme",
                table: "thank_you_templates",
                columns: new[] { "wedding_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "thank_you_templates",
                schema: "inviteme");
        }
    }
}
