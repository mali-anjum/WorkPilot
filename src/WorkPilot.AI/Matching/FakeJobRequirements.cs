using System.Globalization;
using System.Text.RegularExpressions;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Jobs.Matching;

namespace WorkPilot.AI.Matching;

/// <summary>
/// The Fake provider's deterministic answer to an extraction prompt (spec 0019, AC-13): known skill
/// names found in the description (each quoting its sentence), plus "N+ years", remote and salary
/// patterns and visa sponsorship lines, so a fresh clone shows real looking, evidenced scores with
/// no key.
/// </summary>
public static partial class FakeJobRequirements
{
    // About 80 common technologies. Short names that are also everyday words match case sensitively.
    private static readonly string[] KnownSkills =
    [
        "C#", ".NET", "ASP.NET", "Blazor", "Entity Framework", "F#", "Java", "Kotlin", "Scala", "Spring",
        "Python", "Django", "Flask", "FastAPI", "Pandas", "NumPy", "PyTorch", "TensorFlow", "scikit-learn",
        "JavaScript", "TypeScript", "Node.js", "React", "Next.js", "Vue", "Angular", "Svelte", "HTML", "CSS",
        "Tailwind", "GraphQL", "REST", "gRPC", "Go", "Golang", "Rust", "C++", "Ruby", "Rails", "PHP", "Laravel",
        "Swift", "Objective-C", "Elixir", "Erlang", "Haskell", "Clojure", "R", "SQL", "PostgreSQL", "MySQL",
        "SQL Server", "SQLite", "MongoDB", "Redis", "Cassandra", "DynamoDB", "Elasticsearch", "Kafka",
        "RabbitMQ", "Spark", "Hadoop", "Airflow", "Snowflake", "dbt", "AWS", "Azure", "GCP", "Docker",
        "Kubernetes", "Terraform", "Ansible", "Linux", "Git", "CI/CD", "Jenkins", "GitHub Actions", "Microservices",
        "Machine Learning", "LLM", "Figma", "Playwright", "Selenium",
    ];

    private static readonly HashSet<string> CaseSensitiveSkills = new(StringComparer.Ordinal) { "Go", "R", "REST", "Rust", "Swift", "Spark", "Git" };

    private static readonly string[] PreferredMarkers = ["nice to have", "preferred", "bonus", "plus"];

    private static readonly (Regex Pattern, string Name, bool CaseSensitive)[] SkillPatterns = KnownSkills
        .Select(name =>
        {
            var caseSensitive = CaseSensitiveSkills.Contains(name);
            var options = caseSensitive ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            return (new Regex($@"(?<![A-Za-z0-9+#.]){Regex.Escape(name)}(?![A-Za-z0-9+#])", options), name, caseSensitive);
        })
        .ToArray();

    /// <summary>The v1 requirements JSON the Fake returns for <paramref name="description"/>.</summary>
    public static string Build(string description)
    {
        var sentences = Sentences(description);
        var skills = new List<SkillRequirement>();
        foreach (var (pattern, name, _) in SkillPatterns)
        {
            var hit = sentences.Select(s => (Sentence: s, Match: pattern.Match(s))).FirstOrDefault(x => x.Match.Success);
            if (hit.Match is null || skills.Count >= JobRequirementsV1.MaxSkills)
            {
                continue;
            }

            var preferred = PreferredMarkers.Any(m => hit.Sentence.Contains(m, StringComparison.OrdinalIgnoreCase));
            skills.Add(new SkillRequirement
            {
                Name = name,
                Importance = preferred ? SkillImportance.Preferred : SkillImportance.Required,
                Quote = QuoteAround(hit.Sentence, hit.Match.Index),
            });
        }

        var requirements = new JobRequirementsV1
        {
            Skills = skills,
            MinYears = FindYears(sentences),
            Remote = FindRemote(sentences),
            Salary = FindSalary(sentences),
            Sponsorship = FindSponsorship(sentences),
        };
        return MatchingJson.Serialize(requirements);
    }

