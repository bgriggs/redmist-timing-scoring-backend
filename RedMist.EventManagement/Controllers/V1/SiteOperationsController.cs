using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using StackExchange.Redis;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class SiteOperationsController : SiteOperationsControllerBase
{
    public SiteOperationsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConnectionMultiplexer cacheMux, HybridCache hcache, TimeProvider clock)
        : base(loggerFactory, tsContext, cacheMux, hcache, clock)
    {
    }
}
