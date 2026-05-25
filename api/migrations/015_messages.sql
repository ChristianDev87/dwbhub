-- 015_messages.sql
-- Plan 1.0: Persisted Discord messages for bridged channels.
-- Snowflake unique-key (tenant_id, discord_message_id) prevents duplicate
-- inserts when backfill and live gateway events race (Plan 1.0 §3.2 idempotency).
-- soft-delete via deleted_at (nullable) — Discord deletes are not reversible but
-- we keep the row for audit and UI tombstone display.
-- sent_at is Discord-supplied timestamp, NOT server now() — critical for correct
-- chronological ordering (backfill inserts historical messages).
-- dwbhub_user_id uses SET NULL so messages survive user account deletion.
CREATE TABLE messages (
    id                     BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id              BIGINT       NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    channel_id             BIGINT       NOT NULL REFERENCES guild_channels(id) ON DELETE CASCADE,
    discord_message_id     BIGINT       NOT NULL,
    discord_author_id      BIGINT       NOT NULL,
    discord_author_name    TEXT         NOT NULL,
    via_dwbhub             BOOLEAN      NOT NULL DEFAULT false,
    dwbhub_user_id         BIGINT       REFERENCES users(id) ON DELETE SET NULL,
    content                TEXT         NOT NULL,
    sent_at                TIMESTAMPTZ  NOT NULL,
    edited_at              TIMESTAMPTZ,
    deleted_at             TIMESTAMPTZ,
    created_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at             TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_messages_discord UNIQUE (tenant_id, discord_message_id)
);

CREATE INDEX ix_messages_channel_time
    ON messages (channel_id, sent_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX ix_messages_via_dwbhub
    ON messages (tenant_id, via_dwbhub)
    WHERE via_dwbhub = true;
