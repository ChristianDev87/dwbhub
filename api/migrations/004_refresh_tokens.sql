-- refresh_tokens: rotation chain backing the dwbhub_refresh httpOnly cookie.
-- Each row stores a SHA-256 hash of the plaintext (never the plaintext itself)
-- plus a snapshot of the user's role + is_active at issue time. On every refresh
-- we compare the snapshot to the user's current state; if anything changed we
-- revoke and force a re-login. replaced_by_token_id is a forward pointer that
-- builds a chain of rotated tokens — used by theft detection to revoke the
-- whole chain on reuse of an already-revoked token.
CREATE TABLE refresh_tokens (
    id                    BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id             BIGINT      NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    user_id               BIGINT      NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    token_hash            BYTEA       NOT NULL UNIQUE,
    issued_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at            TIMESTAMPTZ NOT NULL,
    revoked_at            TIMESTAMPTZ NULL,
    issued_role           TEXT        NOT NULL CHECK (issued_role IN ('Owner','Admin','Moderator','Member','Guest')),
    issued_was_active     BOOLEAN     NOT NULL,
    replaced_by_token_id  BIGINT      NULL REFERENCES refresh_tokens(id),
    user_agent            TEXT        NULL,
    ip_address            INET        NULL
);
CREATE INDEX ix_refresh_tokens_user_active
    ON refresh_tokens (tenant_id, user_id)
    WHERE revoked_at IS NULL;
