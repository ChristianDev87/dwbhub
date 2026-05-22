-- users: per-tenant identity. Every login resolves a (tenant_id, email) pair.
-- The partial UNIQUE index enforces "exactly one Owner per tenant" — protecting
-- the bootstrap invariant (Plan 0.3d will rely on this when seeding the first owner).
CREATE TABLE users (
    id                BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id         BIGINT      NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    email             CITEXT      NOT NULL,
    email_verified_at TIMESTAMPTZ NULL,
    password_hash     TEXT        NOT NULL,
    display_name      TEXT        NOT NULL,
    role              TEXT        NOT NULL CHECK (role IN ('Owner','Admin','Moderator','Member','Guest')),
    is_active         BOOLEAN     NOT NULL DEFAULT true,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, email)
);

CREATE UNIQUE INDEX ux_users_one_owner_per_tenant
    ON users (tenant_id) WHERE role = 'Owner';
