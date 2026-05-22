-- DWBHUB-NO-TENANT-FILTER: system-wide rate-limit table; allowlisted in
-- tools/check-tenant-filter.ps1 ($tableAllowlist) because rate-limits apply
-- across tenants — an attacker iterating email guesses doesn't get a fresh
-- window just by trying a different tenant slug.
CREATE TABLE login_attempt_log (
    id            BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email         CITEXT      NOT NULL,
    ip_address    INET        NOT NULL,
    success       BOOLEAN     NOT NULL,
    attempted_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_login_attempt_log_lookup
    ON login_attempt_log (email, ip_address, attempted_at DESC);
