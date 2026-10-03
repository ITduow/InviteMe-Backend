-- Feature extension; the frozen v2 schema is unchanged.
-- Ambiguous legacy event data requires reconciliation instead of guessing the main event.
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM inviteme.wedding_events GROUP BY wedding_id HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Workspace adoption requires choosing the main event for weddings with multiple events';
    END IF;
END $$;
ALTER TABLE inviteme.weddings ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE inviteme.wedding_events ADD COLUMN is_main BOOLEAN NOT NULL DEFAULT FALSE;
UPDATE inviteme.wedding_events SET is_main = TRUE;
CREATE UNIQUE INDEX uq_wedding_main_event ON inviteme.wedding_events(wedding_id) WHERE is_main;
