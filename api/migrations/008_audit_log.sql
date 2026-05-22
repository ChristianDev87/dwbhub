-- DWBHUB-NO-TENANT-FILTER: audit_log spans tenants by design (system events
-- like "tenant.created" or "setup.completed" have no tenant context).
-- tenant_id IS NULLABLE — see spec §3.2. Allowlisted in
-- tools/check-tenant-filter.ps1 ($tableAllowlist) in Task 13.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE audit_log (
    id              BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       BIGINT      NULL REFERENCES tenants(id) ON DELETE SET NULL,
    actor_user_id   BIGINT      NULL REFERENCES users(id)   ON DELETE SET NULL,
    event_type      TEXT        NOT NULL,
    payload_json    JSONB       NOT NULL DEFAULT '{}'::jsonb,
    ip_address      INET        NULL,
    user_agent      TEXT        NULL,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    prev_hash       BYTEA       NULL,
    current_hash    BYTEA       NOT NULL UNIQUE
);
CREATE INDEX ix_audit_log_tenant_time ON audit_log (tenant_id, occurred_at DESC);
CREATE INDEX ix_audit_log_actor_time  ON audit_log (actor_user_id, occurred_at DESC);
CREATE INDEX ix_audit_log_event_time  ON audit_log (event_type, occurred_at DESC);

-- Append-only table tuning: aggressive autovacuum keeps the PK index lean
-- (the audit-write CTE uses `ORDER BY id DESC LIMIT 1` on every insert).
ALTER TABLE audit_log SET (
    fillfactor = 100,
    autovacuum_vacuum_scale_factor = 0.05,
    autovacuum_analyze_scale_factor = 0.02
);
