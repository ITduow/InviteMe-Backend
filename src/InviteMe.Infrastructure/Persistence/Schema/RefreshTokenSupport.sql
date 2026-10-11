CREATE TABLE inviteme.refresh_tokens (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    token_hash char(64) NOT NULL,
    family_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT NOW(),
    expires_at timestamp with time zone NOT NULL,
    used_at timestamp with time zone,
    revoked_at timestamp with time zone,
    replaced_by_id uuid,
    CONSTRAINT refresh_tokens_pkey PRIMARY KEY (id),
    CONSTRAINT fk_refresh_tokens_user FOREIGN KEY (user_id) REFERENCES inviteme.users(id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX uq_refresh_tokens_token_hash ON inviteme.refresh_tokens(token_hash);
CREATE INDEX ix_refresh_tokens_user_id ON inviteme.refresh_tokens(user_id);
CREATE INDEX ix_refresh_tokens_family_id ON inviteme.refresh_tokens(family_id);
