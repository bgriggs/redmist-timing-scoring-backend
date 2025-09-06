using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.X2;
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
    private readonly BufferBlock<TimingMessage> input;

    // Specialized processors for different message types
    private readonly ActionBlock<TimingMessage> rmonitorProcessorBlock;
    private readonly ActionBlock<TimingMessage> multiloopProcessorBlock;
    private readonly ActionBlock<TimingMessage> pitProcessorBlock;
    private readonly ActionBlock<TimingMessage> flagProcessorBlock;

    // Aggregator that applies updates to session state
    private readonly ActionBlock<SessionStateUpdate> stateUpdater;

    // Output for broadcasting state changes
    private readonly BroadcastBlock<SessionState> stateChangeBroadcast;
    
    private readonly RMonitorDataProcessorV2 rMonitorDataProcessorV2;
    private readonly MultiloopProcessor multiloopProcessor;
    private readonly PitProcessorV2 pitProcessorV2;
    private readonly FlagProcessorV2 flagProcessorV2;

    public SessionStateProcessingPipeline(SessionState initialState, ILoggerFactory loggerFactory,
        RMonitorDataProcessorV2 rMonitorDataProcessorV2,
        MultiloopProcessor multiloopProcessor,
        PitProcessorV2 pitProcessorV2,
        FlagProcessorV2 flagProcessorV2)
    {
        sessionState = initialState;
        Logger = loggerFactory.CreateLogger(GetType().Name);
        this.rMonitorDataProcessorV2 = rMonitorDataProcessorV2;
        this.multiloopProcessor = multiloopProcessor;
        this.pitProcessorV2 = pitProcessorV2;
        this.flagProcessorV2 = flagProcessorV2;

        // Configure dataflow options
        //var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };
        var executionOptions = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1, // Ensure sequential state updates
            //BoundedCapacity = 100
        };

        // Input buffer - distributes incoming messages to processors
        input = new BufferBlock<TimingMessage>();

        // Individual message processors - each examines and optionally handles messages
        rmonitorProcessorBlock = new ActionBlock<TimingMessage>(rMonitorDataProcessorV2.Process);
        multiloopProcessorBlock = new ActionBlock<TimingMessage>(multiloopProcessor.Process);
        pitProcessorBlock = new ActionBlock<TimingMessage>(pitProcessorV2.Process);
        flagProcessorBlock = new ActionBlock<TimingMessage>(flagProcessorV2.Process);

        // Link input to processors
        //input.LinkTo(rmonitorProcessorBlock, linkOptions);

        // Output broadcast for state changes
        stateChangeBroadcast = new BroadcastBlock<SessionState>(state => state,
            new DataflowBlockOptions { BoundedCapacity = 10 });

        // Link the pipeline
        //SetupPipeline(linkOptions);
    }

    //private void SetupPipeline(DataflowLinkOptions linkOptions)
    //{
    //    // Connect input to all processors
    //    input.LinkTo(rmonitorProcessorBlock, linkOptions);
    //    input.LinkTo(multiloopProcessorBlock, linkOptions);
    //    input.LinkTo(pitProcessorBlock, linkOptions);

    //    // Connect processors to state updater (filtering out null updates)
    //    rmonitorProcessorBlock.LinkTo(stateUpdater, linkOptions, update => update != null);
    //    multiloopProcessorBlock.LinkTo(stateUpdater, linkOptions, update => update != null);
    //    pitProcessorBlock.LinkTo(stateUpdater, linkOptions, update => update != null);

    //    // Discard null updates
    //    var nullTarget = DataflowBlock.NullTarget<SessionStateUpdate?>();
    //    rmonitorProcessorBlock.LinkTo(nullTarget, update => update == null);
    //    multiloopProcessorBlock.LinkTo(nullTarget, update => update == null);
    //    pitProcessorBlock.LinkTo(nullTarget, update => update == null);
    //}

    /// <summary>
    /// Posts a timing message to the input of the processing pipeline.
    /// </summary>
    /// <param name="message">The timing message to process</param>
    /// <returns>True if the message was accepted for processing, false otherwise</returns>
    public bool Post(TimingMessage message)
    {
        return input.Post(message);
    }

    /// <summary>
    /// Subscribes to state changes from the pipeline.
    /// </summary>
    /// <returns>IDisposable that represents the subscription</returns>
    public IDisposable Subscribe(ITargetBlock<SessionState> target)
    {
        var link = stateChangeBroadcast.LinkTo(target);
        return link;
    }

    /// <summary>
    /// Gets the current session state.
    /// </summary>
    public async Task<SessionState> GetCurrentStateAsync()
    {
        await stateLock.WaitAsync();
        try
        {
            // Return a copy of the current state to avoid concurrent modification
            return new SessionState
            {
                // Copy relevant properties from sessionState
                // This would need to be implemented based on SessionState structure
                // For now, return the reference as the exact structure is not visible
            };
        }
        finally
        {
            stateLock.Release();
        }
    }

    ///// <summary>
    ///// Applies state updates to the session state and broadcasts changes.
    ///// </summary>
    //private async Task ApplyStateUpdate(SessionStateUpdate update)
    //{
    //    Logger.LogTrace("Applying state update from {Source} with {ChangeCount} changes", 
    //        update.Source, update.Changes.Count);

    //    await stateLock.WaitAsync();
    //    try
    //    {
    //        bool hasChanges = false;
    //        foreach (var change in update.Changes)
    //        {
    //            try
    //            {
    //                var applied = await change.ApplyToState(sessionState);
    //                if (applied)
    //                {
    //                    hasChanges = true;
    //                    Logger.LogTrace("Applied state change from {Source}: {ChangeType}", 
    //                        update.Source, change.GetType().Name);
    //                }
    //            }
    //            catch (Exception ex)
    //            {
    //                Logger.LogError(ex, "Error applying state change from {Source}: {ChangeType}", 
    //                    update.Source, change.GetType().Name);
    //            }
    //        }

    //        // Broadcast the updated state if there were changes
    //        if (hasChanges)
    //        {
    //            Logger.LogTrace("Broadcasting state change from {Source}", update.Source);
    //            await stateChangeBroadcast.SendAsync(sessionState);
    //        }
    //    }
    //    finally
    //    {
    //        stateLock.Release();
    //    }
    //}

    /// <summary>
    /// Completes the pipeline and waits for all processing to finish.
    /// </summary>
    public async Task CompleteAsync()
    {
        input.Complete();
        
        await Task.WhenAll(
            rmonitorProcessorBlock.Completion,
            multiloopProcessorBlock.Completion,
            pitProcessorBlock.Completion,
            stateUpdater.Completion);
        
        stateChangeBroadcast.Complete();
        await stateChangeBroadcast.Completion;
    }
}
