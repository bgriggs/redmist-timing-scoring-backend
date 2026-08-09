using Prometheus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.Flagtronics;
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
using RedMist.TimingCommon.Models.Mappers;
using System.Text.Json;

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

    /// <summary>RMonitor declares its track length in miles.</summary>
    private const double MetersPerMile = 1609.344;
    private readonly MultiloopProcessor multiloopProcessor;
    private readonly PitProcessor pitProcessor;
    private readonly FlagProcessor flagProcessor;
    private readonly SessionMonitor sessionMonitor;
    private readonly PositionDataEnricher positionEnricher;
    private readonly ControlLogEnricher controlLogEnricher;
    private readonly DriverModeProcessor driverModeProcessor;
    private readonly LapProcessor lapProcessor;
    private readonly DriverEnricher driverEnricher;
    private readonly VideoEnricher videoEnricher;
    private readonly FastestPaceEnricher fastestPaceEnricher;
    private readonly ProjectedLapTimeEnricher projectedLapTimeEnricher;
    private readonly TrackMapService trackMapService;
    private readonly GpsLapPositionEnricher gpsLapPositionEnricher;
    private readonly StaleCarEnricher staleCarEnricher;
    private readonly UpdateConsolidator updateConsolidator;
    private readonly FlagtronicsProcessor flagtronicsProcessor;
    private readonly TelemetrySignalTracker telemetrySignalTracker;

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
        FlagProcessor flagProcessor,
        SessionMonitor sessionMonitor,
        PositionDataEnricher positionEnricher,
        ControlLogEnricher controlLogEnricher,
        DriverModeProcessor driverModeProcessor,
        LapProcessor lapProcessor,
        DriverEnricher driverEnricher,
        VideoEnricher videoEnricher,
        FastestPaceEnricher fastestPaceEnricher,
        ProjectedLapTimeEnricher projectedLapTimeEnricher,
        TrackMapService trackMapService,
        GpsLapPositionEnricher gpsLapPositionEnricher,
        StaleCarEnricher staleCarEnricher,
        UpdateConsolidator updateConsolidator,
        FlagtronicsProcessor flagtronicsProcessor,
        TelemetrySignalTracker telemetrySignalTracker)
    {
        sessionContext = context;
        Logger = loggerFactory.CreateLogger(GetType().Name);

        // Store processor instances
        rMonitorProcessor = rMonitorDataProcessorV2;
        this.multiloopProcessor = multiloopProcessor;
        pitProcessor = pitProcessorV2;
        this.flagProcessor = flagProcessor;
        this.sessionMonitor = sessionMonitor;
        this.positionEnricher = positionEnricher;
        this.controlLogEnricher = controlLogEnricher;
        this.driverModeProcessor = driverModeProcessor;
        this.lapProcessor = lapProcessor;
        this.driverEnricher = driverEnricher;
        this.videoEnricher = videoEnricher;
        this.fastestPaceEnricher = fastestPaceEnricher;
        this.projectedLapTimeEnricher = projectedLapTimeEnricher;
        this.trackMapService = trackMapService;
        this.gpsLapPositionEnricher = gpsLapPositionEnricher;
        this.staleCarEnricher = staleCarEnricher;
        this.updateConsolidator = updateConsolidator;
        this.flagtronicsProcessor = flagtronicsProcessor;
        this.telemetrySignalTracker = telemetrySignalTracker;

        // Wire up flush of pending laps for mid-race reset handling
        rMonitorDataProcessorV2.FlushPendingLaps = () => lapProcessor.FlushPendingLapsAsync();

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

                        // The timing system declares the track's length; it is the only measure of
                        // the circuit that does not come from the GPS being checked against it.
                        if (rMonitorProcessor.TrackLength > 0)
                        {
                            await trackMapService.SetDeclaredLapLengthAsync(
                                rMonitorProcessor.TrackLength * MetersPerMile, sessionContext.CancellationToken);
                        }
                        if (rmonitorChanges != null)
                            allAppliedChanges.AddRange(rmonitorChanges);

                        // ** Pass 2 **
                        bool hasCarPatches = allAppliedChanges.SelectMany(c => c.CarPatches).Any();
                        if (hasCarPatches)
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

                                // Fallback: a completed lap means the car crossed the main S/F line
                                // on track. If Flagtronics reports it stuck in the pit with no GPS to
                                // self-correct, clear the frozen pit state.
                                var ftLapPatches = new List<CarPositionPatch>();
                                foreach (var cn in carNumbers)
                                {
                                    var ftLapPatch = flagtronicsProcessor.NotifyLapCompleted(cn ?? string.Empty);
                                    if (ftLapPatch != null)
                                        ftLapPatches.Add(ftLapPatch);

                                    // The car is demonstrably on track, so the in-pit hold on
                                    // changing its pit-state owner no longer applies.
                                    sessionContext.ReleasePitOwnershipHold(cn);
                                }
                                if (ftLapPatches.Count > 0)
                                    allAppliedChanges.Add(new PatchUpdates([], [.. ftLapPatches]));
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

                                // Re-apply Flagtronics in-car state when it is the active pit source
                                var ftPatch = flagtronicsProcessor.ProcessCar(cn ?? string.Empty);
                                if (ftPatch != null)
                                {
                                    pitPatches.Add(ftPatch);
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

                        // Apply stale car data in case of reset - only every 3 messages or when car patches were created
                        if (rmonitorMessageCounter % 3 == 0 || hasCarPatches)
                        {
                            var staleCarPatches = await staleCarEnricher.ProcessAsync();
                            if (staleCarPatches != null && staleCarPatches.Count > 0)
                                allAppliedChanges.Add(new PatchUpdates([], [.. staleCarPatches]));

                            // Telemetry signal decays on the absence of records, so it has to be
                            // driven from here rather than from the telemetry feed itself. Runs
                            // before the position sweep so a car dropping to zero bars is reflected
                            // in the same pass rather than the next one.
                            var signalChanges = telemetrySignalTracker.Process();
                            if (signalChanges != null)
                                allAppliedChanges.Add(signalChanges);
                            // Which source owns each car's pit state follows from the bars just
                            // recomputed above.
                            sessionContext.UpdatePitOwnership();

                            // Retire GPS-derived track positions here as well as on GPS arrival: when
                            // the GPS source goes down entirely there are no positions left to carry
                            // the sweep, and every car's position would otherwise stay frozen on screen.
                            var stalePositionPatches = gpsLapPositionEnricher.ExpireStalePositions();
                            if (stalePositionPatches.Count > 0)
                                allAppliedChanges.Add(new PatchUpdates([], [.. stalePositionPatches]));
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
                        await _sessionMonitorMetrics.TrackAsync(() => sessionMonitor.ProcessAsync(message));
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
                    else if (message.Type == Backend.Shared.Consts.LAP_COMPLETED_TYPE)
                    {
                        var patches = await fastestPaceEnricher.ProcessAsync(message);
                        if (patches.Count > 0)
                            allAppliedChanges.AddRange(new PatchUpdates([], [.. patches]));

                        var projectedPatch = await projectedLapTimeEnricher.ProcessAsync(message);
                        if (projectedPatch != null)
                            allAppliedChanges.AddRange(new PatchUpdates([], [projectedPatch]));
                    }
                    else if (message.Type == Backend.Shared.Consts.EXTERNAL_PATCH_TYPE)
                    {
                        var externalChanges = await ProcessExternal(message);
                        if (externalChanges != null)
                            allAppliedChanges.Add(externalChanges);
                    }
                    else if (message.Type == Backend.Shared.Consts.FLAGTRONICS_TYPE)
                    {
                        var flagtronicsChanges = await ProcessFlagtronics(message);
                        if (flagtronicsChanges != null)
                            allAppliedChanges.Add(flagtronicsChanges);
                    }
                }

                // ** Phase 3: Client Notification (outside the lock) **
                // Must stay outside: this reaches StatusAggregator, which takes the read lock, and
                // the lock is not reentrant - dispatching it from inside the block above would hang.
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
    /// Applies a batch of pre-formed patches from an external timing source. The source has already
    /// done all protocol parsing and produced RedMist patches, so this only applies them to the
    /// session state and surfaces them for client notification — no source-specific parsing and no
    /// position/gap re-enrichment (the external source computes those itself).
    ///
    /// Lap and flag logging are not skipped, though: completed laps and flag transitions carried by the
    /// patches are forwarded to the same lap/flag processors the RMonitor path uses so they persist.
    /// </summary>
    private async Task<PatchUpdates?> ProcessExternal(TimingMessage message)
    {
        try
        {
            var batch = JsonSerializer.Deserialize<ExternalPatchBatch>(message.Data);
            if (batch == null)
                return null;

            var sessionPatches = new List<SessionStatePatch>();
            foreach (var sp in batch.SessionPatches)
            {
                SessionStateMapper.ApplyPatch(sp, sessionContext.SessionState);
                sessionPatches.Add(sp);
            }

            var carPatches = new List<CarPositionPatch>();
            foreach (var cp in batch.CarPatches)
            {
                if (string.IsNullOrWhiteSpace(cp.Number))
                    continue;

                var car = sessionContext.GetCarByNumber(cp.Number);
                if (car != null)
                {
                    CarPositionMapper.ApplyPatch(cp, car);
                }
                else
                {
                    // New car: materialise from the patch and register it in the context.
                    sessionContext.UpdateCars([CarPositionMapper.PatchToEntity(cp)]);
                }
                carPatches.Add(cp);
            }

            // Corrected positions carried by this batch feed the track-map learner and each car's
            // position around the lap. This is new enrichment the external source does not compute,
            // so it runs here even though ProcessExternal otherwise skips re-enrichment. Its fixes
            // are not judged by the health of any in-car link - the source positions cars itself.
            //
            // A car that has not moved carries no coordinates of its own, so the batch as a whole
            // stands in for it: if anything in it carried a position, the source is demonstrably
            // still positioning, and every car it mentions counts as reporting. When no car in the
            // batch carries one, the position feed itself has gone quiet and positions are left to
            // age out - which is indistinguishable from every car having parked, so it errs toward
            // the time-based projection rather than holding positions that may have gone stale.
            if (carPatches.Any(cp => (cp.Latitude ?? 0) != 0 || (cp.Longitude ?? 0) != 0))
            {
                gpsLapPositionEnricher.MarkSeen(carPatches
                    .Select(cp => cp.Number)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()!, confirmed: false);
            }

            await ApplyGpsEnrichmentAsync(carPatches, fromInCarTelemetry: false);

            // Log completed laps. The external branch otherwise bypasses the lap processor that the
            // RMonitor path runs, so externally-sourced laps would never be persisted.
            var lapCompletionNumbers = carPatches
                .Where(cp => cp.LastLapCompleted != null && !string.IsNullOrWhiteSpace(cp.Number))
                .Select(cp => cp.Number)
                .ToList();
            if (lapCompletionNumbers.Count > 0)
            {
                var cars = sessionContext.SessionState.CarPositions
                    .Where(c => lapCompletionNumbers.Contains(c.Number))
                    .ToList();
                // The external source carries the flag on the session, not per car; stamp it on so the
                // lap log records the flag in force when the lap completed (as the RMonitor path does).
                foreach (var c in cars)
                    c.TrackFlag = sessionContext.SessionState.CurrentFlag;
                await lapProcessor.ProcessAsync(cars);
            }

            // Log flag transitions. The external source supplies the full flag-duration list (from its
            // own flags channel); forward it to the flag processor so it persists to the flag log.
            var flagDurations = sessionPatches.LastOrDefault(sp => sp.FlagDurations != null)?.FlagDurations;
            if (flagDurations != null)
            {
                await flagProcessor.ProcessFlags(
                    sessionContext.SessionState.SessionId, flagDurations, sessionContext.CancellationToken);
            }

            return new PatchUpdates([.. sessionPatches], [.. carPatches]);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing external patch batch");
            _pipelineErrors.WithLabels("external", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Applies Flagtronics vehicle records: per-car GPS, in-car pit state, flags, and driver
    /// source. Unlike the external lane, the primary timing source stays authoritative for
    /// laps and positions; Flagtronics only supplements/overrides its specific domains.
    /// </summary>
    private async Task<PatchUpdates?> ProcessFlagtronics(TimingMessage message)
    {
        try
        {
            // A message that changes nothing yields no updates, but it still says which cars are
            // reporting - and a device whose GPS has died is precisely a car that keeps sending
            // records while changing nothing. Bailing out here would leave its position on screen
            // indefinitely, so enrichment runs on every message rather than only on eventful ones.
            var updates = flagtronicsProcessor.Process(message);

            // Also swept here so an event driven by something other than RMonitor still gets
            // signal bars published - without them every car reads as untrusted and Flagtronics
            // pit detection would be silently disabled for the whole event. The sweep applies its
            // own patches as it computes them, so dropping the result would commit the change
            // server-side and never send it: the next sweep sees no delta and clients stay stale.
            var signalChanges = telemetrySignalTracker.Process();
            sessionContext.UpdatePitOwnership();

            // GPS positions feed the track-map learner and each car's position around the lap,
            // the same enrichment the external patch lane receives. A car reporting an unchanged
            // position produces no patch, so the cars still delivering a usable fix are passed
            // separately - a car sitting still is not the same as one that has gone off the air,
            // and neither is one whose device is talking while its GPS is dead. This runs before
            // any early return: a message that changed nothing is exactly what a device with dead
            // GPS produces, and that is when a stale position most needs retiring.
            var carPatches = updates?.CarPatches.ToList() ?? [];
            gpsLapPositionEnricher.MarkSeen(flagtronicsProcessor.CarsWithUsableFix, confirmed: true);
            await ApplyGpsEnrichmentAsync(carPatches, fromInCarTelemetry: true);

            // The vehicle records carry the driver the station has resolved for each car, which
            // covers far more of the field than the DriverID event feed does. The processor
            // publishes those to the driver cache; the enricher remains the one place a car's
            // driver is written, so the change is read back through it here rather than patched
            // directly. Without this the name would only appear on the next full driver sweep.
            // Guarded separately: the processor has already applied this message's GPS, pit and
            // flag changes to server state, so letting a Redis fault escape to the outer catch
            // would drop those patches while keeping the state they describe, freezing cars on
            // screen until their next change.
            try
            {
                foreach (var carNumber in await flagtronicsProcessor.PublishDriverInfoAsync())
                {
                    var driverPatch = await driverEnricher.ProcessCarAsync(carNumber);
                    if (driverPatch != null)
                        carPatches.Add(driverPatch);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error publishing Flagtronics driver info");
            }

            var sessionPatches = (updates?.SessionPatches ?? []).ToList();
            if (signalChanges != null)
            {
                carPatches.AddRange(signalChanges.CarPatches);
                sessionPatches.AddRange(signalChanges.SessionPatches);
            }

            if (carPatches.Count == 0 && sessionPatches.Count == 0)
                return null;

            return new PatchUpdates([.. sessionPatches], [.. carPatches]);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing Flagtronics vehicle data");
            _pipelineErrors.WithLabels("flagtronics", ex.GetType().Name).Inc();
            return null;
        }
    }

    /// <summary>
    /// Feeds patched GPS positions to the track-map learner and updates each moving car's position
    /// around the lap, appending any resulting patches to the list. Cars whose GPS has gone quiet
    /// have their position retired in the same pass, since a dropout produces no patch of its own.
    /// </summary>
    /// <param name="carPatches">Patches from this batch; enrichment appends to this list.</param>
    /// <param name="fromInCarTelemetry">
    /// Whether these positions came from the cars' own telemetry devices, whose link health is
    /// tracked as signal bars, rather than from the timing source.
    /// </param>
    private async Task ApplyGpsEnrichmentAsync(List<CarPositionPatch> carPatches, bool fromInCarTelemetry)
    {
        // Materialize before the loop: the loop appends track-position patches to carPatches,
        // which would otherwise invalidate this lazy query mid-enumeration.
        var gpsCars = carPatches
            .Where(cp => (cp.Latitude != null || cp.Longitude != null) && !string.IsNullOrWhiteSpace(cp.Number))
            .Select(cp => cp.Number!)
            .Distinct()
            .ToList();

        if (gpsCars.Count > 0)
        {
            await trackMapService.EnsureLoadedAsync(sessionContext.CancellationToken);
            foreach (var cn in gpsCars)
            {
                var car = sessionContext.GetCarByNumber(cn);
                if (car?.Latitude is double lat && car.Longitude is double lon)
                {
                    // Only the racing surface describes the racing line: a lap through the pits or
                    // across the paddock traces geometry the track does not have.
                    var onTrack = !car.IsInPit
                        && !(car.FlaggingZone is int zone && zone > FlagtronicsProcessor.MAX_ON_TRACK_ZONE);
                    await trackMapService.AddSampleAsync(cn, lat, lon, car.LastLapCompleted, onTrack,
                        sessionContext.CancellationToken);
                    var gpsPatch = await gpsLapPositionEnricher.ProcessCarAsync(car, fromInCarTelemetry);
                    if (gpsPatch != null)
                        carPatches.Add(gpsPatch);
                }
            }
        }

        // Sweep even when this batch carried no usable position: a source still talking to us while
        // every car's GPS is dead is exactly when positions need retiring, and it produces no
        // position patches to trigger the sweep by.
        carPatches.AddRange(gpsLapPositionEnricher.ExpireStalePositions());
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
