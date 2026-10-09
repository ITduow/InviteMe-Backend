-- Seating table lifecycle (SEAT-01) and check-in / walk-in workflows (CHK-01).
-- Builds on the PR #4 seating/reception workflow: only ACTIVE tables seat people (GOV-01 R1-08).
-- Applied by EF migration AddSeatingCheckInWorkflows inside its transaction.
SET LOCAL search_path TO inviteme, pg_temp;

-- Accent-insensitive guest lookup at the entrance ("nguyen van a" matches "Nguyễn Văn A").
CREATE EXTENSION IF NOT EXISTS unaccent WITH SCHEMA public;

-- ============================================================
-- 1. Tables: fixed origin (booked vs backup) and status history
-- ============================================================

ALTER TABLE inviteme.tables
    ADD COLUMN table_kind VARCHAR(20) NOT NULL DEFAULT 'PRIMARY';

-- Tables created as BACKUP before kinds existed are spare tables.
UPDATE inviteme.tables SET table_kind = 'BACKUP' WHERE status = 'BACKUP';

ALTER TABLE inviteme.tables
    ADD CONSTRAINT ck_tables_kind
        CHECK (table_kind IN ('PRIMARY', 'BACKUP')),
    ADD CONSTRAINT ck_tables_kind_status
        CHECK (
            (table_kind = 'PRIMARY' AND status IN ('PLANNED', 'ACTIVE', 'INACTIVE'))
            OR
            (table_kind = 'BACKUP' AND status IN ('BACKUP', 'ACTIVE', 'INACTIVE'))
        );

ALTER TABLE inviteme.wedding_settings
    ADD COLUMN booked_table_count     INTEGER  NULL,
    ADD COLUMN backup_table_limit     INTEGER  NOT NULL DEFAULT 0,
    ADD COLUMN default_table_capacity SMALLINT NOT NULL DEFAULT 10,
    -- FALSE (default): walk-ins and on-site overrides are admitted with an over-capacity warning.
    ADD COLUMN block_arrivals_over_capacity BOOLEAN NOT NULL DEFAULT FALSE,
    ADD CONSTRAINT ck_wedding_settings_booked_table_count
        CHECK (booked_table_count IS NULL OR booked_table_count > 0),
    ADD CONSTRAINT ck_wedding_settings_backup_table_limit
        CHECK (backup_table_limit >= 0),
    ADD CONSTRAINT ck_wedding_settings_default_table_capacity
        CHECK (default_table_capacity BETWEEN 1 AND 20);

