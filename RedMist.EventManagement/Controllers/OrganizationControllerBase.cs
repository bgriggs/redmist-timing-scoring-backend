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
/// Base controller for Organization management across API versions
/// </summary>
[ApiController]
[Authorize]
public abstract class OrganizationControllerBase : Controller
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly IControlLogFactory controlLogFactory;
    protected ILogger Logger { get; }


    protected OrganizationControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
    }


    [HttpGet]
    public virtual async Task<Organization?> LoadOrganization()
    {
        Logger.LogTrace("LoadOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        return await db.OrganizationExtView.FirstOrDefaultAsync(x => x.ClientId == clientId);
    }

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
            await db.SaveChangesAsync();
        }
    }

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
