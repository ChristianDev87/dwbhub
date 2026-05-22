using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Migrations;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Logging;
using FluentMigrator.Runner;
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

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
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
