-- Per-tenant override for the outbound-message edit window.
-- NULL means "inherit system default" (currently 10 minutes, see MessageService.EditWindow).
-- A positive value overrides the default; range is 60 seconds to 1 year.
ALTER TABLE tenants
    ADD COLUMN message_edit_window_seconds INTEGER NULL
    CHECK (message_edit_window_seconds IS NULL OR (message_edit_window_seconds BETWEEN 60 AND 31536000));

COMMENT ON COLUMN tenants.message_edit_window_seconds IS
    'Per-tenant override for the outbound-message edit window in seconds. NULL = use system default. Configurable by Owner via PATCH /api/t/{slug}/settings.';
