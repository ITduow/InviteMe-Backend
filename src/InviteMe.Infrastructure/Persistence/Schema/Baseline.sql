-- ============================================================
-- InviteMe - PostgreSQL Physical Schema
-- Fresh database schema for PostgreSQL 15+
-- Based on the current InviteMe ERD + agreed modifications.
-- ============================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS inviteme;
SET search_path TO inviteme, public;

-- ============================================================
-- 0. Common helper
-- ============================================================

CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

-- ============================================================
-- 1. USERS / RBAC
-- ============================================================

CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email           VARCHAR(255) NOT NULL,
    password_hash   VARCHAR(255) NULL,
    full_name       VARCHAR(150) NOT NULL,
    phone           VARCHAR(30) NULL,
    avatar_url      TEXT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_users_status
        CHECK (status IN ('ACTIVE', 'SUSPENDED', 'DISABLED'))
);

CREATE UNIQUE INDEX uq_users_email_lower
    ON users (LOWER(email));

CREATE TABLE roles (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(50) NOT NULL UNIQUE,
    name            VARCHAR(100) NOT NULL,
    description     TEXT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE permissions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(100) NOT NULL UNIQUE,
    name            VARCHAR(150) NOT NULL,
    description     TEXT NULL,
    scope           VARCHAR(30) NOT NULL DEFAULT 'WEDDING',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_permissions_scope
        CHECK (scope IN ('PLATFORM', 'WEDDING'))
);

CREATE TABLE user_roles (
    user_id         UUID NOT NULL,
    role_id         UUID NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (user_id, role_id),

    CONSTRAINT fk_user_roles_user
        FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_user_roles_role
        FOREIGN KEY (role_id)
        REFERENCES roles(id)
        ON DELETE CASCADE
);

