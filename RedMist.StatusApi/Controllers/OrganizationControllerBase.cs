using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Database;
using RedMist.Database.Extensions;

namespace RedMist.StatusApi.Controllers;

/// <summary>
/// Base controller for Organization-related operations.
/// Provides endpoints for retrieving organization information and branding assets.
/// </summary>
/// <remarks>
/// This is an abstract base controller inherited by versioned controllers.
/// </remarks>
[ApiController]
[Authorize]
public abstract class OrganizationControllerBase : ControllerBase
{
    protected readonly IDbContextFactory<TsContext> tsContext;
    protected readonly HybridCache hcache;
    protected ILogger Logger { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizationControllerBase"/> class.
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers.</param>
    /// <param name="tsContext">Database context factory for timing and scoring data.</param>
    /// <param name="hcache">Hybrid cache for distributed caching.</param>
    protected OrganizationControllerBase(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext, HybridCache hcache)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
        this.hcache = hcache;
    }

    /// <summary>
    /// Retrieves the organization logo/icon as an image file.
    /// </summary>
    /// <param name="organizationId">The unique identifier of the organization.</param>
    /// <returns>The organization logo as an image file with appropriate MIME type.</returns>
    /// <response code="200">Returns the organization logo image.</response>
    /// <response code="404">Organization not found or has no logo.</response>
    /// <remarks>
    /// <para>This endpoint is publicly accessible (no authentication required).</para>
    /// <para>Supported image formats: PNG, JPEG, GIF, BMP</para>
    /// <para>Results are cached for 30 minutes to improve performance.</para>
    /// </remarks>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<IActionResult> GetOrganizationIcon(int organizationId)
    {
        Logger.LogTrace("GetOrganizationIcon for organization {organizationId}", organizationId);

        var cacheKey = $"org-icon-{organizationId}";
        var data = await hcache.GetOrCreateAsync(cacheKey,
            async entry => await LoadOrganizationIcon(organizationId),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) });

        if (data == null)
            return NotFound();
        var mimeType = GetImageMimeType(data);
        return File(data, mimeType);
    }

    /// <summary>
    /// Loads organization logo from the database.
    /// </summary>
    /// <param name="organizationId">The unique identifier of the organization.</param>
    /// <returns>Logo image bytes, or empty array if not found.</returns>
    protected async Task<byte[]> LoadOrganizationIcon(int organizationId)
    {
        using var context = await tsContext.CreateDbContextAsync();
        var organization = await context.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);
        if (organization?.Logo == null)
        {
            return context.DefaultOrgImages.FirstOrDefault()?.ImageData ?? [];
        }
        return organization?.Logo ?? [];
    }

    /// <summary>
    /// Determines the MIME type of an image based on its magic number (file signature).
    /// </summary>
    /// <param name="imageBytes">The image file bytes to analyze.</param>
    /// <returns>The MIME type string (e.g., "image/png", "image/jpeg").</returns>
    protected static string GetImageMimeType(byte[] imageBytes)
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
