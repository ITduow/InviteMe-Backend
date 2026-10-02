-- EF owns the transaction. Preserve the historical v1 migration and upgrade in place.
SET LOCAL search_path TO inviteme, pg_temp;
LOCK TABLE guest_groups, guests, guest_participants, walk_ins, invitations, rsvps, rsvp_history, tables, gifts, audit_logs IN ACCESS EXCLUSIVE MODE;

-- Refuse ambiguous legacy records instead of deleting or inventing business data.
DO $preflight$
BEGIN
    IF EXISTS (SELECT 1 FROM rsvps WHERE
        (status IN ('PENDING', 'DECLINED') AND party_size <> 0)
        OR (status = 'ATTENDING' AND party_size < 1)) THEN
        RAISE EXCEPTION 'Schema v2 preflight: RSVP status/party_size inconsistent; reconcile these records before retrying';
    END IF;
    IF EXISTS (SELECT 1 FROM gifts gift LEFT JOIN guest_participants p ON p.id = gift.participant_id
        LEFT JOIN guests g ON g.id = p.guest_id LEFT JOIN walk_ins w ON w.id = gift.walkin_id
        WHERE (gift.participant_id IS NULL AND gift.walkin_id IS NULL)
           OR (gift.participant_id IS NOT NULL AND (g.id IS NULL OR g.wedding_id <> gift.wedding_id))
           OR (gift.walkin_id IS NOT NULL AND (w.id IS NULL OR w.wedding_id <> gift.wedding_id))) THEN
        RAISE EXCEPTION 'Schema v2 preflight: gift has no valid Wedding Guest/walk-in source; reconcile before retrying';
    END IF;
END;
$preflight$;

