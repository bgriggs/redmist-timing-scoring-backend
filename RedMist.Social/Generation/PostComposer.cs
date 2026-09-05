using Microsoft.Extensions.Logging;
using RedMist.Database.Models;
using RedMist.Social.Digest;
using RedMist.Social.Validation;
using System.Text.RegularExpressions;

namespace RedMist.Social.Generation;

/// <summary>
/// Produces publishable copy for one event: generate, check the result against the digest, and put
/// the specific failures back to the model rather than accepting them or retrying blindly.
/// </summary>
/// <remarks>
/// Holds every decision worth testing, which is why it takes an <see cref="ICopyGenerator"/> rather
/// than talking to the API itself. Nothing here decides whether a post is published -- a person does
/// that -- so the job of this class is to hand a reviewer the best draft it can and to be explicit
/// about what it could not verify.
/// </remarks>
public sealed partial class PostComposer(ICopyGenerator generator, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<PostComposer>();

    /// <summary>
    /// Generates copy for a digest, correcting and retrying while attempts remain.
    /// </summary>
    /// <remarks>
    /// Always returns a draft when the model returned anything at all, even one that still fails
    /// validation. Discarding it would leave the event with no post and nothing for a person to fix,
    /// which is worse than a flagged draft: the whole pipeline is built around the assumption that a
    /// human reads every post before it goes out. <see cref="ComposedPost.HasUnverifiedClaims"/> says
    /// which case this is, and review must surface it prominently.
    /// </remarks>
    public async Task<ComposedPost> ComposeAsync(
        EventDigest digest,
        SocialPrompt prompt,
        ComposeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        if (!digest.IsEligibleForPublication)
        {
            throw new InvalidOperationException(
                $"Event {digest.EventId} is not eligible for publication; it must not reach generation.");
        }

        var system = DigestPromptFormatter.BuildSystemMessage(prompt);
        var messages = new List<CopyTurn> { new(CopyRole.User, DigestPromptFormatter.BuildUserMessage(digest)) };

        GeneratedCopy? candidate = null;
        IReadOnlyList<string> problems = [];
        CopyValidationResult? validation = null;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            candidate = await generator.GenerateAsync(
                new CopyRequest(system, messages, options.Model, options.MaxTokens), cancellationToken);

            validation = GeneratedCopyValidator.Validate(candidate.Text, digest);
            problems = FindProblems(candidate, validation, options);

            if (problems.Count == 0)
            {
                logger.LogInformation("Event {EventId}: copy accepted on attempt {Attempt} of {MaxAttempts}",
                    digest.EventId, attempt, options.MaxAttempts);
                return Build(digest, candidate, validation, attempt, problems: []);
            }

            logger.LogWarning("Event {EventId}: attempt {Attempt} of {MaxAttempts} rejected: {Problems}",
                digest.EventId, attempt, options.MaxAttempts, string.Join("; ", problems));

            if (attempt == options.MaxAttempts)
                break;

            messages.Add(new CopyTurn(CopyRole.Assistant, candidate.Text));
            messages.Add(new CopyTurn(CopyRole.User, DigestPromptFormatter.BuildCorrectionMessage(problems)));
        }

        logger.LogError(
            "Event {EventId}: copy still unverified after {MaxAttempts} attempts and will be flagged for close review",
            digest.EventId, options.MaxAttempts);

        return Build(digest, candidate!, validation!, options.MaxAttempts, problems);
    }

    /// <summary>
    /// Everything that disqualifies a draft, phrased so it can be sent straight back to the model.
    /// </summary>
    /// <remarks>
    /// Unverified names are deliberately absent. Ordinary prose produces capitalized phrases that are
    /// not in the digest -- "Saturday", "The" at the start of a sentence -- so treating them as
    /// failures would reject almost every draft. They are carried as review warnings instead.
    /// </remarks>
    private static List<string> FindProblems(
        GeneratedCopy candidate, CopyValidationResult validation, ComposeOptions options)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(candidate.Text))
        {
            // The stop reason is carried through because an empty reply has several causes that look
            // identical from here -- a refusal most of all -- and without it the only evidence left is
            // a blank draft in the review queue.
            problems.Add(string.IsNullOrWhiteSpace(candidate.StopReason)
                ? "The reply was empty."
                : $"The reply was empty (the model stopped with '{candidate.StopReason}').");
        }

        if (candidate.WasTruncated)
        {
            problems.Add(
                "The reply was cut off at the length limit and ends mid-thought. Write a materially " +
                "shorter post that finishes.");
        }

        if (validation.HasInventedNumbers)
        {
            problems.Add(
                "These numbers do not appear in the race data and cannot be published: " +
                string.Join(", ", validation.UnverifiedNumbers));
        }

        if (options.MaxCharacters > 0 && candidate.Text.Length > options.MaxCharacters)
        {
            problems.Add(
                $"The post is {candidate.Text.Length} characters; the limit is {options.MaxCharacters}.");
        }

        return problems;
    }

    private static ComposedPost Build(
        EventDigest digest,
        GeneratedCopy candidate,
        CopyValidationResult validation,
        int attempts,
        IReadOnlyList<string> problems)
    {
        var warnings = new List<string>();

        // Digest warnings first: they say what the facts themselves could not establish, which is the
        // context a reviewer needs before judging anything the copy says.
        foreach (var warning in digest.Provenance.Warnings)
            warnings.Add($"Digest: {warning}");

        foreach (var problem in problems)
            warnings.Add($"UNRESOLVED: {problem}");

        // A link is the one injection payload that pays off on a public page, and it is the one thing
        // the fact checks cannot catch: a crafted entry name is *in* the digest, so the name check
        // passes it by construction and the numeric check never looks at it. Flagged rather than
        // rejected, because a team genuinely named after a domain would otherwise fail every attempt.
        var links = FindLinks(candidate.Text);
        if (links.Count > 0)
        {
            warnings.Add(
                "REVIEW CLOSELY: the copy contains a link or handle, which this pipeline never has a " +
                "legitimate reason to write. Check it did not arrive from an entry list: " +
                string.Join(", ", links));
        }

        if (validation.PossibleUnverifiedNames.Count > 0)
        {
            warnings.Add(
                "Names in the copy that are not in the race data (often ordinary prose, worth a look): " +
                string.Join(", ", validation.PossibleUnverifiedNames));
        }

        return new ComposedPost(
            Text: candidate.Text,
            Attempts: attempts,
            Model: candidate.Model,
            Warnings: warnings,
            HasUnverifiedClaims: problems.Count > 0);
    }

    /// <summary>Anything that would function as a link or an account mention if published.</summary>
    /// <remarks>
    /// Deliberately broad: bare domains count, because "evil.example" is a working link once Facebook
    /// autolinks it. False positives cost a reviewer one glance; a miss costs a public post.
    /// </remarks>
    [GeneratedRegex(
        @"https?://\S+|www\.\S+|@\w+|\b[a-z0-9][a-z0-9-]*\.(?:com|net|org|io|co|ru|cn|xyz|info|link|biz)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkLike();

    private static List<string> FindLinks(string copy) =>
        [.. LinkLike().Matches(copy).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// Knobs for one composition pass.
/// </summary>
/// <param name="Model">Model to generate with.</param>
/// <param name="MaxTokens">Reply ceiling, in tokens. Generous enough that hitting it means something went wrong.</param>
/// <param name="MaxAttempts">
/// How many times to generate before giving up and flagging the draft. Each retry costs a call and
/// the failures that survive one correction round rarely survive three.
/// </param>
/// <param name="MaxCharacters">
/// Length ceiling for the finished post, or zero for none. Not a channel limit -- Facebook's is far
/// higher -- but a long post is a sign the model started narrating, and it is cheap to reject.
/// </param>
public sealed record ComposeOptions(
    string Model,
    int MaxTokens,
    int MaxAttempts,
    int MaxCharacters);

/// <summary>
/// A draft and what is known about it.
/// </summary>
/// <param name="Text">The copy.</param>
/// <param name="Attempts">Generation calls made, counting the ones rejected by validation.</param>
/// <param name="Model">The model that produced <paramref name="Text"/>.</param>
/// <param name="Warnings">Everything a reviewer should read before approving, digest gaps included.</param>
/// <param name="HasUnverifiedClaims">
/// True when the draft still fails validation. It is offered for review anyway, so this is the flag
/// that must be impossible to miss on the review screen.
/// </param>
public sealed record ComposedPost(
    string Text,
    int Attempts,
    string Model,
    IReadOnlyList<string> Warnings,
    bool HasUnverifiedClaims);
