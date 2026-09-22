using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class ViewershipController : ViewershipControllerBase
{
    public ViewershipController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConfiguration configuration, HybridCache hcache, TimeProvider clock)
        : base(loggerFactory, tsContext, configuration, hcache, clock)
    {
    }
}
