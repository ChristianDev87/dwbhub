-- 016_channel_backfill_jobs.sql
-- Plan 1.0: Hangfire-tracked backfill state for channel history pagination.
-- One active job per channel via UNIQUE(channel_id).
-- oldest_fetched_snowflake is the pagination cursor for Discord's `before=` param.
-- hangfire_job_id is populated after Hangfire enqueue (nullable at insert time).
-- status CHECK mirrors the BackfillStatus enum in DwbHub.Core.
CREATE TABLE channel_backfill_jobs (
    id                            BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id                     BIGINT       NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    channel_id                    BIGINT       NOT NULL REFERENCES guild_channels(id) ON DELETE CASCADE,
    status                        TEXT         NOT NULL DEFAULT 'pending',
    started_at                    TIMESTAMPTZ,
    completed_at                  TIMESTAMPTZ,
    fetched_count                 INT          NOT NULL DEFAULT 0,
    oldest_fetched_snowflake      BIGINT,
    last_error                    TEXT,
    hangfire_job_id               TEXT,
    created_at                    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at                    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_backfill_jobs_channel UNIQUE (channel_id),
    CONSTRAINT chk_backfill_jobs_status
        CHECK (status IN ('pending', 'running', 'complete', 'failed', 'cancelled'))
);

CREATE INDEX ix_backfill_jobs_active
    ON channel_backfill_jobs (status)
    WHERE status IN ('pending', 'running');
