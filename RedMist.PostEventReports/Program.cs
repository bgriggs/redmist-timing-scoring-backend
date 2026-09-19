using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NLog.Extensions.Logging;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.PostEventReports;
using RedMist.PostEventReports.Sections;
using RedMist.PostEventReports.Sections.Viewership;
using RedMist.PostEventReports.Suggestions;
using RedMist.PostEventReports.Suggestions.Rules;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddNLog("NLog");

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
string sqlConn = builder.Configuration["ConnectionStrings:Default"] ?? throw new ArgumentNullException("SQL Connection");
builder.Services.AddDbContextFactory<TsContext>(op => op.UseNpgsql(sqlConn));

builder.Services.AddTransient<EmailHelper>();
builder.Services.AddSingleton(PostEventReportSettings.FromConfiguration(builder.Configuration));

// The report is whatever sections are registered here. Adding one - lap and competitor statistics,
// control log usage, sponsor impressions - is a class and a line, with no change to the job, the
// email shell or the schedule.
builder.Services.AddTransient<IReportSection, ViewershipSection>();

// Suggestions are a separate seam from sections, because they are cross-cutting: a section added
// later should be able to contribute one into the same block without owning that block.
builder.Services.AddTransient<IReportSuggestion, NoControlLogSuggestion>();
builder.Services.AddTransient<IReportSuggestion, NoEventUrlSuggestion>();
builder.Services.AddTransient<IReportSuggestion, ShareOnSocialSuggestion>();
builder.Services.AddTransient<IReportSuggestion, EmbedTimingSuggestion>();
builder.Services.AddTransient<IReportSuggestion, NoFlagtronicsSuggestion>();
builder.Services.AddTransient<SuggestionEngine>();

builder.Services.AddHostedService<PostEventReportJob>();

builder.Services.AddHealthChecks()
    .AddNpgSql(sqlConn, name: "postgres", tags: ["db", "postgres"]);

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<PostEventReportJob>>();
var assembly = typeof(PostEventReportJob).Assembly;
logger.LogInformation("Service starting...");
logger.LogInformation("Assembly: {AssemblyName}, Version: {Version}",
    assembly.GetName().Name ?? "unknown", assembly.GetName().Version?.ToString() ?? "unknown");

if (app.Environment.IsDevelopment())
{
    Console.Title = "Post Event Reports";
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
