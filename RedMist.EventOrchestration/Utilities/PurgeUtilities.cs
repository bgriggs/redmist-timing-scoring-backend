using Microsoft.EntityFrameworkCore;
using RedMist.Database;

namespace RedMist.EventOrchestration.Utilities;

public class PurgeUtilities
{
    private readonly IDbContextFactory<TsContext> tsContext;
    private ILogger Logger { get; }

    public PurgeUtilities(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.tsContext = tsContext;
    }


    public async Task DeleteEventStatusLogsFromDatabaseAsync(int eventId, long totalLogs, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Deleting {count} logs from database for event {eventId}", totalLogs, eventId);
        await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

        // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
        try
        {
            await dbContext.EventStatusLogs
                .Where(e => e.EventId == eventId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
        {
            // Fallback for InMemory database
            var logsToDelete = await dbContext.EventStatusLogs
                .Where(e => e.EventId == eventId)
                .ToListAsync(cancellationToken);
            dbContext.EventStatusLogs.RemoveRange(logsToDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

        public async Task DeleteLapsFromDatabaseAsync(int eventId, int sessionId, long totalLaps, CancellationToken cancellationToken)
        {
            Logger.LogInformation("Deleting {count} laps from database for event {eventId}, session {sessionId}", totalLaps, eventId, sessionId);
            await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);

            // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
            try
            {
                await dbContext.CarLapLogs
                    .Where(l => l.EventId == eventId && l.SessionId == sessionId)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
            {
                // Fallback for InMemory database
                var lapsToDelete = await dbContext.CarLapLogs
                    .Where(l => l.EventId == eventId && l.SessionId == sessionId)
                    .ToListAsync(cancellationToken);
                dbContext.CarLapLogs.RemoveRange(lapsToDelete);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        public async Task DeleteAllEventDataAsync(int eventId, CancellationToken cancellationToken)
        {
            Logger.LogInformation("Deleting all data for event {eventId}", eventId);
            await using var dbContext = await tsContext.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                // ExecuteDeleteAsync is not supported by InMemory database, so we need to handle both approaches
                try
                {
                    // Delete child tables first to maintain referential integrity
                    await dbContext.CarLapLogs
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.CarLastLaps
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.CompetitorMetadata
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.EventStatusLogs
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.FlagLog
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.SessionResults
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.Sessions
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.X2Loops
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await dbContext.X2Passings
                        .Where(e => e.EventId == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    // Delete the event itself last
                    await dbContext.Events
                        .Where(e => e.Id == eventId)
                        .ExecuteDeleteAsync(cancellationToken);

                    await transaction.CommitAsync(cancellationToken);
                    Logger.LogInformation("Successfully deleted all data for event {eventId}", eventId);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("ExecuteDelete"))
                {
                    // Fallback for InMemory database
                    Logger.LogDebug("Using fallback delete method for event {eventId}", eventId);

                    var carLapLogs = await dbContext.CarLapLogs
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.CarLapLogs.RemoveRange(carLapLogs);

                    var carLastLaps = await dbContext.CarLastLaps
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.CarLastLaps.RemoveRange(carLastLaps);

                    var competitorMetadata = await dbContext.CompetitorMetadata
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.CompetitorMetadata.RemoveRange(competitorMetadata);

                    var eventStatusLogs = await dbContext.EventStatusLogs
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.EventStatusLogs.RemoveRange(eventStatusLogs);

                    var flagLogs = await dbContext.FlagLog
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.FlagLog.RemoveRange(flagLogs);

                    var sessionResults = await dbContext.SessionResults
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.SessionResults.RemoveRange(sessionResults);

                    var sessions = await dbContext.Sessions
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.Sessions.RemoveRange(sessions);

                    var x2Loops = await dbContext.X2Loops
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.X2Loops.RemoveRange(x2Loops);

                    var x2Passings = await dbContext.X2Passings
                        .Where(e => e.EventId == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.X2Passings.RemoveRange(x2Passings);

                    var events = await dbContext.Events
                        .Where(e => e.Id == eventId)
                        .ToListAsync(cancellationToken);
                    dbContext.Events.RemoveRange(events);

                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    Logger.LogInformation("Successfully deleted all data for event {eventId} using fallback method", eventId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting all data for event {eventId}, rolling back transaction", eventId);
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

    }
