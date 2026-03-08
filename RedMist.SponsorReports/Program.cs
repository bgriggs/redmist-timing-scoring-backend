using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.SponsorReports;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddNLog("NLog");

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

builder.Services.AddTransient<EmailHelper>();
builder.Services.AddHostedService<SponsorReportJob>();

builder.Services.AddHealthChecks()
    .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<SponsorReportJob>>();
var assembly = typeof(SponsorReportJob).Assembly;
logger.LogInformation("Service starting...");
logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}",
    assembly.GetName().Name ?? "unknown", assembly.GetName().Version?.ToString() ?? "unknown");

if (app.Environment.IsDevelopment())
{
    Console.Title = "Sponsor Reports";
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
