using BunnyCDN.Net.Storage;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace RedMist.Backend.Shared.Utilities;

public class BunnyCdn : IDisposable
{
    private readonly BunnyCDNStorage bunnyClient;
    private readonly string apiAccessKey;

    /// <summary>
    /// Held only so it can be redacted out of logged messages. BunnyCDN.Net.Storage puts the access
    /// key into its own exception text ("Authentication failed for storage zone 'x' with access key
    /// 'y'"), so logging one of its failures verbatim writes a live credential to pod stdout.
    /// </summary>
    private readonly string storageAccessKey;

    private readonly IHttpClientFactory httpClientFactory;

    private ILogger Logger { get; }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="storageZoneName">Name from storage page, e.g. redmist-assets</param>
    /// <param name="storageAccessKey">Access key from storage page, same as FTP</param>
    /// <param name="mainReplicationRegion">Name from the replication page, default is de</param>
    /// <param name="apiAccessKey">Overall API key from account settings</param>
    /// <param name="loggerFactory"></param>
    /// <param name="httpClientFactory">Factory for creating HttpClient instances to prevent socket exhaustion</param>
    public BunnyCdn(string storageZoneName, string storageAccessKey, string mainReplicationRegion, string apiAccessKey, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        Logger = loggerFactory.CreateLogger(GetType().Name);
        bunnyClient = new BunnyCDNStorage(storageZoneName, storageAccessKey, mainReplicationRegion);
        var field = typeof(BunnyCDNStorage).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field != null && field.GetValue(bunnyClient) is HttpClient httpClient)
        {
            httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        this.apiAccessKey = apiAccessKey;
        this.storageAccessKey = storageAccessKey;
        this.httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Strips the storage credentials out of a message before it is logged.
    /// </summary>
    /// <remarks>
    /// The storage library reports authentication failures by quoting the access key back, so the one
    /// message most worth logging is also the one that must not be logged as written.
    /// </remarks>
    private string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        if (!string.IsNullOrEmpty(storageAccessKey))
            message = message.Replace(storageAccessKey, "***", StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(apiAccessKey))
            message = message.Replace(apiAccessKey, "***", StringComparison.Ordinal);

        return message;
    }

    /// <summary>
    /// Deletes a single stored object.
    /// </summary>
    /// <param name="destinationPath">Full storage path, e.g. /redmist-assets/social/event-1/x.png</param>
    /// <returns>True when storage confirmed the delete; false otherwise.</returns>
    /// <remarks>
    /// The storage client's own result is returned rather than assumed. It answers false for any
    /// non-success -- a rotated key, a 503, a missing object -- and does not throw, so discarding it
    /// would report every failure as a success. That matters more here than anywhere else in this
    /// class: the caller clears its record of the URL on the strength of this answer, so a delete
    /// that quietly failed leaves a publicly fetchable image that nothing references and nothing
    /// will ever find again.
    /// </remarks>
    public async Task<bool> DeleteAsync(string destinationPath)
    {
        try
        {
            Logger.LogInformation("Deleting '{destinationPath}'", destinationPath);
            var deleted = await bunnyClient.DeleteObjectAsync(destinationPath);
            if (!deleted)
                Logger.LogWarning("Storage refused the delete of '{destinationPath}'", destinationPath);

            return deleted;
        }
        catch (Exception ex)
        {
            Logger.LogError("Error deleting '{destinationPath}': {error}", destinationPath, Redact(ex.ToString()));
            return false;
        }
    }


