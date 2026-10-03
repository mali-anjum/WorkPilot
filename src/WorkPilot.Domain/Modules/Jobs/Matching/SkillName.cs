using System.Text;
using System.Text.RegularExpressions;

namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>
/// Skill name normalization (spec 0019): trim, lower case, hyphens, underscores and whitespace runs
/// to one space, keep <c>#</c>, <c>+</c> and <c>.</c> (so <c>C#</c>, <c>C++</c>, <c>C</c> and
/// <c>.NET</c> stay distinct), drop a trailing version token that follows a space
/// (<c>.NET 8</c> → <c>.net</c>, while <c>python3</c> stays), then map through the aliases.
/// </summary>
public static partial class SkillName
{
    /// <summary>The normalized form of <paramref name="name"/>, before aliases.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var raw in name.Trim())
        {
            var c = char.ToLowerInvariant(raw);
            if (c is '-' or '_' || char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        var text = builder.ToString();
        var version = TrailingVersion().Match(text);
        return version.Success ? text[..version.Index] : text;
    }

    /// <summary>The normalized form of <paramref name="name"/>, mapped through <paramref name="aliases"/> (keys and values normalized).</summary>
    public static string Normalize(string? name, IReadOnlyDictionary<string, string> aliases)
    {
        var normalized = Normalize(name);
        return aliases.TryGetValue(normalized, out var canonical) ? canonical : normalized;
    }

    /// <summary>Normalizes both sides of an alias map, so lookups can use normalized names.</summary>
    public static IReadOnlyDictionary<string, string> NormalizeAliases(IReadOnlyDictionary<string, string> aliases)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, canonical) in aliases)
        {
            var key = Normalize(alias);
            if (key.Length > 0)
            {
                normalized[key] = Normalize(canonical);
            }
        }

        return normalized;
    }

    [GeneratedRegex(@" [0-9][0-9.]*$")]
    private static partial Regex TrailingVersion();
}
