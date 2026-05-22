-- auth_tokens: shared backing table for short-lived single-use tokens.
-- `purpose` discriminates between email-verification and password-reset.
-- `email` is denormalised so rate-limit COUNTs don't need a JOIN through users.
-- `consumed_at IS NOT NULL` means the token has been used or invalidated (one-shot).
CREATE TABLE auth_tokens (
    id                    BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id             BIGINT      NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    user_id               BIGINT      NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    email                 CITEXT      NOT NULL,
    purpose               TEXT        NOT NULL CHECK (purpose IN ('email_verify','password_reset')),
    token_hash            BYTEA       NOT NULL UNIQUE,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at            TIMESTAMPTZ NOT NULL,
    consumed_at           TIMESTAMPTZ NULL,
    ip_address            INET        NULL,
    user_agent            TEXT        NULL
);

CREATE INDEX ix_auth_tokens_email_recent
    ON auth_tokens (email, created_at DESC);

CREATE INDEX ix_auth_tokens_user_purpose_active
    ON auth_tokens (user_id, purpose)
    WHERE consumed_at IS NULL;
