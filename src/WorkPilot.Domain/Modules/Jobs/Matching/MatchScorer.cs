using System.Globalization;
using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Domain.Modules.Jobs.Matching;

/// <summary>
/// The deterministic, explainable scorer (spec 0019, "Scoring rules"). It compares a job's verified
/// requirements (or, when extraction failed, its own columns) with a profile, dimension by
/// dimension, and never calls an AI model. The same inputs always give the same result.
/// </summary>
public static class MatchScorer
{
    /// <summary>Bump when a scoring rule changes: every match is rescored by the sweep (AC-7).</summary>
    public const int ScoringVersion = 1;

    private static readonly HashSet<string> SeniorityWords = new(StringComparer.Ordinal)
    {
        "senior", "junior", "lead", "staff", "principal", "sr", "jr", "i", "ii", "iii", "iv",
    };

    private static readonly char[] TitleSeparators = [' ', '/', ',', '-'];

    /// <summary>Scores <paramref name="job"/> against <paramref name="profile"/>; <paramref name="today"/> closes open ended experience.</summary>
    public static MatchResult Score(JobMatchInput job, MatchProfileInput profile, MatchingSettings settings, DateOnly today)
    {
        var context = new Context(job, profile, settings, today);
        var dimensions = new List<DimensionResult>
        {
            context.ScoreSkills(),
            context.ScoreTitle(),
            context.ScoreExperience(),
            context.ScoreLocation(),
            context.ScoreSalary(),
            context.ScoreEducation(),
            context.ScoreJobType(),
            context.ScoreWorkAuthorization(),
        };

        var known = dimensions.Where(d => d.Status != DimensionStatus.Unknown).ToList();
        var knownWeight = known.Sum(d => d.Weight);
        var totalWeight = dimensions.Sum(d => d.Weight);
        var hasBlocker = dimensions.Any(d => d.Status == DimensionStatus.Blocker);

        int? score = null;
        if (knownWeight > 0)
        {
            var raw = known.Sum(d => context.EarnedOf(d.Name)) / knownWeight * 100m;
            if (hasBlocker)
            {
                raw = Math.Min(raw, settings.BlockerCap);
            }

            score = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        }

        var share = totalWeight == 0 ? 0m : (decimal)knownWeight / totalWeight;
        var confidence = knownWeight == 0 || job.ExtractionFailed ? MatchConfidence.Low
            : share >= settings.Confidence.High ? MatchConfidence.High
            : share >= settings.Confidence.Medium ? MatchConfidence.Medium
            : MatchConfidence.Low;

        var explanation = new MatchExplanation
        {
            Dimensions = dimensions,
            Missing = context.Missing,
            Unknown = context.Unknown,
            Blockers = context.Blockers,
            Unverified = context.Unverified,
            ExtractionFailed = job.ExtractionFailed,
        };
        return new MatchResult(score, confidence, hasBlocker, explanation);
    }

    /// <summary>
    /// The union of the experience date ranges (overlaps merged, an open ended row runs to
    /// <paramref name="today"/>), in years to one decimal.
    /// </summary>
    public static decimal YearsOfExperience(IEnumerable<ExperienceInput> experiences, DateOnly today)
    {
        var ranges = experiences
            .Select(e => (Start: e.StartDate, End: e.EndDate ?? today))
            .Where(r => r.End > r.Start)
            .OrderBy(r => r.Start)
            .ToList();

        var days = 0;
        DateOnly? currentStart = null;
        DateOnly currentEnd = default;
        foreach (var (start, end) in ranges)
        {
            if (currentStart is null || start > currentEnd)
            {
                if (currentStart is { } s)
                {
                    days += currentEnd.DayNumber - s.DayNumber;
                }

                currentStart = start;
                currentEnd = end;
            }
            else if (end > currentEnd)
            {
                currentEnd = end;
            }
        }

        if (currentStart is { } last)
        {
            days += currentEnd.DayNumber - last.DayNumber;
        }

        return Math.Round(days / 365.25m, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>The comparable words of a title: normalized, split on spaces, <c>/</c>, <c>,</c> and <c>-</c>, seniority words removed.</summary>
    public static IReadOnlyList<string> TitleTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        return QuoteVerifier.Normalize(title)
            .Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('(', ')', '[', ']', '.', ':', ';', '!', '?', '"', '\''))
            .Where(t => t.Length > 0 && !SeniorityWords.Contains(t))
            .ToList();
    }

