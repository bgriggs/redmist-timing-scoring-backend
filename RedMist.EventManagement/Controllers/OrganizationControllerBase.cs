using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using RedMist.Backend.Shared.Utilities;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

/// <summary>
/// Base controller for Organization management operations.
/// Provides endpoints for configuring organization settings and control log integration.
/// </summary>
/// <remarks>
/// This is an abstract base controller inherited by versioned controllers.
/// Requires authentication and validates that users can only manage their own organization.
/// </remarks>
[ApiController]
[Authorize]
public abstract class OrganizationControllerBase : Controller
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IControlLogFactory controlLogFactory;
    private readonly AssetsCdn assetsCdn;

    /// <summary>The value this controller writes. The rule for reading one is in OrganizationRoles.</summary>
    private const string AdminRole = OrganizationRoles.Admin;

    protected ILogger Logger { get; }

    private static readonly Counter ControlLogStatsRequestCounter = Metrics.CreateCounter(
        "eventmgmt_controllog_stats_requests_total",
        "Total number of control log statistics requests");

    private static readonly Counter ControlLogStatsFailureCounter = Metrics.CreateCounter(
        "eventmgmt_controllog_stats_failures_total",
        "Total number of failed control log statistics requests");

    private static readonly Histogram ControlLogStatsLatency = Metrics.CreateHistogram(
        "eventmgmt_controllog_stats_duration_seconds",
        "Duration of control log statistics requests in seconds");

    private static readonly Counter OrgUpdateCounter = Metrics.CreateCounter(
        "eventmgmt_org_updates_total",
        "Total number of organization update operations");

    private static readonly Counter CdnUploadCounter = Metrics.CreateCounter(
        "eventmgmt_cdn_uploads_total",
        "Total number of CDN logo upload operations");


    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="controlLogFactory">Factory to create control log providers.</param>
    /// <param name="assetsCdn"></param>
    protected OrganizationControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IControlLogFactory controlLogFactory, AssetsCdn assetsCdn)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
        this.assetsCdn = assetsCdn;
    }


    /// <summary>
    /// Every organization the caller may act for.
    /// </summary>
    /// <returns>The caller's organizations, ascending by id. Empty when it has none.</returns>
    /// <response code="200">Returns the organizations, which may be an empty list.</response>
    /// <remarks>
    /// The entry point: every other action here names an organization by id, and this is where those
    /// ids come from. A relay gets the single organization its Keycloak client belongs to and can
    /// take the first element; a signed-in person gets everything they administer.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<List<Organization>>(StatusCodes.Status200OK)]
    public virtual async Task<ActionResult<List<Organization>>> LoadOrganizations()
    {
        Logger.LogTrace("{m}", nameof(LoadOrganizations));
        using var db = await tsContext.CreateDbContextAsync();
        var permitted = await CallerOrganizations.ResolveAsync(db, User);
        var orgs = await db.Organizations
            .AsNoTracking()
            .Where(o => permitted.Contains(o.Id))
            .OrderBy(o => o.Id)
            .ToListAsync();

        var defaultLogo = orgs.Exists(o => o.Logo == null)
            ? (await db.DefaultOrgImages.FirstOrDefaultAsync())?.ImageData
            : null;
        foreach (var org in orgs.Where(o => o.Logo == null))
        {
            org.Logo = defaultLogo;
        }
        return Ok(orgs);
    }

    /// <summary>
    /// Loads one of the caller's organizations.
    /// </summary>
    /// <returns>The organization details, or null if not found.</returns>
    /// <response code="200">Returns the organization details.</response>
    /// <response code="404">The caller does not act for that organization, or it does not exist.</response>
    /// <remarks>
    /// The organization is named by the caller. <see cref="LoadOrganizations"/> is how a caller finds
    /// out which ids it may name.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<Organization>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<Organization>> LoadOrganization(int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(LoadOrganization), organizationId);
        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
            return NotFound();
        var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == organizationId);
        if (org == null)
            return NotFound();

        if (org != null && org.Logo == null)
        {
            org.Logo = db.DefaultOrgImages.FirstOrDefault()?.ImageData;
        }
        return Ok(org);
    }

    /// <summary>
    /// Whether this organization receives the post-event report email.
    /// </summary>
    /// <param name="organizationId">The organization to read.</param>
    /// <response code="200">The settings. An organization with no row is opted in.</response>
    /// <response code="400">The organization id is unusable.</response>
    /// <response code="404">The caller does not act for that organization.</response>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType<ReportSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<ReportSettingsDto>> ReportSettings(int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(ReportSettings), organizationId);
        if (organizationId < 1)
            return BadRequest("organizationId is required.");

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
            return NotFound();

        // No row means opted in. The report job reads it the same way, so an organization that has
        // never touched this setting is treated identically by both.
        var settings = await db.OrganizationReportSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId);

        return new ReportSettingsDto { SendPostEventReport = settings?.SendPostEventReport ?? true };
    }

    /// <summary>
    /// Turns the post-event report email on or off for this organization.
    /// </summary>
    /// <param name="settings">The desired setting.</param>
    /// <param name="organizationId">The organization to change.</param>
    /// <response code="200">Saved.</response>
    /// <response code="400">The organization id is unusable.</response>
    /// <response code="404">The caller does not act for that organization.</response>
    /// <remarks>
    /// Creates the row on first use, because absence is what "opted in" is stored as.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> SaveReportSettings(ReportSettingsDto settings, int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(SaveReportSettings), organizationId);
        if (organizationId < 1)
            return BadRequest("organizationId is required.");

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
            return NotFound();

        var existing = await db.OrganizationReportSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId);
        if (existing == null)
        {
            db.OrganizationReportSettings.Add(new OrganizationReportSettings
            {
                OrganizationId = organizationId,
                SendPostEventReport = settings.SendPostEventReport,
            });
        }
        else
        {
            existing.SendPostEventReport = settings.SendPostEventReport;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (existing == null)
        {
            // Two first-time saves raced and the other one created the row. OrganizationId is the
            // primary key, so this is the only way that throws here, and the answer is simply to
            // write to the row that now exists - a toggle double-clicked should not be a 500.
            db.ChangeTracker.Clear();
            var created = await db.OrganizationReportSettings
                .FirstOrDefaultAsync(s => s.OrganizationId == organizationId);
            if (created == null)
            {
                throw;
            }
            created.SendPostEventReport = settings.SendPostEventReport;
            await db.SaveChangesAsync();
        }

        return Ok();
    }

    /// <summary>
    /// Updates organization configuration settings.
    /// </summary>
    /// <param name="organization">The organization with updated settings.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Organization updated successfully.</response>
    /// <response code="404">Organization not found.</response>
    /// <remarks>
    /// <para>Users can only update their own organization's settings.</para>
    /// </remarks>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> UpdateOrganization(Organization organization)
    {
        Logger.LogTrace("{o}", nameof(UpdateOrganization));
        OrgUpdateCounter.Inc();

        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organization.Id))
            return NotFound();

        var org = await db.Organizations.FirstOrDefaultAsync(x => x.Id == organization.Id);
        if (org != null)
        {
            org.Website = organization.Website;
            org.ControlLogType = organization.ControlLogType;
            org.ControlLogParams = organization.ControlLogParams;
            org.Classes = organization.Classes;
            org.X2 = organization.X2;
            org.RMonitorIp = organization.RMonitorIp;
            org.RMonitorPort = organization.RMonitorPort;
            org.MultiloopIp = organization.MultiloopIp;
            org.MultiloopPort = organization.MultiloopPort;
            org.OrbitsLogsPath = organization.OrbitsLogsPath;
            org.FlagtronicsUrl = organization.FlagtronicsUrl;
            org.FlagtronicsApiKey = organization.FlagtronicsApiKey;
            org.ShowControlLogConnection = organization.ShowControlLogConnection;
            org.ShowX2Connection = organization.ShowX2Connection;
            org.ShowMultiloopConnection = organization.ShowMultiloopConnection;
            org.ShowOrbitsLogsConnection = organization.ShowOrbitsLogsConnection;
            org.ShowFlagtronicsConnection = organization.ShowFlagtronicsConnection;

            // Update logo. A posted logo identical to the shared placeholder is the read coming
            // back rather than a choice - LoadOrganization substitutes it for an organization with
            // none, and the relay's settings screen loads and posts the whole record - so it is
            // treated as "unchanged" rather than written in as this organization's own.
            // See OrganizationLogo.
            Task? updateCdnTask = null;
            if (OrganizationLogo.IsDefault(organization.Logo, await OrganizationLogo.LoadDefaultAsync(db)))
            {
                // Leave org.Logo alone.
            }
            else if (organization.Logo != null && organization.Logo.Length > 0)
            {
                org.Logo = organization.Logo;
                updateCdnTask = assetsCdn.SaveLogoAsync(org.Id, organization.Logo);
                CdnUploadCounter.Inc();
            }
            else if (organization.Logo != null && organization.Logo.Length == 0)
            {
                org.Logo = null;
                var defaultImage = db.DefaultOrgImages.FirstOrDefault();
                if (defaultImage != null)
                {
                    updateCdnTask = assetsCdn.SaveLogoAsync(org.Id, defaultImage.ImageData);
                    CdnUploadCounter.Inc();
                }
            }

            await db.SaveChangesAsync();
            if (updateCdnTask != null)
                await updateCdnTask;

            return Ok();
        }
        return NotFound();
    }

    /// <summary>
    /// Tests the control log connection and retrieves statistics.
    /// </summary>
    /// <param name="organization">The organization with control log configuration to test.</param>
    /// <returns>Statistics about the control log connection including connection status and entry count.</returns>
    /// <response code="200">Returns control log statistics.</response>
    /// <remarks>
    /// <para>This endpoint validates the control log configuration and attempts to connect to the configured control log system.</para>
    /// <para>Useful for testing control log settings before saving them.</para>
    /// </remarks>
    [HttpPost]
    [Produces("application/json", "application/x-msgpack")]
    public virtual async Task<ControlLogStatistics> GetControlLogStatistics(Organization organization)
    {
        Logger.LogTrace("{m}", nameof(GetControlLogStatistics));
        ControlLogStatsRequestCounter.Inc();

        using var timer = ControlLogStatsLatency.NewTimer();
        var cls = new ControlLogStatistics();

        try
        {
            if (!string.IsNullOrEmpty(organization.ControlLogType) && organization.ControlLogType != "Default" && organization.ControlLogType != "None")
            {
                using var cl = controlLogFactory.CreateControlLog(organization.ControlLogType);
                var logEntries = await cl.LoadControlLogAsync(organization.ControlLogParams);
                cls.IsConnected = logEntries.success;
                cls.TotalEntries = logEntries.logs.Count();
                var isStale = await DetermineControlLogStaleAsync(organization.Id, logEntries.logs);
                cls.IsStaleWarning = isStale;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error getting control log statistics for org {orgId}", organization.Id);
            ControlLogStatsFailureCounter.Inc();
        }

        return cls;
    }

    private async Task<bool> DetermineControlLogStaleAsync(int orgId, IEnumerable<ControlLogEntry> currentControlLog)
    {
        if (!currentControlLog.Any())
            return false;

        using var db = await tsContext.CreateDbContextAsync();
        var sessions = await db.SessionResults
            .AsNoTracking()
            .Where(s => db.Events.Any(e => e.Id == s.EventId && e.OrganizationId == orgId) && s.ControlLogs != null)
            .OrderByDescending(s => s.Start)
            .Take(10)
            .Select(s => new { s.EventId, s.SessionId, s.ControlLogs })
            .ToListAsync();

        sessions = [.. sessions.Where(s => s.ControlLogs.Count > 0).Take(3)];

        foreach (var session in sessions)
        {
            var similarityPercent = AnalyzeControlLogSimilarity(currentControlLog, session.ControlLogs);
            if (similarityPercent >= 50)
            {
                Logger.LogInformation("Control log for org {orgId} is similar to historic log from event {eventId} session {sessionId} with {percent}% similarity",
                    orgId, session.EventId, session.SessionId, similarityPercent);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Compares two control log sets and calculates the percentage of matching entries.
    /// </summary>
    /// <param name="logA">First control log set to compare.</param>
    /// <param name="logB">Second control log set to compare.</param>
    /// <returns>Percentage of similar entries (0-100).</returns>
    private static int AnalyzeControlLogSimilarity(IEnumerable<ControlLogEntry> logA, IEnumerable<ControlLogEntry> logB)
    {
        if (logA == null || logB == null)
            return 0;

        var orderedLogA = logA.OrderBy(x => x.Timestamp).ToList();
        var orderedLogB = logB.OrderBy(x => x.Timestamp).ToList();

        if (orderedLogA.Count == 0 || orderedLogB.Count == 0)
            return 0;

        // Compare the smaller count to avoid index out of range
        var minCount = Math.Min(orderedLogA.Count, orderedLogB.Count);
        var matchCount = 0;

        for (int i = 0; i < minCount; i++)
        {
            if (CompareControlLogEntries(orderedLogA[i], orderedLogB[i]))
            {
                matchCount++;
            }
        }

        // Calculate percentage based on the smaller log set
        var percentSimilar = (int)Math.Round((double)matchCount / minCount * 100);
        return percentSimilar;
    }

    /// <summary>
    /// Compares two control log entries for equality.
    /// </summary>
    /// <param name="entryA">First control log entry.</param>
    /// <param name="entryB">Second control log entry.</param>
    /// <returns>True if entries match, false otherwise.</returns>
    private static bool CompareControlLogEntries(ControlLogEntry entryA, ControlLogEntry entryB)
    {
        if (entryA.OrderId != entryB.OrderId)
            return false;
        if (entryA.Car1 != entryB.Car1)
            return false;
        if (entryA.Car2 != entryB.Car2)
            return false;
        if (entryA.Timestamp != entryB.Timestamp)
            return false;
        if (entryA.Status != entryB.Status)
            return false;
        if (entryA.Corner != entryB.Corner)
            return false;
        if (entryA.Note != entryB.Note)
            return false;
        if (entryA.OtherNotes != entryB.OtherNotes)
            return false;
        return true;
    }

    /// <summary>
    /// Loads the list of administrator email addresses for the organization.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<List<string>>> LoadOrganizationAdministratorsAsync(int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(LoadOrganizationAdministratorsAsync), organizationId);
        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
            return NotFound();
        var adminEmails = await db.UserOrganizationMappings
            .Where(uom => uom.OrganizationId == organizationId)
            .Where(OrganizationRoles.AdministratorMappings)
            .Select(uom => uom.Username)
            .ToListAsync();
        return Ok(adminEmails);
    }

    /// <summary>
    /// Saves the list of administrator email addresses for the organization.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> SaveOrganizationAdministratorsAsync(List<string> usernames, int organizationId)
    {
        Logger.LogTrace("{m} {org}", nameof(SaveOrganizationAdministratorsAsync), organizationId);
        using var db = await tsContext.CreateDbContextAsync();
        if (!await CallerOrganizations.IsPermittedAsync(db, User, organizationId))
            return NotFound();
        // FirstOrDefault, not First: a mapping row can outlive the organization it names, nothing
        // enforces otherwise, and being permitted against a missing row is a 404 rather than a 500.
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.Id == organizationId);
        if (org == null)
            return NotFound();

        // A blank username would become a mapping row that every token lacking a username matches,
        // because the resolver lowercases whatever it is given and compares. [Required] on a string
        // means NOT NULL, not non-empty, so the database would take it.
        if (usernames.Exists(string.IsNullOrWhiteSpace))
            return BadRequest("Usernames cannot be blank.");

        // Only the administrator mappings are replaced. A blanket RemoveRange would also drop
        // every other role this organization has, none of which this endpoint can re-create.
        var existing = await db.UserOrganizationMappings
            .Where(uom => uom.OrganizationId == org.Id)
            .ToListAsync();

        // Ordinal throughout: (Username, OrganizationId) is the primary key and Postgres compares it
        // case sensitively, so matching any other way would target a row the database considers distinct.
        var posted = usernames.Distinct(StringComparer.Ordinal).ToList();

        foreach (var staleAdmin in existing.Where(uom => OrganizationRoles.IsAdmin(uom.Role) && !posted.Contains(uom.Username, StringComparer.Ordinal)))
        {
            db.UserOrganizationMappings.Remove(staleAdmin);
        }

        foreach (var username in posted)
        {
            // Role is not part of the primary key, so a user who already holds a mapping has to be
            // promoted in place. Removing and re-adding would collide with the tracked instance.
            var current = existing.FirstOrDefault(uom => string.Equals(uom.Username, username, StringComparison.Ordinal));
            if (current != null)
            {
                current.Role = AdminRole;
                continue;
            }

            db.UserOrganizationMappings.Add(new UserOrganizationMapping
            {
                OrganizationId = org.Id,
                Username = username,
                Role = AdminRole
            });
        }
        await db.SaveChangesAsync();
        return Ok();
    }
}
