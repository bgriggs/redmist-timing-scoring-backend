using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using StackExchange.Redis;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class EventController : EventControllerBase
{
    public EventController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, IConnectionMultiplexer cacheMux)
        : base(loggerFactory, tsContext, cacheMux)
    {
    }
}
