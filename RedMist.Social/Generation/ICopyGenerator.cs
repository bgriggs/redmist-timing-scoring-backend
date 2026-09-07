namespace RedMist.Social.Generation;

/// <summary>
/// Turns a prompt into post copy. This is the seam between composition, which is deterministic and
/// testable, and the language model, which is neither.
/// </summary>
/// <remarks>
/// Narrow on purpose. Everything that decides whether copy is acceptable -- validation, correction,
/// how many attempts are worth making -- lives in <see cref="PostComposer"/> so it can be exercised
/// without a network call or an API bill.
/// </remarks>
public interface ICopyGenerator
{
    /// <summary>Generates one candidate. Implementations retry transport failures, not bad copy.</summary>
    Task<GeneratedCopy> GenerateAsync(CopyRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One call's worth of prompt: the standing instructions plus the conversation so far.
/// </summary>
/// <param name="SystemPrompt">Instructions that apply to every turn.</param>
/// <param name="Messages">
/// The conversation, oldest first, always starting with a user turn. A correction round appends the
/// rejected copy and an explanation, which gives the model the specific thing to fix rather than
/// making it guess at what was wrong with an identical request.
/// </param>
/// <param name="Model">Model identifier, recorded on the post so a model change can be told from a prompt change.</param>
/// <param name="MaxTokens">Ceiling on the reply length.</param>
public sealed record CopyRequest(
    string SystemPrompt,
    IReadOnlyList<CopyTurn> Messages,
    string Model,
    int MaxTokens);

/// <summary>One message in the conversation.</summary>
public sealed record CopyTurn(CopyRole Role, string Text);

/// <summary>Who wrote a turn.</summary>
public enum CopyRole
{
    User,
    Assistant,
}

/// <summary>
/// What the model produced.
/// </summary>
/// <param name="Text">The copy, trimmed.</param>
/// <param name="Model">
/// The model that actually served the request, which can be more specific than the one asked for.
/// Recorded rather than assumed so a change in output can be traced to a change in model.
/// </param>
/// <param name="WasTruncated">
/// True when generation stopped at the token ceiling rather than finishing. The text is a sentence
/// fragment, so this is a generation failure and not a shorter post.
/// </param>
/// <param name="StopReason">
/// Why generation stopped, verbatim from the API. Carried because an empty reply has several very
/// different causes -- a refusal reads identically to a transport oddity -- and knowing which one it
/// was is the difference between a diagnosable failure and a blank draft nobody can explain.
/// </param>
public sealed record GeneratedCopy(string Text, string Model, bool WasTruncated, string? StopReason = null);