    private sealed class Context(JobMatchInput job, MatchProfileInput profile, MatchingSettings settings, DateOnly today)
    {
        private readonly QuoteVerifier _verifier = new(job.VerificationText);
        private readonly IReadOnlyDictionary<string, string> _aliases = SkillName.NormalizeAliases(settings.SkillAliases);
        private readonly Dictionary<MatchDimension, decimal> _earned = [];
        private readonly JobRequirementsV1? _requirements = job.ExtractionFailed ? null : job.Requirements;

        public List<ExplanationItem> Missing { get; } = [];
        public List<UnknownInfo> Unknown { get; } = [];
        public List<ExplanationItem> Blockers { get; } = [];
        public List<UnverifiedItem> Unverified { get; } = [];

        public decimal EarnedOf(MatchDimension dimension) => _earned.GetValueOrDefault(dimension);

        // Silent, or (when extraction failed) unreadable: the reason a dimension that reads only
        // extracted requirements is Unknown.
        private UnknownReason JobSilence => job.ExtractionFailed ? UnknownReason.ExtractionFailed : UnknownReason.JobSilent;

        public DimensionResult ScoreSkills()
        {
            const MatchDimension dimension = MatchDimension.Skills;
            var verified = new List<SkillRequirement>();
            foreach (var skill in _requirements?.Skills ?? [])
            {
                if (_verifier.IsVerified(skill.Quote))
                {
                    verified.Add(skill);
                }
                else
                {
                    AddUnverified(dimension, skill.Name, skill.Quote);
                }
            }

            // One line per normalized name; Required wins over Preferred.
            var skills = verified
                .GroupBy(s => SkillName.Normalize(s.Name, _aliases))
                .Where(g => g.Key.Length > 0)
                .Select(g => (Key: g.Key, Skill: g.OrderBy(s => s.Importance).First()))
                .ToList();
            if (skills.Count == 0)
            {
                return UnknownResult(dimension, JobSilence);
            }

            if (profile.Skills.Count == 0)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var mine = profile.Skills
                .GroupBy(s => SkillName.Normalize(s.Name, _aliases))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var items = new List<ExplanationItem>();
            decimal total = 0, matched = 0;
            foreach (var (key, skill) in skills)
            {
                var count = skill.Importance == SkillImportance.Required ? 2 : 1;
                total += count;
                var label = $"{skill.Name} ({skill.Importance.ToString().ToLowerInvariant()})";
                if (mine.TryGetValue(key, out var own))
                {
                    matched += count;
                    items.Add(Item(label, DimensionStatus.Met, skill.Quote, new ProfileRef { Kind = ProfileRefKind.Skill, Id = own.Id, Label = own.Name }));
                }
                else
                {
                    var item = Item(label, DimensionStatus.Missed, skill.Quote, null);
                    items.Add(item);
                    if (skill.Importance == SkillImportance.Required)
                    {
                        Missing.Add(item);
                    }
                }
            }

            var status = matched == total ? DimensionStatus.Met : matched == 0 ? DimensionStatus.Missed : DimensionStatus.Partial;
            return Known(dimension, status, matched / total * Weight(dimension), items);
        }

        public DimensionResult ScoreTitle()
        {
            const MatchDimension dimension = MatchDimension.Title;
            var titleTokens = TitleTokens(job.Title).ToHashSet(StringComparer.Ordinal);
            var roles = profile.TargetRoles
                .Select(r => (Role: r, Tokens: TitleTokens(r)))
                .Where(r => r.Tokens.Count > 0)
                .ToList();
            if (roles.Count == 0)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var best = roles
                .Select(r => (r.Role, Share: (decimal)r.Tokens.Count(titleTokens.Contains) / r.Tokens.Count))
                .OrderByDescending(r => r.Share)
                .First();
            var status = best.Share == 1 ? DimensionStatus.Met : best.Share == 0 ? DimensionStatus.Missed : DimensionStatus.Partial;
            var item = new ExplanationItem
            {
                Label = $"Title \"{job.Title}\"",
                Status = status,
                JobQuote = job.Title,
                Verified = true,
                ProfileRef = new ProfileRef { Kind = ProfileRefKind.TargetRole, Label = best.Role },
            };
            return Known(dimension, status, best.Share * Weight(dimension), [item]);
        }

