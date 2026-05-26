-- Plan 1.1 — per-tenant edit-window for messages.
-- NULL = unlimited; otherwise seconds (1 min to 1 year).
ALTER TABLE tenants
    ADD COLUMN message_edit_window_seconds INTEGER NULL
    CHECK (message_edit_window_seconds IS NULL OR (message_edit_window_seconds BETWEEN 60 AND 31536000));

COMMENT ON COLUMN tenants.message_edit_window_seconds IS
    'Maximum age of a message in seconds before edit is no longer allowed. NULL = unlimited.';
