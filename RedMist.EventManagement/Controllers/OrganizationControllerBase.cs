using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.ControlLogs;
using RedMist.Database;
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
    protected ILogger Logger { get; }

    
    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="controlLogFactory">Factory to create control log providers.</param>
    protected OrganizationControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
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
    public virtual async Task<Organization?> LoadOrganization()
    {
        Logger.LogTrace("LoadOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        return await db.OrganizationExtView.FirstOrDefaultAsync(x => x.ClientId == clientId);
    }

    /// <summary>
    /// Updates organization configuration settings.
    /// </summary>
    /// <param name="organization">The organization with updated settings.</param>
    /// <returns>No content on success.</returns>
    /// <response code="200">Organization updated successfully.</response>
    /// <response code="401">User is not authorized to update this organization.</response>
    /// <remarks>
    /// <para>Users can only update their own organization's settings.</para>
    /// <para>Updatable fields include: Website, ControlLogType, ControlLogParams, Orbits configuration, and X2 timing system settings.</para>
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task UpdateOrganization(Organization organization)
    {
        Logger.LogTrace("UpdateOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org != null)
        {
            org.Website = organization.Website;
            org.ControlLogType = organization.ControlLogType;
            org.ControlLogParams = organization.ControlLogParams;
            org.Orbits = organization.Orbits;
            org.X2 = organization.X2;
            org.RMonitorIp = organization.RMonitorIp;
            org.RMonitorPort = organization.RMonitorPort;
            org.MultiloopIp = organization.MultiloopIp;
            org.MultiloopPort = organization.MultiloopPort;
            org.OrbitsLogsPath = organization.OrbitsLogsPath;
            await db.SaveChangesAsync();
        }
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
    public virtual async Task<ControlLogStatistics> GetControlLogStatistics(Organization organization)
    {
        Logger.LogTrace("GetControlLogStatistics");
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
}
