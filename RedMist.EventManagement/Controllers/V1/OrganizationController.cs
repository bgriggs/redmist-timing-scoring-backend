using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.ControlLogs;
using RedMist.Database;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class OrganizationController : OrganizationControllerBase
{
    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IControlLogFactory controlLogFactory)
        : base(loggerFactory, tsContext, controlLogFactory)
    {
    }
}