        public DimensionResult ScoreExperience()
        {
            const MatchDimension dimension = MatchDimension.Experience;
            var minYears = Verified(dimension, _requirements?.MinYears, r => r.Quote, r => $"{r.Value}+ years of experience");
            if (minYears is null)
            {
                return UnknownResult(dimension, JobSilence);
            }

            if (profile.Experiences.Count == 0)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var years = YearsOfExperience(profile.Experiences, today);
            var met = years >= minYears.Value;
            var status = met ? DimensionStatus.Met : DimensionStatus.Partial;
            var label = $"{minYears.Value}+ years of experience";
            var item = Item(label, status, minYears.Quote, new ProfileRef
            {
                Kind = ProfileRefKind.Experience,
                Label = $"{years.ToString("0.0", CultureInfo.InvariantCulture)} years across {profile.Experiences.Count} role(s)",
            });
            if (!met)
            {
                Missing.Add(item);
            }

            var earned = met ? Weight(dimension) : years / minYears.Value * Weight(dimension);
            return Known(dimension, status, earned, [item]);
        }

        public DimensionResult ScoreLocation()
        {
            const MatchDimension dimension = MatchDimension.Location;
            var (arrangement, arrangementQuote) = JobArrangement();
            var places = JobPlaces(dimension, out var unparseable);
            var preference = profile.RemotePreference;
            var preferred = profile.PreferredLocations
                .Select(l => new PlaceInput(PlaceText.NormalizeCity(l.City), l.Country))
                .ToList();

            if (arrangement is null && places.Count == 0)
            {
                return UnknownResult(dimension, unparseable ? UnknownReason.Unparseable : JobSilence);
            }

            if (preference == RemotePreference.Any && preferred.Count == 0)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var preferenceRef = new ProfileRef
            {
                Kind = ProfileRefKind.Preference,
                Label = preferred.Count == 0
                    ? $"Work arrangement: {preference}"
                    : $"Work arrangement: {preference}; preferred: {string.Join(", ", profile.PreferredLocations.Select(Describe))}",
            };

            if (arrangement == WorkArrangement.Remote)
            {
                var accepted = preference is RemotePreference.Remote or RemotePreference.Any;
                var status = accepted ? DimensionStatus.Met : DimensionStatus.Partial;
                var item = Item("Remote job", status, arrangementQuote, preferenceRef);
                return Known(dimension, status, accepted ? Weight(dimension) : Weight(dimension) / 2m, [item]);
            }

            if (places.Count == 0)
            {
                // An onsite or hybrid job that names no place we can read.
                return UnknownResult(dimension, unparseable ? UnknownReason.Unparseable : JobSilence);
            }

            var label = $"{arrangement?.ToString() ?? "Located"} in {string.Join("; ", places.Select(p => p.Label))}";
            var quote = places.First().Quote ?? arrangementQuote;
            if (preferred.Count == 0)
            {
                if (preference == RemotePreference.Remote)
                {
                    return arrangement is null
                        ? Known(dimension, DimensionStatus.Missed, 0, [Item(label, DimensionStatus.Missed, quote, preferenceRef)])
                        : BlockerResult(dimension, label, quote, preferenceRef);
                }

                // Onsite or hybrid accepted, but with no preferred place nothing can be compared.
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var match = places.Any(p => preferred.Any(pref => SamePlace(p.Place, pref)));
            if (match)
            {
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [Item(label, DimensionStatus.Met, quote, preferenceRef)]);
            }

            if (preference == RemotePreference.Remote && arrangement is not null)
            {
                return BlockerResult(dimension, label, quote, preferenceRef);
            }

            return Known(dimension, DimensionStatus.Missed, 0, [Item(label, DimensionStatus.Missed, quote, preferenceRef)]);
        }

        public DimensionResult ScoreSalary()
        {
            const MatchDimension dimension = MatchDimension.Salary;
            var salary = Verified(dimension, _requirements?.Salary, s => s.Quote, s => "Salary");
            if (salary is null || (salary.Min is null && salary.Max is null))
            {
                return UnknownResult(dimension, JobSilence);
            }

            if (profile.MinSalary is not { } minimum || profile.SalaryCurrency is null)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            if (!string.Equals(salary.Currency.Trim(), profile.SalaryCurrency, StringComparison.OrdinalIgnoreCase) || salary.Period != SalaryPeriod.Year)
            {
                return UnknownResult(dimension, UnknownReason.CurrencyDiffers);
            }

            var offered = salary.Max ?? salary.Min!.Value;
            var met = offered >= minimum;
            var status = met ? DimensionStatus.Met : DimensionStatus.Missed;
            var item = Item(
                $"Salary up to {offered.ToString("N0", CultureInfo.InvariantCulture)} {salary.Currency.Trim().ToUpperInvariant()} a year",
                status,
                salary.Quote,
                new ProfileRef { Kind = ProfileRefKind.Preference, Label = $"Minimum salary {minimum.ToString("N0", CultureInfo.InvariantCulture)} {profile.SalaryCurrency}" });
            return Known(dimension, status, met ? Weight(dimension) : 0, [item]);
        }

