namespace RedMist.EventProcessor.EventStatus;

public class EventStatusUpdateEventArgs<T>
{
    public string EventId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public T Data { get; set; } = default!;
}
