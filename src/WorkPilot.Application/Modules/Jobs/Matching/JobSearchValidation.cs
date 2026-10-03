using WorkPilot.Contracts.Jobs;

namespace WorkPilot.Application.Modules.Jobs.Matching;

/// <summary>The bounds of a <see cref="JobListQuery"/> (spec 0021, AC-2): every bad value is a field error.</summary>
public static class JobSearchValidation
{
    /// <summary>The page size when none is asked for (spec 0019, AC-9).</summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest page size allowed.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The allowed <see cref="JobListQuery.PostedWithinDays"/> values.</summary>
    public static readonly IReadOnlyList<int> PostedWithinChoices = [1, 7, 30];

    /// <summary>The field errors of <paramref name="query"/>, keyed by query parameter name; empty when it is valid.</summary>
    public static IReadOnlyDictionary<string, string[]> Validate(JobListQuery query)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1)
        {
            errors["page"] = ["page must be 1 or more."];
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be 1 to {MaxPageSize}."];
        }

        if (query.MinScore is < 0 or > 100)
        {
            errors["minScore"] = ["minScore must be 0 to 100."];
        }

        if (query.PostedWithinDays is { } days && !PostedWithinChoices.Contains(days))
        {
            errors["postedWithinDays"] = ["postedWithinDays must be 1, 7 or 30."];
        }

        if (query.SalaryMin is < 0)
        {
            errors["salaryMin"] = ["salaryMin must be 0 or more."];
        }

        if (query.Sort is not (null or JobSorts.Score or JobSorts.Newest))
        {
            errors["sort"] = [$"sort must be {JobSorts.Score} or {JobSorts.Newest}."];
        }

        return errors;
    }

    /// <summary>The <c>LIKE</c> pattern for a case insensitive contains, with <c>%</c>, <c>_</c> and <c>\</c> escaped by <c>\</c>.</summary>
    public static string ContainsPattern(string text) =>
        "%" + text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}
