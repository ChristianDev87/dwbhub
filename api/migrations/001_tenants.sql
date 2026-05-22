-- tenants: the root of every multi-tenant query.
-- Every other table in Phase 0 takes `tenant_id BIGINT NOT NULL REFERENCES tenants(id)`.
-- This table itself is the only one allowed by tools/check-tenant-filter.ps1
-- to lack a tenant_id column (see allowlist in the lint script).
CREATE TABLE tenants (
    id          BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        TEXT        NOT NULL,
    slug        CITEXT      NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
