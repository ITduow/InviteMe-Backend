-- RSVP-02: RSVP submit/update with an append-only rsvp_history.
-- Applied by EF migration AddRsvpImmutableHistory inside its transaction.
-- Additive and data-preserving: existing history rows are backfilled, never rewritten afterwards.
SET LOCAL search_path TO inviteme, pg_temp;

-- ============================================================
-- 1. Actor identity: a Wedding-Guest answers through an invitation token and has no users row,
--    so changed_by (users.id) alone loses who changed the RSVP. Mirror audit_logs' actor shape.
-- ============================================================

ALTER TABLE inviteme.rsvp_history
    ADD COLUMN actor_type     VARCHAR(20) NULL,
    ADD COLUMN actor_guest_id UUID        NULL;

-- Backfill. Every application path that wrote history with changed_by = NULL was the public
-- invitation-token submission, whose actor is the invitation's own Wedding-Guest.
UPDATE inviteme.rsvp_history h
   SET actor_type = 'USER'
 WHERE h.changed_by IS NOT NULL;

UPDATE inviteme.rsvp_history h
   SET actor_type = 'WEDDING_GUEST',
       actor_guest_id = i.guest_id
  FROM inviteme.rsvps r
  JOIN inviteme.invitations i ON i.id = r.invitation_id
 WHERE h.rsvp_id = r.id
   AND h.changed_by IS NULL;

ALTER TABLE inviteme.rsvp_history
    ALTER COLUMN actor_type SET NOT NULL,
    ADD CONSTRAINT ck_rsvp_history_actor_type
        CHECK (actor_type IN ('USER', 'WEDDING_GUEST', 'SYSTEM')),
    ADD CONSTRAINT ck_rsvp_history_actor_shape
        CHECK (
            (actor_type = 'USER' AND changed_by IS NOT NULL AND actor_guest_id IS NULL)
            OR (actor_type = 'WEDDING_GUEST' AND actor_guest_id IS NOT NULL AND changed_by IS NULL)
            OR (actor_type = 'SYSTEM' AND changed_by IS NULL AND actor_guest_id IS NULL)
        ),
    ADD CONSTRAINT fk_rsvp_history_actor_guest
        FOREIGN KEY (actor_guest_id) REFERENCES inviteme.guests(id) ON DELETE RESTRICT;

-- ============================================================
-- 2. History rows must never be rewritten or removed by referential actions.
--    SET NULL on changed_by would UPDATE history and break the actor shape; CASCADE from rsvps
--    would silently delete evidence. Both become RESTRICT (same retention policy as audit_logs).
-- ============================================================

ALTER TABLE inviteme.rsvp_history
    DROP CONSTRAINT fk_rsvp_history_changed_by,
    ADD CONSTRAINT fk_rsvp_history_changed_by
        FOREIGN KEY (changed_by) REFERENCES inviteme.users(id) ON DELETE RESTRICT,
    DROP CONSTRAINT fk_rsvp_history_rsvp,
    ADD CONSTRAINT fk_rsvp_history_rsvp
        FOREIGN KEY (rsvp_id) REFERENCES inviteme.rsvps(id) ON DELETE RESTRICT;

-- ============================================================
-- 3. Append-only guard: INSERT only. UPDATE, DELETE and TRUNCATE raise a clear error.
-- ============================================================

CREATE FUNCTION inviteme.rsvp_history_append_only()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'rsvp_history is append-only: % is not allowed', TG_OP
        USING ERRCODE = 'restrict_violation',
              HINT = 'Record a new RSVP change instead of editing history.';
END;
$$;

CREATE TRIGGER trg_rsvp_history_append_only
    BEFORE UPDATE OR DELETE ON inviteme.rsvp_history
    FOR EACH ROW EXECUTE FUNCTION inviteme.rsvp_history_append_only();

CREATE TRIGGER trg_rsvp_history_no_truncate
    BEFORE TRUNCATE ON inviteme.rsvp_history
    FOR EACH STATEMENT EXECUTE FUNCTION inviteme.rsvp_history_append_only();

-- ============================================================
-- 4. Ordered per-RSVP history reads. The composite index supersedes the single-column one.
-- ============================================================

DROP INDEX inviteme.ix_rsvp_history_rsvp_id;
CREATE INDEX ix_rsvp_history_rsvp_changed_at ON inviteme.rsvp_history(rsvp_id, changed_at);

COMMENT ON TABLE inviteme.rsvp_history IS
'Append-only RSVP change log (RSVP-02). One row per real change, written in the same transaction as the rsvps update.';
