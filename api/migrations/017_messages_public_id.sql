-- 017_messages_public_id.sql
-- Add a stable UUID public identifier to the messages table so message
-- edit/delete REST endpoints can accept a non-enumerable external key
-- without exposing the internal BIGINT PK.
-- Back-fills existing rows with random UUIDs; new rows get gen_random_uuid().
ALTER TABLE messages
    ADD COLUMN public_id UUID NOT NULL DEFAULT gen_random_uuid();

-- Unique constraint mirrors the pattern used in guild_channels.
ALTER TABLE messages
    ADD CONSTRAINT uq_messages_public_id UNIQUE (public_id);

-- Partial index for the PATCH/DELETE route lookup
-- (tenant_id is always in scope via channel join; channel_id + public_id is the hot path).
CREATE INDEX ix_messages_public_id
    ON messages (public_id)
    WHERE deleted_at IS NULL;
