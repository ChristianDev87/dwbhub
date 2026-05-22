using Dapper;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Migrations;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Logging;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog;

const int defaultRetainStartLogs = 10;

var builder = WebApplication.CreateBuilder(args);

var enableSwagger = builder.Environment.IsDevelopment();

var logDirectory = Environment.GetEnvironmentVariable("DWBHUB_LOG_DIR")
    ?? Path.Combine(builder.Environment.ContentRootPath, "logs");
var retainStartLogs = builder.Configuration.GetValue<int?>("Logging:RetainStartLogs") ?? defaultRetainStartLogs;
var logFilePath = PerStartFileLogger.Initialize(logDirectory, retainStartLogs);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(logFilePath, rollOnFileSizeLimit: true, fileSizeLimitBytes: 100L * 1024 * 1024, retainedFileCountLimit: 10)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddControllers();
if (enableSwagger)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new() { Title = "DwbHub API", Version = "v1" });
    });
}

// --- Database wiring (Plan 0.2) ----------------------------------------
// DWBHUB_SKIP_MIGRATIONS=1 also lets the connection string default to a dummy
// so tooling that reflects over the built DLL (e.g. `swagger tofile`) doesn't
// crash on a missing env var. The dummy is never used in that mode because
// migrations are skipped and no HTTP request runs.
var skipMigrations = Environment.GetEnvironmentVariable("DWBHUB_SKIP_MIGRATIONS") == "1";
var connectionString = Environment.GetEnvironmentVariable("DWBHUB_DB_CONNECTION")
    ?? (skipMigrations
        ? "Host=localhost;Username=tooling;Password=tooling;Database=tooling"
        : throw new InvalidOperationException(
            "DWBHUB_DB_CONNECTION env var is required (set in compose/.env or your shell)."));

// Dapper: map snake_case columns to PascalCase record properties.
DefaultTypeMap.MatchNamesWithUnderscores = true;

// Dapper: map Npgsql's DateTime (UTC) return from TIMESTAMPTZ to DateTimeOffset.
SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddScoped<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();

builder.Services
    .AddFluentMigratorCore()
    .ConfigureRunner(rb => rb
        .AddPostgres()
        .WithGlobalConnectionString(connectionString)
        .ScanIn(typeof(Migration00001_Tenants).Assembly).For.Migrations()
        .ScanIn(typeof(Migration00001_Tenants).Assembly).For.EmbeddedResources())
    .AddLogging(lb => lb.AddFluentMigratorConsole());

var app = builder.Build();

// --- Apply DB migrations (Plan 0.2) ------------------------------------
// DWBHUB_SKIP_MIGRATIONS=1 is honoured here (see top of file for full rationale).
if (!skipMigrations)
{
    using var scope = app.Services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
    Log.Information("[Migrations] Applied up to current version (VersionInfo table)");
}

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.MapControllers();

Log.Information("DwbHub.Api starting. LogFile={LogFile}", logFilePath);

app.Run();

public partial class Program;

/// <summary>
/// Converts Npgsql's UTC DateTime (returned for TIMESTAMPTZ) to DateTimeOffset
/// so Dapper can materialize records that use DateTimeOffset for timestamp columns.
/// </summary>
file sealed class DateTimeOffsetTypeHandler : Dapper.SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name} to DateTimeOffset")
    };

    public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value;
}
