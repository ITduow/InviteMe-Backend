using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeddingWebsiteVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wedding_website_versions",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    document_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("wedding_website_versions_pkey", x => x.id);
                    table.CheckConstraint("ck_wedding_website_versions_kind", "kind IN ('BASELINE', 'AUTO_SAVE', 'PUBLISHED', 'RESTORED')");
                    table.CheckConstraint("ck_wedding_website_versions_version", "version >= 0");
                    table.ForeignKey(
                        name: "fk_wedding_website_versions_wedding_settings",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "wedding_settings",
                        principalColumn: "wedding_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wedding_website_versions_wedding_created",
                schema: "inviteme",
                table: "wedding_website_versions",
                columns: new[] { "wedding_id", "created_at" });

            migrationBuilder.Sql("""
                INSERT INTO inviteme.wedding_website_versions
                    (id, wedding_id, version, kind, document_json, created_by, created_at)
                SELECT gen_random_uuid(), wedding_id, website_version, 'BASELINE', website_draft_json, NULL, updated_at
                FROM inviteme.wedding_settings
                WHERE website_draft_json IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wedding_website_versions",
                schema: "inviteme");
        }
    }
}
