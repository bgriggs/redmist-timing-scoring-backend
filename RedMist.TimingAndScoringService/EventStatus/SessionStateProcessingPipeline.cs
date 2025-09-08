using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Threading.Tasks.Dataflow;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// 
/// </summary>
/// <see cref="DataflowPipeline.md"/>
public class SessionStateProcessingPipeline
{
    private ILogger Logger { get; }
    private readonly SessionContext sessionState;
    private readonly SemaphoreSlim stateLock = new(1, 1);

    // Input buffer for all incoming timing data
    private readonly BroadcastBlock<TimingMessage> input = new(tm => tm);

    // Specialized processors for different message types
    private readonly TransformBlock<TimingMessage, SessionStateUpdate?> rmonitorProcessorBlock;
    private readonly TransformBlock<TimingMessage, SessionStateUpdate?> multiloopProcessorBlock;
    private readonly TransformBlock<TimingMessage, SessionStateUpdate?> pitProcessorBlock;
    private readonly TransformBlock<TimingMessage, SessionStateUpdate?> flagProcessorBlock;
    private readonly TransformBlock<SessionStateUpdate?, SessionStateUpdate?> positionMetadataBlock;
    private readonly TransformBlock<SessionStateUpdate?, (SessionStatePatch?, CarPositionPatch[])> updateConsolidatorBlock;
    private readonly ActionBlock<(SessionStatePatch?, CarPositionPatch[])> statusAggregatorBlock;

    private readonly RMonitorDataProcessorV2 rMonitorDataProcessorV2;
    private readonly MultiloopProcessor multiloopProcessor;
    private readonly PitProcessorV2 pitProcessorV2;
    private readonly FlagProcessorV2 flagProcessorV2;
    private readonly PositionDataEnricher positionEnricher;
    private readonly UpdateConsolidator updateConsolidator;


    public SessionStateProcessingPipeline(SessionContext initialState, ILoggerFactory loggerFactory,
        RMonitorDataProcessorV2 rMonitorDataProcessorV2,
        MultiloopProcessor multiloopProcessor,
        PitProcessorV2 pitProcessorV2,
        FlagProcessorV2 flagProcessorV2,
        PositionDataEnricher positionEnricher,
        UpdateConsolidator updateConsolidator,
        StatusAggregatorV2 statusAggregatorV2)
    {
        sessionState = initialState;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.rMonitorDataProcessorV2 = rMonitorDataProcessorV2;
        this.multiloopProcessor = multiloopProcessor;
        this.pitProcessorV2 = pitProcessorV2;
        this.flagProcessorV2 = flagProcessorV2;
        this.positionEnricher = positionEnricher;
        this.updateConsolidator = updateConsolidator;

        // Configure dataflow options
        //var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        var executionOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1, // Ensure sequential state updates
        };

        // Individual message processors - each examines and optionally handles messages
        rmonitorProcessorBlock = new TransformBlock<TimingMessage, SessionStateUpdate?>(rMonitorDataProcessorV2.Process, executionOptions);
        multiloopProcessorBlock = new TransformBlock<TimingMessage, SessionStateUpdate?>(multiloopProcessor.Process, executionOptions);
        pitProcessorBlock = new TransformBlock<TimingMessage, SessionStateUpdate?>(pitProcessorV2.Process, executionOptions);
        flagProcessorBlock = new TransformBlock<TimingMessage, SessionStateUpdate?>(flagProcessorV2.Process, executionOptions);
        positionMetadataBlock = new TransformBlock<SessionStateUpdate?, SessionStateUpdate?>(positionEnricher.Process, executionOptions);
        updateConsolidatorBlock = new TransformBlock<SessionStateUpdate?, (SessionStatePatch?, CarPositionPatch[])>(updateConsolidator.Process, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 20 });
        statusAggregatorBlock = new ActionBlock<(SessionStatePatch?, CarPositionPatch[])>(statusAggregatorV2.Process, executionOptions);

        // Link input to processors based on message type -- this is where the message decoding happens
        input.LinkTo(rmonitorProcessorBlock, tm => tm.Type == Backend.Shared.Consts.RMONITOR_TYPE);
        input.LinkTo(multiloopProcessorBlock, tm => tm.Type == Backend.Shared.Consts.MULTILOOP_TYPE);
        input.LinkTo(pitProcessorBlock, tm => tm.Type == Backend.Shared.Consts.X2PASS_TYPE || tm.Type == Backend.Shared.Consts.EVENT_CHANGED_TYPE);
        input.LinkTo(flagProcessorBlock, tm => tm.Type == Backend.Shared.Consts.FLAGS_TYPE);

        // Link RMonitor to position metadata enricher (filtering out null updates) -- this is inferred position data
        rmonitorProcessorBlock.LinkTo(positionMetadataBlock, update => update != null);

        // Link processors to update consolidator (filtering out null updates) -- this batches updates to reduce client update frequency
        rmonitorProcessorBlock.LinkTo(updateConsolidatorBlock, update => update != null);
        positionMetadataBlock.LinkTo(updateConsolidatorBlock, update => update != null);
        multiloopProcessorBlock.LinkTo(updateConsolidatorBlock, update => update != null);
        pitProcessorBlock.LinkTo(updateConsolidatorBlock, update => update != null);
        flagProcessorBlock.LinkTo(updateConsolidatorBlock, update => update != null);

        // Link updates to status aggregator -- this applies updates to session state and sends to clients
        updateConsolidatorBlock.LinkTo(statusAggregatorBlock);
    }

    /// <summary>
    /// Posts a timing message to the input of the processing pipeline.
    /// </summary>
    /// <param name="message">The timing message to process</param>
    /// <returns>True if the message was accepted for processing, false otherwise</returns>
    public bool Post(TimingMessage message)
    {
        return input.Post(message);
    }
}
