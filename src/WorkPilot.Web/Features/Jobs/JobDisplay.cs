using System.Globalization;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Features.Jobs;

/// <summary>How the jobs pages word and color a job (spec 0021, AC-3, AC-5). Pure, so it is unit tested on its own.</summary>
public static class JobDisplay
{
    /// <summary>Rows per page on <c>/jobs</c> (AC-1).</summary>
    public const int PageSize = 25;

    /// <summary>The score badge: <c>82 · High</c>.</summary>
    public static string ScoreLabel(JobListItemDto item) =>
        item.Confidence is null ? $"{item.Score}" : $"{item.Score} · {item.Confidence}";

    /// <summary>The score band: blocked is danger, 70 and up success, 40 and up warning, else danger.</summary>
    public static StatusKind ScoreStatus(int? score, bool hasBlocker) => (score, hasBlocker) switch
    {
        (null, _) => StatusKind.Neutral,
        (_, true) => StatusKind.Danger,
        ( >= 70, _) => StatusKind.Success,
        ( >= 40, _) => StatusKind.Warning,
        _ => StatusKind.Danger,
    };

    /// <summary>Why an unscored row has no score (AC-3); null for a scored row.</summary>
    public static string? NotScoredReason(string matchStatus) => matchStatus switch
    {
        JobMatchStatuses.NotEnoughInfo => "Not enough information",
        JobMatchStatuses.Pending => "Scoring…",
        JobMatchStatuses.ProfileIncomplete => "Complete your profile",
        _ => null,
    };

    /// <summary>A source type as shown on its badge.</summary>
    public static string SourceLabel(string sourceType) =>
        sourceType.Length == 0 ? sourceType : char.ToUpperInvariant(sourceType[0]) + sourceType[1..];

    /// <summary>The salary range, or null when neither end is known.</summary>
    public static string? Salary(decimal? min, decimal? max) => (min, max) switch
    {
        (null, null) => null,
        ({ } low, null) => $"From {Amount(low)}",
        (null, { } high) => $"Up to {Amount(high)}",
        ({ } low, { } high) when low == high => Amount(low),
        ({ } low, { } high) => $"{Amount(low)} to {Amount(high)}",
    };

    /// <summary>
    /// The page numbers to show for <paramref name="current"/> of <paramref name="total"/>: the
    /// first, the last, and two either side of the current one; null marks a gap.
    /// </summary>
    public static IReadOnlyList<int?> PageWindow(int current, int total)
    {
        var pages = new List<int?>();
        for (var page = 1; page <= total; page++)
        {
            if (page == 1 || page == total || Math.Abs(page - current) <= 2)
            {
                pages.Add(page);
            }
            else if (pages.Count > 0 && pages[^1] is not null)
            {
                pages.Add(null);
            }
        }

        return pages;
    }

    /// <summary>How many pages <paramref name="total"/> rows fill, at least one.</summary>
    public static int PageCount(int total, int pageSize) => Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

    /// <summary>A posting URL from an external feed becomes a link only when it is http or https.</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string Amount(decimal value) => value.ToString("#,0", CultureInfo.InvariantCulture);
}
