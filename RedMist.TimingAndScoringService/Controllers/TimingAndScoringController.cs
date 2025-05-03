using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class TimingAndScoringController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;

    private ILogger Logger { get; }


    public TimingAndScoringController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
    }


    

   

   
}
