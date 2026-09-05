using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using RedMist.Database;
using RedMist.Social.Generation;
using RedMist.SocialCompose;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddNLog("NLog");

// Maps DateTime to "timestamp without time zone", which is what every table in this database uses.
// Every other service sets this; omitting it here would make the same rows read back differently.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

// Checked for blank, not just null: appsettings.json ships the key as "" and an unset Helm value
// arrives the same way, so a null check alone never fires and the failure surfaces later from inside
// the DI factory, naming a constructor parameter instead of the setting that is actually missing.
string claudeApiKey = builder.Configuration["Claude:ApiKey"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(claudeApiKey))
    throw new InvalidOperationException("Claude:ApiKey is not configured; post copy cannot be generated without it.");

builder.Services.AddSingleton(SocialComposeSettings.FromConfiguration(builder.Configuration));

// Generous relative to a single generation, tight enough that a hung connection cannot hold the job
// open until Kubernetes kills it and leaves the run half done.
builder.Services.AddHttpClient(ClaudeCopyGenerator.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(2));

builder.Services.AddSingleton<ICopyGenerator>(sp => new ClaudeCopyGenerator(
    sp.GetRequiredService<IHttpClientFactory>(),
    claudeApiKey,
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<PostComposer>();
builder.Services.AddHostedService<SocialComposeJob>();

builder.Services.AddHealthChecks()
    .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<SocialComposeJob>>();
var assembly = typeof(SocialComposeJob).Assembly;
logger.LogInformation("Service starting...");
logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}",
    assembly.GetName().Name ?? "unknown", assembly.GetName().Version?.ToString() ?? "unknown");

if (app.Environment.IsDevelopment())
{
    Console.Title = "Social Compose";
}

app.MapHealthChecks("/healthz/startup", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.Run();
