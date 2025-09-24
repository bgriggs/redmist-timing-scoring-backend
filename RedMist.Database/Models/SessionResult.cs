using Microsoft.EntityFrameworkCore;
using RedMist.TimingCommon.Models;

namespace RedMist.Database.Models;

[PrimaryKey(nameof(EventId), nameof(SessionId))]
public class SessionResult
{
    public int EventId { get; set; }
    public int SessionId { get; set; }
    public DateTime Start { get; set; }
    public Payload? Payload { get; set; }
    public SessionState? SessionState { get; set; }
}