ALTER TABLE guest_groups ADD COLUMN side VARCHAR(20) NULL,
    ADD CONSTRAINT ck_guest_groups_side CHECK (side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER'));
ALTER TABLE guests RENAME COLUMN status TO record_status;
ALTER TABLE guests RENAME CONSTRAINT ck_guests_status TO ck_guests_record_status;
ALTER TABLE guests ADD COLUMN expected_companion_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN notes TEXT NULL,
    ADD CONSTRAINT ck_guests_expected_companion_count CHECK (expected_companion_count >= 0);

ALTER TABLE invitations ADD COLUMN configuration JSONB NULL,
    ADD COLUMN previewed_at TIMESTAMPTZ NULL, ADD COLUMN approved_by UUID NULL,
    ADD COLUMN approved_at TIMESTAMPTZ NULL, ADD COLUMN published_at TIMESTAMPTZ NULL,
    ADD CONSTRAINT fk_invitations_approved_by FOREIGN KEY (approved_by) REFERENCES users(id) ON DELETE SET NULL;
ALTER TABLE invitations DROP CONSTRAINT ck_invitations_status;
-- READY did not record an approval. Require review rather than claiming approval occurred.
UPDATE invitations SET status = 'IN_REVIEW' WHERE status = 'READY';
ALTER TABLE invitations ADD CONSTRAINT ck_invitations_status CHECK
    (status IN ('DRAFT', 'IN_REVIEW', 'APPROVED', 'PUBLISHED', 'SENT', 'OPENED', 'REVOKED', 'EXPIRED'));

DROP VIEW v_guest_rsvp_summary;
ALTER TABLE rsvps RENAME COLUMN party_size TO confirmed_party_size;
ALTER TABLE rsvps RENAME CONSTRAINT ck_rsvps_party_size TO ck_rsvps_confirmed_party_size;
ALTER TABLE rsvps ADD CONSTRAINT ck_rsvps_status_party_size CHECK
    ((status IN ('PENDING', 'DECLINED') AND confirmed_party_size = 0)
     OR (status = 'ATTENDING' AND confirmed_party_size >= 1));
ALTER TABLE rsvp_history RENAME COLUMN old_party_size TO old_confirmed_party_size;
ALTER TABLE rsvp_history RENAME COLUMN new_party_size TO new_confirmed_party_size;
ALTER TABLE rsvp_history RENAME CONSTRAINT ck_rsvp_history_party_sizes TO ck_rsvp_history_confirmed_party_sizes;

DROP VIEW v_table_occupancy;
ALTER TABLE tables RENAME COLUMN table_name TO table_number;
ALTER TABLE tables RENAME CONSTRAINT uq_tables_name TO uq_tables_number;
ALTER TABLE tables ADD COLUMN status VARCHAR(20) NOT NULL DEFAULT 'PLANNED',
    ADD COLUMN activated_at TIMESTAMPTZ NULL,
    ADD CONSTRAINT ck_tables_status CHECK (status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE'));

-- Preserve the original donor participant ID independently from the v2 guest-level FK.
CREATE TABLE gift_participant_legacy (
    gift_id UUID PRIMARY KEY REFERENCES gifts(id) ON DELETE CASCADE,
    participant_id UUID NOT NULL,
    migrated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE gift_participant_legacy IS
'Upgrade provenance only: original v1 gift participant UUID retained after conversion to guest_id; not a v2 business entity.';
INSERT INTO gift_participant_legacy (gift_id, participant_id)
    SELECT id, participant_id FROM gifts WHERE participant_id IS NOT NULL;
DROP TRIGGER trg_validate_gift_scope ON gifts;
ALTER TABLE gifts ADD COLUMN guest_id UUID NULL;
UPDATE gifts gift SET guest_id = p.guest_id FROM guest_participants p WHERE p.id = gift.participant_id;
ALTER TABLE gifts DROP CONSTRAINT ck_gifts_source_not_both,
    DROP CONSTRAINT fk_gifts_participant, DROP CONSTRAINT fk_gifts_walkin;
ALTER TABLE gifts DROP COLUMN participant_id;
ALTER TABLE gifts ADD CONSTRAINT fk_gifts_guest FOREIGN KEY (guest_id) REFERENCES guests(id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_gifts_walkin FOREIGN KEY (walkin_id) REFERENCES walk_ins(id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_gifts_source_xor CHECK
        ((guest_id IS NOT NULL AND walkin_id IS NULL) OR (guest_id IS NULL AND walkin_id IS NOT NULL));
CREATE INDEX ix_gifts_guest_id ON gifts(guest_id);

ALTER TABLE audit_logs DROP CONSTRAINT ck_audit_logs_actor_type, DROP CONSTRAINT ck_audit_logs_actor_shape;
UPDATE audit_logs SET actor_type = 'WEDDING_GUEST' WHERE actor_type = 'GUEST';
ALTER TABLE audit_logs ADD CONSTRAINT ck_audit_logs_actor_type
        CHECK (actor_type IN ('USER', 'WEDDING_GUEST', 'SYSTEM', 'AI')),

    ADD CONSTRAINT ck_audit_logs_actor_shape
        CHECK (
            (
                actor_type = 'USER'
                AND actor_user_id IS NOT NULL
                AND actor_guest_id IS NULL
            )
            OR
            (
                actor_type = 'WEDDING_GUEST'
                AND actor_guest_id IS NOT NULL
                AND actor_user_id IS NULL
            )
            OR
            (
                actor_type IN ('SYSTEM', 'AI')
                AND actor_user_id IS NULL
                AND actor_guest_id IS NULL
            )
        );


CREATE OR REPLACE FUNCTION set_table_activated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.status = 'ACTIVE' AND NEW.activated_at IS NULL THEN
        IF TG_OP = 'INSERT' THEN
            NEW.activated_at := NOW();
        ELSIF OLD.status IS DISTINCT FROM 'ACTIVE' THEN
            NEW.activated_at := NOW();
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_tables_set_activated_at
BEFORE INSERT OR UPDATE OF status
ON tables
FOR EACH ROW
EXECUTE FUNCTION set_table_activated_at();

ALTER FUNCTION inviteme.set_table_activated_at() SET search_path = inviteme, pg_temp;

CREATE OR REPLACE FUNCTION validate_gift_scope()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_target_wedding UUID;
BEGIN
    IF NEW.guest_id IS NOT NULL THEN
        SELECT g.wedding_id
          INTO v_target_wedding
          FROM guests g
         WHERE g.id = NEW.guest_id;

        IF v_target_wedding IS NULL OR v_target_wedding <> NEW.wedding_id THEN
            RAISE EXCEPTION 'Gift Wedding Guest does not belong to wedding';
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
BEFORE INSERT OR UPDATE OF wedding_id, guest_id, walkin_id
ON gifts
FOR EACH ROW
EXECUTE FUNCTION validate_gift_scope();

ALTER FUNCTION inviteme.validate_gift_scope() SET search_path = inviteme, pg_temp;

CREATE OR REPLACE VIEW v_table_occupancy AS
SELECT
    t.id AS table_id,
    t.wedding_id,
    t.table_number,
    t.capacity,
    t.status,
    COUNT(sa.id) AS occupied,
    GREATEST(t.capacity - COUNT(sa.id), 0) AS remaining
FROM tables t
LEFT JOIN seating_assignments sa
  ON sa.table_id = t.id
GROUP BY t.id, t.wedding_id, t.table_number, t.capacity, t.status;

CREATE OR REPLACE VIEW v_guest_rsvp_summary AS
SELECT
    g.id AS guest_id,
    g.wedding_id,
    g.guest_code,
    g.full_name,
    (1 + g.expected_companion_count) AS estimated_party_size,
    i.id AS invitation_id,
    i.status AS invitation_status,
    r.id AS rsvp_id,
    r.status AS rsvp_status,
    r.confirmed_party_size,
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
    g.expected_companion_count,
    i.id,
    i.status,
    r.id,
    r.status,
    r.confirmed_party_size;

COMMENT ON TABLE guests IS
'Physical data entity for the Wedding Guest actor registered in a wedding guest list.';

COMMENT ON COLUMN guests.expected_companion_count IS
'Estimated number of accompanying people before RSVP; estimated party size = 1 + expected_companion_count.';

COMMENT ON COLUMN rsvps.confirmed_party_size IS
'Final party size confirmed through RSVP; distinct from the pre-RSVP estimated party size.';

COMMENT ON COLUMN tables.status IS
'Operational table lifecycle: PLANNED, ACTIVE, BACKUP, INACTIVE.';

COMMENT ON TABLE gifts IS
'Gift record independent from RSVP attendance; a Wedding Guest may send a gift even when RSVP is DECLINED.';
