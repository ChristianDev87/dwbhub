-- 013_guild_channels.sql
-- Plan 1.0: Discord channels known per guild, with bridge-toggle state.
-- Mixed-ID pattern: internal BIGINT id for FK joins, external UUID public_id
-- for URL exposure (non-enumerable), matching guilds (011) convention.
-- CASCADE from both tenants and guilds — removing a guild removes its channels.
CREATE TABLE guild_channels (
    id                   BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id            BIGINT       NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    guild_id             BIGINT       NOT NULL REFERENCES guilds(id) ON DELETE CASCADE,
    public_id            UUID         NOT NULL DEFAULT gen_random_uuid(),
    discord_channel_id   BIGINT       NOT NULL,
    name                 TEXT         NOT NULL,
    channel_type         SMALLINT     NOT NULL,
    position             INT          NOT NULL,
    is_bridged           BOOLEAN      NOT NULL DEFAULT false,
    bridged_at           TIMESTAMPTZ,
    last_synced_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_guild_channels_discord UNIQUE (tenant_id, discord_channel_id),
    CONSTRAINT uq_guild_channels_public_id UNIQUE (public_id)
);

CREATE INDEX ix_guild_channels_guild ON guild_channels (tenant_id, guild_id);
CREATE INDEX ix_guild_channels_bridged ON guild_channels (tenant_id, is_bridged) WHERE is_bridged = true;
