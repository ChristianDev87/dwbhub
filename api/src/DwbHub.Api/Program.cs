using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Application.Bot;
using DwbHub.Application.Setup;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Migrations;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Bot;
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

// --- Database wiring ---------------------------------------------------
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

// --- Auth wiring -------------------------------------------------------
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

// --- Refresh tokens ----------------------------------------------------
builder.Services.AddSingleton<ITokenHasher, TokenHasher>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IRefreshTokenRepository,
                           DwbHub.Data.Repositories.RefreshTokenRepository>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

// --- Email + verify + reset --------------------------------------------
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

// --- Setup wizard ------------------------------------------------------
var bootstrapTokenFile = Environment.GetEnvironmentVariable("DWBHUB_BOOTSTRAP_TOKEN_FILE")
    ?? "/data/dwbhub/bootstrap-token.txt";

builder.Services.AddSingleton<IBootstrapTokenWriter>(_ =>
    new DwbHub.Infrastructure.Setup.FileBootstrapTokenWriter(bootstrapTokenFile));
builder.Services.AddScoped<DwbHub.Core.Repositories.ISystemBootstrapLockRepository,
                           DwbHub.Data.Repositories.SystemBootstrapLockRepository>();
builder.Services.AddScoped<IBootstrapTokenProvisioner, BootstrapTokenProvisioner>();
builder.Services.AddScoped<ISetupService, SetupService>();

// --- Audit log + background jobs ---------------------------------------
builder.Services.AddScoped<DwbHub.Core.Repositories.IAuditLogRepository,
                           DwbHub.Data.Repositories.AuditLogRepository>();
builder.Services.AddSingleton<DwbHub.Core.Repositories.IAuditVerifyStateRepository,
                              DwbHub.Data.Repositories.AuditVerifyStateRepository>();
builder.Services.AddScoped<DwbHub.Application.Audit.IAuditWriter,
                           DwbHub.Application.Audit.AuditWriter>();

// --- Tenant resolution -------------------------------------------------
builder.Services.AddScoped<DwbHub.Application.Tenancy.ITenantContext,
                           DwbHub.Infrastructure.Tenancy.TenantContext>();
builder.Services.AddScoped<DwbHub.Application.Tenancy.IGuildContext,
                           DwbHub.Infrastructure.Tenancy.GuildContext>();
builder.Services.AddScoped<DwbHub.Application.Tenancy.ITenantSettingsService,
                           DwbHub.Application.Tenancy.TenantSettingsService>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IGuildRepository,
                           DwbHub.Data.Repositories.GuildRepository>();

// --- Bot token encryption ----------------------------------------------
var encryptionKey = Environment.GetEnvironmentVariable("DWBHUB_ENCRYPTION_KEY")
    ?? throw new InvalidOperationException(
        "DWBHUB_ENCRYPTION_KEY env var is required (Base64-encoded 32 bytes).");

builder.Services.AddSingleton<DwbHub.Application.Encryption.IBotTokenEncryptor>(
    new DwbHub.Infrastructure.Encryption.AesGcmBotTokenEncryptor(encryptionKey));
builder.Services.AddScoped<DwbHub.Core.Repositories.IGuildBotCredentialRepository,
                           DwbHub.Data.Repositories.GuildBotCredentialRepository>();

// --- BotConnectionManager ----------------------------------------------
// In e2e (DWBHUB_DISCORD_TEST_MODE=fake-rest) we also swap the bot-connection
// factory. The real Discord.NET factory would auto-reconnect against the
// gateway forever when given a fake-shape token, which used to flood the API
// log with "401 Unauthorized" (one per retry, ~95 in 4 minutes per guild).
// The fake factory hands out an in-process stub that transitions through the
// state machine without touching Discord. Both fakes are guarded inside
// their ctors against ASPNETCORE_ENVIRONMENT=Production.
if (Environment.GetEnvironmentVariable("DWBHUB_DISCORD_TEST_MODE") == "fake-rest")
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "DWBHUB_DISCORD_TEST_MODE=fake-rest is forbidden when ASPNETCORE_ENVIRONMENT=Production");
    builder.Services.AddSingleton<IBotConnectionFactory, DwbHub.Infrastructure.Bot.FakeBotConnectionFactory>();
}
else
{
    builder.Services.AddSingleton<IBotConnectionFactory, DiscordNetBotConnectionFactory>();
}
builder.Services.AddSingleton<BotConnectionManager>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<BotConnectionManager>());

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
        // Disable the default WS-Federation claim-name remapping so that
        // custom claims like "tid", "tslug", and "role" are accessible under
        // their original short names (e.g. ctx.User.FindFirst("tid")).
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKeyBytes),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            // JwtIssuer stores roles in a plain "role" claim (not ClaimTypes.Role).
            // Without MapInboundClaims the JWT middleware does not remap it, so we
            // must tell the validation layer which claim name carries role values.
            RoleClaimType = "role",
        };
    });
