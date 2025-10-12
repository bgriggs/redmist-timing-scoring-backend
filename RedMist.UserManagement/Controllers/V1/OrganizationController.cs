using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;

namespace RedMist.UserManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class OrganizationController : OrganizationControllerBase
{
    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IConfiguration configuration)
        : base(loggerFactory, tsContext, configuration)
    {
    }
}
