using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>
/// The exact, normalized match rule for job deduplication (spec 0017, AC-3):
/// two postings are the same role when their normalized company, title and
/// location are equal. A missing location only matches a missing location.
/// </summary>
public static partial class JobDedupKey
{
    /// <summary>
    /// The version of this rule. Bump it whenever the normalization changes:
    /// every job whose <see cref="Job.DedupRuleVersion"/> is lower is then
    /// stale, and the reconcile job recomputes and merges it (AC-9).
    /// </summary>
    public const int CurrentRuleVersion = 1;

    private static readonly HashSet<string> LegalSuffixes =
    [
        "inc", "incorporated", "llc", "ltd", "limited", "gmbh", "ag", "sa", "bv", "plc", "corp", "corporation", "co",
    ];

    private static readonly Dictionary<string, string> TitleAbbreviations = new(StringComparer.Ordinal)
    {
        ["sr"] = "senior",
        ["jr"] = "junior",
        ["mgr"] = "manager",
    };

    /// <summary>SHA-256 (lowercase hex, 64 chars) of the normalized <c>company|title|location</c>.</summary>
    public static string For(string company, string title, string? location)
    {
        var canonical = string.Join('|', NormalizeCompany(company), NormalizeTitle(title), NormalizeLocation(location));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Lowercase, no punctuation, one trailing legal suffix removed (never the only word).</summary>
    public static string NormalizeCompany(string company)
    {
        var words = Words(company);
        if (words.Count > 1 && LegalSuffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    /// <summary>Lowercase, no punctuation, with <c>sr</c>, <c>jr</c> and <c>mgr</c> spelled out.</summary>
    public static string NormalizeTitle(string title) =>
        string.Join(' ', Words(title).Select(w => TitleAbbreviations.GetValueOrDefault(w, w)));

    /// <summary>Lowercase, no punctuation; a missing location becomes the empty string.</summary>
    public static string NormalizeLocation(string? location) => string.Join(' ', Words(location));

    // Lowercases, drops punctuation and symbols, and splits on whitespace (which collapses it).
    private static List<string> Words(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var kept = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (!char.IsPunctuation(c) && !char.IsSymbol(c))
            {
                kept.Append(c);
            }
        }

        return Whitespace().Split(kept.ToString()).Where(w => w.Length > 0).ToList();
    }

    [GeneratedRegex(@"[\s ​]+")]
    private static partial Regex Whitespace();
}
