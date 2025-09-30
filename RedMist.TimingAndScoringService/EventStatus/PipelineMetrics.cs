using Prometheus;

namespace RedMist.TimingAndScoringService.EventStatus;

/// <summary>
/// Provides metrics tracking for pipeline blocks
/// </summary>
public class PipelineMetrics
{
    private readonly Counter _messagesProcessed;
    private readonly Counter _messagesOutput;
    private readonly Histogram _processingDuration;
    private readonly Gauge _activeMessages;
    private readonly Counter _errors;
    private readonly string _blockName;

    public PipelineMetrics(string blockName)
    {
        _blockName = blockName;
        
        _messagesProcessed = Metrics.CreateCounter(
            "pipeline_messages_processed_total", 
            "Total number of messages processed by pipeline block",
            "block_name");

        _messagesOutput = Metrics.CreateCounter(
            "pipeline_messages_output_total", 
            "Total number of messages output by pipeline block",
            "block_name");

        _processingDuration = Metrics.CreateHistogram(
            "pipeline_processing_duration_seconds", 
            "Duration of message processing in pipeline blocks",
            "block_name");

        _activeMessages = Metrics.CreateGauge(
            "pipeline_active_messages", 
            "Number of messages currently being processed",
            "block_name");

        _errors = Metrics.CreateCounter(
            "pipeline_block_errors_total", 
            "Total number of errors in pipeline block",
            "block_name", "error_type");
    }

    /// <summary>
    /// Wraps a function call with metrics tracking
    /// </summary>
    public T Track<T>(Func<T> operation)
    {
        _activeMessages.WithLabels(_blockName).Inc();
        _messagesProcessed.WithLabels(_blockName).Inc();
        
        using var timer = _processingDuration.WithLabels(_blockName).NewTimer();
        try
        {
            var result = operation();
            
            // Count output if result is not null (for transform blocks)
            if (result != null)
            {
                _messagesOutput.WithLabels(_blockName).Inc();
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _errors.WithLabels(_blockName, ex.GetType().Name).Inc();
            throw;
        }
        finally
        {
            _activeMessages.WithLabels(_blockName).Dec();
        }
    }

    /// <summary>
    /// Wraps an async function call with metrics tracking
    /// </summary>
    public async Task<T> TrackAsync<T>(Func<Task<T>> operation)
    {
        _activeMessages.WithLabels(_blockName).Inc();
        _messagesProcessed.WithLabels(_blockName).Inc();
        
        using var timer = _processingDuration.WithLabels(_blockName).NewTimer();
        try
        {
            var result = await operation();
            
            // Count output if result is not null (for transform blocks)
            if (result != null)
            {
                if (result is PatchUpdates pu)
                {
                    if (pu.SessionPatches.Count > 0 || pu.CarPatches.Count > 0)
                        _messagesOutput.WithLabels(_blockName).Inc();
                }
                else
                {
                    _messagesOutput.WithLabels(_blockName).Inc();
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _errors.WithLabels(_blockName, ex.GetType().Name).Inc();
            throw;
        }
        finally
        {
            _activeMessages.WithLabels(_blockName).Dec();
        }
    }

    /// <summary>
    /// Wraps an async action with metrics tracking
    /// </summary>
    public async Task TrackAsync(Func<Task> operation)
    {
        _activeMessages.WithLabels(_blockName).Inc();
        _messagesProcessed.WithLabels(_blockName).Inc();
        
        using var timer = _processingDuration.WithLabels(_blockName).NewTimer();
        try
        {
            await operation();
            
            // Action blocks always produce output (even if it's just processing)
            _messagesOutput.WithLabels(_blockName).Inc();
        }
        catch (Exception ex)
        {
            _errors.WithLabels(_blockName, ex.GetType().Name).Inc();
            throw;
        }
        finally
        {
            _activeMessages.WithLabels(_blockName).Dec();
        }
    }

    /// <summary>
    /// Wraps a synchronous action with metrics tracking
    /// </summary>
    public void Track(Action operation)
    {
        _activeMessages.WithLabels(_blockName).Inc();
        _messagesProcessed.WithLabels(_blockName).Inc();
        
        using var timer = _processingDuration.WithLabels(_blockName).NewTimer();
        try
        {
            operation();
            
            // Action blocks always produce output (even if it's just processing)
            _messagesOutput.WithLabels(_blockName).Inc();
        }
        catch (Exception ex)
        {
            _errors.WithLabels(_blockName, ex.GetType().Name).Inc();
            throw;
        }
        finally
        {
            _activeMessages.WithLabels(_blockName).Dec();
        }
    }

    /// <summary>
    /// Gets current metrics for logging purposes
    /// </summary>
    public (double MessagesProcessed, double MessagesOutput, double ActiveMessages, double AvgProcessingTime, double FilterRatio) GetCurrentMetrics()
    {
        var messagesProcessed = _messagesProcessed.WithLabels(_blockName).Value;
        var messagesOutput = _messagesOutput.WithLabels(_blockName).Value;
        var activeMessages = _activeMessages.WithLabels(_blockName).Value;
        
        // Get average processing time from histogram
        var histogram = _processingDuration.WithLabels(_blockName);
        var avgProcessingTime = histogram.Count > 0 ? histogram.Sum / histogram.Count : 0;
        
        // Calculate filter ratio (output/input) - useful for understanding how much filtering occurs
        var filterRatio = messagesProcessed > 0 ? messagesOutput / messagesProcessed : 0;
        
        return (messagesProcessed, messagesOutput, activeMessages, avgProcessingTime, filterRatio);
    }
}