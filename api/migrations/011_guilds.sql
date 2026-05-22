-- 011_guilds.sql
-- Multi-Guild schema (Plan 0.6). Mixed-ID pattern: internal BIGINT id for FK
-- joins, external UUID public_id for URL exposure (non-enumerable).
-- gen_random_uuid() is available via the pgcrypto extension enabled in 000_extensions.sql.
CREATE TABLE guilds (
    id                      BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    public_id               UUID         NOT NULL UNIQUE DEFAULT gen_random_uuid(),
    tenant_id               BIGINT       NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    discord_guild_id        TEXT         NOT NULL,
    display_name            TEXT         NOT NULL,
    is_active               BOOLEAN      NOT NULL DEFAULT true,
    registered_by_user_id   BIGINT       NOT NULL REFERENCES users(id),
    registered_at           TIMESTAMPTZ  NOT NULL DEFAULT now(),
    last_connected_at       TIMESTAMPTZ  NULL,
    created_at              TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ  NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, discord_guild_id),
    CHECK (length(discord_guild_id) BETWEEN 17 AND 20),
    CHECK (length(display_name) BETWEEN 1 AND 100)
);

CREATE INDEX ix_guilds_public_id ON guilds (public_id);
CREATE INDEX ix_guilds_tenant_id ON guilds (tenant_id);
