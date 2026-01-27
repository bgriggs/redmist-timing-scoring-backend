using BunnyCDN.Net.Storage;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace RedMist.Backend.Shared.Utilities;

public class BunnyCdn : IDisposable
{
    private readonly BunnyCDNStorage bunnyClient;
    private readonly string apiAccessKey;
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
        this.httpClientFactory = httpClientFactory;
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
            Logger.LogError(ex, "Error cleaning destination: {destinationPath}", destinationPath);
            return 2;
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
            Logger.LogError(ex, "Error copying files");
            return 3;
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
            Logger.LogError(ex, "Error uploading stream to '{destinationPath}'", destinationPath);
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
        httpClient.DefaultRequestHeaders.Add("AccessKey", apiAccessKey);
        var message = new HttpRequestMessage(HttpMethod.Post, url);
        var response = await httpClient.SendAsync(message);
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
