using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                schema: "inviteme",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "plan_orders",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    paid_amount = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_orders", x => x.id);
                    table.CheckConstraint("ck_plan_orders_amounts", "amount > 0 AND paid_amount >= 0");
                    table.CheckConstraint("ck_plan_orders_paid", "(status = 'PAID') = (paid_at IS NOT NULL)");
                    table.CheckConstraint("ck_plan_orders_status", "status IN ('PENDING','UNDERPAID','PAID','CANCELLED')");
                    table.ForeignKey(
                        name: "fk_plan_orders_plan",
                        column: x => x.plan_id,
                        principalSchema: "inviteme",
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_plan_orders_resolver",
                        column: x => x.resolved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_plan_orders_user",
                        column: x => x.user_id,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_plan_orders_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plan_payments",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    content = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    transaction_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_payments", x => x.id);
                    table.CheckConstraint("ck_plan_payments_status", "status IN ('APPLIED','REVIEW')");
                    table.ForeignKey(
                        name: "fk_plan_payments_order",
                        column: x => x.order_id,
                        principalSchema: "inviteme",
                        principalTable: "plan_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_orders_status",
                schema: "inviteme",
                table: "plan_orders",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_orders_wedding",
                schema: "inviteme",
                table: "plan_orders",
                columns: new[] { "wedding_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_plan_orders_code",
                schema: "inviteme",
                table: "plan_orders",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_payments_order",
                schema: "inviteme",
                table: "plan_payments",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_payments_status",
                schema: "inviteme",
                table: "plan_payments",
                columns: new[] { "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "uq_plan_payments_provider_id",
                schema: "inviteme",
                table: "plan_payments",
                columns: new[] { "provider", "provider_transaction_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_order",
                schema: "inviteme",
                table: "subscriptions",
                column: "order_id",
                principalSchema: "inviteme",
                principalTable: "plan_orders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_order",
                schema: "inviteme",
                table: "subscriptions");

            migrationBuilder.DropTable(
                name: "plan_payments",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "plan_orders",
                schema: "inviteme");

            migrationBuilder.DropColumn(
                name: "order_id",
                schema: "inviteme",
                table: "subscriptions");
        }
    }
}