CREATE TABLE role_permissions (
    role_id         UUID NOT NULL,
    permission_id   UUID NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (role_id, permission_id),

    CONSTRAINT fk_role_permissions_role
        FOREIGN KEY (role_id)
        REFERENCES roles(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_role_permissions_permission
        FOREIGN KEY (permission_id)
        REFERENCES permissions(id)
        ON DELETE CASCADE
);

-- ============================================================
-- 2. PLANS / SUBSCRIPTIONS
-- ============================================================

CREATE TABLE plans (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(50) NOT NULL UNIQUE,
    name            VARCHAR(100) NOT NULL,
    price           NUMERIC(14,2) NOT NULL DEFAULT 0,
    currency        CHAR(3) NOT NULL DEFAULT 'VND',
    billing_period  VARCHAR(20) NOT NULL DEFAULT 'ONE_TIME',
    limits_json     JSONB NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_plans_price
        CHECK (price >= 0),

    CONSTRAINT ck_plans_currency
        CHECK (currency ~ '^[A-Z]{3}$'),

    CONSTRAINT ck_plans_billing_period
        CHECK (billing_period IN ('ONE_TIME', 'MONTHLY', 'YEARLY')),

    CONSTRAINT ck_plans_status
        CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

CREATE TABLE subscriptions (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID NOT NULL,
    plan_id         UUID NOT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at      TIMESTAMPTZ NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_subscriptions_user
        FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_subscriptions_plan
        FOREIGN KEY (plan_id)
        REFERENCES plans(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_subscriptions_status
        CHECK (status IN ('ACTIVE', 'EXPIRED', 'CANCELLED', 'SUSPENDED')),

    CONSTRAINT ck_subscriptions_dates
        CHECK (expires_at IS NULL OR expires_at >= started_at)
);

CREATE INDEX ix_subscriptions_user_id ON subscriptions(user_id);
CREATE INDEX ix_subscriptions_plan_id ON subscriptions(plan_id);

-- ============================================================
-- 3. TEMPLATES
-- ============================================================

CREATE TABLE templates (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            VARCHAR(150) NOT NULL,
    slug            VARCHAR(160) NOT NULL UNIQUE,
    theme           VARCHAR(100) NULL,
    preview_url     TEXT NULL,
    configuration   JSONB NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_templates_status
        CHECK (status IN ('ACTIVE', 'INACTIVE', 'DRAFT'))
);

CREATE TABLE template_sections (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id     UUID NOT NULL,
    section_key     VARCHAR(100) NOT NULL,
    config_json     JSONB NULL,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_template_sections_template
        FOREIGN KEY (template_id)
        REFERENCES templates(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_template_sections
        UNIQUE (template_id, section_key)
);

CREATE INDEX ix_template_sections_template_id
    ON template_sections(template_id);

-- ============================================================
-- 4. WEDDINGS
-- ============================================================

CREATE TABLE weddings (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id   UUID NOT NULL,
    title           VARCHAR(200) NOT NULL,
    slug            VARCHAR(180) NOT NULL UNIQUE,
    status          VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    max_capacity    INTEGER NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_weddings_owner
        FOREIGN KEY (owner_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_weddings_status
        CHECK (status IN ('DRAFT', 'PUBLISHED', 'CANCELLED', 'ARCHIVED')),

    CONSTRAINT ck_weddings_max_capacity
        CHECK (max_capacity IS NULL OR max_capacity > 0)
);

CREATE INDEX ix_weddings_owner_user_id ON weddings(owner_user_id);

CREATE TABLE wedding_settings (
    wedding_id                  UUID PRIMARY KEY,
    visibility                  VARCHAR(20) NOT NULL DEFAULT 'PRIVATE',
    timezone                    VARCHAR(100) NOT NULL DEFAULT 'Asia/Ho_Chi_Minh',
    rsvp_deadline               TIMESTAMPTZ NULL,
    rsvp_reminder_days_before   SMALLINT NOT NULL DEFAULT 2,
    settings_json               JSONB NULL,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_wedding_settings_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_wedding_settings_visibility
        CHECK (visibility IN ('PRIVATE', 'UNLISTED', 'PUBLIC')),

    CONSTRAINT ck_wedding_settings_reminder_days
        CHECK (rsvp_reminder_days_before >= 0)
);

CREATE TABLE wedding_members (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    user_id         UUID NOT NULL,
    member_role     VARCHAR(30) NOT NULL DEFAULT 'CO_HOST',
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    joined_at       TIMESTAMPTZ NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_wedding_members_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_wedding_members_user
        FOREIGN KEY (user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_wedding_members
        UNIQUE (wedding_id, user_id),

    CONSTRAINT ck_wedding_members_role
        CHECK (member_role IN ('CO_HOST')),

    CONSTRAINT ck_wedding_members_status
        CHECK (status IN ('INVITED', 'ACTIVE', 'REVOKED'))
);

CREATE INDEX ix_wedding_members_wedding_id
    ON wedding_members(wedding_id);

CREATE INDEX ix_wedding_members_user_id
    ON wedding_members(user_id);

CREATE TABLE wedding_member_permissions (
    member_id       UUID NOT NULL,
    permission_id   UUID NOT NULL,
    granted_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (member_id, permission_id),

    CONSTRAINT fk_wmp_member
        FOREIGN KEY (member_id)
        REFERENCES wedding_members(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_wmp_permission
        FOREIGN KEY (permission_id)
        REFERENCES permissions(id)
        ON DELETE CASCADE
);

-- ============================================================
-- 5. WEDDING PAGE CONTENT
-- ============================================================

CREATE TABLE love_stories (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    title           VARCHAR(200) NULL,
    content         TEXT NOT NULL,
    visibility      VARCHAR(20) NOT NULL DEFAULT 'VISIBLE',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_love_stories_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_love_stories_visibility
        CHECK (visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE'))
);

CREATE INDEX ix_love_stories_wedding_id ON love_stories(wedding_id);

CREATE TABLE venues (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    name            VARCHAR(200) NOT NULL,
    address         TEXT NULL,
    latitude        NUMERIC(9,6) NULL,
    longitude       NUMERIC(9,6) NULL,
    capacity        INTEGER NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_venues_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_venues_latitude
        CHECK (latitude IS NULL OR latitude BETWEEN -90 AND 90),

    CONSTRAINT ck_venues_longitude
        CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180),

    CONSTRAINT ck_venues_capacity
        CHECK (capacity IS NULL OR capacity > 0)
);

CREATE INDEX ix_venues_wedding_id ON venues(wedding_id);

CREATE TABLE wedding_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    venue_id        UUID NULL,
    name            VARCHAR(200) NOT NULL,
    description     TEXT NULL,
    start_at        TIMESTAMPTZ NOT NULL,
    end_at          TIMESTAMPTZ NULL,
    capacity_limit  INTEGER NULL,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_wedding_events_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_wedding_events_venue
        FOREIGN KEY (venue_id)
        REFERENCES venues(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_wedding_events_dates
        CHECK (end_at IS NULL OR end_at >= start_at),

    CONSTRAINT ck_wedding_events_capacity
        CHECK (capacity_limit IS NULL OR capacity_limit > 0)
);

CREATE INDEX ix_wedding_events_wedding_id
    ON wedding_events(wedding_id);

CREATE INDEX ix_wedding_events_venue_id
    ON wedding_events(venue_id);

CREATE TABLE wedding_media (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    media_url       TEXT NOT NULL,
    storage_key     TEXT NULL,
    media_type      VARCHAR(30) NOT NULL,
    caption         TEXT NULL,
    visibility      VARCHAR(20) NOT NULL DEFAULT 'VISIBLE',
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_wedding_media_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_wedding_media_type
        CHECK (media_type IN ('IMAGE', 'VIDEO', 'OTHER')),

    CONSTRAINT ck_wedding_media_visibility
        CHECK (visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE'))
);

CREATE INDEX ix_wedding_media_wedding_id
    ON wedding_media(wedding_id);

-- ============================================================
-- 6. GUEST MANAGEMENT
-- ============================================================

CREATE TABLE guest_groups (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    name            VARCHAR(150) NOT NULL,
    description     TEXT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_guest_groups_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_guest_groups_name
        UNIQUE (wedding_id, name)
);

CREATE INDEX ix_guest_groups_wedding_id
    ON guest_groups(wedding_id);

CREATE TABLE guests (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id          UUID NOT NULL,
    group_id            UUID NULL,
    guest_code          VARCHAR(50) NOT NULL,
    full_name           VARCHAR(150) NOT NULL,
    phone               VARCHAR(30) NULL,
    email               VARCHAR(255) NULL,
    relationship        VARCHAR(100) NULL,
    side                VARCHAR(20) NOT NULL DEFAULT 'MUTUAL',
    status              VARCHAR(20) NOT NULL DEFAULT 'ACTIVE',
    allowed_plus_one    BOOLEAN NOT NULL DEFAULT FALSE,
    max_plus_one        INTEGER NOT NULL DEFAULT 0,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_guests_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_guests_group
        FOREIGN KEY (group_id)
        REFERENCES guest_groups(id)
        ON DELETE SET NULL,

    CONSTRAINT uq_guests_guest_code
        UNIQUE (wedding_id, guest_code),

    CONSTRAINT ck_guests_side
        CHECK (side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')),

    CONSTRAINT ck_guests_status
        CHECK (status IN ('ACTIVE', 'ARCHIVED')),

    CONSTRAINT ck_guests_plus_one
        CHECK (
            max_plus_one >= 0
            AND (allowed_plus_one OR max_plus_one = 0)
        )
);

CREATE INDEX ix_guests_wedding_id ON guests(wedding_id);
CREATE INDEX ix_guests_group_id ON guests(group_id);
CREATE INDEX ix_guests_phone ON guests(phone);
CREATE INDEX ix_guests_email_lower ON guests(LOWER(email));

CREATE TABLE guest_participants (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    guest_id            UUID NOT NULL,
    full_name           VARCHAR(150) NOT NULL,
    participant_type    VARCHAR(20) NOT NULL,
    attendance_status   VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    dietary_note        TEXT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_guest_participants_guest
        FOREIGN KEY (guest_id)
        REFERENCES guests(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_guest_participant_type
        CHECK (
            participant_type IN (
                'PRIMARY',
                'SPOUSE',
                'CHILD',
                'PLUS_ONE',
                'OTHER'
            )
        ),

    CONSTRAINT ck_guest_participant_attendance
        CHECK (
            attendance_status IN (
                'PENDING',
                'ATTENDING',
                'DECLINED',
                'WAITLISTED'
            )
        ),

    CONSTRAINT uq_guest_participants_id_guest
        UNIQUE (id, guest_id)
);

CREATE INDEX ix_guest_participants_guest_id
    ON guest_participants(guest_id);

CREATE INDEX ix_guest_participants_attendance
    ON guest_participants(attendance_status);

CREATE UNIQUE INDEX uq_guest_primary_participant
    ON guest_participants(guest_id)
    WHERE participant_type = 'PRIMARY';

CREATE TABLE guest_notes (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    guest_id        UUID NOT NULL,
    participant_id  UUID NULL,
    created_by      UUID NULL,
    note_type       VARCHAR(30) NOT NULL DEFAULT 'GENERAL',
    content         TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_guest_notes_guest
        FOREIGN KEY (guest_id)
        REFERENCES guests(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_guest_notes_participant_same_guest
        FOREIGN KEY (participant_id, guest_id)
        REFERENCES guest_participants(id, guest_id)
        ON DELETE CASCADE,

    CONSTRAINT fk_guest_notes_created_by
        FOREIGN KEY (created_by)
        REFERENCES users(id)
        ON DELETE SET NULL
);

CREATE INDEX ix_guest_notes_guest_id ON guest_notes(guest_id);
CREATE INDEX ix_guest_notes_participant_id ON guest_notes(participant_id);

-- ============================================================
-- 7. INVITATIONS / RSVP
-- ============================================================

CREATE TABLE invitations (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    guest_id        UUID NOT NULL,
    token_hash      CHAR(64) NOT NULL UNIQUE,
    status          VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    expires_at      TIMESTAMPTZ NULL,
    sent_at         TIMESTAMPTZ NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_invitations_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_invitations_guest
        FOREIGN KEY (guest_id)
        REFERENCES guests(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_invitations_guest
        UNIQUE (wedding_id, guest_id),

    CONSTRAINT ck_invitations_status
        CHECK (
            status IN (
                'DRAFT',
                'READY',
                'SENT',
                'OPENED',
                'REVOKED',
                'EXPIRED'
            )
        )
);

CREATE INDEX ix_invitations_wedding_id ON invitations(wedding_id);
CREATE INDEX ix_invitations_guest_id ON invitations(guest_id);

CREATE TABLE invitation_deliveries (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    invitation_id           UUID NOT NULL,
    channel                 VARCHAR(20) NOT NULL,
    recipient               VARCHAR(255) NOT NULL,
    status                  VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    provider_message_id     VARCHAR(255) NULL,
    sent_at                 TIMESTAMPTZ NULL,
    delivered_at            TIMESTAMPTZ NULL,
    failed_at               TIMESTAMPTZ NULL,
    error_message           TEXT NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_invitation_deliveries_invitation
        FOREIGN KEY (invitation_id)
        REFERENCES invitations(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_invitation_delivery_channel
        CHECK (channel IN ('EMAIL', 'SMS')),

    CONSTRAINT ck_invitation_delivery_status
        CHECK (status IN ('PENDING', 'SENT', 'DELIVERED', 'FAILED'))
);

CREATE INDEX ix_invitation_deliveries_invitation_id
    ON invitation_deliveries(invitation_id);

CREATE INDEX ix_invitation_deliveries_status
    ON invitation_deliveries(status);

CREATE TABLE rsvps (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    invitation_id   UUID NOT NULL UNIQUE,
    status          VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    party_size      INTEGER NOT NULL DEFAULT 0,
    submitted_at    TIMESTAMPTZ NULL,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_rsvps_invitation
        FOREIGN KEY (invitation_id)
        REFERENCES invitations(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_rsvps_status
        CHECK (status IN ('PENDING', 'ATTENDING', 'DECLINED')),

    CONSTRAINT ck_rsvps_party_size
        CHECK (party_size >= 0)
);

CREATE TABLE rsvp_history (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rsvp_id         UUID NOT NULL,
    old_status      VARCHAR(20) NULL,
    new_status      VARCHAR(20) NOT NULL,
    old_party_size  INTEGER NULL,
    new_party_size  INTEGER NOT NULL,
    old_snapshot    JSONB NULL,
    new_snapshot    JSONB NULL,
    changed_by      UUID NULL,
    reason          TEXT NULL,
    changed_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_rsvp_history_rsvp
        FOREIGN KEY (rsvp_id)
        REFERENCES rsvps(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_rsvp_history_changed_by
        FOREIGN KEY (changed_by)
        REFERENCES users(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_rsvp_history_old_status
        CHECK (
            old_status IS NULL
            OR old_status IN ('PENDING', 'ATTENDING', 'DECLINED')
        ),

    CONSTRAINT ck_rsvp_history_new_status
        CHECK (new_status IN ('PENDING', 'ATTENDING', 'DECLINED')),

    CONSTRAINT ck_rsvp_history_party_sizes
        CHECK (
            (old_party_size IS NULL OR old_party_size >= 0)
            AND new_party_size >= 0
        )
);

CREATE INDEX ix_rsvp_history_rsvp_id ON rsvp_history(rsvp_id);
CREATE INDEX ix_rsvp_history_changed_at ON rsvp_history(changed_at);

-- ============================================================
-- 8. WAITLIST
-- ============================================================

CREATE TABLE waitlist_entries (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id          UUID NOT NULL,
    guest_id            UUID NOT NULL,
    participant_id      UUID NULL,
    requested_slots     INTEGER NOT NULL DEFAULT 1,
    status              VARCHAR(20) NOT NULL DEFAULT 'WAITING',
    priority            INTEGER NULL,
    reason              TEXT NULL,
    requested_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    promoted_at         TIMESTAMPTZ NULL,
    cancelled_at        TIMESTAMPTZ NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_waitlist_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_waitlist_guest
        FOREIGN KEY (guest_id)
        REFERENCES guests(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_waitlist_participant
        FOREIGN KEY (participant_id)
        REFERENCES guest_participants(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_waitlist_requested_slots
        CHECK (requested_slots > 0),

    CONSTRAINT ck_waitlist_status
        CHECK (
            status IN ('WAITING', 'PROMOTED', 'CANCELLED', 'EXPIRED')
        )
);

CREATE INDEX ix_waitlist_wedding_status
    ON waitlist_entries(wedding_id, status);

CREATE INDEX ix_waitlist_guest_id
    ON waitlist_entries(guest_id);

CREATE UNIQUE INDEX uq_waitlist_active_participant
    ON waitlist_entries(wedding_id, participant_id)
    WHERE participant_id IS NOT NULL AND status = 'WAITING';

CREATE UNIQUE INDEX uq_waitlist_active_party
    ON waitlist_entries(wedding_id, guest_id)
    WHERE participant_id IS NULL AND status = 'WAITING';

-- ============================================================
-- 9. SEATING
-- ============================================================

CREATE TABLE tables (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    table_name      VARCHAR(100) NOT NULL,
    capacity        INTEGER NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_tables_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_tables_name
        UNIQUE (wedding_id, table_name),

    CONSTRAINT ck_tables_capacity
        CHECK (capacity > 0),

    CONSTRAINT ck_tables_version
        CHECK (version >= 1)
);

CREATE INDEX ix_tables_wedding_id ON tables(wedding_id);

-- Optional seat-level management.
-- Keep this table if exact chair positions are required.
CREATE TABLE seats (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    table_id        UUID NOT NULL,
    seat_number     INTEGER NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_seats_table
        FOREIGN KEY (table_id)
        REFERENCES tables(id)
        ON DELETE CASCADE,

    CONSTRAINT uq_seats_number
        UNIQUE (table_id, seat_number),

    CONSTRAINT ck_seats_number
        CHECK (seat_number > 0)
);

CREATE INDEX ix_seats_table_id ON seats(table_id);

CREATE TABLE seating_assignments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    participant_id  UUID NOT NULL UNIQUE,
    table_id        UUID NOT NULL,
    seat_id         UUID NULL,
    assigned_by     UUID NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_seating_assignment_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_seating_assignment_participant
        FOREIGN KEY (participant_id)
        REFERENCES guest_participants(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_seating_assignment_table
        FOREIGN KEY (table_id)
        REFERENCES tables(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_seating_assignment_seat
        FOREIGN KEY (seat_id)
        REFERENCES seats(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_seating_assignment_assigned_by
        FOREIGN KEY (assigned_by)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_seating_assignment_version
        CHECK (version >= 1)
);

CREATE UNIQUE INDEX uq_seating_assignment_seat
    ON seating_assignments(seat_id)
    WHERE seat_id IS NOT NULL;

CREATE INDEX ix_seating_assignments_wedding_id
    ON seating_assignments(wedding_id);

CREATE INDEX ix_seating_assignments_table_id
    ON seating_assignments(table_id);

CREATE TABLE seating_change_logs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    assignment_id   UUID NULL,
    participant_id  UUID NOT NULL,
    actor_id        UUID NOT NULL,
    from_table_id   UUID NULL,
    to_table_id     UUID NULL,
    from_seat_id    UUID NULL,
    to_seat_id      UUID NULL,
    action          VARCHAR(30) NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_seating_logs_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_seating_logs_assignment
        FOREIGN KEY (assignment_id)
        REFERENCES seating_assignments(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_seating_logs_participant
        FOREIGN KEY (participant_id)
        REFERENCES guest_participants(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_seating_logs_actor
        FOREIGN KEY (actor_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_seating_logs_from_table
        FOREIGN KEY (from_table_id)
        REFERENCES tables(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_seating_logs_to_table
        FOREIGN KEY (to_table_id)
        REFERENCES tables(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_seating_logs_from_seat
        FOREIGN KEY (from_seat_id)
        REFERENCES seats(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_seating_logs_to_seat
        FOREIGN KEY (to_seat_id)
        REFERENCES seats(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_seating_logs_action
        CHECK (action IN ('ASSIGN', 'MOVE', 'UNASSIGN', 'SEAT_CHANGE'))
);

CREATE INDEX ix_seating_change_logs_wedding_id
    ON seating_change_logs(wedding_id);

CREATE INDEX ix_seating_change_logs_participant_id
    ON seating_change_logs(participant_id);

CREATE INDEX ix_seating_change_logs_created_at
    ON seating_change_logs(created_at);

-- Enforce participant/wedding consistency, seat/table consistency,
-- attendance status, and table capacity under concurrent writes.
CREATE OR REPLACE FUNCTION validate_seating_assignment()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_capacity      INTEGER;
    v_table_wedding_id    UUID;
    v_participant_wedding UUID;
    v_attendance_status   VARCHAR(20);
    v_seat_table_id       UUID;
    v_current_count       INTEGER;
BEGIN
    SELECT t.capacity, t.wedding_id
      INTO v_table_capacity, v_table_wedding_id
      FROM tables t
     WHERE t.id = NEW.table_id
     FOR UPDATE;

    IF v_table_wedding_id IS NULL THEN
        RAISE EXCEPTION 'Table % does not exist', NEW.table_id;
    END IF;

    IF v_table_wedding_id <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Table does not belong to wedding';
    END IF;

    SELECT g.wedding_id, gp.attendance_status
      INTO v_participant_wedding, v_attendance_status
      FROM guest_participants gp
      JOIN guests g ON g.id = gp.guest_id
     WHERE gp.id = NEW.participant_id;

    IF v_participant_wedding IS NULL THEN
        RAISE EXCEPTION 'Participant % does not exist', NEW.participant_id;
    END IF;

    IF v_participant_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Participant does not belong to wedding';
    END IF;

    IF v_attendance_status <> 'ATTENDING' THEN
        RAISE EXCEPTION 'Only ATTENDING participants can be seated';
    END IF;

    IF NEW.seat_id IS NOT NULL THEN
        SELECT s.table_id
          INTO v_seat_table_id
          FROM seats s
         WHERE s.id = NEW.seat_id;

        IF v_seat_table_id IS NULL OR v_seat_table_id <> NEW.table_id THEN
            RAISE EXCEPTION 'Seat does not belong to selected table';
        END IF;
    END IF;

    IF TG_OP = 'UPDATE' THEN
        SELECT COUNT(*)
          INTO v_current_count
          FROM seating_assignments sa
         WHERE sa.table_id = NEW.table_id
           AND sa.id <> OLD.id;
    ELSE
        SELECT COUNT(*)
          INTO v_current_count
          FROM seating_assignments sa
         WHERE sa.table_id = NEW.table_id;
    END IF;

    IF v_current_count >= v_table_capacity THEN
        RAISE EXCEPTION 'Table capacity exceeded';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_seating_assignment
BEFORE INSERT OR UPDATE OF wedding_id, participant_id, table_id, seat_id
ON seating_assignments
FOR EACH ROW
EXECUTE FUNCTION validate_seating_assignment();

-- ============================================================
-- 10. WALK-INS / CHECK-IN
-- ============================================================

CREATE TABLE walk_ins (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    full_name       VARCHAR(150) NOT NULL,
    phone           VARCHAR(30) NULL,
    party_size      INTEGER NOT NULL DEFAULT 1,
    note            TEXT NULL,
    created_by      UUID NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_walkins_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_walkins_created_by
        FOREIGN KEY (created_by)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_walkins_party_size
        CHECK (party_size > 0)
);

CREATE INDEX ix_walkins_wedding_id ON walk_ins(wedding_id);

CREATE TABLE check_ins (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    participant_id  UUID NULL,
    walkin_id       UUID NULL,
    checked_in_by   UUID NOT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'CHECKED_IN',
    checked_in_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_checkins_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_checkins_participant
        FOREIGN KEY (participant_id)
        REFERENCES guest_participants(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_checkins_walkin
        FOREIGN KEY (walkin_id)
        REFERENCES walk_ins(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_checkins_checked_in_by
        FOREIGN KEY (checked_in_by)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_checkins_target_xor
        CHECK (
            (participant_id IS NOT NULL AND walkin_id IS NULL)
            OR
            (participant_id IS NULL AND walkin_id IS NOT NULL)
        ),

    CONSTRAINT ck_checkins_status
        CHECK (status IN ('CHECKED_IN', 'VOID'))
);

CREATE UNIQUE INDEX uq_checkins_participant
    ON check_ins(wedding_id, participant_id)
    WHERE participant_id IS NOT NULL;

CREATE UNIQUE INDEX uq_checkins_walkin
    ON check_ins(wedding_id, walkin_id)
    WHERE walkin_id IS NOT NULL;

CREATE INDEX ix_checkins_wedding_id ON check_ins(wedding_id);
CREATE INDEX ix_checkins_checked_in_at ON check_ins(checked_in_at);

-- ============================================================
-- 11. GIFTS / WISHES
-- ============================================================

CREATE TABLE gifts (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id          UUID NOT NULL,
    participant_id      UUID NULL,
    walkin_id           UUID NULL,
    amount              NUMERIC(14,2) NOT NULL DEFAULT 0,
    currency            CHAR(3) NOT NULL DEFAULT 'VND',
    method              VARCHAR(30) NOT NULL,
    provider            VARCHAR(50) NULL,
    transaction_ref     VARCHAR(150) NULL,
    idempotency_key     VARCHAR(150) NULL,
    transaction_status  VARCHAR(30) NOT NULL DEFAULT 'COMPLETED',
    received_by         UUID NULL,
    received_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_gifts_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_gifts_participant
        FOREIGN KEY (participant_id)
        REFERENCES guest_participants(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_gifts_walkin
        FOREIGN KEY (walkin_id)
        REFERENCES walk_ins(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_gifts_received_by
        FOREIGN KEY (received_by)
        REFERENCES users(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_gifts_amount
        CHECK (amount >= 0),

    CONSTRAINT ck_gifts_currency
        CHECK (currency ~ '^[A-Z]{3}$'),

    CONSTRAINT ck_gifts_method
        CHECK (method IN ('CASH', 'BANK_QR', 'BANK_TRANSFER', 'OTHER')),

    CONSTRAINT ck_gifts_transaction_status
        CHECK (
            transaction_status IN (
                'PENDING',
                'COMPLETED',
                'FAILED',
                'REFUNDED'
            )
        ),

    CONSTRAINT ck_gifts_source_not_both
        CHECK (
            NOT (
                participant_id IS NOT NULL
                AND walkin_id IS NOT NULL
            )
        )
);

CREATE UNIQUE INDEX uq_gifts_transaction
    ON gifts(wedding_id, provider, transaction_ref)
    WHERE provider IS NOT NULL
      AND transaction_ref IS NOT NULL;

CREATE UNIQUE INDEX uq_gifts_idempotency
    ON gifts(idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX ix_gifts_wedding_id ON gifts(wedding_id);
CREATE INDEX ix_gifts_participant_id ON gifts(participant_id);
CREATE INDEX ix_gifts_received_at ON gifts(received_at);

CREATE TABLE gift_messages (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    gift_id         UUID NOT NULL UNIQUE,
    message         TEXT NOT NULL,
    visibility      VARCHAR(20) NOT NULL DEFAULT 'PRIVATE',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_gift_messages_gift
        FOREIGN KEY (gift_id)
        REFERENCES gifts(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_gift_messages_visibility
        CHECK (visibility IN ('PRIVATE', 'PUBLIC', 'HIDDEN'))
);

-- ============================================================
-- 12. NOTIFICATIONS
-- ============================================================

CREATE TABLE notifications (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id          UUID NOT NULL,
    recipient_user_id   UUID NULL,
    recipient_guest_id  UUID NULL,
    notification_type   VARCHAR(30) NOT NULL,
    channel             VARCHAR(20) NOT NULL,
    template            VARCHAR(100) NULL,
    content             TEXT NOT NULL,
    status              VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    scheduled_at        TIMESTAMPTZ NULL,
    sent_at             TIMESTAMPTZ NULL,
    attempt_count       INTEGER NOT NULL DEFAULT 0,
    last_error          TEXT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_notifications_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_notifications_user
        FOREIGN KEY (recipient_user_id)
        REFERENCES users(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_notifications_guest
        FOREIGN KEY (recipient_guest_id)
        REFERENCES guests(id)
        ON DELETE CASCADE,

    CONSTRAINT ck_notifications_recipient_xor
        CHECK (
            (
                recipient_user_id IS NOT NULL
                AND recipient_guest_id IS NULL
            )
            OR
            (
                recipient_user_id IS NULL
                AND recipient_guest_id IS NOT NULL
            )
        ),

    CONSTRAINT ck_notifications_channel
        CHECK (channel IN ('IN_APP', 'EMAIL', 'SMS')),

    CONSTRAINT ck_notifications_status
        CHECK (
            status IN (
                'PENDING',
                'SCHEDULED',
                'SENT',
                'FAILED',
                'CANCELLED'
            )
        ),

    CONSTRAINT ck_notifications_attempt_count
        CHECK (attempt_count >= 0)
);

CREATE INDEX ix_notifications_wedding_id
    ON notifications(wedding_id);

CREATE INDEX ix_notifications_status_schedule
    ON notifications(status, scheduled_at);

CREATE INDEX ix_notifications_recipient_user
    ON notifications(recipient_user_id);

CREATE INDEX ix_notifications_recipient_guest
    ON notifications(recipient_guest_id);

-- ============================================================
-- 13. AI
-- ============================================================

CREATE TABLE ai_generations (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NOT NULL,
    type            VARCHAR(50) NOT NULL,
    input_ref       VARCHAR(255) NULL,
    input_json      JSONB NULL,
    output_json     JSONB NULL,
    model           VARCHAR(100) NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    created_by      UUID NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_ai_generations_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_ai_generations_created_by
        FOREIGN KEY (created_by)
        REFERENCES users(id)
        ON DELETE SET NULL,

    CONSTRAINT ck_ai_generations_type
        CHECK (
            type IN (
                'GUEST_CLASSIFICATION',
                'SEATING_SUGGESTION',
                'CONTENT_GENERATION',
                'THANK_YOU'
            )
        ),

    CONSTRAINT ck_ai_generations_status
        CHECK (status IN ('PENDING', 'SUCCEEDED', 'FAILED'))
);

CREATE INDEX ix_ai_generations_wedding_id
    ON ai_generations(wedding_id);

CREATE INDEX ix_ai_generations_type
    ON ai_generations(type);

-- ============================================================
-- 14. AUDIT LOGS
-- ============================================================

CREATE TABLE audit_logs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id      UUID NULL,
    actor_type      VARCHAR(20) NOT NULL,
    actor_user_id   UUID NULL,
    actor_guest_id  UUID NULL,
    action          VARCHAR(100) NOT NULL,
    entity_type     VARCHAR(100) NOT NULL,
    entity_id       UUID NULL,
    metadata        JSONB NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_audit_logs_wedding
        FOREIGN KEY (wedding_id)
        REFERENCES weddings(id)
        ON DELETE SET NULL,

    CONSTRAINT fk_audit_logs_actor_user
        FOREIGN KEY (actor_user_id)
        REFERENCES users(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_audit_logs_actor_guest
        FOREIGN KEY (actor_guest_id)
        REFERENCES guests(id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_audit_logs_actor_type
        CHECK (actor_type IN ('USER', 'GUEST', 'SYSTEM', 'AI')),

    CONSTRAINT ck_audit_logs_actor_shape
        CHECK (
            (
                actor_type = 'USER'
                AND actor_user_id IS NOT NULL
                AND actor_guest_id IS NULL
            )
            OR
            (
                actor_type = 'GUEST'
                AND actor_guest_id IS NOT NULL
                AND actor_user_id IS NULL
            )
            OR
            (
                actor_type IN ('SYSTEM', 'AI')
                AND actor_user_id IS NULL
                AND actor_guest_id IS NULL
            )
        )
);

CREATE INDEX ix_audit_logs_wedding_id ON audit_logs(wedding_id);
CREATE INDEX ix_audit_logs_entity ON audit_logs(entity_type, entity_id);
CREATE INDEX ix_audit_logs_created_at ON audit_logs(created_at);

-- ============================================================
-- 15. VALIDATION TRIGGERS FOR CROSS-WEDDING CONSISTENCY
-- ============================================================

CREATE OR REPLACE FUNCTION validate_guest_group_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_group_wedding UUID;
BEGIN
    IF NEW.group_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT wedding_id
      INTO v_group_wedding
      FROM guest_groups
     WHERE id = NEW.group_id;

    IF v_group_wedding IS NULL OR v_group_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Guest group does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_guest_group_scope
BEFORE INSERT OR UPDATE OF wedding_id, group_id
ON guests
FOR EACH ROW
EXECUTE FUNCTION validate_guest_group_scope();

CREATE OR REPLACE FUNCTION validate_event_venue_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_venue_wedding UUID;
BEGIN
    IF NEW.venue_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT wedding_id
      INTO v_venue_wedding
      FROM venues
     WHERE id = NEW.venue_id;

    IF v_venue_wedding IS NULL OR v_venue_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Venue does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_event_venue_scope
BEFORE INSERT OR UPDATE OF wedding_id, venue_id
ON wedding_events
FOR EACH ROW
EXECUTE FUNCTION validate_event_venue_scope();

CREATE OR REPLACE FUNCTION validate_invitation_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_guest_wedding UUID;
BEGIN
    SELECT wedding_id
      INTO v_guest_wedding
      FROM guests
     WHERE id = NEW.guest_id;

    IF v_guest_wedding IS NULL OR v_guest_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Guest does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_invitation_scope
BEFORE INSERT OR UPDATE OF wedding_id, guest_id
ON invitations
FOR EACH ROW
EXECUTE FUNCTION validate_invitation_scope();

CREATE OR REPLACE FUNCTION validate_waitlist_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_guest_wedding UUID;
    v_participant_guest UUID;
BEGIN
    SELECT wedding_id
      INTO v_guest_wedding
      FROM guests
     WHERE id = NEW.guest_id;

    IF v_guest_wedding IS NULL OR v_guest_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Waitlist guest does not belong to wedding';
    END IF;

    IF NEW.participant_id IS NOT NULL THEN
        SELECT guest_id
          INTO v_participant_guest
          FROM guest_participants
         WHERE id = NEW.participant_id;

        IF v_participant_guest IS NULL OR v_participant_guest <> NEW.guest_id THEN
            RAISE EXCEPTION 'Waitlist participant does not belong to guest';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_waitlist_scope
BEFORE INSERT OR UPDATE OF wedding_id, guest_id, participant_id
ON waitlist_entries
FOR EACH ROW
EXECUTE FUNCTION validate_waitlist_scope();

CREATE OR REPLACE FUNCTION validate_checkin_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_target_wedding UUID;
BEGIN
    IF NEW.participant_id IS NOT NULL THEN
        SELECT g.wedding_id
          INTO v_target_wedding
          FROM guest_participants gp
          JOIN guests g ON g.id = gp.guest_id
         WHERE gp.id = NEW.participant_id;
    ELSE
        SELECT w.wedding_id
          INTO v_target_wedding
          FROM walk_ins w
         WHERE w.id = NEW.walkin_id;
    END IF;

    IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Check-in target does not belong to wedding';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_checkin_scope
BEFORE INSERT OR UPDATE OF wedding_id, participant_id, walkin_id
ON check_ins
FOR EACH ROW
EXECUTE FUNCTION validate_checkin_scope();

CREATE OR REPLACE FUNCTION validate_gift_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_target_wedding UUID;
BEGIN
    IF NEW.participant_id IS NOT NULL THEN
        SELECT g.wedding_id
          INTO v_target_wedding
          FROM guest_participants gp
          JOIN guests g ON g.id = gp.guest_id
         WHERE gp.id = NEW.participant_id;

        IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
            RAISE EXCEPTION 'Gift participant does not belong to wedding';
        END IF;
    END IF;

    IF NEW.walkin_id IS NOT NULL THEN
        SELECT w.wedding_id
          INTO v_target_wedding
          FROM walk_ins w
         WHERE w.id = NEW.walkin_id;

        IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
            RAISE EXCEPTION 'Gift walk-in does not belong to wedding';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_gift_scope
BEFORE INSERT OR UPDATE OF wedding_id, participant_id, walkin_id
ON gifts
FOR EACH ROW
EXECUTE FUNCTION validate_gift_scope();

-- ============================================================
-- 16. READ MODELS / VIEWS
-- ============================================================

CREATE OR REPLACE VIEW v_wedding_headcount AS
SELECT
    g.wedding_id,
    COUNT(*) FILTER (
        WHERE gp.attendance_status = 'ATTENDING'
    ) AS confirmed_headcount,
    COUNT(*) FILTER (
        WHERE gp.attendance_status = 'WAITLISTED'
    ) AS waitlisted_participants,
    COUNT(*) FILTER (
        WHERE gp.attendance_status = 'PENDING'
    ) AS pending_participants,
    COUNT(*) FILTER (
        WHERE gp.attendance_status = 'DECLINED'
    ) AS declined_participants
FROM guests g
JOIN guest_participants gp
  ON gp.guest_id = g.id
GROUP BY g.wedding_id;

CREATE OR REPLACE VIEW v_table_occupancy AS
SELECT
    t.id AS table_id,
    t.wedding_id,
    t.table_name,
    t.capacity,
    COUNT(sa.id) AS occupied,
    GREATEST(t.capacity - COUNT(sa.id), 0) AS remaining
FROM tables t
LEFT JOIN seating_assignments sa
  ON sa.table_id = t.id
GROUP BY t.id, t.wedding_id, t.table_name, t.capacity;

CREATE OR REPLACE VIEW v_guest_rsvp_summary AS
SELECT
    g.id AS guest_id,
    g.wedding_id,
    g.guest_code,
    g.full_name,
    i.id AS invitation_id,
    i.status AS invitation_status,
    r.id AS rsvp_id,
    r.status AS rsvp_status,
    r.party_size,
    COUNT(gp.id) FILTER (
        WHERE gp.attendance_status = 'ATTENDING'
    ) AS attending_participants,
    COUNT(gp.id) FILTER (
        WHERE gp.attendance_status = 'WAITLISTED'
    ) AS waitlisted_participants
FROM guests g
LEFT JOIN invitations i
  ON i.guest_id = g.id
 AND i.wedding_id = g.wedding_id
LEFT JOIN rsvps r
  ON r.invitation_id = i.id
LEFT JOIN guest_participants gp
  ON gp.guest_id = g.id
GROUP BY
    g.id,
    g.wedding_id,
    g.guest_code,
    g.full_name,
    i.id,
    i.status,
    r.id,
    r.status,
    r.party_size;

-- ============================================================
-- 17. UPDATED_AT TRIGGERS
-- ============================================================

DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'users',
        'plans',
        'subscriptions',
        'templates',
        'template_sections',
        'weddings',
        'wedding_settings',
        'wedding_members',
        'love_stories',
        'venues',
        'wedding_events',
        'wedding_media',
        'guest_groups',
        'guests',
        'guest_participants',
        'guest_notes',
        'invitations',
        'rsvps',
        'waitlist_entries',
        'tables',
        'seating_assignments',
        'walk_ins',
        'gifts',
        'gift_messages',
        'notifications'
    ]
    LOOP
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE ON %I FOR EACH ROW EXECUTE FUNCTION set_updated_at()',
            'trg_' || t || '_updated_at',
            t
        );
    END LOOP;
END;
$$;

-- ============================================================
-- 18. BASIC SEED: ROLES / PERMISSIONS
-- ============================================================

INSERT INTO roles (code, name, description)
VALUES
    ('ADMIN', 'Administrator', 'Platform administrator'),
    ('USER',  'User',          'Standard InviteMe account')
ON CONFLICT (code) DO NOTHING;

INSERT INTO permissions (code, name, scope)
VALUES
    ('WEDDING_VIEW',       'View wedding',              'WEDDING'),
    ('WEDDING_EDIT',       'Edit wedding',              'WEDDING'),
    ('GUEST_VIEW',         'View guests',               'WEDDING'),
    ('GUEST_EDIT',         'Edit guests',               'WEDDING'),
    ('INVITATION_SEND',    'Send invitations',          'WEDDING'),
    ('RSVP_VIEW',          'View RSVP',                 'WEDDING'),
    ('SEATING_VIEW',       'View seating chart',        'WEDDING'),
    ('SEATING_EDIT',       'Edit seating chart',        'WEDDING'),
    ('CHECKIN_MANAGE',     'Manage event check-in',     'WEDDING'),
    ('GIFT_VIEW',          'View gifts',                'WEDDING'),
    ('GIFT_EDIT',          'Manage gifts',              'WEDDING'),
    ('ANALYTICS_VIEW',     'View analytics',            'WEDDING'),
    ('PLATFORM_MANAGE',    'Manage platform',           'PLATFORM')
ON CONFLICT (code) DO NOTHING;

COMMIT;

-- ============================================================
-- APPLICATION-LEVEL TRANSACTION RULES
-- ============================================================
--
-- 1. RSVP capacity race condition:
--    BEGIN;
--    SELECT * FROM inviteme.weddings
--     WHERE id = :wedding_id
--     FOR UPDATE;
--
--    Recalculate ATTENDING participants.
--    If request fits max_capacity -> confirm.
--    Otherwise -> create WAITLIST_ENTRIES.
--    COMMIT;
--
-- 2. Optimistic seating:
--    UPDATE inviteme.seating_assignments
--       SET table_id = :new_table_id,
--           version = version + 1
--     WHERE id = :assignment_id
--       AND version = :expected_version;
--
--    If affected rows = 0 -> return HTTP 409 Conflict.
--
-- 3. Gift idempotency:
--    Reuse the same idempotency_key for retries.
--    uq_gifts_idempotency prevents duplicate inserts.
--
-- 4. Invitation token:
--    Store SHA-256(raw_token) in token_hash.
--    The raw token is sent to the guest URL and is not stored.
--
-- 5. Audit retention:
--    Prefer soft-delete/anonymization for users/guests referenced by audit_logs.
-- ============================================================
