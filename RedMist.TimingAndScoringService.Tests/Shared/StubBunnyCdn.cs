using BigMission.TestHelpers.Testing;
using RedMist.Backend.Shared.Utilities;
using System.Net;
using System.Reflection;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Builds <see cref="BunnyCdn"/> instances that answer from a stub transport instead of the network.
/// </summary>
internal static class StubBunnyCdn
{
    public const string StorageZone = "redmist-test-zone";

    public static BunnyCdn Create(StubHttpMessageHandler? storageTransport = null, IHttpClientFactory? apiClientFactory = null)
    {
        var cdn = new BunnyCdn(StorageZone, "storage-key", "de", "api-key", new DebugLoggerFactory(),
            apiClientFactory ?? StubHttpMessageHandler.Returning(HttpStatusCode.OK).AsClientFactory());
        if (storageTransport != null)
        {
            UseStubTransport(cdn, storageTransport);
        }
        return cdn;
    }

    /// <summary>
    /// Swaps the <see cref="HttpClient"/> the vendor's storage client built for itself. It offers no
    /// seam of its own, and the production code already reaches for the same private field to raise
    /// the upload timeout.
    /// </summary>
    public static void UseStubTransport(BunnyCdn cdn, StubHttpMessageHandler handler)
    {
        var storageField = typeof(BunnyCdn).GetField("bunnyClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storage = storageField.GetValue(cdn)!;
        var httpField = storage.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (HttpClient)httpField.GetValue(storage)!;
        httpField.SetValue(storage, new HttpClient(handler, disposeHandler: false) { BaseAddress = original.BaseAddress });
        original.Dispose();
    }
}
