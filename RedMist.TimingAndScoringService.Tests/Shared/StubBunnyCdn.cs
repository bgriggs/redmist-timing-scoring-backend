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

    /// <summary>
    /// Builds a <see cref="BunnyCdn"/> whose storage transport is always stubbed. A caller that does
    /// not care what storage answers still gets a blocked transport rather than the vendor's live
    /// <see cref="HttpClient"/>, so no test can reach storage.bunnycdn.com by omission.
    /// </summary>
    public static BunnyCdn Create(StubHttpMessageHandler? storageTransport = null, IHttpClientFactory? apiClientFactory = null)
    {
        var cdn = new BunnyCdn(StorageZone, "storage-key", "de", "api-key", new DebugLoggerFactory(),
            apiClientFactory ?? StubHttpMessageHandler.Returning(HttpStatusCode.OK).AsClientFactory());
        UseStubTransport(cdn, storageTransport ?? StubHttpMessageHandler.Returning(HttpStatusCode.NotImplemented));
        return cdn;
    }

    /// <summary>
    /// Swaps the <see cref="HttpClient"/> the vendor's storage client built for itself. It offers no
    /// seam of its own, and the production code already reaches for the same private field to raise
    /// the upload timeout.
    /// </summary>
    /// <remarks>
    /// Both fields are looked up by name, so a vendor package bump can break this. It throws rather
    /// than handing back a CDN that would quietly talk to the real storage zone. The tests whose only
    /// assertion is a failure a real, failed network call would also produce additionally assert that
    /// the stub received the request.
    /// </remarks>
    public static void UseStubTransport(BunnyCdn cdn, StubHttpMessageHandler handler)
    {
        var storageField = typeof(BunnyCdn).GetField("bunnyClient", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BunnyCdn.bunnyClient is gone; the network-blocking seam no longer works.");
        var storage = storageField.GetValue(cdn)!;
        var httpField = storage.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{storage.GetType().Name}._http is gone; the network-blocking seam no longer works.");
        var original = (HttpClient)httpField.GetValue(storage)!;
        httpField.SetValue(storage, new HttpClient(handler, disposeHandler: false) { BaseAddress = original.BaseAddress });
        original.Dispose();
    }
}
