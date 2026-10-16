-- RSVP-03 (Guest03): the RSVP form keeps one free-text note per guest in guest_notes (note_type = 'RSVP').
-- Applied by EF migration AddRsvpGuestNote. Additive only: no existing row uses note_type 'RSVP'.
SET LOCAL search_path TO inviteme, pg_temp;

CREATE UNIQUE INDEX uq_guest_notes_rsvp_note
    ON inviteme.guest_notes(guest_id)
    WHERE note_type = 'RSVP' AND participant_id IS NULL;
