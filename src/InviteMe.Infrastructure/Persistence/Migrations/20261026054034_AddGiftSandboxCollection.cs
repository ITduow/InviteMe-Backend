using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftSandboxCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "collected_by_platform",
                schema: "inviteme",
                table: "planning_gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "payout_id",
                schema: "inviteme",
                table: "planning_gift_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "planning_gift_payouts",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    bank_bin = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_payouts", x => x.id);
                    table.CheckConstraint("ck_planning_gift_payouts_amount", "amount >= 10000");
                    table.CheckConstraint("ck_planning_gift_payouts_paid", "(status = 'PAID') = (paid_at IS NOT NULL)");
                    table.CheckConstraint("ck_planning_gift_payouts_status", "status IN ('PENDING','PAID','CANCELLED')");
                    table.ForeignKey(
                        name: "fk_planning_gift_payouts_canceller",
                        column: x => x.cancelled_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_payouts_creator",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_gift_payouts_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_gift_ledger",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    gift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_ledger", x => x.id);
                    table.CheckConstraint("ck_planning_gift_ledger_amount", "amount > 0");
                    table.CheckConstraint("ck_planning_gift_ledger_source", "(type = 'CREDIT' AND gift_id IS NOT NULL AND payout_id IS NULL) OR (type = 'DEBIT' AND payout_id IS NOT NULL AND gift_id IS NULL)");
                    table.CheckConstraint("ck_planning_gift_ledger_type", "type IN ('CREDIT','DEBIT')");
                    table.ForeignKey(
                        name: "fk_planning_gift_ledger_gift",
                        column: x => x.gift_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_gifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_gift_ledger_payout",
                        column: x => x.payout_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_gift_payouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_gift_ledger_transaction",
                        column: x => x.transaction_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_gift_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_ledger_user",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_ledger_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_gift_ledger_wedding",
                schema: "inviteme",
                table: "planning_gift_ledger",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_ledger_gift",
                schema: "inviteme",
                table: "planning_gift_ledger",
                column: "gift_id",
                unique: true,
                filter: "gift_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_ledger_payout",
                schema: "inviteme",
                table: "planning_gift_ledger",
                column: "payout_id",
                unique: true,
                filter: "payout_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_planning_gift_payouts_status",
                schema: "inviteme",
                table: "planning_gift_payouts",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_payouts_code",
                schema: "inviteme",
                table: "planning_gift_payouts",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_payouts_idempotency",
                schema: "inviteme",
                table: "planning_gift_payouts",
                columns: new[] { "wedding_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_payouts_pending",
                schema: "inviteme",
                table: "planning_gift_payouts",
                column: "wedding_id",
                unique: true,
                filter: "status = 'PENDING'");

            migrationBuilder.AddForeignKey(
                name: "fk_planning_gift_transactions_payout",
                schema: "inviteme",
                table: "planning_gift_transactions",
                column: "payout_id",
                principalSchema: "inviteme",
                principalTable: "planning_gift_payouts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_planning_gift_transactions_payout",
                schema: "inviteme",
                table: "planning_gift_transactions");

            migrationBuilder.DropTable(
                name: "planning_gift_ledger",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_gift_payouts",
                schema: "inviteme");

            migrationBuilder.DropColumn(
                name: "collected_by_platform",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "payout_id",
                schema: "inviteme",
                table: "planning_gift_transactions");
        }
    }
}
