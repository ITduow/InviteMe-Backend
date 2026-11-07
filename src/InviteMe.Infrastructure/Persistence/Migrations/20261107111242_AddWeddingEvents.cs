using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeddingEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wedding_events",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Asia/Ho_Chi_Minh"),
                    venue_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    map_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("wedding_events_pkey", x => x.id);
                    table.CheckConstraint("ck_wedding_events_dates", "ends_at IS NULL OR starts_at IS NULL OR ends_at >= starts_at");
                    table.CheckConstraint("ck_wedding_events_sort_order", "sort_order >= 0");
                    table.ForeignKey(
                        name: "fk_wedding_events_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wedding_events_order",
                schema: "inviteme",
                table: "wedding_events",
                columns: new[] { "wedding_id", "sort_order", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "uq_wedding_events_key",
                schema: "inviteme",
                table: "wedding_events",
                columns: new[] { "wedding_id", "event_key" },
                unique: true);

            // Preserve non-primary ceremonies folded into settings_json by the legacy cleanup migration.
            migrationBuilder.Sql("""
                INSERT INTO inviteme.wedding_events
                    (id, wedding_id, event_key, title, description, starts_at, ends_at, time_zone_id,
                     venue_name, address, sort_order)
                SELECT
                    (legacy_event.value->>'id')::uuid,
                    settings.wedding_id,
                    legacy_event.value->>'id',
                    COALESCE(NULLIF(legacy_event.value->>'name', ''), 'Sự kiện cưới'),
                    NULLIF(legacy_event.value->>'description', ''),
                    NULLIF(legacy_event.value->>'start_at', '')::timestamptz,
                    NULLIF(legacy_event.value->>'end_at', '')::timestamptz,
                    COALESCE(NULLIF(settings.timezone, ''), 'Asia/Ho_Chi_Minh'),
                    venue.value->>'name',
                    venue.value->>'address',
                    GREATEST(COALESCE(NULLIF(legacy_event.value->>'sort_order', '')::integer, 0), 0)
                FROM inviteme.wedding_settings AS settings
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(settings.settings_json->'_retiredWeddingSetup'->'events') = 'array'
                         THEN settings.settings_json->'_retiredWeddingSetup'->'events' ELSE '[]'::jsonb END
                ) AS legacy_event(value)
                LEFT JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(settings.settings_json->'_retiredWeddingSetup'->'venues') = 'array'
                         THEN settings.settings_json->'_retiredWeddingSetup'->'venues' ELSE '[]'::jsonb END
                ) AS venue(value) ON venue.value->>'id' = legacy_event.value->>'venue_id'
                WHERE COALESCE((legacy_event.value->>'is_main')::boolean, false) = false
                  AND NULLIF(legacy_event.value->>'id', '') IS NOT NULL
                ON CONFLICT (id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wedding_events",
                schema: "inviteme");
        }
    }
}
