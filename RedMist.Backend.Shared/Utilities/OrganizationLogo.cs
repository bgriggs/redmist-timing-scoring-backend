using Microsoft.EntityFrameworkCore;
using RedMist.Database;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// The shared placeholder image, and how to avoid an organization adopting it by accident.
/// </summary>
/// <remarks>
/// <para>
/// Reads substitute the default image for an organization that has none, so a page always has
/// something to render. That makes every read-modify-write round trip a trap: an editor loads the
/// organization, the operator changes the website, the form posts everything back, and the
/// placeholder is written in as that organization's own logo. Nobody touched the image, and
/// afterwards nothing distinguishes it from a deliberate choice.
/// </para>
/// <para>
/// It is not one client's problem to solve. The landing UI's organization editor and the relay's
/// settings screen both load and post the whole record, through different services, so a fix in
/// either would leave the other adopting the placeholder. Refusing the write is the only place that
/// covers both.
/// </para>
/// <para>
/// The cost is that an organization cannot deliberately choose the placeholder as its own logo.
/// That is not a loss: it already renders exactly that image when it has none.
/// </para>
/// </remarks>
public static class OrganizationLogo
{
    /// <summary>The shared placeholder, or null when none is configured.</summary>
    public static async Task<byte[]?> LoadDefaultAsync(TsContext context, CancellationToken cancellationToken = default)
        => (await context.DefaultOrgImages.FirstOrDefaultAsync(cancellationToken))?.ImageData;

    /// <summary>
    /// Whether <paramref name="logo"/> is the shared placeholder rather than an organization's own.
    /// </summary>
    public static bool IsDefault(byte[]? logo, byte[]? defaultLogo)
        => logo is { Length: > 0 } && defaultLogo is { Length: > 0 }
           && logo.AsSpan().SequenceEqual(defaultLogo);
}
