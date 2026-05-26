using Dapper;
using DwbHub.Core.Messaging;
using DwbHub.Core.Repositories;

namespace DwbHub.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IChannelBackfillJobRepository"/>.
/// Every mutating statement includes <c>AND tenant_id = @TenantId</c> so
/// cross-tenant operations are rejected at the data layer (defense-in-depth).
/// Status is persisted as its lower-cased enum name to match the CHECK constraint
/// in migration 016.
/// </summary>
public sealed class ChannelBackfillJobRepository(IDbConnectionFactory connectionFactory)
    : IChannelBackfillJobRepository
{
    // Maps the TEXT column back to the BackfillStatus enum.
    private static BackfillStatus ParseStatus(string s) => s switch
    {
        "pending" => BackfillStatus.Pending,
        "running" => BackfillStatus.Running,
        "complete" => BackfillStatus.Complete,
        "failed" => BackfillStatus.Failed,
        "cancelled" => BackfillStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown backfill status: '{s}'"),
    };

    private const string SelectColumns = """
        id, tenant_id, channel_id, status, started_at, completed_at,
        fetched_count, oldest_fetched_snowflake, last_error, hangfire_job_id,
        created_at, updated_at
        """;

    public async Task<ChannelBackfillJob?> GetByChannelAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = $"""
            SELECT {SelectColumns}
            FROM channel_backfill_jobs
            WHERE tenant_id  = @TenantId
              AND channel_id = @ChannelId;
            """;
        var row = await conn.QuerySingleOrDefaultAsync<BackfillJobRow>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, ChannelId = channelId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return row is null ? null : MapRow(row);
    }

    public async Task<ChannelBackfillJob?> GetByIdAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = $"""
            SELECT {SelectColumns}
            FROM channel_backfill_jobs
            WHERE tenant_id = @TenantId
              AND id        = @JobId;
            """;
        var row = await conn.QuerySingleOrDefaultAsync<BackfillJobRow>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return row is null ? null : MapRow(row);
    }

    public async Task<ChannelBackfillJob> InsertPendingAsync(
        long tenantId,
        long channelId,
        CancellationToken ct = default)
    {
        // UPSERT semantics: `channel_backfill_jobs` has UNIQUE(channel_id), so a
        // channel that was previously bridged + unbridged still has a backfill
        // row (status=cancelled or complete). When the user re-bridges, we
        // RESET the existing row to pending state instead of inserting (which
        // would fail with a unique-constraint violation).
        //
        // All progress fields are cleared so the new backfill run starts fresh:
        //   - fetched_count → 0
        //   - oldest_fetched_snowflake → NULL (full re-scan from newest)
        //   - started_at / completed_at → NULL
        //   - last_error → NULL
        //   - hangfire_job_id → NULL (Bridge action stamps the new one)
        //
        // Tenant guard: include tenant_id in the conflict's DO UPDATE WHERE so a
        // cross-tenant collision (defense-in-depth) cannot overwrite another
        // tenant's row. UNIQUE is on channel_id alone, but channel_id implies
        // tenant_id via the foreign key — this WHERE is a belt-and-suspenders
        // assertion that surfaces as a no-RETURNING failure if ever tripped.
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = $"""
            WITH upsert AS (
                INSERT INTO channel_backfill_jobs (tenant_id, channel_id, status)
                VALUES (@TenantId, @ChannelId, 'pending')
                ON CONFLICT (channel_id) DO UPDATE SET
                    status                    = 'pending',
                    fetched_count             = 0,
                    oldest_fetched_snowflake  = NULL,
                    started_at                = NULL,
                    completed_at              = NULL,
                    last_error                = NULL,
                    hangfire_job_id           = NULL,
                    updated_at                = now()
                WHERE channel_backfill_jobs.tenant_id = @TenantId
                RETURNING {SelectColumns}
            )
            SELECT * FROM upsert;
            """;
        var row = await conn.QuerySingleAsync<BackfillJobRow>(
            new CommandDefinition(sql,
                new { TenantId = tenantId, ChannelId = channelId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return MapRow(row);
    }

    public async Task<bool> SetHangfireJobIdAsync(
        long tenantId,
        long jobId,
        string hangfireJobId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE channel_backfill_jobs
               SET hangfire_job_id = @HangfireJobId,
                   updated_at      = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId, HangfireJobId = hangfireJobId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> AdvanceCursorAsync(
        long tenantId,
        long jobId,
        long oldestSnowflake,
        int fetchedCount,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE channel_backfill_jobs
               SET oldest_fetched_snowflake = @OldestSnowflake,
                   fetched_count            = fetched_count + @FetchedCount,
                   updated_at               = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new
                {
                    TenantId = tenantId,
                    JobId = jobId,
                    OldestSnowflake = oldestSnowflake,
                    FetchedCount = fetchedCount,
                },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> MarkRunningAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        // COALESCE keeps the original started_at on Hangfire retries (status = 'running' / 'failed').
        const string sql = """
            UPDATE channel_backfill_jobs
               SET status     = 'running',
                   started_at = COALESCE(started_at, now()),
                   updated_at = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId
               AND status IN ('pending', 'running', 'failed');
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> MarkCompleteAsync(
        long tenantId,
        long jobId,
        int finalCount,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE channel_backfill_jobs
               SET status       = 'complete',
                   completed_at = now(),
                   fetched_count = @FinalCount,
                   updated_at   = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId, FinalCount = finalCount },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> MarkFailedAsync(
        long tenantId,
        long jobId,
        string error,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE channel_backfill_jobs
               SET status       = 'failed',
                   completed_at = now(),
                   last_error   = @Error,
                   updated_at   = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId, Error = error },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> MarkCancelledAsync(
        long tenantId,
        long jobId,
        CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct).ConfigureAwait(false);
        const string sql = """
            UPDATE channel_backfill_jobs
               SET status       = 'cancelled',
                   completed_at = now(),
                   updated_at   = now()
             WHERE tenant_id = @TenantId
               AND id        = @JobId;
            """;
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(sql,
                new { TenantId = tenantId, JobId = jobId },
                cancellationToken: ct))
            .ConfigureAwait(false);
        return affected > 0;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static ChannelBackfillJob MapRow(BackfillJobRow r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        ChannelId = r.ChannelId,
        Status = ParseStatus(r.Status),
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        FetchedCount = r.FetchedCount,
        OldestFetchedSnowflake = r.OldestFetchedSnowflake,
        LastError = r.LastError,
        HangfireJobId = r.HangfireJobId,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    /// <summary>
    /// Flat projection for Dapper (Dapper maps snake_case → PascalCase via
    /// <c>DefaultTypeMap.MatchNamesWithUnderscores = true</c>, already configured
    /// in the test fixtures; the API host sets it in Program.cs).
    /// </summary>
    private sealed class BackfillJobRow
    {
        public long Id { get; set; }
        public long TenantId { get; set; }
        public long ChannelId { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public int FetchedCount { get; set; }
        public long? OldestFetchedSnowflake { get; set; }
        public string? LastError { get; set; }
        public string? HangfireJobId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
