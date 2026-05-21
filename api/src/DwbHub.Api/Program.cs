using DwbHub.Infrastructure.Logging;
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

var app = builder.Build();

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
