-- DWBHUB-NO-TENANT-FILTER: singleton state row tracking incremental verify progress.
-- Allowlisted in tools/check-tenant-filter.ps1 in Task 13.
CREATE TABLE audit_verify_state (
    id                 SMALLINT    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    last_verified_id   BIGINT      NOT NULL DEFAULT 0,
    last_verified_hash BYTEA       NULL,
    last_run_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_run_status    TEXT        NOT NULL DEFAULT 'ok'
                                   CHECK (last_run_status IN ('ok','broken','running'))
);
INSERT INTO audit_verify_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
