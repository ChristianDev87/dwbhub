using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Application.Setup;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Migrations;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Logging;
using FluentMigrator.Runner;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Serilog;
using System.Text;

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
var connectionString = Environment.GetEnvironmentVariable("DWBHUB_DB_CONNECTION")
    ?? throw new InvalidOperationException(
        "DWBHUB_DB_CONNECTION env var is required (set in compose/.env or your shell).");

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

// --- Auth wiring (Plan 0.3a) -------------------------------------------
var jwtSecret = Environment.GetEnvironmentVariable("DWBHUB_JWT_SECRET")
    ?? throw new InvalidOperationException(
        "DWBHUB_JWT_SECRET env var is required (Base64-encoded 32+ bytes).");

builder.Services.AddSingleton<IPasswordHasher>(new BCryptPasswordHasher());
builder.Services.AddSingleton<IJwtIssuer>(new JwtIssuer(jwtSecret));

builder.Services.AddScoped<DwbHub.Core.Repositories.IUserRepository,
                           DwbHub.Data.Repositories.UserRepository>();
builder.Services.AddScoped<DwbHub.Core.Repositories.ILoginAttemptRepository,
                           DwbHub.Data.Repositories.LoginAttemptRepository>();
builder.Services.AddScoped<ILoginService, LoginService>();

// --- Refresh tokens (Plan 0.3b) ----------------------------------------
builder.Services.AddSingleton<ITokenHasher, TokenHasher>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IRefreshTokenRepository,
                           DwbHub.Data.Repositories.RefreshTokenRepository>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

// --- Email + verify + reset (Plan 0.3c) --------------------------------
var smtpHost = Environment.GetEnvironmentVariable("DWBHUB_SMTP_HOST")
    ?? throw new InvalidOperationException("DWBHUB_SMTP_HOST env var is required.");
var smtpPort = int.Parse(Environment.GetEnvironmentVariable("DWBHUB_SMTP_PORT")
    ?? throw new InvalidOperationException("DWBHUB_SMTP_PORT env var is required."));
var smtpFrom = Environment.GetEnvironmentVariable("DWBHUB_SMTP_FROM")
    ?? throw new InvalidOperationException("DWBHUB_SMTP_FROM env var is required.");
var publicBaseUrl = Environment.GetEnvironmentVariable("DWBHUB_PUBLIC_BASE_URL")
    ?? "http://localhost:5173";

builder.Services.AddSingleton<IEmailSender>(_ => new DwbHub.Infrastructure.Email.MailKitEmailSender(smtpHost, smtpPort, smtpFrom));
builder.Services.AddSingleton<IEmailTemplateRenderer, DwbHub.Infrastructure.Email.TemplateEmailRenderer>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IAuthTokenRepository,
                           DwbHub.Data.Repositories.AuthTokenRepository>();
builder.Services.AddScoped<IEmailVerificationService>(sp =>
    new EmailVerificationService(
        sp.GetRequiredService<DwbHub.Core.Repositories.IAuthTokenRepository>(),
        sp.GetRequiredService<DwbHub.Core.Repositories.ITenantRepository>(),
        sp.GetRequiredService<DwbHub.Core.Repositories.IUserRepository>(),
        sp.GetRequiredService<ITokenHasher>(),
        sp.GetRequiredService<ITokenGenerator>(),
        sp.GetRequiredService<IEmailTemplateRenderer>(),
        sp.GetRequiredService<IEmailSender>(),
        publicBaseUrl,
        sp.GetRequiredService<DwbHub.Application.Audit.IAuditWriter>()));
builder.Services.AddScoped<IPasswordResetService>(sp =>
    new PasswordResetService(
        sp.GetRequiredService<DwbHub.Core.Repositories.IAuthTokenRepository>(),
        sp.GetRequiredService<DwbHub.Core.Repositories.ITenantRepository>(),
        sp.GetRequiredService<DwbHub.Core.Repositories.IUserRepository>(),
        sp.GetRequiredService<ITokenHasher>(),
        sp.GetRequiredService<ITokenGenerator>(),
        sp.GetRequiredService<IPasswordHasher>(),
        sp.GetRequiredService<IEmailTemplateRenderer>(),
        sp.GetRequiredService<IEmailSender>(),
        publicBaseUrl,
        sp.GetRequiredService<DwbHub.Application.Audit.IAuditWriter>()));

// --- Setup wizard (Plan 0.3d) ------------------------------------------
var bootstrapTokenFile = Environment.GetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE")
    ?? "/data/dwbhub/bootstrap-token.txt";

