namespace RedMist.Backend.Shared.Utilities;

public interface IArchiveStorage
{
    Task<bool> UploadEventLogsAsync(Stream stream, int eventId);
    Task<bool> UploadSessionLogsAsync(Stream stream, int eventId, int sessionId);
    Task<bool> UploadSessionLapsAsync(Stream stream, int eventId, int sessionId);
    Task<bool> UploadSessionCarLapsAsync(Stream stream, int eventId, int sessionId, string carNum);
}
