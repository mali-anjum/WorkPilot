using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Infrastructure.Modules.Jobs.Sources;

/// <summary>
/// Lever's public postings API (spec 0017, AC-6): one company's site,
/// <c>GET {BaseUrl}postings/{site}?mode=json</c>, no key needed. Maps each
/// posting only as far as <see cref="RawJobPosting"/>; cleanup is the shared
/// <see cref="JobNormalizer"/>'s job.
/// </summary>
public sealed partial class LeverJobSource(HttpClient http) : IJobSource
{
    /// <summary>Named HttpClient (and resilience pipeline) name.</summary>
    public const string ClientName = "Lever";

    /// <inheritdoc />
    public string SourceType => "Lever";

    /// <summary>The employer's own ATS: first hand data.</summary>
    public decimal ProvenanceConfidence => 1.0m;

    /// <inheritdoc />
    public JobSourceDefinition? Describe(string board)
    {
        var site = board?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SiteName().IsMatch(site))
        {
            return null;
        }

        return new JobSourceDefinition($"lever:{site}", JsonSerializer.Serialize(new LeverConfig(site)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken)
    {
        var site = ReadSite(source);

        // The site name was validated against a strict pattern when the source
        // was registered and again here, so it can't change the host or path.
        using var response = await http.GetAsync($"postings/{site}?mode=json", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        // The site name, never JobSource.CompanyName: the raw company feeds the
        // content hash, and a rename must not look like a changed posting. The
        // company name is applied later, to display and match only (spec 0017).
        return Parse(document.RootElement, site);
    }

    /// <inheritdoc />
    public RawJobPosting ParseStored(JobSource source, string rawContent)
    {
        using var document = JsonDocument.Parse(rawContent);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? Map(document.RootElement, ReadSite(source))
            : throw new JsonException("A stored Lever posting must be a JSON object.");
    }

    /// <summary>
    /// Maps a Lever postings response body (a JSON array). Lever gives no
    /// company name, so every posting gets <paramref name="company"/> (the
    /// site name). Throws
    /// <see cref="JsonException"/> when the body isn't an array.
    /// </summary>
    public static IReadOnlyList<RawJobPosting> Parse(JsonElement root, string company)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Lever response is not a postings array.");
        }

        var postings = new List<RawJobPosting>(root.GetArrayLength());
        foreach (var posting in root.EnumerateArray())
        {
            if (posting.ValueKind == JsonValueKind.Object)
            {
                postings.Add(Map(posting, company));
            }
        }

        return postings;
    }

    private static RawJobPosting Map(JsonElement posting, string company) => new(
        ExternalId: ReadString(posting, "id") ?? string.Empty,
        SourceUrl: ReadString(posting, "hostedUrl") ?? string.Empty,
        Title: ReadString(posting, "text") ?? string.Empty,
        Company: company,
        LocationText: ReadLocation(posting),
        DescriptionHtml: ReadDescription(posting),
        PostedAt: posting.TryGetProperty("createdAt", out var createdAt) && createdAt.ValueKind == JsonValueKind.Number && createdAt.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null,
        RawContent: posting.GetRawText());

    // categories.location, with a remote or hybrid workplace type added when the text doesn't say so.
    private static string? ReadLocation(JsonElement posting)
    {
        var location = posting.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Object
            ? ReadString(categories, "location")
            : null;

        var workplace = ReadString(posting, "workplaceType")?.Trim().ToLowerInvariant() switch
        {
            "remote" => RemoteTypes.Remote,
            "hybrid" => RemoteTypes.Hybrid,
            _ => null,
        };

        if (workplace is null || (location?.Contains(workplace, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return location;
        }

        return string.IsNullOrWhiteSpace(location) ? workplace : $"{location} ({workplace})";
    }

    // description, then each list as a heading plus its items, then additional.
    private static string? ReadDescription(JsonElement posting)
    {
        var html = new StringBuilder();
        Append(html, ReadString(posting, "description"));
        if (posting.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array)
        {
            foreach (var list in lists.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object))
            {
                var heading = ReadString(list, "text");
                if (!string.IsNullOrWhiteSpace(heading))
                {
                    Append(html, $"<h3>{heading}</h3>");
                }

                var content = ReadString(list, "content");
                Append(html, content is null ? null : $"<ul>{content}</ul>");
            }
        }

        Append(html, ReadString(posting, "additional"));
        return html.Length == 0 ? null : html.ToString();
    }

    private static void Append(StringBuilder html, string? part)
    {
        if (!string.IsNullOrWhiteSpace(part))
        {
            html.Append(part).Append('\n');
        }
    }

    private static string ReadSite(JobSource source)
    {
        var config = source.Config is null ? null : JsonSerializer.Deserialize<LeverConfig>(source.Config);
        var site = config?.Site ?? string.Empty;
        return SiteName().IsMatch(site)
            ? site
            : throw new InvalidOperationException($"Job source {source.Id} has no valid Lever site name in its config.");
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[a-z0-9_-]{1,100}$")]
    private static partial Regex SiteName();

    private sealed record LeverConfig(
        [property: System.Text.Json.Serialization.JsonPropertyName("site")] string Site);
}
