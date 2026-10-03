ALTER TABLE inviteme.invitations
    ADD COLUMN version integer NOT NULL DEFAULT 1,
    ADD COLUMN published_configuration jsonb,
    ADD COLUMN protected_token text,
    ADD COLUMN reviewed_at timestamp with time zone,
    ADD COLUMN opened_at timestamp with time zone;
ALTER TABLE inviteme.invitation_deliveries
    ADD COLUMN request_key varchar(32),
    ADD COLUMN is_sandbox boolean NOT NULL DEFAULT false,
    ADD COLUMN publication_hash char(64),
    ADD COLUMN lease_id uuid,
    ADD COLUMN lease_until timestamp with time zone,
    ADD COLUMN attempt_count integer NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX uq_invitation_delivery_request ON inviteme.invitation_deliveries(invitation_id,request_key) WHERE request_key IS NOT NULL;

-- No legacy invitation is automatically published and no legacy delivery is dispatched.
-- A new built-in template is added only if the slug is unused.
INSERT INTO inviteme.templates(name,slug,theme,configuration,status)
VALUES ('InviteMe Classic','inviteme-classic','classic','{"schemaVersion":1,"presetId":"classic"}'::jsonb,'ACTIVE')
ON CONFLICT (slug) DO NOTHING;
