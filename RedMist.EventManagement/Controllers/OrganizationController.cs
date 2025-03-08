using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.TimingCommon.Models.Configuration;
using System.Security.Claims;

namespace RedMist.EventManagement.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class OrganizationController : Controller
{
    private readonly IDbContextFactory<TsContext> tsContext;

    private ILogger Logger { get; }


    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    [HttpGet]
    public async Task<Organization?> GetCurrentOrganization()
    {
        Logger.LogTrace("GetCurrentOrganization");
        var clientId = User.FindFirstValue("client_id");
        using var db = await tsContext.CreateDbContextAsync();
        return await db.Organizations.FirstOrDefaultAsync(x => x.ClientId == clientId);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task UpdateOrganization(Organization organization)
    {
        Logger.LogTrace("GetCurrentOrganization");
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
}
