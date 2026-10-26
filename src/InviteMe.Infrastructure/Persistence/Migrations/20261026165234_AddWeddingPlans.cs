using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeddingPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "wedding_id",
                schema: "inviteme",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_subscriptions_wedding_active",
                schema: "inviteme",
                table: "subscriptions",
                column: "wedding_id",
                unique: true,
                filter: "wedding_id IS NOT NULL AND status = 'ACTIVE'");

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_wedding",
                schema: "inviteme",
                table: "subscriptions",
                column: "wedding_id",
                principalSchema: "inviteme",
                principalTable: "weddings",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // The three plans sold per wedding (one-time). Admins edit price and limits later; existing weddings keep
            // every feature because they have no subscription row (legacy).
            migrationBuilder.Sql("""
                INSERT INTO inviteme.plans (code, name, price, currency, billing_period, limits_json, status) VALUES
                ('FREE', 'Miễn phí', 0, 'VND', 'ONE_TIME', '{"guestPlaces":50,"mediaImages":10,"emailInvites":50,"smsInvites":0,"reminderRounds":0,"coHosts":0,"seatingTables":5,"checkin":false,"giftQr":false,"giftExport":false,"aiThankYouRuns":0,"premiumTemplates":false,"brandingBadge":true,"customSlug":false,"publicLinkDaysAfterWedding":7,"publicSeatLookup":false,"retentionDaysAfterWedding":30}', 'ACTIVE'),
                ('BASIC', 'Cơ bản', 299000, 'VND', 'ONE_TIME', '{"guestPlaces":300,"mediaImages":60,"emailInvites":600,"smsInvites":100,"reminderRounds":2,"coHosts":2,"seatingTables":null,"checkin":true,"giftQr":true,"giftExport":true,"aiThankYouRuns":1,"premiumTemplates":true,"brandingBadge":false,"customSlug":true,"publicLinkDaysAfterWedding":183,"publicSeatLookup":true,"retentionDaysAfterWedding":365}', 'ACTIVE'),
                ('PREMIUM', 'Cao cấp', 699000, 'VND', 'ONE_TIME', '{"guestPlaces":1000,"mediaImages":200,"emailInvites":2000,"smsInvites":500,"reminderRounds":null,"coHosts":10,"seatingTables":null,"checkin":true,"giftQr":true,"giftExport":true,"aiThankYouRuns":5,"premiumTemplates":true,"brandingBadge":false,"customSlug":true,"publicLinkDaysAfterWedding":730,"publicSeatLookup":true,"retentionDaysAfterWedding":730}', 'ACTIVE')
                ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, price = EXCLUDED.price, currency = EXCLUDED.currency,
                    billing_period = EXCLUDED.billing_period, limits_json = EXCLUDED.limits_json, status = EXCLUDED.status;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM inviteme.subscriptions WHERE wedding_id IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM inviteme.plans WHERE code IN ('FREE','BASIC','PREMIUM') AND NOT EXISTS (SELECT 1 FROM inviteme.subscriptions s WHERE s.plan_id = plans.id);");
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_wedding",
                schema: "inviteme",
                table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "uq_subscriptions_wedding_active",
                schema: "inviteme",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "wedding_id",
                schema: "inviteme",
                table: "subscriptions");
        }
    }
}
