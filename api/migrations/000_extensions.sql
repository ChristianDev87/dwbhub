-- Enable required PostgreSQL extensions.
-- citext supports case-insensitive UNIQUE indexes on tenant slugs (Plan 0.2)
-- and will later cover user emails (Plan 0.3) without an explicit functional index.
CREATE EXTENSION IF NOT EXISTS citext;
