using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using System.Threading.Tasks.Dataflow;

namespace RedMist.TimingAndScoringService.EventStatus;

public class SessionStateProcessingPipeline
{
    private ILogger Logger { get; }
    private readonly SessionState sessionState;
    private readonly SemaphoreSlim stateLock = new(1, 1);

    // Input buffer for all incoming timing data
    private readonly BroadcastBlock<TimingMessage> inputBroadcast;

    public SessionStateProcessingPipeline(SessionState initialState, ILoggerFactory loggerFactory)
    {
        sessionState = initialState;
        Logger = loggerFactory.CreateLogger(GetType().Name);

        // Configure dataflow options
        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        var executionOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1, // Ensure sequential state updates
            BoundedCapacity = 100
        };

        // Input broadcast - distributes incoming messages to all processors
        inputBroadcast = new BroadcastBlock<TimingMessage>(msg => msg,
            new DataflowBlockOptions { BoundedCapacity = 50 });

        //// Individual message processors - each examines and optionally handles messages
        //rmonitorProcessor = new TransformBlock<TimingMessage, SessionStateUpdate?>(
        //    ProcessRMonitorMessage, executionOptions);

        //multiloopProcessor = new TransformBlock<TimingMessage, SessionStateUpdate?>(
        //    ProcessMultiloopMessage, executionOptions);

        //x2PassingProcessor = new TransformBlock<TimingMessage, SessionStateUpdate?>(
        //    ProcessX2PassingMessage, executionOptions);

        //flagProcessor = new TransformBlock<TimingMessage, SessionStateUpdate?>(
        //    ProcessFlagMessage, executionOptions);

        //// State updater - applies updates to the shared session state
        //stateUpdater = new ActionBlock<SessionStateUpdate>(
        //    ApplyStateUpdate, executionOptions);

        //// Output broadcast for state changes
        //stateChangeBroadcast = new BroadcastBlock<SessionState>(state => state,
        //    new DataflowBlockOptions { BoundedCapacity = 10 });

        // Link the pipeline
        SetupPipeline(linkOptions);
    }

    private void SetupPipeline(DataflowLinkOptions linkOptions)
    {
        //// Connect input to all processors
        //inputBroadcast.LinkTo(rmonitorProcessor, linkOptions);
        //inputBroadcast.LinkTo(multiloopProcessor, linkOptions);
        //inputBroadcast.LinkTo(x2PassingProcessor, linkOptions);
        //inputBroadcast.LinkTo(flagProcessor, linkOptions);

        //// Connect processors to state updater (filtering out null updates)
        //rmonitorProcessor.LinkTo(stateUpdater, linkOptions, update => update != null);
        //multiloopProcessor.LinkTo(stateUpdater, linkOptions, update => update != null);
        //x2PassingProcessor.LinkTo(stateUpdater, linkOptions, update => update != null);
        //flagProcessor.LinkTo(stateUpdater, linkOptions, update => update != null);

        //// Discard null updates
        //var nullTarget = DataflowBlock.NullTarget<SessionStateUpdate?>();
        //rmonitorProcessor.LinkTo(nullTarget, update => update == null);
        //multiloopProcessor.LinkTo(nullTarget, update => update == null);
        //x2PassingProcessor.LinkTo(nullTarget, update => update == null);
        //flagProcessor.LinkTo(nullTarget, update => update == null);
    }
}