builder.Services.AddSingleton<IBootstrapTokenWriter>(_ =>
    new DwbHub.Infrastructure.Setup.FileBootstrapTokenWriter(bootstrapTokenFile));
builder.Services.AddScoped<DwbHub.Core.Repositories.ISystemBootstrapLockRepository,
                           DwbHub.Data.Repositories.SystemBootstrapLockRepository>();
builder.Services.AddScoped<IBootstrapTokenProvisioner, BootstrapTokenProvisioner>();
builder.Services.AddScoped<ISetupService, SetupService>();

// --- Audit log + background jobs (Plan 0.4) -----------------------------
builder.Services.AddScoped<DwbHub.Core.Repositories.IAuditLogRepository,
                           DwbHub.Data.Repositories.AuditLogRepository>();
builder.Services.AddSingleton<DwbHub.Core.Repositories.IAuditVerifyStateRepository,
                              DwbHub.Data.Repositories.AuditVerifyStateRepository>();
builder.Services.AddScoped<DwbHub.Application.Audit.IAuditWriter,
                           DwbHub.Application.Audit.AuditWriter>();
builder.Services.AddScoped<DwbHub.Infrastructure.Background.AuditVerifyCore>();
builder.Services.AddScoped<DwbHub.Application.Background.IAuditVerifyIncrementalJob,
                           DwbHub.Infrastructure.Background.AuditVerifyIncrementalJob>();
builder.Services.AddScoped<DwbHub.Application.Background.IAuditVerifyFullJob,
                           DwbHub.Infrastructure.Background.AuditVerifyFullJob>();
builder.Services.AddScoped<DwbHub.Application.Background.ILoginAttemptPruneJob,
                           DwbHub.Infrastructure.Background.LoginAttemptPruneJob>();
builder.Services.AddScoped<DwbHub.Application.Background.IAuthTokenPruneJob,
                           DwbHub.Infrastructure.Background.AuthTokenPruneJob>();

builder.Services.AddHangfire(cfg => cfg
    .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString),
        new Hangfire.PostgreSql.PostgreSqlStorageOptions
        {
            SchemaName = "hangfire",
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(15),
        })
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings());

builder.Services.AddHangfireServer(opts =>
{
    opts.ServerName = $"dwbhub-api-{Environment.MachineName}";
    opts.WorkerCount = 2;
});

var jwtKeyBytes = Convert.FromBase64String(jwtSecret);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKeyBytes),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// --- Apply DB migrations (Plan 0.2) ------------------------------------
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
    Log.Information("[Migrations] Applied up to current version (VersionInfo table)");
}

// --- Setup-wizard bootstrap (Plan 0.3d) ---------------------------------
// Run the provisioner once at startup, after migrations have created the
// system_bootstrap_lock table. Synchronous (we want startup to fail if the
// DB is unreachable, not to silently skip).
using (var scope = app.Services.CreateScope())
{
    var provisioner = scope.ServiceProvider.GetRequiredService<IBootstrapTokenProvisioner>();
    await provisioner.ProvisionAsync();
}

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

// --- Hangfire dashboard (Plan 0.4) --------------------------------------
// Mount AFTER UseAuthorization so httpContext.User is populated for our filter.
app.UseHangfireDashboard("/api/admin/hangfire", new DashboardOptions
{
    Authorization = [new DwbHub.Infrastructure.Background.OwnerOnlyHangfireAuthFilter()],
    DisplayStorageConnectionString = false,
    IgnoreAntiforgeryToken = false,
});

Hangfire.RecurringJob.AddOrUpdate<DwbHub.Application.Background.IAuditVerifyIncrementalJob>(
    "audit-verify-incremental",
    job => job.RunAsync(CancellationToken.None),
    "0 3 * * *", new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
Hangfire.RecurringJob.AddOrUpdate<DwbHub.Application.Background.IAuditVerifyFullJob>(
    "audit-verify-full-weekly",
    job => job.RunAsync(CancellationToken.None),
    "0 4 * * 0", new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
Hangfire.RecurringJob.AddOrUpdate<DwbHub.Application.Background.ILoginAttemptPruneJob>(
    "login-attempt-prune",
    job => job.RunAsync(CancellationToken.None),
    "0 3 * * 0", new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
Hangfire.RecurringJob.AddOrUpdate<DwbHub.Application.Background.IAuthTokenPruneJob>(
    "auth-token-prune",
    job => job.RunAsync(CancellationToken.None),
    "30 3 * * *", new Hangfire.RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

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