        public DimensionResult ScoreEducation()
        {
            const MatchDimension dimension = MatchDimension.Education;
            var degree = Verified(dimension, _requirements?.Degree, d => d.Quote, d => $"{d.Level} degree");
            if (degree is null)
            {
                return UnknownResult(dimension, JobSilence);
            }

            var best = profile.Educations.Where(e => e.Level != DegreeLevel.None).MaxBy(e => e.Level);
            if (best is null)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var label = degree.Field is { Length: > 0 } field ? $"{degree.Level} degree in {field}" : $"{degree.Level} degree";
            if (best.Level >= degree.Level)
            {
                var item = Item(label, DimensionStatus.Met, degree.Quote, new ProfileRef { Kind = ProfileRefKind.Education, Id = best.Id, Label = $"{best.Degree}, {best.Field}" });
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [item]);
            }

            var minYears = _requirements?.MinYears is { } y && _verifier.IsVerified(y.Quote) ? y.Value : (int?)null;
            if (degree.OrEquivalentExperience && minYears is { } needed && YearsOfExperience(profile.Experiences, today) >= needed && profile.Experiences.Count > 0)
            {
                var item = Item($"{label} or equivalent experience", DimensionStatus.Met, degree.Quote, new ProfileRef { Kind = ProfileRefKind.Experience, Label = "Equivalent experience" });
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [item]);
            }