CREATE TABLE inviteme.table_status_history (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wedding_id        UUID NOT NULL,
    table_id          UUID NOT NULL,
    from_status       VARCHAR(20) NULL,
    to_status         VARCHAR(20) NOT NULL,
    reason_code       VARCHAR(30) NULL,
    note              TEXT NULL,
    overflow_snapshot JSONB NULL,
    changed_by        UUID NULL,
    changed_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_table_status_history_wedding FOREIGN KEY (wedding_id)
        REFERENCES inviteme.weddings(id) ON DELETE CASCADE,
    CONSTRAINT fk_table_status_history_table FOREIGN KEY (table_id)
        REFERENCES inviteme.tables(id) ON DELETE CASCADE,
    CONSTRAINT fk_table_status_history_changed_by FOREIGN KEY (changed_by)
        REFERENCES inviteme.users(id) ON DELETE SET NULL,
    CONSTRAINT ck_table_status_history_to_status
        CHECK (to_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')),
    CONSTRAINT ck_table_status_history_from_status
        CHECK (from_status IS NULL OR from_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')),
    CONSTRAINT ck_table_status_history_reason_code
        CHECK (reason_code IS NULL OR reason_code IN (
            'EXTRA_COMPANIONS', 'WAITLIST_PROMOTION', 'LATE_RSVP_CHANGE',
            'WALK_IN', 'PLANNING_SHORTFALL', 'OTHER')),
    CONSTRAINT ck_table_status_history_backup_activation_reason
        CHECK (NOT (from_status = 'BACKUP' AND to_status = 'ACTIVE') OR reason_code IS NOT NULL),
    CONSTRAINT ck_table_status_history_other_note
        CHECK (reason_code IS DISTINCT FROM 'OTHER' OR note IS NOT NULL)
);

CREATE INDEX ix_table_status_history_wedding_changed_at
    ON inviteme.table_status_history (wedding_id, changed_at);
CREATE INDEX ix_table_status_history_table_id
    ON inviteme.table_status_history (table_id);

-- ============================================================
-- 2. Check-ins: method, on-site override, voiding
-- ============================================================

ALTER TABLE inviteme.check_ins
    ADD COLUMN method                 VARCHAR(20) NOT NULL DEFAULT 'QR',
    ADD COLUMN onsite_override_reason VARCHAR(30) NULL,
    ADD COLUMN voided_by              UUID NULL,
    ADD COLUMN voided_at              TIMESTAMPTZ NULL,
    ADD COLUMN void_reason            TEXT NULL;

-- Existing rows predate method/void tracking; record what is known before constraining.
UPDATE inviteme.check_ins SET method = 'WALK_IN' WHERE walkin_id IS NOT NULL;
UPDATE inviteme.check_ins
   SET voided_at = created_at, void_reason = 'Voided before void tracking was introduced'
 WHERE status = 'VOID';

ALTER TABLE inviteme.check_ins
    ADD CONSTRAINT fk_checkins_voided_by FOREIGN KEY (voided_by)
        REFERENCES inviteme.users(id) ON DELETE SET NULL,
    ADD CONSTRAINT ck_checkins_method
        CHECK (method IN ('QR', 'MANUAL', 'WALK_IN')),
    ADD CONSTRAINT ck_checkins_method_target
        CHECK ((method = 'WALK_IN') = (walkin_id IS NOT NULL)),
    ADD CONSTRAINT ck_checkins_override_reason
        CHECK (onsite_override_reason IS NULL OR onsite_override_reason IN (
            'DECLINED_ARRIVED', 'PENDING_ARRIVED', 'WAITLISTED_ARRIVED')),
    ADD CONSTRAINT ck_checkins_void_shape
        CHECK (
            (status = 'CHECKED_IN' AND voided_at IS NULL AND voided_by IS NULL AND void_reason IS NULL)
            OR
            (status = 'VOID' AND voided_at IS NOT NULL AND void_reason IS NOT NULL)
        );

-- Only an effective check-in is unique, so a voided mistake can be checked in again.
DROP INDEX inviteme.uq_checkins_participant;
CREATE UNIQUE INDEX uq_checkins_participant
    ON inviteme.check_ins (wedding_id, participant_id)
    WHERE participant_id IS NOT NULL AND status = 'CHECKED_IN';

DROP INDEX inviteme.uq_checkins_walkin;
CREATE UNIQUE INDEX uq_checkins_walkin
    ON inviteme.check_ins (wedding_id, walkin_id)
    WHERE walkin_id IS NOT NULL AND status = 'CHECKED_IN';

-- ============================================================
-- 3. Walk-ins: side, related invited guest, assigned table
-- ============================================================

ALTER TABLE inviteme.walk_ins
    ADD COLUMN side             VARCHAR(20) NULL,
    ADD COLUMN related_guest_id UUID NULL,
    ADD COLUMN table_id         UUID NULL,
    ADD CONSTRAINT fk_walkins_related_guest FOREIGN KEY (related_guest_id)
        REFERENCES inviteme.guests(id) ON DELETE SET NULL,
    ADD CONSTRAINT fk_walkins_table FOREIGN KEY (table_id)
        REFERENCES inviteme.tables(id) ON DELETE SET NULL,
    ADD CONSTRAINT ck_walkins_side
        CHECK (side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')),
    ADD CONSTRAINT ck_walkins_full_name_not_blank
        CHECK (btrim(full_name) <> '');

CREATE INDEX ix_walkins_table_id ON inviteme.walk_ins (table_id);

-- ============================================================
-- 4. Shared table occupancy: seated participants + assigned walk-ins
-- ============================================================

CREATE OR REPLACE FUNCTION inviteme.table_occupied_seats(
    p_table_id           UUID,
    p_exclude_assignment UUID DEFAULT NULL,
    p_exclude_walkin     UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE sql
STABLE
SET search_path = inviteme, pg_temp
AS $$
    SELECT (SELECT COUNT(*)
              FROM seating_assignments sa
             WHERE sa.table_id = p_table_id
               AND sa.id IS DISTINCT FROM p_exclude_assignment)::INTEGER
         + (SELECT COALESCE(SUM(w.party_size), 0)
              FROM walk_ins w
             WHERE w.table_id = p_table_id
               AND w.id IS DISTINCT FROM p_exclude_walkin)::INTEGER;
$$;

CREATE OR REPLACE FUNCTION inviteme.validate_seating_assignment()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = inviteme, pg_temp
AS $$
DECLARE
    v_table_capacity      INTEGER;
    v_table_wedding_id    UUID;
    v_table_status        VARCHAR(20);
    v_participant_wedding UUID;
    v_attendance_status   VARCHAR(20);
    v_seat_table_id       UUID;
BEGIN
    SELECT t.capacity, t.wedding_id, t.status
      INTO v_table_capacity, v_table_wedding_id, v_table_status
      FROM tables t
     WHERE t.id = NEW.table_id
     FOR UPDATE;

    IF v_table_wedding_id IS NULL THEN
        RAISE EXCEPTION 'Table % does not exist', NEW.table_id;
    END IF;
    IF v_table_wedding_id <> NEW.wedding_id THEN
        RAISE EXCEPTION 'Table does not belong to wedding';
    END IF;
    IF v_table_status <> 'ACTIVE' THEN
        RAISE EXCEPTION 'TABLE_NOT_SEATABLE: table % is %', NEW.table_id, v_table_status;
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
        SELECT s.table_id INTO v_seat_table_id FROM seats s WHERE s.id = NEW.seat_id;
        IF v_seat_table_id IS NULL OR v_seat_table_id <> NEW.table_id THEN
            RAISE EXCEPTION 'Seat does not belong to selected table';
        END IF;
    END IF;

    IF table_occupied_seats(
           NEW.table_id,
           CASE WHEN TG_OP = 'UPDATE' THEN OLD.id END,
           NULL) >= v_table_capacity THEN
        RAISE EXCEPTION 'Table capacity exceeded';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION inviteme.validate_walkin()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = inviteme, pg_temp
AS $$
DECLARE
    v_wedding  UUID;
    v_capacity INTEGER;
    v_status   VARCHAR(20);
BEGIN
    IF NEW.related_guest_id IS NOT NULL THEN
        SELECT wedding_id INTO v_wedding FROM guests WHERE id = NEW.related_guest_id;
        IF v_wedding IS DISTINCT FROM NEW.wedding_id THEN
            RAISE EXCEPTION 'Related guest does not belong to wedding';
        END IF;
    END IF;

    IF NEW.table_id IS NOT NULL THEN
        SELECT wedding_id, capacity, status
          INTO v_wedding, v_capacity, v_status
          FROM tables
         WHERE id = NEW.table_id
         FOR UPDATE;

        IF v_wedding IS DISTINCT FROM NEW.wedding_id THEN
            RAISE EXCEPTION 'Table does not belong to wedding';
        END IF;
        IF v_status <> 'ACTIVE' THEN
            RAISE EXCEPTION 'TABLE_NOT_SEATABLE: table % is %', NEW.table_id, v_status;
        END IF;
        IF table_occupied_seats(
               NEW.table_id,
               NULL,
               CASE WHEN TG_OP = 'UPDATE' THEN OLD.id END) + NEW.party_size > v_capacity THEN
            RAISE EXCEPTION 'Table capacity exceeded';
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_walkin
BEFORE INSERT OR UPDATE OF wedding_id, related_guest_id, table_id, party_size
ON inviteme.walk_ins
FOR EACH ROW
EXECUTE FUNCTION inviteme.validate_walkin();

CREATE OR REPLACE VIEW inviteme.v_table_occupancy AS
SELECT
    t.id AS table_id,
    t.wedding_id,
    t.table_number,
    t.capacity,
    t.status,
    inviteme.table_occupied_seats(t.id)::BIGINT AS occupied,
    GREATEST(t.capacity - inviteme.table_occupied_seats(t.id), 0)::BIGINT AS remaining
FROM inviteme.tables t;
