using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RedMist.Backend.Shared.Utilities;
using RedMist.ControlLogs;
using RedMist.Database;

namespace RedMist.EventManagement.Controllers.V1;

[Route("v{version:apiVersion}/[controller]/[action]")]
[Route("[controller]/[action]")]
[ApiVersion("1.0")]
public class OrganizationController : OrganizationControllerBase
{
    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, 
        IControlLogFactory controlLogFactory, AssetsCdn assetsCdn)
        : base(loggerFactory, tsContext, controlLogFactory, assetsCdn)
    {
    }
}
