-- Run only after comparing an existing database with Baseline.sql.
-- This records the supplied business schema as already applied; it creates no business tables.
BEGIN;

DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'users', 'roles', 'permissions', 'user_roles', 'role_permissions',
        'plans', 'subscriptions', 'templates', 'template_sections',
        'weddings', 'wedding_settings', 'wedding_members', 'wedding_member_permissions',
        'love_stories', 'venues', 'wedding_events', 'wedding_media',
        'guest_groups', 'guests', 'guest_participants', 'guest_notes',
        'invitations', 'invitation_deliveries', 'rsvps', 'rsvp_history',
        'waitlist_entries', 'tables', 'seats', 'seating_assignments',
        'seating_change_logs', 'walk_ins', 'check_ins', 'gifts', 'gift_messages',
        'notifications', 'ai_generations', 'audit_logs'
    ];
    missing TEXT;
BEGIN
    SELECT string_agg(table_name, ', ' ORDER BY table_name)
      INTO missing
      FROM unnest(expected) AS table_name
     WHERE to_regclass(format('inviteme.%I', table_name)) IS NULL;

    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'Cannot mark InviteMe baseline; missing tables: %', missing;
    END IF;
END;
$$;

CREATE TABLE IF NOT EXISTS inviteme."__EFMigrationsHistory" (
    "MigrationId" VARCHAR(150) NOT NULL,
    "ProductVersion" VARCHAR(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO inviteme."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924000100_ExistingSchemaBaseline', '10.0.12')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
