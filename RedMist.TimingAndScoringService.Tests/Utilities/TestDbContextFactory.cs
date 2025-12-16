using Microsoft.EntityFrameworkCore;
using RedMist.Database;

namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// Simple implementation of IDbContextFactory for testing
/// </summary>
public class TestDbContextFactory(DbContextOptions<TsContext> options) : IDbContextFactory<TsContext>
{
    public TsContext CreateDbContext()
    {
        return new TsContext(options);
    }

    public async ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(new TsContext(options));
    }
}
