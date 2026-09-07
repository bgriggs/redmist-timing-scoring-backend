using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Social.Imaging;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class SocialController : SocialControllerBase
{
    public SocialController(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TsContext> tsContext,
        SocialImageCleanup imageCleanup,
        TimeProvider timeProvider)
        : base(loggerFactory, tsContext, imageCleanup, timeProvider)
    {
    }
}
