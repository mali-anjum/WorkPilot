using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkPilot.Domain.Modules.Jobs;

/// <summary>
/// One posting exactly as a job source returned it, mapped only as far as the
/// source's own field names (spec 0008). Any field may be empty or messy; the
/// <see cref="JobNormalizer"/> decides what is usable.
/// </summary>
/// <param name="ExternalId">The source's own stable id for the posting.</param>
/// <param name="SourceUrl">The posting's public URL at the source.</param>
/// <param name="Title">Raw title text.</param>
/// <param name="Company">Raw company name.</param>
/// <param name="LocationText">Raw free text location, if any.</param>
/// <param name="DescriptionHtml">Raw description; HTML, possibly entity encoded, or plain text.</param>
/// <param name="PostedAt">When the source says it was published, if it says.</param>
/// <param name="RawContent">The posting's verbatim payload (e.g. its JSON), kept on the snapshot.</param>
public sealed record RawJobPosting(
    string ExternalId,
    string SourceUrl,
    string Title,
    string Company,
    string? LocationText,
    string? DescriptionHtml,
    DateTimeOffset? PostedAt,
    string RawContent);

/// <summary>A posting after normalization: clean canonical fields plus its content hash.</summary>
public sealed record NormalizedJob(
    string ExternalId,
    string SourceUrl,
    string Title,
    string Company,
    string? Location,
    string? RemoteType,
    string? Description,
    DateTimeOffset? PostedAt,
    string RawContent,
    string ContentHash);

/// <summary>Canonical <see cref="Job.RemoteType"/> values. Null means unknown, never assumed on site.</summary>
public static class RemoteTypes
{
    public const string Remote = "Remote";
    public const string Hybrid = "Hybrid";
}

/// <summary>
/// A search against a source: an optional keyword filter on the posting title,
/// where any one term matching (case insensitive) keeps the posting (spec 0008).
/// </summary>
public sealed class JobSearchQuery
{
    private JobSearchQuery(IReadOnlyList<string> terms) => Terms = terms;

    /// <summary>The individual lowercase terms; empty means "match everything".</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>Splits free text keywords on whitespace and commas.</summary>
    public static JobSearchQuery Parse(string? keywords) => new(
        (keywords ?? string.Empty)
            .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList());

    /// <summary>True when no terms were given, or the title contains any term.</summary>
    public bool Matches(string title) =>
        Terms.Count == 0 || Terms.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The single, source independent normalizer (spec 0008, AC-3): every source's
/// raw postings go through the same cleanup, so the same content always yields
/// the same canonical fields and <see cref="NormalizedJob.ContentHash"/>
/// (which job deduplication, scope feature 10, relies on).
/// </summary>
public static partial class JobNormalizer
{
    /// <summary>
    /// Normalizes a raw posting, or returns <c>null</c> when a required field
    /// (id, url, title, company) is missing after cleanup, so the caller can
    /// skip and count it.
    /// </summary>
    public static NormalizedJob? Normalize(RawJobPosting raw)
    {
        var externalId = CleanLine(raw.ExternalId);
        var sourceUrl = CleanLine(raw.SourceUrl);
        var title = CleanLine(raw.Title);
        var company = CleanLine(raw.Company);
        if (externalId.Length == 0 || sourceUrl.Length == 0 || title.Length == 0 || company.Length == 0)
        {
            return null;
        }

        var location = NullIfEmpty(CleanLine(raw.LocationText));
        var description = NullIfEmpty(HtmlToPlainText(raw.DescriptionHtml));

        return new NormalizedJob(
            externalId,
            sourceUrl,
            title,
            company,
            location,
            DeriveRemoteType(location),
            description,
            raw.PostedAt?.ToUniversalTime(),
            raw.RawContent,
            ComputeContentHash(title, company, location, description));
    }

    /// <summary>Derives remote type from location text: <c>Hybrid</c> wins over <c>Remote</c>; anything else is unknown.</summary>
    public static string? DeriveRemoteType(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        if (HybridWord().IsMatch(location))
        {
            return RemoteTypes.Hybrid;
        }

        return RemoteWord().IsMatch(location) ? RemoteTypes.Remote : null;
    }

    /// <summary>SHA-256 (lowercase hex) of the normalized title, company, location and description.</summary>
    public static string ComputeContentHash(string title, string company, string? location, string? description)
    {
        var canonical = string.Join('\n', title, company, location ?? string.Empty, description ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Turns HTML (including entity encoded HTML such as <c>&amp;lt;p&amp;gt;</c>)
    /// into plain text: block elements become line breaks, list items get a
    /// leading dash, scripts and styles are dropped, whitespace is collapsed.
    /// </summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = html;

        // Entity encoded markup ("&lt;p&gt;...") carries no real tags yet.
        if (!text.Contains('<') && text.Contains("&lt;", StringComparison.OrdinalIgnoreCase))
        {
            text = WebUtility.HtmlDecode(text);
        }

        text = ScriptOrStyle().Replace(text, " ");
        text = ListItemOpen().Replace(text, "\n- ");
        text = LineBreakTag().Replace(text, "\n");
        text = BlockClose().Replace(text, "\n");
        text = AnyTag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(CleanLine)
            .ToList();

        // Collapse runs of blank lines to one, and trim leading/trailing blanks.
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0))
            {
                continue;
            }

            result.Add(line == "-" ? string.Empty : line);
        }

        while (result.Count > 0 && result[^1].Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return string.Join('\n', result);
    }

    /// <summary>Trims and collapses all inner whitespace (including non breaking spaces) to single spaces.</summary>
    public static string CleanLine(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : Whitespace().Replace(value, " ").Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex(@"[\s ​]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bhybrid\b", RegexOptions.IgnoreCase)]
    private static partial Regex HybridWord();

    [GeneratedRegex(@"\bremote\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteWord();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpen();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</(p|div|li|ul|ol|h[1-6]|tr|table|section|article|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockClose();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
}
