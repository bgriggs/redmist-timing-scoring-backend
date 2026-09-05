using RedMist.Database.Models;
using RedMist.Social.Generation;
using System.Globalization;

namespace RedMist.SocialCompose;

/// <summary>
/// What one compose run is allowed to do. Read from configuration once at startup so the whole run
/// works from a fixed set of values rather than re-reading them per event.
/// </summary>
public sealed class SocialComposeSettings
{
    /// <summary>Model to generate with.</summary>
    public required string Model { get; init; }

    /// <summary>Reply ceiling in tokens.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Generation calls per event before the draft is flagged and handed over anyway.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>Length ceiling for the finished post in characters, or zero for none.</summary>
    public required int MaxCharacters { get; init; }

    /// <summary>Channel these posts are drafted for.</summary>
    public required SocialChannel Channel { get; init; }

    /// <summary>
    /// How long after an event ends to wait before drafting.
    /// </summary>
    /// <remarks>
    /// Not a politeness delay. An event's EndDate is a date, so it lands at midnight, and composing
    /// the moment it passes would draft from a race that is still running. The default matches the
    /// day the archive job already waits before it treats an event as finished.
    /// </remarks>
    public required TimeSpan SettlePeriod { get; init; }

    /// <summary>
    /// How far back to consider events. Bounds the job to recent racing so a first run, or a run
    /// after an outage, cannot work through the entire back catalog.
    /// </summary>
    public required TimeSpan LookbackWindow { get; init; }

    /// <summary>
    /// Hard ceiling on events drafted in one run. Every event costs API calls, so this is the stop
    /// on an unexpected burst -- a bulk import, a corrected batch of end dates -- turning into a bill.
    /// </summary>
    public required int MaxEventsPerRun { get; init; }

    public ComposeOptions ToComposeOptions() => new(Model, MaxTokens, MaxAttempts, MaxCharacters);

    /// <summary>
    /// Reads settings, falling back to defaults chosen to be safe rather than thorough: a short
    /// lookback and a low event ceiling, both of which can only cause a post to be missed, never an
    /// unwanted one to be drafted.
    /// </summary>
    public static SocialComposeSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new SocialComposeSettings
        {
            Model = Read(configuration, "Social:Model") ?? "claude-sonnet-5",
            MaxTokens = ReadInt(configuration, "Social:MaxTokens", 1024, minimum: 1),
            MaxAttempts = ReadInt(configuration, "Social:MaxAttempts", 3, minimum: 1),
            MaxCharacters = ReadInt(configuration, "Social:MaxCharacters", 1200, minimum: 0),
            Channel = ReadEnum(configuration, "Social:Channel", SocialChannel.Facebook),
            SettlePeriod = TimeSpan.FromHours(ReadInt(configuration, "Social:SettleHours", 24, minimum: 0)),
            LookbackWindow = TimeSpan.FromDays(ReadInt(configuration, "Social:LookbackDays", 14, minimum: 1)),
            MaxEventsPerRun = ReadInt(configuration, "Social:MaxEventsPerRun", 10, minimum: 1),
        };
    }

    private static string? Read(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Reads an integer, refusing anything that does not parse or that falls below
    /// <paramref name="minimum"/> rather than falling back to the default.
    /// </summary>
    /// <remarks>
    /// The range check is not defensive padding. Every one of these values fails obscurely rather than
    /// loudly when it is out of range: zero attempts leaves the composer with no candidate to return,
    /// zero tokens makes the API reject every call, and a negative lookback inverts the window so the
    /// job reports "nothing to draft" forever. A typo in a Helm value should stop the job at startup,
    /// where the cause is obvious.
    /// </remarks>
    private static int ReadInt(IConfiguration configuration, string key, int fallback, int minimum)
    {
        var raw = Read(configuration, key);
        if (raw is null)
            return fallback;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Configuration value '{key}' is not an integer: '{raw}'.");

        if (value < minimum)
            throw new InvalidOperationException($"Configuration value '{key}' must be at least {minimum}, but was {value}.");

        return value;
    }

    private static TEnum ReadEnum<TEnum>(IConfiguration configuration, string key, TEnum fallback)
        where TEnum : struct, Enum
    {
        var raw = Read(configuration, key);
        if (raw is null)
            return fallback;

        if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' is not a known {typeof(TEnum).Name}: '{raw}'. " +
                $"Expected one of {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }

        return value;
    }
}
