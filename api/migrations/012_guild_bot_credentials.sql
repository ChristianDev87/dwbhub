-- 012_guild_bot_credentials.sql
-- Plan 0.7: AES-256-GCM at-rest encryption of Discord bot tokens.
--   - nonce: 12 bytes (96-bit IV, GCM standard)
--   - ciphertext: variable (= plaintext length, no padding in GCM)
--   - tag: 16 bytes (128-bit auth tag)
-- Strict 1:1 to guilds via UNIQUE(guild_id). CASCADE on guild deletion
-- ensures no dangling encrypted secrets remain when a guild is removed.
-- Encryption happens entirely in the C# layer; Postgres sees opaque BYTEA.

CREATE TABLE guild_bot_credentials (
    id           BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    guild_id     BIGINT      NOT NULL UNIQUE REFERENCES guilds(id) ON DELETE CASCADE,
    tenant_id    BIGINT      NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    nonce        BYTEA       NOT NULL,
    ciphertext   BYTEA       NOT NULL,
    tag          BYTEA       NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CHECK (octet_length(nonce) = 12),
    CHECK (octet_length(tag) = 16),
    CHECK (octet_length(ciphertext) BETWEEN 50 AND 200)
);

CREATE INDEX ix_guild_bot_credentials_tenant_id ON guild_bot_credentials (tenant_id);
-- UNIQUE(guild_id) auto-creates an index — no separate ix_guild_id needed.
