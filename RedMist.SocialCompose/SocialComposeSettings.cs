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

    /// <summary>
    /// Whether to photograph each session's results page and attach the pictures to the draft.
    /// </summary>
    /// <remarks>
    /// Separately switchable because it is the one part of a run that needs a browser. Turning it
    /// off leaves text-only drafts rather than stopping the job, which is what you want if the
    /// timing page changes shape and every capture starts failing.
    /// </remarks>
    public required bool ImagesEnabled { get; init; }

    /// <summary>
    /// Most images attached to one post. An event is usually two races, and a post carrying a
    /// picture of every session stops being a post and starts being an album.
    /// </summary>
    public required int MaxImagesPerPost { get; init; }

    /// <summary>How the session page is rendered and cropped.</summary>
    public required SessionImageCaptureOptions ImageCapture { get; init; }

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
            ImagesEnabled = ReadBool(configuration, "Social:Images:Enabled", true),
            MaxImagesPerPost = ReadInt(configuration, "Social:Images:MaxPerPost", 2, minimum: 1),
            ImageCapture = ReadCaptureOptions(configuration),
        };
    }

    /// <summary>
    /// Reads the capture options.
    /// </summary>
    /// <remarks>
    /// The selectors are configuration rather than constants because they belong to a page in another
    /// repository that ships on its own schedule. When a class name changes there, this should be a
    /// Helm value away from working again rather than a backend release.
    /// </remarks>
    /// <summary>
    /// Reads a setting that has no safe default, failing at startup rather than at first use. Blank
    /// counts as missing: an unset Helm value arrives as an empty string, not as null.
    /// </summary>
    private static string Required(IConfiguration configuration, string key) =>
        Read(configuration, key)
        ?? throw new InvalidOperationException(
            $"{key} is not configured. It names the site the results pictures are taken from, and " +
            "differs per environment, so there is no default that is right anywhere else.");

    private static SessionImageCaptureOptions ReadCaptureOptions(IConfiguration configuration) => new(
        // No default. Every other setting here falls back to something harmless, but this one names
        // the site whose pages get photographed, and the event ids handed to it come from whichever
        // database this instance is pointed at. Defaulting it to production meant an unconfigured
        // test deployment either photographed nothing -- an id absent there never resolves, so each
        // capture waits out the ready timeout and the drafts arrive pictureless -- or photographed a
        // real production race and attached it to a test draft. Neither raises an error, so the
        // setting has to be demanded rather than assumed.
        BaseUrl: Required(configuration, "Social:Images:BaseUrl"),

        // The landing UI's embed flag, which drops the site toolbar and footer at the Angular level
        // rather than leaving them to be hidden after they have already rendered.
        // groupClass renders the field by class, expand opens those groups (they arrive shut, which
        // is no use to a screenshot nobody can click), and topPerClass keeps each group to its podium.
        // Together they are what makes the picture agree with the copy: the digest carries a three-deep
        // podium per class, and this shows exactly that rather than an overall order in which most
        // class winners are below the fold.
        QueryString: ReadAllowingEmpty(configuration, "Social:Images:QueryString")
            ?? "embed=1&groupClass=1&expand=1&topPerClass=3",

        // 900 CSS px at 2x. Wide enough for the timing columns, narrow enough that the rows are still
        // legible once a feed scales the picture down to phone width.
        ViewportWidth: ReadInt(configuration, "Social:Images:ViewportWidth", 900, minimum: 320),

        // Taller than the picture needs to be. The clip below takes the results panel at whatever
        // height it lands at, so this only has to be large enough not to constrain it: six classes at
        // three cars each measures about 1185.
        ViewportHeight: ReadInt(configuration, "Social:Images:ViewportHeight", 1500, minimum: 320),
        DeviceScaleFactor: ReadInt(configuration, "Social:Images:DeviceScaleFactor", 2, minimum: 1),
        ReadySelector: Read(configuration, "Social:Images:ReadySelector") ?? ".car-row-container",

        // Clipping to the results panel is only safe because topPerClass bounds its height. Without
        // that a 55-car field measures about 885 by 2763 and the picture is cropped to nothing in a
        // feed; with it, six classes come to roughly 900 by 1185 and the crop fits the content exactly.
        // A class-heavy event still produces a taller image -- set this empty to fall back to the
        // viewport if that becomes a problem.
        ClipSelector: ReadAllowingEmpty(configuration, "Social:Images:ClipSelector") ?? ".timing-viewer",

        // The tab strip, the rotating sponsor panel and the viewer's own controls. All verified
        // present on the live page; a selector that matches nothing is harmless. Site navigation is
        // absent because the embed flag above already removes it.
        HideSelectors: ReadList(configuration, "Social:Images:HideSelectors",
            [".mat-mdc-tab-header", "app-sponsor-rotator", "app-sponsor-marquee",
             ".header-left", ".header-right"]),

        // Sponsor impressions are reported as the page renders, and those writes are real -- they are
        // what the sponsor rollup and the monthly reports are computed from. A nightly screenshot run
        // would otherwise invent impressions, for events that finished days ago, seen by nobody.
        BlockedUrlSubstrings: ReadList(configuration, "Social:Images:BlockedUrlSubstrings",
            ["/SponsorTelemetry/"]),
        ReadyTimeout: TimeSpan.FromSeconds(ReadInt(configuration, "Social:Images:ReadyTimeoutSeconds", 45, minimum: 1)),
        SettleDelay: TimeSpan.FromSeconds(ReadInt(configuration, "Social:Images:SettleSeconds", 4, minimum: 0)));

    /// <summary>
    /// Reads a value where blank is a meaningful choice rather than "not configured", so a selector
    /// or query string can be switched off from a Helm value instead of falling back to the default.
    /// </summary>
    private static string? ReadAllowingEmpty(IConfiguration configuration, string key) => configuration[key];

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback)
    {
        var raw = Read(configuration, key);
        if (raw is null)
            return fallback;

        if (!bool.TryParse(raw, out var value))
            throw new InvalidOperationException($"Configuration value '{key}' is not true or false: '{raw}'.");

        return value;
    }

    /// <summary>
    /// Reads a comma-separated list, so a Helm value can carry one without nested YAML.
    /// </summary>
    /// <remarks>
    /// An explicitly empty value means an empty list, not the default. Otherwise there would be no way
    /// to turn one of these lists off from configuration: setting it to "" would silently restore the
    /// built-in entries, which is the opposite of what anyone typing that intends.
    /// </remarks>
    private static IReadOnlyList<string> ReadList(IConfiguration configuration, string key, string[] fallback)
    {
        var raw = ReadAllowingEmpty(configuration, key);
        if (raw is null)
            return fallback;

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
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
