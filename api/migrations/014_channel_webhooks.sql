-- 014_channel_webhooks.sql
-- Plan 1.0: Per-bridged-channel Discord webhook credentials stored encrypted.
-- AES-256-GCM envelope (ciphertext + nonce + auth_tag + key_version), matching
-- guild_bot_credentials (012) shape for uniform future key rotation.
-- Strict 1:1 to guild_channels via UNIQUE(channel_id).
-- CASCADE on channel deletion — no dangling secrets when a channel is removed.
-- RESTRICT on user deletion — preserve audit trail of who provisioned the webhook.
CREATE TABLE channel_webhooks (
    id                   BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id            BIGINT       NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    channel_id           BIGINT       NOT NULL REFERENCES guild_channels(id) ON DELETE CASCADE,
    discord_webhook_id   BIGINT       NOT NULL,
    ciphertext           BYTEA        NOT NULL,
    nonce                BYTEA        NOT NULL,
    auth_tag             BYTEA        NOT NULL,
    key_version          INT          NOT NULL DEFAULT 1,
    created_by_user_id   BIGINT       NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_channel_webhooks_channel UNIQUE (channel_id)
);
