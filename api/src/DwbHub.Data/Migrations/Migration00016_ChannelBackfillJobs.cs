using FluentMigrator;

namespace DwbHub.Data.Migrations;

/// <summary>
/// Creates the <c>channel_backfill_jobs</c> table for
/// Hangfire-tracked backfill state with snowflake cursor pagination.
/// </summary>
[Migration(16, "Create channel_backfill_jobs table (Hangfire-tracked backfill state with snowflake cursor, Plan 1.0)")]
public sealed class Migration00016_ChannelBackfillJobs : ForwardOnlyMigration
{
    public override void Up()
    {
        Execute.EmbeddedScript("016_channel_backfill_jobs.sql");
    }
}
