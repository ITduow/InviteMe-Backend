-- Run only after comparing an existing database with BaselineV2.sql.
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
    IF EXISTS (SELECT 1 FROM (VALUES
        ('guest_groups','side'), ('guests','record_status'), ('guests','expected_companion_count'), ('guests','notes'),
        ('invitations','configuration'), ('invitations','previewed_at'), ('invitations','approved_by'),
        ('invitations','approved_at'), ('invitations','published_at'), ('rsvps','confirmed_party_size'),
        ('rsvp_history','old_confirmed_party_size'), ('rsvp_history','new_confirmed_party_size'),
        ('tables','table_number'), ('tables','status'), ('tables','activated_at'), ('gifts','guest_id')
    ) required(table_name,column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c
        WHERE c.table_schema = 'inviteme' AND c.table_name = required.table_name AND c.column_name = required.column_name))
    OR EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'inviteme'
        AND ((table_name = 'guests' AND column_name = 'status') OR (table_name = 'gifts' AND column_name = 'participant_id'))) THEN
        RAISE EXCEPTION 'Expected complete v2 column signatures; reconcile schema before marking';
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

INSERT INTO inviteme."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002000100_AlignBusinessSchemaV2', '10.0.12')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
