using Prometheus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.InCarDriverMode;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.EventStatus.PenaltyEnricher;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.EventStatus.Video;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.EventStatus;

/// <summary>
/// Responsible for processing session state updates with sequential processing and immediate context.
/// </summary>
public class SessionStateProcessingPipeline
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;

    // Processor instances
    private readonly RMonitorDataProcessor rMonitorProcessor;
    private readonly MultiloopProcessor multiloopProcessor;
    private readonly PitProcessor pitProcessor;
    private readonly FlagProcessorV2 flagProcessor;
    private readonly SessionMonitor sessionMonitor;
    private readonly PositionDataEnricher positionEnricher;
    private readonly ControlLogEnricher controlLogEnricher;
    private readonly DriverModeProcessor driverModeProcessor;
    private readonly LapProcessor lapProcessor;
    private readonly DriverEnricher driverEnricher;
    private readonly VideoEnricher videoEnricher;
    private readonly UpdateConsolidator updateConsolidator;

    // Metrics for pipeline performance
    private readonly PipelineMetrics _overallProcessorMetrics = new("sequential_processor");
    private readonly PipelineMetrics _rmonitorMetrics = new("rmonitor_processor");
    private readonly PipelineMetrics _multiloopMetrics = new("multiloop_processor");
    private readonly PipelineMetrics _pitMetrics = new("pit_processor");
    private readonly PipelineMetrics _flagMetrics = new("flag_processor");
    private readonly PipelineMetrics _sessionMonitorMetrics = new("session_monitor");
    private readonly PipelineMetrics _positionMetadataMetrics = new("position_metadata");
    private readonly PipelineMetrics _updateConsolidatorMetrics = new("update_consolidator");

    // Overall pipeline metrics
    private readonly Gauge _pipelineHealth = Metrics.CreateGauge(
        "pipeline_health_score",
        "Overall health score of the pipeline (0-1)");

    private readonly Counter _pipelineErrors = Metrics.CreateCounter(
        "pipeline_errors_total",
        "Total number of errors in the pipeline",
        ["processor_name", "error_type"]);

    private const int EXTERNAL_DATA_FULL_UPDATE_MESSAGE_INTERVAL = 60;
    private long rmonitorMessageCounter = 0;


    public SessionStateProcessingPipeline(SessionContext context, ILoggerFactory loggerFactory,
        RMonitorDataProcessor rMonitorDataProcessorV2,
        MultiloopProcessor multiloopProcessor,
        PitProcessor pitProcessorV2,
        FlagProcessorV2 flagProcessorV2,
        SessionMonitor sessionMonitor,
        PositionDataEnricher positionEnricher,
        ControlLogEnricher controlLogEnricher,
        DriverModeProcessor driverModeProcessor,
        LapProcessor lapProcessor,
        DriverEnricher driverEnricher,
        VideoEnricher videoEnricher,
        UpdateConsolidator updateConsolidator)
    {
        sessionContext = context;
        Logger = loggerFactory.CreateLogger(GetType().Name);

        // Store processor instances
        rMonitorProcessor = rMonitorDataProcessorV2;
        this.multiloopProcessor = multiloopProcessor;
        pitProcessor = pitProcessorV2;
        flagProcessor = flagProcessorV2;
        this.sessionMonitor = sessionMonitor;
        this.positionEnricher = positionEnricher;
        this.controlLogEnricher = controlLogEnricher;
        this.driverModeProcessor = driverModeProcessor;
        this.lapProcessor = lapProcessor;
        this.driverEnricher = driverEnricher;
        this.videoEnricher = videoEnricher;
        this.updateConsolidator = updateConsolidator;

        // Wire up the notification from pit processor to lap processor
        pitProcessorV2.NotifyLapProcessorOfPitMessages = async (carNumbers) =>
        {
            foreach (var carNumber in carNumbers)
            {
                await lapProcessor.ProcessPendingLapForCarAsync(carNumber);
            }
        };

        // Start metrics monitoring
        StartMetricsMonitoring();
    }

    /// <summary>
    /// Posts a timing message to the input of the processing pipeline.
    /// </summary>
    /// <param name="message">The timing message to process</param>
    /// <returns>True if the message was accepted for processing, false otherwise</returns>
    public async Task PostAsync(TimingMessage message)
    {
        try
        {
            await _overallProcessorMetrics.TrackAsync(async () =>
            {
                var allAppliedChanges = new List<PatchUpdates>();

                // Acquire write lock once for the entire message processing
                using (await sessionContext.SessionStateLock.AcquireWriteLockAsync(sessionContext.CancellationToken))
                {
                    // ** Pass 1: Primary Message Processing **
                    if (message.Type == Backend.Shared.Consts.RMONITOR_TYPE)
                    {
                        var rmonitorChanges = await _rmonitorMetrics.TrackAsync(() =>
                            rMonitorProcessor.ProcessAsync(message, sessionContext));
                        if (rmonitorChanges != null)
                            allAppliedChanges.AddRange(rmonitorChanges);

                        // ** Pass 2 **
                        if (allAppliedChanges.SelectMany(c => c.CarPatches).Any())
                        {
                            var enrichmentChanges = ProcessPositionEnrichment();
                            if (enrichmentChanges != null)
                                allAppliedChanges.AddRange(enrichmentChanges);

                            // Check for any lap changes
                            var lapChanges = allAppliedChanges.SelectMany(c => c.CarPatches).Where(cp => cp.LastLapCompleted != null).ToList();
                            if (lapChanges.Count > 0)
                            {
                                var carNumbers = lapChanges.Select(c => c.Number).ToList();
                                var cars = sessionContext.SessionState.CarPositions.Where(c => carNumbers.Contains(c.Number)).ToList();
                                await lapProcessor.ProcessAsync(cars);

                                // Run check for changes to driver mode and send updates to cars
                                await driverModeProcessor.ProcessAsync(sessionContext.CancellationToken);
                            }

                            // Apply pit data in case of reset
                            var distinctNumbers = allAppliedChanges
                                .SelectMany(c => c.CarPatches)
                                .Select(c => c.Number)
                                .Distinct()
                                .Where(n => !string.IsNullOrWhiteSpace(n));

                            var pitPatches = new List<CarPositionPatch>();
                            foreach (var cn in distinctNumbers)
                            {
                                var pitPatch = pitProcessor.ProcessCar(cn ?? string.Empty);
                                if (pitPatch != null)
                                {
                                    pitPatches.Add(pitPatch);
                                }
                            }
                            allAppliedChanges.Add(new PatchUpdates([], [.. pitPatches]));

                            // Apply driver data in case of reset
                            var driverPatches = new List<CarPositionPatch>();
                            foreach (var cn in distinctNumbers)
                            {
                                var driverPatch = await driverEnricher.ProcessCarAsync(cn ?? string.Empty);
                                if (driverPatch != null)
                                {
                                    driverPatches.Add(driverPatch);
                                }
                            }
                            allAppliedChanges.Add(new PatchUpdates([], [.. driverPatches]));

                            // Apply video data in case of reset
                            var videoPatches = new List<CarPositionPatch>();
                            foreach (var cn in distinctNumbers)
                            {
                                var videoPatch = await videoEnricher.ProcessCarAsync(cn ?? string.Empty);
                                if (videoPatch != null)
                                {
                                    videoPatches.Add(videoPatch);
                                }
                            }
                            allAppliedChanges.Add(new PatchUpdates([], [.. videoPatches]));

                            // Apply multiloop data in case of reset
                            if (sessionContext.IsMultiloopActive)
                            {
                                multiloopProcessor.ApplyCarValues(sessionContext.SessionState.CarPositions);
                            }

                            // Apply penalties
                            var penaltyPatches = controlLogEnricher.Process();
                            if (penaltyPatches != null && penaltyPatches.Count > 0)
                                allAppliedChanges.Add(new PatchUpdates([], [.. penaltyPatches]));
                        }

                        // Perform a full external data update at defined intervals. 60 is at most once per
                        // minute assuming 1 RMonitor message per second for timer updates.
                        if (rmonitorMessageCounter % EXTERNAL_DATA_FULL_UPDATE_MESSAGE_INTERVAL == 0)
                        {
                            await driverEnricher.ProcessApplyFullAsync();
                            await videoEnricher.ProcessApplyFullAsync();
                        }

                        rmonitorMessageCounter++;
                    }
                    else if (message.Type == Backend.Shared.Consts.MULTILOOP_TYPE)
                    {
                        var multiloopChanges = ProcessMultiloop(message);
                        if (multiloopChanges != null)
                            allAppliedChanges.AddRange(multiloopChanges);
                    }
                    else if (message.Type == Backend.Shared.Consts.X2PASS_TYPE || message.Type == Backend.Shared.Consts.EVENT_CONFIGURATION_CHANGED)
                    {
                        var pitChanges = await ProcessPit(message);
                        if (pitChanges != null)
                            allAppliedChanges.AddRange(pitChanges);
                    }
                    else if (message.Type == Backend.Shared.Consts.FLAGS_TYPE)
                    {
                        var flagChanges = await ProcessFlag(message);
                        if (flagChanges != null)
                            allAppliedChanges.AddRange(flagChanges);
                    }
                    else if (message.Type == Backend.Shared.Consts.EVENT_SESSION_CHANGED_TYPE)
                    {
                        await _sessionMonitorMetrics.TrackAsync(() => sessionMonitor.Process(message));
                    }
                    else if (message.Type == Backend.Shared.Consts.DRIVER_EVENT_TYPE)
                    {
                        var patches = driverEnricher.Process(message);
                        if (patches != null)
                            allAppliedChanges.AddRange(patches);
                    }
                    else if (message.Type == Backend.Shared.Consts.DRIVER_TRANS_TYPE)
                    {
                        var patches = driverEnricher.Process(message);
                        if (patches != null)
                            allAppliedChanges.AddRange(patches);
                    }
                    else if (message.Type == Backend.Shared.Consts.VIDEO_TYPE)
                    {
                        var patches = videoEnricher.Process(message);
                        if (patches != null)
                            allAppliedChanges.AddRange(patches);
                    }
                }

                // ** Phase 3: Client Notification (outside the lock) **
                if (allAppliedChanges.Count > 0)
                {
                    _ = Task.Run(async () => await NotifyClients(allAppliedChanges));
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing message sequentially: {MessageType}", message.Type);
            _pipelineErrors.WithLabels("sequential_processor", ex.GetType().Name).Inc();
        }
    }

    /// <summary>
    /// Processes multiloop messages with immediate application.
    /// </summary>
    private PatchUpdates? ProcessMultiloop(TimingMessage message)
    {
        try
        {
            return _multiloopMetrics.Track(() =>
            {
                return multiloopProcessor.Process(message);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in multiloop processor");
            _pipelineErrors.WithLabels("multiloop", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Processes pit messages with immediate application.
    /// </summary>
    private async Task<PatchUpdates?> ProcessPit(TimingMessage message)
    {
        try
        {
            return await _pitMetrics.TrackAsync(async () =>
            {
                return await pitProcessor.Process(message);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in pit processor");
            _pipelineErrors.WithLabels("pit", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Processes flag messages with immediate application.
    /// </summary>
    private async Task<PatchUpdates?> ProcessFlag(TimingMessage message)
    {
        try
        {
            return await _flagMetrics.TrackAsync(async () =>
            {
                return await flagProcessor.Process(message);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in flag processor");
            _pipelineErrors.WithLabels("flag", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Processes position enrichment with immediate application.
    /// </summary>
    private PatchUpdates? ProcessPositionEnrichment()
    {
        try
        {
            return _positionMetadataMetrics.Track(positionEnricher.Process);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in position enrichment processor");
            _pipelineErrors.WithLabels("position_enrichment", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Notifies clients of the applied changes.
    /// </summary>
    private async Task NotifyClients(List<PatchUpdates> appliedChanges)
    {
        try
        {
            await _updateConsolidatorMetrics.TrackAsync(async () =>
            {
                foreach (var ap in appliedChanges)
                {
                    await updateConsolidator.Process(ap);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error notifying clients");
            _pipelineErrors.WithLabels("client_notification", ex.GetType().Name).Inc();
        }
    }

    /// <summary>
    /// Starts the metrics monitoring task.
    /// </summary>
    private void StartMetricsMonitoring()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    PrintPipelineMetrics();

                    // Update pipeline health score
                    var healthScore = CalculatePipelineHealth();
                    _pipelineHealth.Set(healthScore);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error in pipeline metrics monitoring");
                }
            }
        });
    }

    private void PrintPipelineMetrics()
    {
        try
        {
            var overallMetrics = _overallProcessorMetrics.GetCurrentMetrics();
            var rmonitorMetrics = _rmonitorMetrics.GetCurrentMetrics();
            var multiloopMetrics = _multiloopMetrics.GetCurrentMetrics();
            var pitMetrics = _pitMetrics.GetCurrentMetrics();
            var flagMetrics = _flagMetrics.GetCurrentMetrics();
            var sessionMonitorMetrics = _sessionMonitorMetrics.GetCurrentMetrics();
            var positionMetadataMetrics = _positionMetadataMetrics.GetCurrentMetrics();
            var updateConsolidatorMetrics = _updateConsolidatorMetrics.GetCurrentMetrics();

            Logger.LogDebug(Environment.NewLine +
                           "Overall: {SeqMsgs} processed, {SeqActive} active, {SeqAvgTime:F2}ms avg | " + Environment.NewLine +
                           "RMonitor: {RMonMsgs} processed, {RMonActive} active, {RMonAvgTime:F2}ms avg | " + Environment.NewLine +
                           "Multiloop: {MLMsgs} processed, {MLActive} active, {MLAvgTime:F2}ms avg | " + Environment.NewLine +
                           "Pit: {PitMsgs} processed, {PitActive} active, {PitAvgTime:F2}ms avg | " + Environment.NewLine +
                           "Flag: {FlagMsgs} processed, {FlagActive} active, {FlagAvgTime:F2}ms avg",
                overallMetrics.MessagesProcessed, overallMetrics.ActiveMessages, overallMetrics.AvgProcessingTime * 1000,
                rmonitorMetrics.MessagesProcessed, rmonitorMetrics.ActiveMessages, rmonitorMetrics.AvgProcessingTime * 1000,
                multiloopMetrics.MessagesProcessed, multiloopMetrics.ActiveMessages, multiloopMetrics.AvgProcessingTime * 1000,
                pitMetrics.MessagesProcessed, pitMetrics.ActiveMessages, pitMetrics.AvgProcessingTime * 1000,
                flagMetrics.MessagesProcessed, flagMetrics.ActiveMessages, flagMetrics.AvgProcessingTime * 1000);

            Logger.LogDebug(Environment.NewLine +
                           "SessionMonitor: {SMMsgs} processed, {SMActive} active, {SMAvgTime:F2}ms avg | " + Environment.NewLine +
                           "PositionMetadata: {PMMsgs} processed, {PMActive} active, {PMAvgTime:F2}ms avg" + Environment.NewLine +
                           "UpdateConsolidator: {UCMsgs} processed, {UCActive} active, {UCAvgTime:F2}ms avg | ",
                sessionMonitorMetrics.MessagesProcessed, sessionMonitorMetrics.ActiveMessages, sessionMonitorMetrics.AvgProcessingTime * 1000,
                positionMetadataMetrics.MessagesProcessed, positionMetadataMetrics.ActiveMessages, positionMetadataMetrics.AvgProcessingTime * 1000,
                updateConsolidatorMetrics.MessagesProcessed, updateConsolidatorMetrics.ActiveMessages, updateConsolidatorMetrics.AvgProcessingTime * 1000);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error logging pipeline metrics");
        }
    }

    private double CalculatePipelineHealth()
    {
        try
        {
            var allMetrics = new[]
            {
                _overallProcessorMetrics.GetCurrentMetrics(),
                _rmonitorMetrics.GetCurrentMetrics(),
                _multiloopMetrics.GetCurrentMetrics(),
                _pitMetrics.GetCurrentMetrics(),
                _flagMetrics.GetCurrentMetrics(),
                _sessionMonitorMetrics.GetCurrentMetrics(),
                _positionMetadataMetrics.GetCurrentMetrics(),
                _updateConsolidatorMetrics.GetCurrentMetrics(),
            };

            double healthScore = 1.0;

            // Check for backed up blocks (reduce health if any block has >10 active messages)
            foreach (var metrics in allMetrics)
            {
                if (metrics.ActiveMessages > 10)
                {
                    healthScore -= 0.1;
                }
            }

            // Check for slow processing (reduce health if any block averages >1 second)
            foreach (var metrics in allMetrics)
            {
                if (metrics.AvgProcessingTime > 1.0)
                {
                    healthScore -= 0.1;
                }
            }

            return Math.Max(0.0, Math.Min(1.0, healthScore));
        }
        catch
        {
            return 0.5; // Default to neutral health if calculation fails
        }
    }
}