            var missed = Item(label, DimensionStatus.Missed, degree.Quote, new ProfileRef { Kind = ProfileRefKind.Education, Id = best.Id, Label = $"{best.Degree}, {best.Field}" });
            Missing.Add(missed);
            return Known(dimension, DimensionStatus.Missed, 0, [missed]);
        }

        public DimensionResult ScoreJobType()
        {
            const MatchDimension dimension = MatchDimension.JobType;
            var jobType = Verified(dimension, _requirements?.JobType, t => t.Quote, t => t.Value.ToString());
            if (jobType is null)
            {
                return UnknownResult(dimension, JobSilence);
            }

            if (profile.JobTypes.Count == 0)
            {
                return UnknownResult(dimension, UnknownReason.ProfileNotSet);
            }

            var met = profile.JobTypes.Contains(jobType.Value);
            var status = met ? DimensionStatus.Met : DimensionStatus.Missed;
            var item = Item(jobType.Value.ToString(), status, jobType.Quote, new ProfileRef
            {
                Kind = ProfileRefKind.Preference,
                Label = $"Job types: {string.Join(", ", profile.JobTypes)}",
            });
            return Known(dimension, status, met ? Weight(dimension) : 0, [item]);
        }

        public DimensionResult ScoreWorkAuthorization()
        {
            const MatchDimension dimension = MatchDimension.WorkAuthorization;
            var places = JobPlaces(dimension, out _);
            var countries = places.Select(p => p.Place.Country).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (countries.Count == 0)
            {
                return UnknownResult(dimension, JobSilence);
            }

            var quote = places.First().Quote;
            var label = $"Work in {string.Join(", ", countries)}";
            if (countries.All(c => profile.AuthorizedCountries.Contains(c, StringComparer.Ordinal)))
            {
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [Item(label, DimensionStatus.Met, quote, new ProfileRef
                {
                    Kind = ProfileRefKind.Preference,
                    Label = $"Authorized in {string.Join(", ", profile.AuthorizedCountries)}",
                })]);
            }

            if (!profile.NeedsSponsorshipElsewhere)
            {
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [Item(label, DimensionStatus.Met, quote, new ProfileRef
                {
                    Kind = ProfileRefKind.Preference,
                    Label = "No sponsorship needed",
                })]);
            }

            var sponsorship = Verified(dimension, _requirements?.Sponsorship, s => s.Quote, s => s.Offered ? "Visa sponsorship offered" : "No visa sponsorship");
            var needsRef = new ProfileRef { Kind = ProfileRefKind.Preference, Label = "Needs visa sponsorship" };
            if (sponsorship is { Offered: true })
            {
                return Known(dimension, DimensionStatus.Met, Weight(dimension), [Item("Visa sponsorship offered", DimensionStatus.Met, sponsorship.Quote, needsRef)]);
            }

            if (sponsorship is { Offered: false })
            {
                return BlockerResult(dimension, "No visa sponsorship", sponsorship.Quote, needsRef);
            }

            return UnknownResult(dimension, JobSilence);
        }

        // The job's work arrangement: extracted when verified, else (fallback only) the RemoteType column.
        private (WorkArrangement? Arrangement, string? Quote) JobArrangement()
        {
            if (job.ExtractionFailed)
            {
                return job.RemoteType switch
                {
                    RemoteTypes.Remote => (WorkArrangement.Remote, null),
                    RemoteTypes.Hybrid => (WorkArrangement.Hybrid, null),
                    _ => (null, null),
                };
            }

            var remote = Verified(MatchDimension.Location, _requirements?.Remote, r => r.Quote, r => r.Type.ToString());
            return remote is null ? (null, null) : (remote.Type, remote.Quote);
        }

        // The job's places: extracted and verified, else (fallback only) the parsed Location column.
        // Unverified places are recorded once, under the Location dimension.
        private List<(PlaceInput Place, string Label, string? Quote)> JobPlaces(MatchDimension dimension, out bool unparseable)
        {
            unparseable = false;
            if (job.ExtractionFailed)
            {
                var parsed = PlaceText.ParseJobLocation(job.Location);
                unparseable = parsed is null && !string.IsNullOrWhiteSpace(job.Location);
                return parsed is null ? [] : [(parsed, job.Location!.Trim(), null)];
            }

            var places = new List<(PlaceInput, string, string?)>();
            foreach (var location in _requirements?.Locations ?? [])
            {
                if (!_verifier.IsVerified(location.Quote))
                {
                    if (dimension == MatchDimension.Location)
                    {
                        AddUnverified(dimension, Describe(location.City, location.Country), location.Quote);
                    }

                    continue;
                }

                places.Add((new PlaceInput(PlaceText.NormalizeCity(location.City), IsoCodes.FindCountry(location.Country)), Describe(location.City, location.Country), location.Quote));
            }

            return places;
        }

        private T? Verified<T>(MatchDimension dimension, T? value, Func<T, string> quote, Func<T, string> label)
            where T : class
        {
            if (value is null)
            {
                return null;
            }

            if (_verifier.IsVerified(quote(value)))
            {
                return value;
            }

            AddUnverified(dimension, label(value), quote(value));
            return null;
        }

        private void AddUnverified(MatchDimension dimension, string label, string? quote)
        {
            if (Unverified.Any(u => u.Dimension == dimension && u.Label == label && u.JobQuote == (quote ?? string.Empty)))
            {
                return;
            }

            Unverified.Add(new UnverifiedItem { Dimension = dimension, Label = label, JobQuote = quote ?? string.Empty });
        }

        // A line from a verified extracted quote, or (fallback) from a job column, which has no quote
        // and counts as verified.
        private static ExplanationItem Item(string label, DimensionStatus status, string? quote, ProfileRef? profileRef) => new()
        {
            Label = label,
            Status = status,
            JobQuote = quote,
            Verified = true,
            ProfileRef = profileRef,
        };

        private int Weight(MatchDimension dimension) => settings.Weights.Of(dimension);

        private DimensionResult Known(MatchDimension dimension, DimensionStatus status, decimal earned, List<ExplanationItem> items)
        {
            _earned[dimension] = earned;
            return new DimensionResult
            {
                Name = dimension,
                Status = status,
                Earned = Math.Round(earned, 2, MidpointRounding.AwayFromZero),
                Weight = Weight(dimension),
                Items = items,
            };
        }

        private DimensionResult UnknownResult(MatchDimension dimension, UnknownReason reason)
        {
            Unknown.Add(new UnknownInfo { Dimension = dimension, Reason = reason });
            return new DimensionResult { Name = dimension, Status = DimensionStatus.Unknown, Earned = 0, Weight = Weight(dimension) };
        }

        private DimensionResult BlockerResult(MatchDimension dimension, string label, string? quote, ProfileRef profileRef)
        {
            var item = new ExplanationItem
            {
                Label = label,
                Status = DimensionStatus.Blocker,
                JobQuote = quote,
                Verified = true,
                ProfileRef = profileRef,
                Dimension = dimension,
            };
            Blockers.Add(item);
            return Known(dimension, DimensionStatus.Blocker, 0, [item]);
        }

        private static bool SamePlace(PlaceInput job, PlaceInput preferred)
        {
            if (job.Country is null || !string.Equals(job.Country, preferred.Country, StringComparison.Ordinal))
            {
                return false;
            }

            return preferred.City is null || string.Equals(job.City, preferred.City, StringComparison.Ordinal);
        }

        private static string Describe(PreferredLocation location) => Describe(location.City, location.Country);

        private static string Describe(string? city, string country) =>
            string.IsNullOrWhiteSpace(city) ? country.Trim() : $"{city.Trim()}, {country.Trim()}";
    }
}