    /// <summary>
    /// Deletes contents of specified path.
    /// </summary>
    /// <param name="maxConcurrency"></param>
    /// <param name="destinationPath"></param>
    /// <returns>0 for success, 2 for error</returns>
    public async Task<int> CleanDestinationAsync(int maxConcurrency, string destinationPath)
    {
        SemaphoreSlim semaphore = new(maxConcurrency);
        try
        {
            var list = await bunnyClient.GetStorageObjectsAsync(destinationPath);
            List<Task> tasks = [];
            foreach (var obj in list)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async delegate
                {
                    try
                    {
                        string fp = obj.FullPath.TrimEnd('/');
                        if (obj.IsDirectory)
                        {
                            fp += "/";
                        }
                        Logger.LogInformation("Deleting object: {fp}", fp);
                        if (await bunnyClient.DeleteObjectAsync(fp))
                        {
                            Logger.LogInformation("Deleted object: {fp}", fp);
                        }
                        else
                        {
                            Logger.LogWarning("Failed to delete object: {fp}", fp);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Logger.LogError("Error cleaning destination '{destinationPath}': {error}", destinationPath, Redact(ex.ToString()));
            return 2;
        }
        finally
        {
            semaphore?.Dispose();
        }
        return 0;
    }

    private async Task<int> CopyToDestinationAsync(string sourcePath, string destinationPath, int maxConcurrency)
    {
        var list = GetAllFiles(sourcePath).ToList();
        Logger.LogInformation($"Found {list.Count} files to upload.");
        SemaphoreSlim semaphore = new(maxConcurrency);
        try
        {
            var tasks = new List<Task>();
            foreach (string file in list)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async delegate
                {
                    try
                    {
                        string text = GetDestinationPath(sourcePath, file, destinationPath).Replace("\\", "/");
                        Logger.LogInformation($"Copying '{file}' to '{text}'");
                        await bunnyClient.UploadAsync(file, text, validateChecksum: true);
                        Logger.LogInformation("Finished '" + file + "'");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Logger.LogError("Error copying files: {error}", Redact(ex.ToString()));
            return 3;
        }
        finally
        {
            semaphore?.Dispose();
        }
        return 0;
    }

    private static IEnumerable<string> GetAllFiles(string path)
    {
        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }
        if (Directory.Exists(path))
        {
            foreach (string item in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                yield return item;
            }
            yield break;
        }
        throw new FileNotFoundException("Path not found: " + path);
    }

    private static string GetDestinationPath(string sourceBasePath, string sourceFilePath, string destinationPrefix)
    {
        if (!Path.IsPathRooted(sourceBasePath) || !Path.IsPathRooted(sourceFilePath))
        {
            throw new ArgumentException("Both sourceBasePath and sourceFilePath must be absolute paths.");
        }
        string text = Path.GetRelativePath(sourceBasePath, sourceFilePath);
        if (text == ".")
        {
            text = Path.GetFileName(sourceFilePath);
        }
        return Path.Combine(destinationPrefix, text);
    }

    public async Task<bool> UploadAsync(Stream stream, string destinationPath)
    {
        try
        {
            Logger.LogInformation("Uploading stream to '{destinationPath}'", destinationPath);
            await bunnyClient.UploadAsync(stream, destinationPath, validateChecksum: true);
            Logger.LogInformation("Upload completed to '{destinationPath}'", destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            // Redacted, and passed as a message rather than as the exception argument. The storage
            // library quotes the access key in its own authentication-failure text, and NLog renders
            // an attached exception in full -- so logging it as an exception would put a live
            // credential in pod stdout. ToString rather than Message so the stack and any inner
            // exception survive: for a DNS or TLS failure the message alone is just the sentence
            // telling you to read the inner exception that is no longer there.
            Logger.LogError("Error uploading stream to '{destinationPath}': {error}", destinationPath, Redact(ex.ToString()));
            return false;
        }
    }

    /// <summary>
    /// Purges the CDN cache for the specified pull zone.
    /// </summary>
    /// <param name="cdnId">From CDN URL, e.g. 5008374</param>
    /// <returns>true if command was successful</returns>
    public async Task<bool> PurgeCacheAsync(string cdnId)
    {
        var url = $"https://api.bunny.net/pullzone/{cdnId}/purgeCache";
        var httpClient = httpClientFactory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Add("AccessKey", apiAccessKey);
        using var response = await httpClient.SendAsync(message);
        return response.IsSuccessStatusCode;
    }

    public void Dispose()
    {
        try
        {
            var field = typeof(BunnyCDNStorage).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null && field.GetValue(bunnyClient) is HttpClient httpClient)
            {
                httpClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error disposing BunnyCdn HttpClient");
        }
        GC.SuppressFinalize(this);
    }
}