builder.Services.AddAuthorization();

// --- SignalR hub -------------------------------------------------------
builder.Services.AddSignalR(opts =>
{
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
    opts.MaximumReceiveMessageSize = 64 * 1024; // 64 KiB — defense in depth (hub has no client-callable methods)
});

// Singleton: matches IHubContext<T> lifetime.
builder.Services.AddSingleton<DwbHub.Application.Messaging.IMessagesBroadcaster,
                              DwbHub.Api.Messaging.SignalRMessagesBroadcaster>();

// --- Messaging services -------------------------------------------------
// IChannelWebhookCipher reuses DWBHUB_ENCRYPTION_KEY — one master key, two ciphers.
builder.Services.AddSingleton<DwbHub.Application.Messaging.IChannelWebhookCipher>(
    new DwbHub.Infrastructure.Messaging.AesGcmChannelWebhookCipher(encryptionKey));

builder.Services.AddScoped<DwbHub.Core.Repositories.IMessageRepository,
                           DwbHub.Data.Repositories.MessageRepository>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IGuildChannelRepository,
                           DwbHub.Data.Repositories.GuildChannelRepository>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IChannelWebhookRepository,
                           DwbHub.Data.Repositories.ChannelWebhookRepository>();
builder.Services.AddScoped<DwbHub.Core.Repositories.IChannelBackfillJobRepository,
                           DwbHub.Data.Repositories.ChannelBackfillJobRepository>();

builder.Services.AddScoped<DwbHub.Application.Messaging.IMessageService,
                           DwbHub.Application.Messaging.MessageService>();
builder.Services.AddScoped<DwbHub.Application.Messaging.IChannelSyncService,
                           DwbHub.Application.Messaging.ChannelSyncService>();
builder.Services.AddScoped<DwbHub.Application.Messaging.IChannelWebhookService,
                           DwbHub.Application.Messaging.ChannelWebhookService>();
builder.Services.AddScoped<DwbHub.Application.Messaging.IBackfillRunner,
                           DwbHub.Application.Messaging.BackfillRunner>();

// DiscordRestChannelClient needs an HttpClient for the webhook-execute path.
// AddHttpClient<TInterface, TImplementation> registers both the typed client
// and the IHttpClientFactory binding so DI can resolve the concrete ctor.
//
// When DWBHUB_DISCORD_TEST_MODE=fake-rest the scripted fake is substituted so
// e2e tests can run without a real Discord bot. The fake is FORBIDDEN in
// Production (double-guarded: here and inside FakeDiscordRestChannelClient ctor).
if (Environment.GetEnvironmentVariable("DWBHUB_DISCORD_TEST_MODE") == "fake-rest")
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "DWBHUB_DISCORD_TEST_MODE=fake-rest is forbidden when ASPNETCORE_ENVIRONMENT=Production");
    builder.Services.AddScoped<DwbHub.Application.Messaging.IDiscordRestChannelClient,
                               DwbHub.Infrastructure.Messaging.FakeDiscordRestChannelClient>();
}
else
{
    builder.Services.AddHttpClient<DwbHub.Application.Messaging.IDiscordRestChannelClient,
                                   DwbHub.Infrastructure.Messaging.DiscordRestChannelClient>();
}

// JWT bearer for SignalR: the SignalR client sends the access token as
// ?access_token=... in the WebSocket/LongPolling upgrade URL. We must extract it
// from the query string for /api/hubs/* paths ONLY — other paths are unaffected.
builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
{
    opts.Events ??= new JwtBearerEvents();
    var originalOnMessageReceived = opts.Events.OnMessageReceived;
    opts.Events.OnMessageReceived = async ctx =>
    {
        if (originalOnMessageReceived is not null)
            await originalOnMessageReceived(ctx);

        // Only read the query-string token for hub upgrade requests.
        if (string.IsNullOrEmpty(ctx.Token))
        {
            var accessToken = ctx.Request.Query["access_token"];
            var path = ctx.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/hubs"))
                ctx.Token = accessToken;
        }
    };
});

var app = builder.Build();

// --- Apply DB migrations -----------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
    Log.Information("[Migrations] Applied up to current version (VersionInfo table)");
}

// --- Setup-wizard bootstrap ---------------------------------------------
// Run after migrations (needs system_bootstrap_lock table). Synchronous — startup fails if DB is unreachable.
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

// --- Tenant resolver middleware -----------------------------------------
// After UseAuthentication (needs HttpContext.User/'tid' claim), before UseAuthorization — unknown tenants get 404 not 401 (authorization never runs for non-existent tenants).
app.UseMiddleware<DwbHub.Infrastructure.Tenancy.TenantResolverMiddleware>();

app.UseAuthorization();

// --- Hangfire dashboard -------------------------------------------------
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

// --- SignalR hub -------------------------------------------------------
app.MapHub<DwbHub.Api.Hubs.MessagesHub>("/api/hubs/messages");

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
