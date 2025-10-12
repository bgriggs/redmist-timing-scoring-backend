using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;

namespace RedMist.StatusApi.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class OrganizationController : OrganizationControllerBase
{
    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
        : base(loggerFactory, tsContext, hcache)
    {
    }
}
