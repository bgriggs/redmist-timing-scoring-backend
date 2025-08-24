using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.ControlLogs;
using RedMist.Database;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class OrganizationController : Controller
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly IControlLogFactory controlLogFactory;

    private ILogger Logger { get; }


    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.controlLogFactory = controlLogFactory;
    }


    [HttpGet]
    public async Task<Organization?> LoadOrganization()
    {
        Logger.LogTrace("LoadOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        return await db.OrganizationExtView.FirstOrDefaultAsync(x => x.ClientId == clientId);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task UpdateOrganization(Organization organization)
    {
        Logger.LogTrace("UpdateOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        var org = await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (org != null)
        {
            //org.Name = organization.Name;
            org.Website = organization.Website;
            //org.Logo = organization.Logo;
            org.ControlLogType = organization.ControlLogType;
            org.ControlLogParams = organization.ControlLogParams;
            org.Orbits = organization.Orbits;
            org.X2 = organization.X2;
            await db.SaveChangesAsync();
        }
    }

    [HttpPost]
    public async Task<ControlLogStatistics> GetControlLogStatistics(Organization organization)
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
