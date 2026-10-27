using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftTransferProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "proof_id",
                schema: "inviteme",
                table: "planning_gifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "planning_gift_proofs",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_proofs", x => x.id);
                    table.CheckConstraint("ck_planning_gift_proofs_size", "octet_length(content) BETWEEN 1 AND 5242880");
                    table.ForeignKey(
                        name: "fk_planning_gift_proofs_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_proofs_user",
                        column: x => x.uploaded_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_proofs_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_gift_proofs_wedding",
                schema: "inviteme",
                table: "planning_gift_proofs",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_proofs_gift",
                schema: "inviteme",
                table: "planning_gift_proofs",
                column: "gift_id",
                unique: true,
                filter: "gift_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_planning_gifts_proof",
                schema: "inviteme",
                table: "planning_gifts",
                column: "proof_id",
                principalSchema: "inviteme",
                principalTable: "planning_gift_proofs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_planning_gifts_proof",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropTable(
                name: "planning_gift_proofs",
                schema: "inviteme");

            migrationBuilder.DropColumn(
                name: "proof_id",
                schema: "inviteme",
                table: "planning_gifts");
        }
    }
}
