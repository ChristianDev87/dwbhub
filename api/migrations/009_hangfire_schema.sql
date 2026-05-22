-- DWBHUB-NO-TENANT-FILTER: Hangfire schema is system-wide infrastructure.
-- The hangfire.* tables (Job, Queue, State, Server, Set, List, Counter,
-- Hash, JobParameter) are auto-created by Hangfire itself on first start
-- via PrepareSchemaIfNecessary = true; this migration only ensures the
-- schema exists so Hangfire can put its tables there.
CREATE SCHEMA IF NOT EXISTS hangfire;
