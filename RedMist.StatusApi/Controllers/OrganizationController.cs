﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;

namespace RedMist.StatusApi.Controllers;

[ApiController]
[Route("[controller]/[action]")]
[Authorize]
public class OrganizationController : ControllerBase
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;

    private ILogger Logger { get; }


    public OrganizationController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
    }


    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizationIcon(int organizationId)
    {
        Logger.LogTrace("GetOrganizationIcon for organization {organizationId}", organizationId);

        var cacheKey = $"org-icon-{organizationId}";
        var data = await hcache.GetOrCreateAsync(cacheKey,
            async entry => await LoadOrganizationIcon(organizationId),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) });

        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        if (organization == null || organization.Logo == null)
            return NotFound();
        var mimeType = GetImageMimeType(organization.Logo);
        return File(organization.Logo, mimeType);
    }

    private async Task<byte[]> LoadOrganizationIcon(int organizationId)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        return organization?.Logo ?? [];
    }

    private static string GetImageMimeType(byte[] imageBytes)
    {
        // Quick magic number detection (PNG, JPEG, GIF, BMP)
        if (imageBytes.Length >= 4)
        {
            if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 &&
                imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
                return "image/png";

            if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
                return "image/jpeg";

            if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 &&
                imageBytes[2] == 0x46)
                return "image/gif";

            if (imageBytes[0] == 0x42 && imageBytes[1] == 0x4D)
                return "image/bmp";
        }

        return "application/octet-stream"; // fallback
    }
}
