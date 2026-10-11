using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestFileImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve legacy free text before enforcing the new classification codes.
            migrationBuilder.Sql("""
                UPDATE inviteme.guests SET
                    notes = concat_ws(E'\n', NULLIF(notes, ''), 'Quan hệ trước chuyển đổi: ' || relationship),
                    relationship = NULL
                WHERE relationship IS NOT NULL
                  AND relationship NOT IN ('FAMILY', 'FRIEND', 'COLLEAGUE', 'NEIGHBOR', 'PARTNER', 'OTHER');
                """);
            migrationBuilder.AlterColumn<string>(
                name: "side",
                schema: "inviteme",
                table: "guests",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20,
                oldDefaultValue: "MUTUAL");

            migrationBuilder.CreateTable(
                name: "guest_import_sessions",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    preview_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    selection_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_import_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_guest_import_sessions_weddings_wedding_id",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_guests_relationship",
                schema: "inviteme",
                table: "guests",
                sql: "relationship IS NULL OR relationship IN ('FAMILY', 'FRIEND', 'COLLEAGUE', 'NEIGHBOR', 'PARTNER', 'OTHER')");

            migrationBuilder.CreateIndex(
                name: "IX_guest_import_sessions_expires_at",
                schema: "inviteme",
                table: "guest_import_sessions",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_import_sessions",
                schema: "inviteme");

            migrationBuilder.DropCheckConstraint(
                name: "ck_guests_relationship",
                schema: "inviteme",
                table: "guests");

            migrationBuilder.AlterColumn<string>(
                name: "side",
                schema: "inviteme",
                table: "guests",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MUTUAL",
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20,
                oldNullable: true);
        }
    }
}
