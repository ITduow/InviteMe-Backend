-- Additive support for template-based guest imports after InvitationLifecycleSupport.sql.
SET search_path TO inviteme, public;
UPDATE guests SET
    notes = concat_ws(E'\n', NULLIF(notes, ''), 'Quan hệ trước chuyển đổi: ' || relationship),
    relationship = NULL
WHERE relationship IS NOT NULL
  AND relationship NOT IN ('FAMILY', 'FRIEND', 'COLLEAGUE', 'NEIGHBOR', 'PARTNER', 'OTHER');
ALTER TABLE guests ALTER COLUMN side DROP NOT NULL;
ALTER TABLE guests ALTER COLUMN side DROP DEFAULT;
ALTER TABLE guests ADD CONSTRAINT ck_guests_relationship
    CHECK (relationship IS NULL OR relationship IN ('FAMILY', 'FRIEND', 'COLLEAGUE', 'NEIGHBOR', 'PARTNER', 'OTHER'));
CREATE TABLE guest_import_sessions (
    id uuid NOT NULL,
    wedding_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    preview_json jsonb NOT NULL,
    result_json jsonb,
    selection_json jsonb,
    CONSTRAINT "PK_guest_import_sessions" PRIMARY KEY (id),
    CONSTRAINT "FK_guest_import_sessions_weddings_wedding_id" FOREIGN KEY (wedding_id)
        REFERENCES weddings(id) ON DELETE CASCADE
);
CREATE INDEX "IX_guest_import_sessions_expires_at" ON guest_import_sessions(expires_at);
