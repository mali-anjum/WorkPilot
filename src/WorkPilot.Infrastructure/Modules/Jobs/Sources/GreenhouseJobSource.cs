using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs;

namespace WorkPilot.Infrastructure.Modules.Jobs.Sources;

/// <summary>
/// Greenhouse's public Job Board API (spec 0008): one company's board,
/// <c>GET {BaseUrl}boards/{token}/jobs?content=true</c>, no key needed. Maps
/// each posting only as far as <see cref="RawJobPosting"/>; cleanup is the
/// shared <see cref="JobNormalizer"/>'s job.
/// </summary>
public sealed partial class GreenhouseJobSource(HttpClient http) : IJobSource
{
    /// <summary>Named HttpClient (and resilience pipeline) name.</summary>
    public const string ClientName = "Greenhouse";

    /// <inheritdoc />
    public string SourceType => "Greenhouse";

    /// <summary>The employer's own ATS: first hand data.</summary>
    public decimal ProvenanceConfidence => 1.0m;

    /// <inheritdoc />
    public JobSourceDefinition? Describe(string board)
    {
        var token = board?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!BoardToken().IsMatch(token))
        {
            return null;
        }

        return new JobSourceDefinition($"greenhouse:{token}", JsonSerializer.Serialize(new GreenhouseConfig(token)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken)
    {
        var token = ReadBoardToken(source);

        // The token was validated against a strict pattern when the source was
        // registered and again here, so it can't change the host or path.
        using var response = await http.GetAsync($"boards/{token}/jobs?content=true", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return Parse(document.RootElement, token);
    }

    /// <summary>Maps a Greenhouse <c>jobs</c> response body. Throws <see cref="JsonException"/> when it isn't one.</summary>
    public static IReadOnlyList<RawJobPosting> Parse(JsonElement root, string boardToken)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jobs", out var jobs)
            || jobs.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Greenhouse response has no 'jobs' array.");
        }

        var postings = new List<RawJobPosting>(jobs.GetArrayLength());
        foreach (var job in jobs.EnumerateArray())
        {
            if (job.ValueKind == JsonValueKind.Object)
            {
                postings.Add(Map(job, boardToken));
            }
        }

        return postings;
    }

    /// <inheritdoc />
    public RawJobPosting ParseStored(JobSource source, string rawContent)
    {
        using var document = JsonDocument.Parse(rawContent);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? Map(document.RootElement, ReadBoardToken(source))
            : throw new JsonException("A stored Greenhouse posting must be a JSON object.");
    }

    // One element of the "jobs" array; its raw JSON is what a snapshot keeps.
    private static RawJobPosting Map(JsonElement job, string boardToken) => new(
        ExternalId: ReadScalar(job, "id") ?? string.Empty,
        SourceUrl: ReadScalar(job, "absolute_url") ?? string.Empty,
        Title: ReadScalar(job, "title") ?? string.Empty,
        Company: ReadScalar(job, "company_name") ?? boardToken,
        LocationText: job.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object
            ? ReadScalar(location, "name")
            : null,
        DescriptionHtml: ReadScalar(job, "content"),
        PostedAt: ReadDate(job, "first_published") ?? ReadDate(job, "updated_at"),
        RawContent: job.GetRawText());

    private static string ReadBoardToken(JobSource source)
    {
        var config = source.Config is null ? null : JsonSerializer.Deserialize<GreenhouseConfig>(source.Config);
        var token = config?.BoardToken ?? string.Empty;
        return BoardToken().IsMatch(token)
            ? token
            : throw new InvalidOperationException($"Job source {source.Id} has no valid Greenhouse board token in its config.");
    }

    private static string? ReadScalar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ReadScalar(element, property), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    [GeneratedRegex("^[a-z0-9_-]{1,100}$")]
    private static partial Regex BoardToken();

    private sealed record GreenhouseConfig(
        [property: System.Text.Json.Serialization.JsonPropertyName("boardToken")] string BoardToken);
}
