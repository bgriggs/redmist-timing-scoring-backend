using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.Database.Models;
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

    protected ILogger Logger { get; }


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
    /// Loads the organization details for the authenticated user.
    /// </summary>
    /// <returns>The organization details, or null if not found.</returns>
    /// <response code="200">Returns the organization details.</response>
    /// <remarks>
    /// The organization is determined by the authenticated user's client_id claim.
    /// </remarks>
    [HttpGet]
    [Produces("application/json", "application/x-msgpack")]
    [ProducesResponseType<Organization>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<Organization>> LoadOrganization()
    {
        Logger.LogTrace("{m}", nameof(LoadOrganization));
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound();

        if (org != null && org.Logo == null)
        {
            org.Logo = db.DefaultOrgImages.FirstOrDefault()?.ImageData;
        }
        return Ok(org);
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
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
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

            // Update logo
            Task? updateCdnTask = null;
            if (organization.Logo != null && organization.Logo.Length > 0)
            {
                org.Logo = organization.Logo;
                updateCdnTask = assetsCdn.SaveLogoAsync(org.Id, organization.Logo);
            }
            else if (organization.Logo != null && organization.Logo.Length == 0)
            {
                org.Logo = null;
                var defaultImage = db.DefaultOrgImages.FirstOrDefault();
                if (defaultImage != null)
                    updateCdnTask = assetsCdn.SaveLogoAsync(org.Id, defaultImage.ImageData);
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
        var cls = new ControlLogStatistics();
        if (!string.IsNullOrEmpty(organization.ControlLogType))
        {
            var cl = controlLogFactory.CreateControlLog(organization.ControlLogType);
            var logEntries = await cl.LoadControlLogAsync(organization.ControlLogParams);
            cls.IsConnected = logEntries.success;
            cls.TotalEntries = logEntries.logs.Count();
        }

        return cls;
    }

    /// <summary>
    /// Loads the list of administrator email addresses for the organization.
    /// </summary>
    /// <returns></returns>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<List<string>>> LoadOrganizationAdministratorsAsync()
    {
        Logger.LogTrace("{m}", nameof(LoadOrganizationAdministratorsAsync));
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound();
        var adminEmails = await db.UserOrganizationMappings.Where(uom => uom.OrganizationId == org.Id && uom.Role == "admin")
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
    public virtual async Task<IActionResult> SaveOrganizationAdministratorsAsync(List<string> usernames)
    {
        Logger.LogTrace("{m}", nameof(SaveOrganizationAdministratorsAsync));
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org == null)
            return NotFound();

        var existingAdmins = await db.UserOrganizationMappings
            .Where(uom => uom.OrganizationId == org.Id)
            .ToListAsync();
        db.UserOrganizationMappings.RemoveRange(existingAdmins);
        foreach (var username in usernames)
        {
            var newMapping = new UserOrganizationMapping
            {
                OrganizationId = org.Id,
                Username = username,
                Role = "admin"
            };
            db.UserOrganizationMappings.Add(newMapping);
        }
        await db.SaveChangesAsync();
        return Ok();
    }
}
