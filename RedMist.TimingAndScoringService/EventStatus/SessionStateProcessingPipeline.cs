using Prometheus;
using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.LapData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.PenaltyEnricher;
using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Responsible for processing session state updates with sequential processing and immediate context.
/// </summary>
/// <see cref="DataflowPipeline.md"/>
public class SessionStateProcessingPipeline
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionContext;

    // Processor instances
    private readonly RMonitorDataProcessorV2 rMonitorProcessor;
    private readonly MultiloopProcessor multiloopProcessor;
    private readonly PitProcessorV2 pitProcessor;
    private readonly FlagProcessorV2 flagProcessor;
    private readonly SessionMonitorV2 sessionMonitor;
    private readonly PositionDataEnricher positionEnricher;
    private readonly ControlLogEnricher controlLogEnricher;
    private readonly ResetProcessor resetProcessor;
    private readonly LapProcessor lapProcessor;
    private readonly UpdateConsolidator updateConsolidator;
    private readonly StatusAggregatorV2 statusAggregator;

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
    private readonly Counter _inputMessagesTotal = Metrics.CreateCounter(
        "pipeline_input_messages_total",
        "Total number of messages received by the pipeline",
        ["message_type"]);

    private readonly Gauge _pipelineHealth = Metrics.CreateGauge(
        "pipeline_health_score",
        "Overall health score of the pipeline (0-1)");

    private readonly Counter _pipelineErrors = Metrics.CreateCounter(
        "pipeline_errors_total",
        "Total number of errors in the pipeline",
        ["processor_name", "error_type"]);

    public SessionStateProcessingPipeline(SessionContext context, ILoggerFactory loggerFactory,
        RMonitorDataProcessorV2 rMonitorDataProcessorV2,
        MultiloopProcessor multiloopProcessor,
        PitProcessorV2 pitProcessorV2,
        FlagProcessorV2 flagProcessorV2,
        SessionMonitorV2 sessionMonitorV2,
        PositionDataEnricher positionEnricher,
        ControlLogEnricher controlLogEnricher,
        ResetProcessor resetProcessor,
        LapProcessor lapProcessor,
        UpdateConsolidator updateConsolidator,
        StatusAggregatorV2 statusAggregatorV2)
    {
        sessionContext = context;
        Logger = loggerFactory.CreateLogger(GetType().Name);

        // Store processor instances
        rMonitorProcessor = rMonitorDataProcessorV2;
        this.multiloopProcessor = multiloopProcessor;
        pitProcessor = pitProcessorV2;
        flagProcessor = flagProcessorV2;
        sessionMonitor = sessionMonitorV2;
        this.positionEnricher = positionEnricher;
        this.controlLogEnricher = controlLogEnricher;
        this.resetProcessor = resetProcessor;
        this.lapProcessor = lapProcessor;
        this.updateConsolidator = updateConsolidator;
        statusAggregator = statusAggregatorV2;

        // Wire up the notification from pit processor to lap processor
        pitProcessorV2.NotifyLapProcessorOfPitMessages = async (carNumbers) =>
        {
            foreach (var carNumber in carNumbers)
            {
                await lapProcessor.ProcessPendingLapForCar(carNumber);
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
    public async Task Post(TimingMessage message)
    {
        try
        {
            await _overallProcessorMetrics.TrackAsync(async () =>
            {
                var allAppliedChanges = new List<PatchUpdates>();

                // Acquire write lock once for the entire message processing
                using (await sessionContext.SessionStateLock.AcquireWriteLockAsync(sessionContext.CancellationToken))
                {
                    // ** Phase 1: Primary Message Processing  **
                    if (message.Type == Backend.Shared.Consts.RMONITOR_TYPE)
                    {
                        var rmonitorChanges = await _rmonitorMetrics.TrackAsync(() =>
                            rMonitorProcessor.ProcessAsync(message, sessionContext));
                        if (rmonitorChanges != null)
                            allAppliedChanges.AddRange(rmonitorChanges);

                        // ** Phase 2: Position Enrichment **
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
                                await lapProcessor.Process(cars);
                            }

                            // Apply pit data in case of reset
                            var distinctNumbers = allAppliedChanges.SelectMany(c => c.CarPatches).Select(c => c.Number).Distinct();
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

                            // Apply penalties
                            var penaltyPatches = controlLogEnricher.Process();
                            if (penaltyPatches != null && penaltyPatches.Count > 0)
                                allAppliedChanges.Add(new PatchUpdates([], [.. penaltyPatches]));
                        }
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
