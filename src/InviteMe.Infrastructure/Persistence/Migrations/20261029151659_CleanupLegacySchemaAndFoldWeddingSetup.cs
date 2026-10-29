using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CleanupLegacySchemaAndFoldWeddingSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_audit_logs_actor_guest",
                schema: "inviteme",
                table: "audit_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_notifications_guest",
                schema: "inviteme",
                table: "notifications");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "main_event_at", schema: "inviteme", table: "wedding_settings",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "media_catalog_json", schema: "inviteme", table: "wedding_settings",
                type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb");
            migrationBuilder.AddColumn<string>(
                name: "previous_slugs_json", schema: "inviteme", table: "wedding_settings",
                type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb");
            migrationBuilder.AddColumn<string>(
                name: "venue_address", schema: "inviteme", table: "wedding_settings",
                type: "varchar(2000)", maxLength: 2000, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "venue_name", schema: "inviteme", table: "wedding_settings",
                type: "varchar(200)", maxLength: 200, nullable: true);

            // A wedding must have a settings row before its normalized setup data is folded into it.
            migrationBuilder.Sql("""
                INSERT INTO inviteme.wedding_settings (wedding_id)
                SELECT w.id FROM inviteme.weddings w
                WHERE NOT EXISTS (SELECT 1 FROM inviteme.wedding_settings s WHERE s.wedding_id = w.id);

                UPDATE inviteme.wedding_settings s
                SET main_event_at = e.start_at,
                    venue_name = v.name,
                    venue_address = v.address
                FROM inviteme.wedding_events e
                LEFT JOIN inviteme.venues v ON v.id = e.venue_id
                WHERE e.wedding_id = s.wedding_id AND e.is_main;

                UPDATE inviteme.wedding_settings s
                SET media_catalog_json = COALESCE(m.items, '[]'::jsonb)
                FROM (
                    SELECT wedding_id, jsonb_agg(jsonb_build_object(
                        'id', id, 'url', media_url, 'storageKey', storage_key,
                        'mediaType', media_type, 'createdAt', created_at
                    ) ORDER BY created_at DESC, id) AS items
                    FROM inviteme.wedding_media
                    GROUP BY wedding_id
                ) m
                WHERE m.wedding_id = s.wedding_id;

                UPDATE inviteme.wedding_settings s
                SET previous_slugs_json = COALESCE(h.items, '[]'::jsonb)
                FROM (
                    SELECT wedding_id, jsonb_agg(jsonb_build_object(
                        'slug', lower(slug), 'changedAt', released_at
                    ) ORDER BY released_at, id) AS items
                    FROM inviteme.wedding_slug_history
                    GROUP BY wedding_id
                ) h
                WHERE h.wedding_id = s.wedding_id;

                UPDATE inviteme.wedding_settings s
                SET settings_json =
                    (CASE WHEN jsonb_typeof(s.settings_json) = 'object' THEN s.settings_json
                          WHEN s.settings_json IS NULL THEN '{}'::jsonb
                          ELSE jsonb_build_object('previousSettingsValue', s.settings_json) END)
                    || jsonb_build_object('_retiredWeddingSetup', jsonb_build_object(
                        'events', COALESCE(e.items, '[]'::jsonb),
                        'venues', COALESCE(v.items, '[]'::jsonb)
                    ))
                FROM (
                    SELECT w.id AS wedding_id,
                        COALESCE((SELECT jsonb_agg(to_jsonb(ev) ORDER BY ev.is_main DESC, ev.start_at, ev.id)
                                  FROM inviteme.wedding_events ev WHERE ev.wedding_id = w.id), '[]'::jsonb) AS items
                    FROM inviteme.weddings w
                ) e
                JOIN (
                    SELECT w.id AS wedding_id,
                        COALESCE((SELECT jsonb_agg(to_jsonb(vn) ORDER BY vn.created_at, vn.id)
                                  FROM inviteme.venues vn WHERE vn.wedding_id = w.id), '[]'::jsonb) AS items
                    FROM inviteme.weddings w
                ) v ON v.wedding_id = e.wedding_id
                WHERE s.wedding_id = e.wedding_id;

                DROP VIEW IF EXISTS inviteme.v_guest_rsvp_summary;
                DROP VIEW IF EXISTS inviteme.v_table_occupancy;
                DROP VIEW IF EXISTS inviteme.v_wedding_headcount;
                DROP TABLE IF EXISTS inviteme.gift_participant_legacy;
                """);

            migrationBuilder.DropTable(
                name: "ai_generations",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "check_ins",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "gift_messages",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "guest_notes",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "invitation_deliveries",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "love_stories",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "rsvp_history",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "seating_change_logs",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "table_status_history",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "template_sections",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "waitlist_entries",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "wedding_events",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "wedding_media",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "wedding_slug_history",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "gifts",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "rsvps",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "seating_assignments",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "walk_ins",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "invitations",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "guest_participants",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "seats",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "guests",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "tables",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "guest_groups",
                schema: "inviteme");

            // These functions and an optional table were specific to the retired schema-v1 model.
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS inviteme.table_occupied_seats(uuid, uuid, uuid);
                DROP FUNCTION IF EXISTS inviteme.validate_seating_assignment();
                DROP FUNCTION IF EXISTS inviteme.set_table_activated_at();
                DROP FUNCTION IF EXISTS inviteme.validate_guest_group_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_event_venue_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_invitation_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_waitlist_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_checkin_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_gift_scope();
                DROP FUNCTION IF EXISTS inviteme.validate_walkin();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "main_event_at",
                schema: "inviteme",
                table: "wedding_settings");

            migrationBuilder.DropColumn(
                name: "media_catalog_json",
                schema: "inviteme",
                table: "wedding_settings");

            migrationBuilder.DropColumn(
                name: "previous_slugs_json",
                schema: "inviteme",
                table: "wedding_settings");

            migrationBuilder.DropColumn(
                name: "venue_address",
                schema: "inviteme",
                table: "wedding_settings");

            migrationBuilder.DropColumn(
                name: "venue_name",
                schema: "inviteme",
                table: "wedding_settings");

            migrationBuilder.CreateTable(
                name: "ai_generations",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    input_ref = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    model = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    output_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PENDING"),
                    type = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ai_generations_pkey", x => x.id);
                    table.CheckConstraint("ck_ai_generations_status", "status IN ('PENDING', 'SUCCEEDED', 'FAILED')");
                    table.CheckConstraint("ck_ai_generations_type", "type IN ( 'GUEST_CLASSIFICATION', 'SEATING_SUGGESTION', 'CONTENT_GENERATION', 'THANK_YOU' )");
                    table.ForeignKey(
                        name: "fk_ai_generations_created_by",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_ai_generations_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guest_groups",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    description = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    side = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("guest_groups_pkey", x => x.id);
                    table.CheckConstraint("ck_guest_groups_side", "side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
                    table.ForeignKey(
                        name: "fk_guest_groups_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "love_stories",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    visibility = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "VISIBLE"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("love_stories_pkey", x => x.id);
                    table.CheckConstraint("ck_love_stories_visibility", "visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE')");
                    table.ForeignKey(
                        name: "fk_love_stories_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tables",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PLANNED"),
                    table_kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PRIMARY"),
                    table_number = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("tables_pkey", x => x.id);
                    table.CheckConstraint("ck_tables_capacity", "capacity > 0");
                    table.CheckConstraint("ck_tables_kind", "table_kind IN ('PRIMARY', 'BACKUP')");
                    table.CheckConstraint("ck_tables_kind_status", "(table_kind = 'PRIMARY' AND status IN ('PLANNED', 'ACTIVE', 'INACTIVE')) OR (table_kind = 'BACKUP' AND status IN ('BACKUP', 'ACTIVE', 'INACTIVE'))");
                    table.CheckConstraint("ck_tables_status", "status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
                    table.CheckConstraint("ck_tables_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_tables_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "template_sections",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    config_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    section_key = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("template_sections_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_sections_template",
                        column: x => x.template_id,
                        principalSchema: "inviteme",
                        principalTable: "templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    address = table.Column<string>(type: "text", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("venues_pkey", x => x.id);
                    table.CheckConstraint("ck_venues_capacity", "capacity IS NULL OR capacity > 0");
                    table.CheckConstraint("ck_venues_latitude", "latitude IS NULL OR latitude BETWEEN -90 AND 90");
                    table.CheckConstraint("ck_venues_longitude", "longitude IS NULL OR longitude BETWEEN -180 AND 180");
                    table.ForeignKey(
                        name: "fk_venues_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wedding_media",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    caption = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    media_type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    media_url = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    storage_key = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    visibility = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "VISIBLE"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("wedding_media_pkey", x => x.id);
                    table.CheckConstraint("ck_wedding_media_type", "media_type IN ('IMAGE', 'VIDEO', 'OTHER')");
                    table.CheckConstraint("ck_wedding_media_visibility", "visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE')");
                    table.ForeignKey(
                        name: "fk_wedding_media_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wedding_slug_history",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    slug = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wedding_slug_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_wedding_slug_history_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guests",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    allowed_plus_one = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    email = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    expected_companion_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    full_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_code = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    max_plus_one = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    notes = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    record_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "ACTIVE"),
                    relationship = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    side = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "MUTUAL"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("guests_pkey", x => x.id);
                    table.CheckConstraint("ck_guests_expected_companion_count", "expected_companion_count >= 0");
                    table.CheckConstraint("ck_guests_plus_one", "max_plus_one >= 0 AND (allowed_plus_one OR max_plus_one = 0)");
                    table.CheckConstraint("ck_guests_record_status", "record_status IN ('ACTIVE', 'ARCHIVED')");
                    table.CheckConstraint("ck_guests_side", "side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
                    table.ForeignKey(
                        name: "fk_guests_group",
                        column: x => x.group_id,
                        principalSchema: "inviteme",
                        principalTable: "guest_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_guests_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    seat_number = table.Column<int>(type: "integer", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("seats_pkey", x => x.id);
                    table.CheckConstraint("ck_seats_number", "seat_number > 0");
                    table.ForeignKey(
                        name: "fk_seats_table",
                        column: x => x.table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "table_status_history",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    from_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    overflow_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    reason_code = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("table_status_history_pkey", x => x.id);
                    table.CheckConstraint("ck_table_status_history_backup_activation_reason", "NOT (from_status = 'BACKUP' AND to_status = 'ACTIVE') OR reason_code IS NOT NULL");
                    table.CheckConstraint("ck_table_status_history_from_status", "from_status IS NULL OR from_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
                    table.CheckConstraint("ck_table_status_history_other_note", "reason_code IS DISTINCT FROM 'OTHER' OR note IS NOT NULL");
                    table.CheckConstraint("ck_table_status_history_reason_code", "reason_code IS NULL OR reason_code IN ('EXTRA_COMPANIONS', 'WAITLIST_PROMOTION', 'LATE_RSVP_CHANGE', 'WALK_IN', 'PLANNING_SHORTFALL', 'OTHER')");
                    table.CheckConstraint("ck_table_status_history_to_status", "to_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
                    table.ForeignKey(
                        name: "fk_table_status_history_changed_by",
                        column: x => x.changed_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_table_status_history_table",
                        column: x => x.table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_table_status_history_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wedding_events",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    capacity_limit = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    description = table.Column<string>(type: "text", nullable: true),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_main = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("wedding_events_pkey", x => x.id);
                    table.CheckConstraint("ck_wedding_events_capacity", "capacity_limit IS NULL OR capacity_limit > 0");
                    table.CheckConstraint("ck_wedding_events_dates", "end_at IS NULL OR end_at >= start_at");
                    table.ForeignKey(
                        name: "fk_wedding_events_venue",
                        column: x => x.venue_id,
                        principalSchema: "inviteme",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_wedding_events_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guest_participants",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    attendance_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PENDING"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    dietary_note = table.Column<string>(type: "text", nullable: true),
                    full_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    participant_type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("guest_participants_pkey", x => x.id);
                    table.UniqueConstraint("uq_guest_participants_id_guest", x => new { x.id, x.guest_id });
                    table.CheckConstraint("ck_guest_participant_attendance", "attendance_status IN ( 'PENDING', 'ATTENDING', 'DECLINED', 'WAITLISTED' )");
                    table.CheckConstraint("ck_guest_participant_type", "participant_type IN ( 'PRIMARY', 'SPOUSE', 'CHILD', 'PLUS_ONE', 'OTHER' )");
                    table.ForeignKey(
                        name: "fk_guest_participants_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    configuration = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    previewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    protected_token = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "DRAFT"),
                    token_hash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("invitations_pkey", x => x.id);
                    table.CheckConstraint("ck_invitations_status", "status IN ( 'DRAFT', 'IN_REVIEW', 'APPROVED', 'PUBLISHED', 'SENT', 'OPENED', 'REVOKED', 'EXPIRED' )");
                    table.ForeignKey(
                        name: "fk_invitations_approved_by",
                        column: x => x.approved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_invitations_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_invitations_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "walk_ins",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    party_size = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    phone = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    related_guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    side = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("walk_ins_pkey", x => x.id);
                    table.CheckConstraint("ck_walkins_full_name_not_blank", "btrim(full_name) <> ''");
                    table.CheckConstraint("ck_walkins_party_size", "party_size > 0");
                    table.CheckConstraint("ck_walkins_side", "side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
                    table.ForeignKey(
                        name: "fk_walkins_created_by",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_walkins_related_guest",
                        column: x => x.related_guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_walkins_table",
                        column: x => x.table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_walkins_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guest_notes",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_type = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false, defaultValue: "GENERAL"),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("guest_notes_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fk_guest_notes_created_by",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_guest_notes_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_guest_notes_participant_same_guest",
                        columns: x => new { x.participant_id, x.guest_id },
                        principalSchema: "inviteme",
                        principalTable: "guest_participants",
                        principalColumns: new[] { "id", "guest_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seating_assignments",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("seating_assignments_pkey", x => x.id);
                    table.CheckConstraint("ck_seating_assignment_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_seating_assignment_assigned_by",
                        column: x => x.assigned_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_seating_assignment_participant",
                        column: x => x.participant_id,
                        principalSchema: "inviteme",
                        principalTable: "guest_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_seating_assignment_seat",
                        column: x => x.seat_id,
                        principalSchema: "inviteme",
                        principalTable: "seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_assignment_table",
                        column: x => x.table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_seating_assignment_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: true),
                    promoted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    requested_slots = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "WAITING"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("waitlist_entries_pkey", x => x.id);
                    table.CheckConstraint("ck_waitlist_requested_slots", "requested_slots > 0");
                    table.CheckConstraint("ck_waitlist_status", "status IN ('WAITING', 'PROMOTED', 'CANCELLED', 'EXPIRED')");
                    table.ForeignKey(
                        name: "fk_waitlist_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_waitlist_participant",
                        column: x => x.participant_id,
                        principalSchema: "inviteme",
                        principalTable: "guest_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_waitlist_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitation_deliveries",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_sandbox = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    publication_hash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: true),
                    recipient = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    request_key = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PENDING")
                },
                constraints: table =>
                {
                    table.PrimaryKey("invitation_deliveries_pkey", x => x.id);
                    table.CheckConstraint("ck_invitation_delivery_channel", "channel IN ('EMAIL', 'SMS')");
                    table.CheckConstraint("ck_invitation_delivery_status", "status IN ('PENDING', 'SENT', 'DELIVERED', 'FAILED')");
                    table.ForeignKey(
                        name: "fk_invitation_deliveries_invitation",
                        column: x => x.invitation_id,
                        principalSchema: "inviteme",
                        principalTable: "invitations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rsvps",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    confirmed_party_size = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PENDING"),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("rsvps_pkey", x => x.id);
                    table.CheckConstraint("ck_rsvps_confirmed_party_size", "confirmed_party_size >= 0");
                    table.CheckConstraint("ck_rsvps_status", "status IN ('PENDING', 'ATTENDING', 'DECLINED')");
                    table.CheckConstraint("ck_rsvps_status_party_size", "(status IN ('PENDING', 'DECLINED') AND confirmed_party_size = 0) OR (status = 'ATTENDING' AND confirmed_party_size >= 1)");
                    table.ForeignKey(
                        name: "fk_rsvps_invitation",
                        column: x => x.invitation_id,
                        principalSchema: "inviteme",
                        principalTable: "invitations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "check_ins",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    checked_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    checked_in_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "QR"),
                    onsite_override_reason = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "CHECKED_IN"),
                    void_reason = table.Column<string>(type: "text", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    walkin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("check_ins_pkey", x => x.id);
                    table.CheckConstraint("ck_checkins_method", "method IN ('QR', 'MANUAL', 'WALK_IN')");
                    table.CheckConstraint("ck_checkins_method_target", "(method = 'WALK_IN') = (walkin_id IS NOT NULL)");
                    table.CheckConstraint("ck_checkins_override_reason", "onsite_override_reason IS NULL OR onsite_override_reason IN ('DECLINED_ARRIVED', 'PENDING_ARRIVED', 'WAITLISTED_ARRIVED')");
                    table.CheckConstraint("ck_checkins_status", "status IN ('CHECKED_IN', 'VOID')");
                    table.CheckConstraint("ck_checkins_target_xor", "(participant_id IS NOT NULL AND walkin_id IS NULL) OR (participant_id IS NULL AND walkin_id IS NOT NULL)");
                    table.CheckConstraint("ck_checkins_void_shape", "(status = 'CHECKED_IN' AND voided_at IS NULL AND voided_by IS NULL AND void_reason IS NULL) OR (status = 'VOID' AND voided_at IS NOT NULL AND void_reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_checkins_checked_in_by",
                        column: x => x.checked_in_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkins_participant",
                        column: x => x.participant_id,
                        principalSchema: "inviteme",
                        principalTable: "guest_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkins_voided_by",
                        column: x => x.voided_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_checkins_walkin",
                        column: x => x.walkin_id,
                        principalSchema: "inviteme",
                        principalTable: "walk_ins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_checkins_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gifts",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    currency = table.Column<string>(type: "char(3)", maxLength: 3, nullable: false, defaultValue: "VND"),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    method = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    provider = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    received_by = table.Column<Guid>(type: "uuid", nullable: true),
                    transaction_ref = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    transaction_status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false, defaultValue: "COMPLETED"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    walkin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("gifts_pkey", x => x.id);
                    table.CheckConstraint("ck_gifts_amount", "amount >= 0");
                    table.CheckConstraint("ck_gifts_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_gifts_method", "method IN ('CASH', 'BANK_QR', 'BANK_TRANSFER', 'OTHER')");
                    table.CheckConstraint("ck_gifts_source_xor", "(guest_id IS NOT NULL AND walkin_id IS NULL) OR (guest_id IS NULL AND walkin_id IS NOT NULL)");
                    table.CheckConstraint("ck_gifts_transaction_status", "transaction_status IN ( 'PENDING', 'COMPLETED', 'FAILED', 'REFUNDED' )");
                    table.ForeignKey(
                        name: "fk_gifts_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gifts_received_by",
                        column: x => x.received_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_gifts_walkin",
                        column: x => x.walkin_id,
                        principalSchema: "inviteme",
                        principalTable: "walk_ins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gifts_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seating_change_logs",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    action = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    from_seat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_seat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("seating_change_logs_pkey", x => x.id);
                    table.CheckConstraint("ck_seating_logs_action", "action IN ('ASSIGN', 'MOVE', 'UNASSIGN', 'SEAT_CHANGE')");
                    table.ForeignKey(
                        name: "fk_seating_logs_actor",
                        column: x => x.actor_id,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_seating_logs_assignment",
                        column: x => x.assignment_id,
                        principalSchema: "inviteme",
                        principalTable: "seating_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_logs_from_seat",
                        column: x => x.from_seat_id,
                        principalSchema: "inviteme",
                        principalTable: "seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_logs_from_table",
                        column: x => x.from_table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_logs_participant",
                        column: x => x.participant_id,
                        principalSchema: "inviteme",
                        principalTable: "guest_participants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_seating_logs_to_seat",
                        column: x => x.to_seat_id,
                        principalSchema: "inviteme",
                        principalTable: "seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_logs_to_table",
                        column: x => x.to_table_id,
                        principalSchema: "inviteme",
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seating_logs_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rsvp_history",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    actor_guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    new_confirmed_party_size = table.Column<int>(type: "integer", nullable: false),
                    new_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    new_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    old_confirmed_party_size = table.Column<int>(type: "integer", nullable: true),
                    old_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    old_status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    rsvp_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("rsvp_history_pkey", x => x.id);
                    table.CheckConstraint("ck_rsvp_history_actor_shape", "(actor_type = 'USER' AND changed_by IS NOT NULL AND actor_guest_id IS NULL) OR (actor_type = 'WEDDING_GUEST' AND actor_guest_id IS NOT NULL AND changed_by IS NULL) OR (actor_type = 'SYSTEM' AND changed_by IS NULL AND actor_guest_id IS NULL)");
                    table.CheckConstraint("ck_rsvp_history_actor_type", "actor_type IN ('USER', 'WEDDING_GUEST', 'SYSTEM')");
                    table.CheckConstraint("ck_rsvp_history_confirmed_party_sizes", "(old_confirmed_party_size IS NULL OR old_confirmed_party_size >= 0) AND new_confirmed_party_size >= 0");
                    table.CheckConstraint("ck_rsvp_history_new_status", "new_status IN ('PENDING', 'ATTENDING', 'DECLINED')");
                    table.CheckConstraint("ck_rsvp_history_old_status", "old_status IS NULL OR old_status IN ('PENDING', 'ATTENDING', 'DECLINED')");
                    table.ForeignKey(
                        name: "fk_rsvp_history_actor_guest",
                        column: x => x.actor_guest_id,
                        principalSchema: "inviteme",
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rsvp_history_changed_by",
                        column: x => x.changed_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rsvp_history_rsvp",
                        column: x => x.rsvp_id,
                        principalSchema: "inviteme",
                        principalTable: "rsvps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gift_messages",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    gift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    visibility = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "PRIVATE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("gift_messages_pkey", x => x.id);
                    table.CheckConstraint("ck_gift_messages_visibility", "visibility IN ('PRIVATE', 'PUBLIC', 'HIDDEN')");
                    table.ForeignKey(
                        name: "fk_gift_messages_gift",
                        column: x => x.gift_id,
                        principalSchema: "inviteme",
                        principalTable: "gifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_generations_type",
                schema: "inviteme",
                table: "ai_generations",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_ai_generations_wedding_id",
                schema: "inviteme",
                table: "ai_generations",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_checkins_checked_in_at",
                schema: "inviteme",
                table: "check_ins",
                column: "checked_in_at");

            migrationBuilder.CreateIndex(
                name: "ix_checkins_wedding_id",
                schema: "inviteme",
                table: "check_ins",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_checkins_participant",
                schema: "inviteme",
                table: "check_ins",
                columns: new[] { "wedding_id", "participant_id" },
                unique: true,
                filter: "participant_id IS NOT NULL AND status = 'CHECKED_IN'");

            migrationBuilder.CreateIndex(
                name: "uq_checkins_walkin",
                schema: "inviteme",
                table: "check_ins",
                columns: new[] { "wedding_id", "walkin_id" },
                unique: true,
                filter: "walkin_id IS NOT NULL AND status = 'CHECKED_IN'");

            migrationBuilder.CreateIndex(
                name: "gift_messages_gift_id_key",
                schema: "inviteme",
                table: "gift_messages",
                column: "gift_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gifts_guest_id",
                schema: "inviteme",
                table: "gifts",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_gifts_received_at",
                schema: "inviteme",
                table: "gifts",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_gifts_wedding_id",
                schema: "inviteme",
                table: "gifts",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_gifts_idempotency",
                schema: "inviteme",
                table: "gifts",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_gifts_transaction",
                schema: "inviteme",
                table: "gifts",
                columns: new[] { "wedding_id", "provider", "transaction_ref" },
                unique: true,
                filter: "provider IS NOT NULL AND transaction_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_guest_groups_wedding_id",
                schema: "inviteme",
                table: "guest_groups",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_guest_groups_name",
                schema: "inviteme",
                table: "guest_groups",
                columns: new[] { "wedding_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guest_notes_guest_id",
                schema: "inviteme",
                table: "guest_notes",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_guest_notes_participant_id",
                schema: "inviteme",
                table: "guest_notes",
                column: "participant_id");

            migrationBuilder.CreateIndex(
                name: "uq_guest_notes_rsvp_note",
                schema: "inviteme",
                table: "guest_notes",
                column: "guest_id",
                unique: true,
                filter: "note_type = 'RSVP' AND participant_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_guest_participants_attendance",
                schema: "inviteme",
                table: "guest_participants",
                column: "attendance_status");

            migrationBuilder.CreateIndex(
                name: "ix_guest_participants_guest_id",
                schema: "inviteme",
                table: "guest_participants",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "uq_guest_primary_participant",
                schema: "inviteme",
                table: "guest_participants",
                column: "guest_id",
                unique: true,
                filter: "participant_type = 'PRIMARY'");

            migrationBuilder.CreateIndex(
                name: "ix_guests_group_id",
                schema: "inviteme",
                table: "guests",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_guests_phone",
                schema: "inviteme",
                table: "guests",
                column: "phone");

            migrationBuilder.CreateIndex(
                name: "ix_guests_wedding_id",
                schema: "inviteme",
                table: "guests",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_guests_guest_code",
                schema: "inviteme",
                table: "guests",
                columns: new[] { "wedding_id", "guest_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitation_deliveries_invitation_id",
                schema: "inviteme",
                table: "invitation_deliveries",
                column: "invitation_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitation_deliveries_status",
                schema: "inviteme",
                table: "invitation_deliveries",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_invitation_delivery_request",
                schema: "inviteme",
                table: "invitation_deliveries",
                columns: new[] { "invitation_id", "request_key" },
                unique: true,
                filter: "request_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "invitations_token_hash_key",
                schema: "inviteme",
                table: "invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitations_guest_id",
                schema: "inviteme",
                table: "invitations",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_invitations_wedding_id",
                schema: "inviteme",
                table: "invitations",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_invitations_guest",
                schema: "inviteme",
                table: "invitations",
                columns: new[] { "wedding_id", "guest_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_love_stories_wedding_id",
                schema: "inviteme",
                table: "love_stories",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_rsvp_history_changed_at",
                schema: "inviteme",
                table: "rsvp_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_rsvp_history_rsvp_changed_at",
                schema: "inviteme",
                table: "rsvp_history",
                columns: new[] { "rsvp_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "rsvps_invitation_id_key",
                schema: "inviteme",
                table: "rsvps",
                column: "invitation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seating_assignments_table_id",
                schema: "inviteme",
                table: "seating_assignments",
                column: "table_id");

            migrationBuilder.CreateIndex(
                name: "ix_seating_assignments_wedding_id",
                schema: "inviteme",
                table: "seating_assignments",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "seating_assignments_participant_id_key",
                schema: "inviteme",
                table: "seating_assignments",
                column: "participant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_seating_assignment_seat",
                schema: "inviteme",
                table: "seating_assignments",
                column: "seat_id",
                unique: true,
                filter: "seat_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_seating_change_logs_created_at",
                schema: "inviteme",
                table: "seating_change_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_seating_change_logs_participant_id",
                schema: "inviteme",
                table: "seating_change_logs",
                column: "participant_id");

            migrationBuilder.CreateIndex(
                name: "ix_seating_change_logs_wedding_id",
                schema: "inviteme",
                table: "seating_change_logs",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_seats_table_id",
                schema: "inviteme",
                table: "seats",
                column: "table_id");

            migrationBuilder.CreateIndex(
                name: "uq_seats_number",
                schema: "inviteme",
                table: "seats",
                columns: new[] { "table_id", "seat_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_table_status_history_table_id",
                schema: "inviteme",
                table: "table_status_history",
                column: "table_id");

            migrationBuilder.CreateIndex(
                name: "ix_table_status_history_wedding_changed_at",
                schema: "inviteme",
                table: "table_status_history",
                columns: new[] { "wedding_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tables_wedding_id",
                schema: "inviteme",
                table: "tables",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_tables_number",
                schema: "inviteme",
                table: "tables",
                columns: new[] { "wedding_id", "table_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_sections_template_id",
                schema: "inviteme",
                table: "template_sections",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "uq_template_sections",
                schema: "inviteme",
                table: "template_sections",
                columns: new[] { "template_id", "section_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_venues_wedding_id",
                schema: "inviteme",
                table: "venues",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_guest_id",
                schema: "inviteme",
                table: "waitlist_entries",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_wedding_status",
                schema: "inviteme",
                table: "waitlist_entries",
                columns: new[] { "wedding_id", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_waitlist_active_participant",
                schema: "inviteme",
                table: "waitlist_entries",
                columns: new[] { "wedding_id", "participant_id" },
                unique: true,
                filter: "participant_id IS NOT NULL AND status = 'WAITING'");

            migrationBuilder.CreateIndex(
                name: "uq_waitlist_active_party",
                schema: "inviteme",
                table: "waitlist_entries",
                columns: new[] { "wedding_id", "guest_id" },
                unique: true,
                filter: "participant_id IS NULL AND status = 'WAITING'");

            migrationBuilder.CreateIndex(
                name: "ix_walkins_table_id",
                schema: "inviteme",
                table: "walk_ins",
                column: "table_id");

            migrationBuilder.CreateIndex(
                name: "ix_walkins_wedding_id",
                schema: "inviteme",
                table: "walk_ins",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_wedding_events_venue_id",
                schema: "inviteme",
                table: "wedding_events",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_wedding_events_wedding_id",
                schema: "inviteme",
                table: "wedding_events",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_wedding_main_event",
                schema: "inviteme",
                table: "wedding_events",
                column: "wedding_id",
                unique: true,
                filter: "is_main");

            migrationBuilder.CreateIndex(
                name: "ix_wedding_media_wedding_id",
                schema: "inviteme",
                table: "wedding_media",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_wedding_slug_history_wedding",
                schema: "inviteme",
                table: "wedding_slug_history",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_wedding_slug_history_lower",
                schema: "inviteme",
                table: "wedding_slug_history",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_audit_logs_actor_guest",
                schema: "inviteme",
                table: "audit_logs",
                column: "actor_guest_id",
                principalSchema: "inviteme",
                principalTable: "guests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_guest",
                schema: "inviteme",
                table: "notifications",
                column: "recipient_guest_id",
                principalSchema: "inviteme",
                principalTable: "guests",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
