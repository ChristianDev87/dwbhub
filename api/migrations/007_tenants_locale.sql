-- Adds the per-tenant default email language. Existing rows get 'de' via the
-- column default. CHECK constraint mirrors the shape of auth_tokens.purpose
-- (Plan 0.3c). Plan 0.3d's setup wizard sets this for the first tenant; the
-- email-rendering paths in Plan 0.3c continue to read locale from the request
-- Accept-Language header for now (tenant.locale becomes the fallback later).
ALTER TABLE tenants
    ADD COLUMN locale TEXT NOT NULL DEFAULT 'de'
        CHECK (locale IN ('de','en'));