    private static YearsRequirement? FindYears(IReadOnlyList<string> sentences)
    {
        foreach (var sentence in sentences)
        {
            var match = YearsPattern().Match(sentence);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var years) && years <= 60)
            {
                return new YearsRequirement { Value = years, Quote = QuoteAround(sentence, match.Index) };
            }
        }

        return null;
    }

    private static RemoteRequirement? FindRemote(IReadOnlyList<string> sentences)
    {
        foreach (var (pattern, type) in new[] { (HybridPattern(), WorkArrangement.Hybrid), (OnsitePattern(), WorkArrangement.Onsite), (RemotePattern(), WorkArrangement.Remote) })
        {
            foreach (var sentence in sentences)
            {
                var match = pattern.Match(sentence);
                if (match.Success)
                {
                    return new RemoteRequirement { Type = type, Quote = QuoteAround(sentence, match.Index) };
                }
            }
        }

        return null;
    }

    private static SalaryRequirement? FindSalary(IReadOnlyList<string> sentences)
    {
        foreach (var sentence in sentences)
        {
            var match = SalaryPattern().Match(sentence);
            if (!match.Success)
            {
                continue;
            }

            var min = Amount(match.Groups["min"].Value, match.Groups["minK"].Success);
            var max = Amount(match.Groups["max"].Value, match.Groups["maxK"].Success);
            if (min is null && max is null)
            {
                continue;
            }

            return new SalaryRequirement { Min = min, Max = max, Currency = "USD", Period = SalaryPeriod.Year, Quote = QuoteAround(sentence, match.Index) };
        }

        return null;
    }

    private static SponsorshipRequirement? FindSponsorship(IReadOnlyList<string> sentences)
    {
        foreach (var sentence in sentences)
        {
            var match = SponsorshipPattern().Match(sentence);
            if (match.Success)
            {
                return new SponsorshipRequirement { Offered = !NegationPattern().IsMatch(sentence), Quote = QuoteAround(sentence, match.Index) };
            }
        }

        return null;
    }

    private static decimal? Amount(string digits, bool thousands)
    {
        if (!decimal.TryParse(digits.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return thousands ? value * 1000 : value;
    }

    // Sentences split on ". " and new lines, trimmed, empty ones dropped.
    private static List<string> Sentences(string description) =>
        SentenceSplit().Split(description)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    // The sentence itself, or (when longer than a quote may be) a window of it around the hit,
    // which is still verbatim text from the description.
    private static string QuoteAround(string sentence, int index)
    {
        if (sentence.Length <= JobRequirementsV1.MaxQuoteLength)
        {
            return sentence;
        }

        var start = Math.Clamp(index - 100, 0, sentence.Length - JobRequirementsV1.MaxQuoteLength);
        return sentence.Substring(start, JobRequirementsV1.MaxQuoteLength).Trim();
    }

    [GeneratedRegex(@"\.\s+|\r?\n")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"(\d{1,2})\+?\s*(?:years|yrs)", RegexOptions.IgnoreCase)]
    private static partial Regex YearsPattern();

    [GeneratedRegex(@"\bhybrid\b", RegexOptions.IgnoreCase)]
    private static partial Regex HybridPattern();

    [GeneratedRegex(@"\bon[- ]?site\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnsitePattern();

    [GeneratedRegex(@"\bremote\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"(?:\$|USD\s?)\s?(?<min>\d[\d,]*(?:\.\d+)?)\s?(?<minK>[kK])?\s*(?:-|–|to)\s*(?:\$|USD\s?)?\s?(?<max>\d[\d,]*(?:\.\d+)?)\s?(?<maxK>[kK])?")]
    private static partial Regex SalaryPattern();

    [GeneratedRegex(@"visa sponsorship|sponsor", RegexOptions.IgnoreCase)]
    private static partial Regex SponsorshipPattern();

    [GeneratedRegex(@"\b(?:no|not|unable|cannot|can't|won't|will not|does not|do not)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NegationPattern();
}
