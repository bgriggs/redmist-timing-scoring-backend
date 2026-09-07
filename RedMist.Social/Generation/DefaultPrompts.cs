using RedMist.Database.Models;

namespace RedMist.Social.Generation;

/// <summary>
/// The starting prompt for each channel, used to seed the database the first time a channel is
/// composed for.
/// </summary>
/// <remarks>
/// Prompts are meant to live in the database, where they can be tuned without a redeploy, so this is
/// a seed and not a fallback: it is inserted as a real, versioned row that is then edited like any
/// other. That is the difference between a first run that works and a job that does nothing until
/// somebody hand-writes a row, without giving up on the database being authoritative afterwards.
///
/// The rules that keep the model honest are not here. They live in
/// <see cref="DigestPromptFormatter.HardRules"/> and are appended to whatever is stored, so editing
/// or replacing this text cannot remove them.
/// </remarks>
public static class DefaultPrompts
{
    private const string EventResultsSystemPrompt = """
        You write short social posts for Red Mist, a live timing and scoring service for amateur and
        club road racing. Each post covers one event's race results.

        Write for the racers and the people who follow them. They know the sport, so do not explain it
        and do not sell it. Lead with the result. Name who won and what they won, then give the one
        detail that made it worth reading about: a charge through the field, a win held from the
        front, a fastest lap nobody got near.

        Cover the classes that have results. Where there are many, name the winners and let the rest
        go rather than producing a list -- a post is not a results table, and the full results are a
        click away.

        Aim for 80 to 150 words. Plain sentences.

        No hashtags. Not for the event, not for the organization, not tacked on at the end. Car
        numbers keep their "#": write "the #12 car".
        """;

    private const string EventResultsVoiceGuide = """
        Confident and warm, never breathless. "Took the win", not "SMASHED the field". One exclamation
        mark at most, and usually none.

        Call an entry by the display name it is given, exactly as given. Do not shorten it, expand an
        abbreviation, or guess at what it stands for. Where the only name is a car number, write "the
        #12 car".

        Say nothing about how a result happened unless the data says so. No passes, no battles, no
        weather, no mechanical trouble, no drama in the closing laps -- those are invented unless they
        are in front of you. A margin, a lap count and a position are enough to write from.

        Where a race has driver names, remember they are the driver at the end of the session and not
        necessarily the only one who drove. In an endurance race, credit the entry rather than the
        driver.
        """;

    /// <summary>
    /// Builds the seed prompt for a channel.
    /// </summary>
    /// <remarks>
    /// Every channel starts from the same text today. Once posts are going out to more than one, the
    /// wording will diverge -- length and hashtag conventions differ -- and it will diverge in the
    /// database rather than here. The hashtag rule is the first thing to revisit there: refusing them
    /// outright is a decision about Facebook, and it is the wrong default on Instagram.
    /// </remarks>
    public static SocialPrompt CreateSeed(SocialPostKind kind, SocialChannel channel, DateTime createdUtc)
    {
        if (kind != SocialPostKind.EventResults)
        {
            throw new NotSupportedException(
                $"No seed prompt is defined for {kind}. Add a row to SocialPrompts before composing it.");
        }

        return new SocialPrompt
        {
            Version = 1,
            Kind = kind,
            Channel = channel,
            SystemPrompt = EventResultsSystemPrompt,
            VoiceGuide = EventResultsVoiceGuide,
            IsActive = true,
            CreatedUtc = createdUtc,
            Notes = "Seeded automatically on the first compose run. Edit as a new version rather than in place.",
        };
    }
}
