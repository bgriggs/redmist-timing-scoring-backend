using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using RedMist.Database;

namespace RedMist.Backend.Shared.Services;

/// <summary>
/// Validates per-event access codes for private events.
/// Caches event privacy state to avoid hitting the database on every subscribe / API call.
/// </summary>
public interface IEventAccessValidator
{
    /// <summary>
    /// Returns true if the supplied access code grants access to the given event.
    /// Non-private events always return true; private events require an exact code match.
    /// </summary>
    Task<bool> ValidateAsync(int eventId, string? accessCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached privacy state for the event (IsPrivate, AccessCode).
    /// AccessCode is only non-null for private events.
    /// </summary>
    Task<EventAccess> GetAccessAsync(int eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the cached privacy state for the event. Called when an event's configuration changes.
    /// </summary>
    Task InvalidateAsync(int eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Compact representation of an event's privacy configuration.
/// </summary>
public sealed record EventAccess(bool IsPrivate, string? AccessCode);

public sealed class EventAccessValidator : IEventAccessValidator
{
    private static readonly HybridCacheEntryOptions cacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    private readonly IDbContextFactory<TsContext> tsContext;
    private readonly HybridCache hcache;
    private readonly ILogger<EventAccessValidator> logger;

    public EventAccessValidator(IDbContextFactory<TsContext> tsContext, HybridCache hcache, ILogger<EventAccessValidator> logger)
    {
        this.tsContext = tsContext;
        this.hcache = hcache;
        this.logger = logger;
    }

    public async Task<EventAccess> GetAccessAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var key = string.Format(Consts.EVENT_ACCESS, eventId);
        return await hcache.GetOrCreateAsync(key,
            async ct =>
            {
                using var db = await tsContext.CreateDbContextAsync(ct);
                var row = await db.Events
                    .AsNoTracking()
                    .Where(e => e.Id == eventId)
                    .Select(e => new { e.IsPrivate, e.AccessCode })
                    .FirstOrDefaultAsync(ct);
                return row == null
                    ? new EventAccess(false, null)
                    : new EventAccess(row.IsPrivate, row.AccessCode);
            },
            cacheOptions, cancellationToken: cancellationToken);
    }

    public async Task<bool> ValidateAsync(int eventId, string? accessCode, CancellationToken cancellationToken = default)
    {
        var access = await GetAccessAsync(eventId, cancellationToken);
        if (!access.IsPrivate)
            return true;

        if (string.IsNullOrEmpty(access.AccessCode))
        {
            logger.LogWarning("Event {eventId} is marked private but has no access code configured; denying access", eventId);
            return false;
        }

        return string.Equals(accessCode, access.AccessCode, StringComparison.Ordinal);
    }

    public async Task InvalidateAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var key = string.Format(Consts.EVENT_ACCESS, eventId);
        await hcache.RemoveAsync(key, cancellationToken);
    }
}
