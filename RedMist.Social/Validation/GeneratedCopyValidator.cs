using RedMist.Social.Digest;
using System.Text.RegularExpressions;

namespace RedMist.Social.Validation;

/// <summary>
/// Checks generated post copy against the facts it was supposed to be written from.
/// </summary>
/// <remarks>
/// The premise is that a generator handed a digest should never produce a number that was not in it.
/// Rather than trying to understand the copy, this extracts numeric tokens from the text and from the
/// digest using the same tokenizer and reports anything in the former that is missing from the latter.
/// <para>
/// KNOWN LIMIT: this catches invented values, not misattributed ones. Copy that pairs real numbers
/// with the wrong car, class or session -- "Rival Racing won with 312 laps" when 312 belongs to the
/// actual winner -- passes cleanly, because every token is genuinely in the digest. A clean result
/// means "nothing was fabricated", never "this is accurate". Catching recombination is what the
/// human review gate is for, and the reason it is not optional.
/// </para>
/// <para>
/// Numbers are checked strictly because inventing one is the failure that puts a wrong result in
/// front of the public. Names are checked loosely and advisory-only: telling a fabricated team from
/// an ordinary capitalized phrase ("Victory Lane", "Saturday Night") is not reliably decidable here,
/// so name findings are for a human to glance at, never for automatic rejection.
/// </para>
/// </remarks>
public static partial class GeneratedCopyValidator
{
    /// <summary>
    /// Digits, keeping "1:58.402" whole. A comma only joins when it groups exactly three digits, so a
    /// thousands separator stays one token while a comma-joined list ("1,2,3") stays three.
    /// </summary>
    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+(?:[.:]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumericToken();

    /// <summary>A word that could form part of a proper name, including hyphenated and possessive forms.</summary>
    [GeneratedRegex(@"[\p{L}][\p{L}'’-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <summary>
    /// Spelled-out numbers a generator may reach for instead of digits. Without this, "seven lead
    /// changes" reads as an unverifiable claim even when the digest says seven.
    /// </summary>
    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
        ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
        ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13",
        ["fourteen"] = "14", ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
        ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20", ["thirty"] = "30",
        ["forty"] = "40", ["fifty"] = "50", ["sixty"] = "60", ["seventy"] = "70",
        ["eighty"] = "80", ["ninety"] = "90",
        ["first"] = "1", ["second"] = "2", ["third"] = "3", ["fourth"] = "4", ["fifth"] = "5",
        ["sixth"] = "6", ["seventh"] = "7", ["eighth"] = "8", ["ninth"] = "9", ["tenth"] = "10",
    };

    /// <summary>
    /// Quantity words with no single digit behind them. They are always reported rather than mapped:
    /// "one hundred laps" would otherwise verify on the "one" alone, which is in almost every digest.
    /// </summary>
    private static readonly HashSet<string> UnquantifiableWords =
        new(StringComparer.OrdinalIgnoreCase) { "hundred", "thousand", "dozen" };

    /// <summary>
    /// Validates copy against the digest it was generated from.
    /// </summary>
    /// <param name="copy">The generated post text.</param>
    /// <param name="digest">The facts the copy was supposed to be drawn from.</param>
    public static CopyValidationResult Validate(string copy, EventDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (string.IsNullOrWhiteSpace(copy))
            return new CopyValidationResult([], []);

        var facts = digest.AssertableFacts();

        // Only the numeric facts, never every fact string: digits inside names ("GP1",
        // "Saturday 8 Hour") would otherwise pre-authorize most small integers.
        var knownNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in digest.NumericFacts())
        {
            foreach (var token in NumericToken().Matches(fact).Cast<Match>())
                knownNumbers.Add(Normalize(token.Value));
        }

        return new CopyValidationResult(
            UnverifiedNumbers: FindUnverifiedNumbers(copy, knownNumbers),
            PossibleUnverifiedNames: FindPossibleUnverifiedNames(copy, facts));
    }

    private static List<string> FindUnverifiedNumbers(string copy, HashSet<string> knownNumbers)
    {
        var unverified = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Check(string raw, string normalized)
        {
            if (normalized.Length == 0 || knownNumbers.Contains(normalized))
                return;
            if (seen.Add(normalized))
                unverified.Add(raw);
        }

        foreach (var match in NumericToken().Matches(copy).Cast<Match>())
            Check(match.Value, Normalize(match.Value));

        foreach (var match in Word().Matches(copy).Cast<Match>())
        {
            // Split compounds: Word() includes "-", so "twenty-one" arrives as one token and would
            // otherwise miss the dictionary entirely.
            foreach (var part in match.Value.Split('-', StringSplitOptions.RemoveEmptyEntries))
            {
                if (NumberWords.TryGetValue(part, out var asDigits))
                    Check(part, asDigits);
                else if (UnquantifiableWords.Contains(part))
                    Check(part, part.ToLowerInvariant());
            }
        }

        return unverified;
    }

    /// <summary>
    /// Flags runs of two or more consecutive capitalized words that appear nowhere in the facts.
    /// Single words are ignored: a lone capitalized word is usually just the start of a sentence, and
    /// reporting those would bury the occasional real finding in noise. Requiring two words lets a name
    /// that opens a sentence still be caught while grammatical capitalization on its own cannot.
    /// </summary>
    private static List<string> FindPossibleUnverifiedNames(string copy, IReadOnlySet<string> facts)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sentence in copy.Split(['.', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = Word().Matches(sentence).Cast<Match>().Select(m => m.Value).ToList();
            var run = new List<string>();

            // From 0: a name can open a sentence. A lone opening word never reaches the >= 2 test
            // below, so grammatical capitalization still cannot produce a finding on its own.
            for (int i = 0; i <= words.Count; i++)
            {
                var isCapitalized = i < words.Count && char.IsUpper(words[i][0]);
                if (isCapitalized)
                {
                    run.Add(words[i]);
                    continue;
                }

                if (run.Count >= 2)
                {
                    var candidate = string.Join(' ', run);
                    if (!facts.Any(f => f.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                        && seen.Add(candidate))
                    {
                        findings.Add(candidate);
                    }
                }

                run.Clear();
            }
        }

        return findings;
    }

    /// <summary>Commas are thousands separators in copy but absent from the facts, so they are dropped.</summary>
    private static string Normalize(string token) => token.Replace(",", string.Empty);
}

/// <summary>
/// What validation found. Numeric findings are disqualifying; name findings are for human review.
/// </summary>
/// <param name="UnverifiedNumbers">Numbers in the copy that do not appear anywhere in the digest.</param>
/// <param name="PossibleUnverifiedNames">
/// Capitalized phrases absent from the digest. Advisory only -- ordinary prose produces these.
/// </param>
public sealed record CopyValidationResult(
    IReadOnlyList<string> UnverifiedNumbers,
    IReadOnlyList<string> PossibleUnverifiedNames)
{
    /// <summary>
    /// True when the copy states a number the digest cannot support. This is the condition that
    /// should send a draft back for regeneration rather than to a reviewer.
    /// </summary>
    public bool HasInventedNumbers => UnverifiedNumbers.Count > 0;
}
