using RedMist.Backend.Shared.Services;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Extensions;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;

namespace RedMist.TimingAndScoringService.EventStatus;

public class ConsistencyCheckService : BackgroundService
{
    private readonly SessionContext sessionContext;
    private readonly IMediator mediator;
    private readonly TimeProvider timeProvider;
    private readonly ConsistencyCheckOptions options;
    private DateTimeOffset lastConsistencyError = DateTime.MinValue;
    private DateTimeOffset lastRelayForceReconnect = DateTime.MinValue;

    private ILogger Logger { get; }

    public ConsistencyCheckService(ILoggerFactory loggerFactory, SessionContext sessionContext,
        IMediator mediator, TimeProvider? timeProvider = null, IOptions<ConsistencyCheckOptions>? options = null, 
        ConsistencyCheckOptions? directOptions = null)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.sessionContext = sessionContext;
        this.mediator = mediator;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.options = directOptions ?? options?.Value ?? new ConsistencyCheckOptions();
    }

    // Expose internal state for testing
    public DateTimeOffset LastConsistencyError => lastConsistencyError;
    public DateTimeOffset LastRelayForceReconnect => lastRelayForceReconnect;
    public ConsistencyCheckOptions Options => options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Logger.LogInformation("Checking car consistency...");
                await PerformConsistencyCheck(stoppingToken);
                await Task.Delay(options.MainLoopInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "An error occurred in ConsistencyCheckService.");
                Logger.LogInformation("Throttling service for {delay}", options.ErrorThrottleDelay);
                await Task.Delay(options.ErrorThrottleDelay, stoppingToken);
            }
        }
    }

    public async Task PerformConsistencyCheck(CancellationToken stoppingToken)
    {
        var cars = await GetCarsAsync(stoppingToken);
        var areConsistent = CarsConsistencyCheck.AreCarsConsistent(cars, Logger);
        if (!areConsistent)
        {
            // Perform quick re-checks to see if the inconsistency resolves itself
            // with new timing system updates
            int retryCount = 0;
            while (retryCount < options.MaxConsistencyErrorsBeforeRelayReset)
            {
                await Task.Delay(options.ConsistencyErrorRecheckInterval, stoppingToken);
                cars = await GetCarsAsync(stoppingToken);
                areConsistent = CarsConsistencyCheck.AreCarsConsistent(cars, Logger);
                if (areConsistent)
                {
                    Logger.LogInformation("Car consistency restored after {retries} retries", retryCount);
                    break;
                }
                retryCount++;
            }
            if (!areConsistent)
            {
                await SendRelayReset();
            }
        }
    }

    protected virtual async Task<ImmutableList<CarPosition>> GetCarsAsync(CancellationToken stoppingToken)
    {
        using (await sessionContext.SessionStateLock.AcquireReadLockAsync(stoppingToken))
        {
            return [.. sessionContext.SessionState.CarPositions.DeepCopy()];
        }
    }

    protected virtual async Task SendRelayReset()
    {
        bool forceTimingDataReset = false;

        var now = timeProvider.GetUtcNow();
        var timeSinceLastReset = (now - lastConsistencyError).TotalMinutes;
        var timeSinceLastForceTimingDataReset = (now - lastRelayForceReconnect).TotalMinutes;

        // Do not reset relay more often than once every minute
        // Do not force timing data system reconnect more often than once every 3 minutes
        if (timeSinceLastReset >= options.MinResetIntervalMinutes && 
            timeSinceLastReset < options.ForceResetThresholdMinutes && 
            timeSinceLastForceTimingDataReset >= options.MinForceReconnectIntervalMinutes)
        {
            forceTimingDataReset = true;
            Logger.LogInformation("Consistency check: forcing timing data reset due to recent cached data request failure to resolve.");
        }

        await mediator.Publish(new RelayResetRequest { EventId = sessionContext.EventId, ForceTimingDataReset = forceTimingDataReset }, CancellationToken.None);

        lastConsistencyError = timeProvider.GetUtcNow();
        if (forceTimingDataReset)
        {
            lastRelayForceReconnect = timeProvider.GetUtcNow();
        }

        Logger.LogWarning("Sending relay reset after {maxRetries} retries, force reconnect = {rc}", options.MaxConsistencyErrorsBeforeRelayReset, forceTimingDataReset);
    }

    // For testing purposes - allows resetting internal state
    public void ResetInternalState()
    {
        lastConsistencyError = DateTime.MinValue;
        lastRelayForceReconnect = DateTime.MinValue;
    }
}

public class ConsistencyCheckOptions
{
    public TimeSpan MainLoopInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ErrorThrottleDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConsistencyErrorRecheckInterval { get; set; } = TimeSpan.FromMilliseconds(750);
    public int MaxConsistencyErrorsBeforeRelayReset { get; set; } = 3;
    public double MinResetIntervalMinutes { get; set; } = 1.0;
    public double ForceResetThresholdMinutes { get; set; } = 2.0;
    public double MinForceReconnectIntervalMinutes { get; set; } = 3.0;
}
