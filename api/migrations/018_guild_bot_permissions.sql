-- Plan 1.1 — proactive bot-permission check at gateway-ready.
-- Tri-state: NULL = not yet checked, FALSE = lacks, TRUE = has MANAGE_MESSAGES.
ALTER TABLE guilds
    ADD COLUMN bot_can_manage_messages BOOLEAN NULL;

COMMENT ON COLUMN guilds.bot_can_manage_messages IS
    'Set on bot connect via Discord GET /guilds/{id}/members/@me. NULL = not yet checked.';
