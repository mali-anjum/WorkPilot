using WorkPilot.Domain.Common;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Domain.Tests;

// The matching engine's Domain rules (spec 0019): skill names, quote checks, the scorer's eight
// dimensions, renormalization, confidence, blockers, the fallback, the fingerprint, the settings,
// the requirements row lifecycle, the match profile rules and place parsing. No database.
public class JobMatchingTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    private const string Description =
        "We build payment rails. You have 5+ years of experience with C# and PostgreSQL. " +
        "Kubernetes is nice to have. A Bachelor degree in Computer Science or equivalent experience is required. " +
        "This role is remote. The salary range is $120,000 - $160,000 per year. " +
        "This is a full time position. We do not offer visa sponsorship. The office is in Berlin, Germany.";

    // ---------- builders ----------

    private static MatchProfileInput Profile(
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<ExperienceInput>? experiences = null,
        IReadOnlyList<EducationInput>? educations = null,
        RemotePreference remote = RemotePreference.Any,
        IReadOnlyList<PreferredLocation>? locations = null,
        IReadOnlyList<JobType>? jobTypes = null,
        decimal? minSalary = null,
        string? currency = null,
        IReadOnlyList<string>? authorized = null,
        bool needsSponsorship = true,
        int threshold = 70) => new(
        Guid.Parse("01a10000-0000-7000-8000-000000000001"),
        (skills ?? []).Select((s, i) => new ProfileSkillInput(Guid.Parse($"01a10000-0000-7000-8000-{i + 100:D12}"), s)).ToList(),
        roles ?? [],
        experiences ?? [],
        educations ?? [],
        remote,
        locations ?? [],
        jobTypes ?? [],
        minSalary,
        currency,
        authorized ?? [],
        needsSponsorship,
        threshold);

    private static ExperienceInput Experience(string start, string? end) =>
        new(Guid.NewGuid(), "Engineer", "Acme", DateOnly.Parse(start), end is null ? null : DateOnly.Parse(end));

    private static JobMatchInput Job(JobRequirementsV1? requirements, string title = "Backend Engineer", string? location = null, string? remoteType = null, bool failed = false, string description = Description) =>
        new(Guid.NewGuid(), title, location, remoteType, description, requirements, failed);

    private static SkillRequirement Skill(string name, string quote, SkillImportance importance = SkillImportance.Required) =>
        new() { Name = name, Importance = importance, Quote = quote };

    private const string CSharpQuote = "You have 5+ years of experience with C# and PostgreSQL";
    private const string KubernetesQuote = "Kubernetes is nice to have";

    private static MatchingSettings Settings() => new();

    private static DimensionResult Dimension(MatchResult result, MatchDimension name) =>
        result.Explanation.Dimensions.Single(d => d.Name == name);

    // ---------- skill names ----------

    [Theory]
    [InlineData("C#", "c#")]
    [InlineData("C++", "c++")]
    [InlineData("C", "c")]
    [InlineData(".NET", ".net")]
    [InlineData(".NET 8", ".net")]
    [InlineData("Java 17", "java")]
    [InlineData("python3", "python3")]
    [InlineData("es6", "es6")]
    [InlineData("  Machine_Learning  ", "machine learning")]
    [InlineData("node-js", "node js")]
    [InlineData("Spring   Boot 3.2", "spring boot")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Skill_names_normalize_keeping_language_symbols_distinct(string? raw, string expected) =>
        Assert.Equal(expected, SkillName.Normalize(raw));

    [Fact]
    public void Skill_aliases_map_both_sides_normalized()
    {
        var aliases = SkillName.NormalizeAliases(new Dictionary<string, string> { ["C-Sharp"] = "C#", ["JS"] = "JavaScript" });

        Assert.Equal("c#", SkillName.Normalize("c sharp", aliases));
        Assert.Equal("javascript", SkillName.Normalize("js", aliases));
        Assert.Equal("python", SkillName.Normalize("Python", aliases));
    }

    // ---------- quote verification (AC-6) ----------

    [Fact]
    public void A_quote_verifies_after_whitespace_and_case_normalization()
    {
        var verifier = new QuoteVerifier("Line one.\n\n  You   HAVE 5+ years\tof experience.");

        Assert.True(verifier.IsVerified("you have 5+ years of experience"));
    }

    [Theory]
    [InlineData("short one")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("This sentence is not in the posting at all")]
    public void A_short_missing_or_absent_quote_does_not_verify(string? quote) =>
        Assert.False(new QuoteVerifier(Description).IsVerified(quote));

    // ---------- skills, renormalization, confidence, rounding (AC-2, AC-3) ----------

    [Fact]
    public void Dimensions_always_list_all_eight_in_fixed_order()
    {
        var result = MatchScorer.Score(Job(new JobRequirementsV1()), Profile(), Settings(), Today);

        Assert.Equal(Enum.GetValues<MatchDimension>(), result.Explanation.Dimensions.Select(d => d.Name));
    }

    // covers: AC-3
    [Fact]
    public void Only_skills_known_renormalizes_to_the_skill_ratio_with_low_confidence()
    {
        var job = Job(new JobRequirementsV1 { Skills = [Skill("C#", CSharpQuote), Skill("Kubernetes", KubernetesQuote, SkillImportance.Preferred)] });
        var profile = Profile(skills: ["C#"]);

        var result = MatchScorer.Score(job, profile, Settings(), Today);

        // C# (Required, 2) of C# + Kubernetes (Preferred, 1): 2/3 of the skills weight, the only known one.
        Assert.Equal(67, result.Score);
        Assert.Equal(MatchConfidence.Low, result.Confidence);
        Assert.Equal(DimensionStatus.Partial, Dimension(result, MatchDimension.Skills).Status);
        Assert.Equal(7, result.Explanation.Unknown.Count);
    }

    // covers: AC-3
    [Fact]
    public void The_final_score_rounds_half_away_from_zero()
    {
        // Three Required skills (2 each) and two Preferred (1 each); only one Preferred is met.
        var job = Job(new JobRequirementsV1
        {
            Skills =
            [
                Skill("C#", CSharpQuote),
                Skill("PostgreSQL", CSharpQuote),
                Skill("Kafka", "You have 5+ years of experience with C# and PostgreSQL"),
                Skill("Kubernetes", KubernetesQuote, SkillImportance.Preferred),
                Skill("Redis", KubernetesQuote, SkillImportance.Preferred),
            ],
        });

        var result = MatchScorer.Score(job, Profile(skills: ["Kubernetes"]), Settings(), Today);

        // matched 1 of 2+2+2+1+1 = 8 → 12.5 → 13
        Assert.Equal(13, result.Score);
    }

    // covers: AC-3
    [Theory]
    [InlineData(50, 0, MatchConfidence.Medium)] // known 0.5 exactly
    [InlineData(80, 0, MatchConfidence.High)]   // known 0.8 exactly
    [InlineData(49, 1, MatchConfidence.Low)]
    [InlineData(79, 1, MatchConfidence.Medium)]
    public void Confidence_bands_turn_at_half_and_four_fifths_of_the_weight(int knownSkillsWeight, int unknownTitleWeight, MatchConfidence expected)
    {
        var settings = new MatchingSettings
        {
            Weights = new MatchWeights
            {
                Skills = knownSkillsWeight,
                Title = unknownTitleWeight,
                Experience = 100 - knownSkillsWeight - unknownTitleWeight,
                Location = 0,
                Salary = 0,
                Education = 0,
                JobType = 0,
                WorkAuthorization = 0,
            },
        };
        var job = Job(new JobRequirementsV1 { Skills = [Skill("C#", CSharpQuote)] });

        var result = MatchScorer.Score(job, Profile(skills: ["C#"]), settings, Today);

        Assert.Equal(expected, result.Confidence);
    }

    // covers: AC-3
    [Fact]
    public void Nothing_known_leaves_the_score_null_with_low_confidence()
    {
        var result = MatchScorer.Score(Job(new JobRequirementsV1()), Profile(), Settings(), Today);

        Assert.Null(result.Score);
        Assert.Equal(MatchConfidence.Low, result.Confidence);
        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Skills, Reason: UnknownReason.JobSilent });
        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Title, Reason: UnknownReason.ProfileNotSet });
    }

    [Fact]
    public void Skills_are_unknown_when_the_profile_lists_none()
    {
        var result = MatchScorer.Score(Job(new JobRequirementsV1 { Skills = [Skill("C#", CSharpQuote)] }), Profile(), Settings(), Today);

        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Skills, Reason: UnknownReason.ProfileNotSet });
    }

    [Fact]
    public void A_skill_listed_twice_counts_once_as_required()
    {
        var job = Job(new JobRequirementsV1 { Skills = [Skill("c#", KubernetesQuote, SkillImportance.Preferred), Skill("C#", CSharpQuote)] });

        var result = MatchScorer.Score(job, Profile(skills: ["C#"]), Settings(), Today);

        var item = Assert.Single(Dimension(result, MatchDimension.Skills).Items);
        Assert.Equal("C# (required)", item.Label);
    }

    // covers: AC-2
    [Fact]
    public void A_met_skill_points_at_the_profile_skill_and_quotes_the_posting()
    {
        var profile = Profile(skills: ["csharp"]);
        var settings = new MatchingSettings { SkillAliases = new Dictionary<string, string> { ["csharp"] = "c#" } };

        var result = MatchScorer.Score(Job(new JobRequirementsV1 { Skills = [Skill("C#", CSharpQuote)] }), profile, settings, Today);

        var item = Assert.Single(Dimension(result, MatchDimension.Skills).Items);
        Assert.Equal(DimensionStatus.Met, item.Status);
        Assert.Equal(CSharpQuote, item.JobQuote);
        Assert.True(item.Verified);
        Assert.Equal(ProfileRefKind.Skill, item.ProfileRef!.Kind);
        Assert.Equal(profile.Skills[0].Id, item.ProfileRef.Id);
    }

    // ---------- missing requirements (AC-4) ----------

    // covers: AC-4
    [Fact]
    public void Unmet_required_skills_years_and_degree_are_missing_with_their_quotes()
    {
        var job = Job(new JobRequirementsV1
        {
            Skills = [Skill("C#", CSharpQuote), Skill("Kubernetes", KubernetesQuote, SkillImportance.Preferred)],
            MinYears = new YearsRequirement { Value = 5, Quote = CSharpQuote },
            Degree = new DegreeRequirement { Level = DegreeLevel.Master, Quote = "A Bachelor degree in Computer Science or equivalent experience is required" },
        });
        var profile = Profile(skills: ["Go"], experiences: [Experience("2024-01-01", "2025-01-01")], educations: [new EducationInput(Guid.NewGuid(), "BS", "CS", DegreeLevel.Bachelor)]);

        var result = MatchScorer.Score(job, profile, Settings(), Today);

        Assert.Equal(["C# (required)", "5+ years of experience", "Master degree"], result.Explanation.Missing.Select(m => m.Label));
        Assert.All(result.Explanation.Missing, m => Assert.False(string.IsNullOrEmpty(m.JobQuote)));
        Assert.DoesNotContain(result.Explanation.Missing, m => m.Label.StartsWith("Kubernetes", StringComparison.Ordinal));
    }

    // ---------- unverified quotes (AC-6) ----------

    // covers: AC-6
    [Fact]
    public void A_hallucinated_quote_is_unverified_and_scores_nothing()
    {
        var job = Job(new JobRequirementsV1
        {
            Skills = [Skill("C#", CSharpQuote), Skill("COBOL", "Deep COBOL mainframe expertise is essential")],
        });

        var result = MatchScorer.Score(job, Profile(skills: ["C#"]), Settings(), Today);

        var unverified = Assert.Single(result.Explanation.Unverified);
        Assert.Equal(MatchDimension.Skills, unverified.Dimension);
        Assert.Equal("COBOL", unverified.Label);
        Assert.Equal(DimensionStatus.Met, Dimension(result, MatchDimension.Skills).Status);
        Assert.Equal(100, result.Score);
        Assert.Empty(result.Explanation.Missing);
    }

    [Fact]
    public void An_unverified_years_quote_leaves_experience_unknown()
    {
        var job = Job(new JobRequirementsV1 { MinYears = new YearsRequirement { Value = 3, Quote = "3 years" } });

        var result = MatchScorer.Score(job, Profile(experiences: [Experience("2020-01-01", null)]), Settings(), Today);

        Assert.Equal(DimensionStatus.Unknown, Dimension(result, MatchDimension.Experience).Status);
        Assert.Contains(result.Explanation.Unverified, u => u.Dimension == MatchDimension.Experience);
    }

    // ---------- title (AC-2) ----------

    // covers: AC-2
    [Theory]
    [InlineData("Senior Backend Engineer", DimensionStatus.Met)]
    [InlineData("Sr. Backend Engineer - Payments", DimensionStatus.Met)]
    [InlineData("Backend Developer", DimensionStatus.Partial)]
    [InlineData("Data Analyst", DimensionStatus.Missed)]
    public void Title_compares_target_roles_without_seniority_words(string title, DimensionStatus expected)
    {
        var result = MatchScorer.Score(Job(new JobRequirementsV1(), title: title), Profile(roles: ["Backend Engineer"]), Settings(), Today);

        var dimension = Dimension(result, MatchDimension.Title);
        Assert.Equal(expected, dimension.Status);
        Assert.Equal(title, dimension.Items[0].JobQuote);
        Assert.Equal("Backend Engineer", dimension.Items[0].ProfileRef!.Label);
    }

    [Fact]
    public void Title_is_unknown_with_no_target_roles()
    {
        var result = MatchScorer.Score(Job(new JobRequirementsV1()), Profile(), Settings(), Today);

        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Title, Reason: UnknownReason.ProfileNotSet });
    }

    // ---------- experience ----------

    [Fact]
    public void Years_merge_overlaps_and_run_open_ended_rows_to_today()
    {
        var years = MatchScorer.YearsOfExperience(
            [Experience("2020-01-01", "2022-01-01"), Experience("2021-01-01", "2023-01-01"), Experience("2025-10-03", null)],
            Today);

        // 2020-01-01..2023-01-01 is 3 years, plus one open year to today.
        Assert.Equal(4.0m, years);
    }

    [Fact]
    public void Too_few_years_is_partial_by_their_share()
    {
        var job = Job(new JobRequirementsV1 { MinYears = new YearsRequirement { Value = 4, Quote = CSharpQuote } });

        var result = MatchScorer.Score(job, Profile(experiences: [Experience("2024-10-03", "2026-10-03")]), Settings(), Today);

        var dimension = Dimension(result, MatchDimension.Experience);
        Assert.Equal(DimensionStatus.Partial, dimension.Status);
        Assert.Equal(7.5m, dimension.Earned); // 2.0 of 4 years of 15
    }

    // ---------- location and remote, blockers (AC-5) ----------

    [Theory]
    [InlineData(RemotePreference.Remote, DimensionStatus.Met, 15)]
    [InlineData(RemotePreference.Onsite, DimensionStatus.Partial, 7.5)]
    public void A_remote_job_is_met_for_remote_and_half_for_onsite(RemotePreference preference, DimensionStatus status, decimal earned)
    {
        var job = Job(new JobRequirementsV1 { Remote = new RemoteRequirement { Type = WorkArrangement.Remote, Quote = "This role is remote" } });

        var result = MatchScorer.Score(job, Profile(remote: preference, locations: [new PreferredLocation { Country = "DE" }]), Settings(), Today);

        var dimension = Dimension(result, MatchDimension.Location);
        Assert.Equal(status, dimension.Status);
        Assert.Equal(earned, dimension.Earned);
    }

    [Fact]
    public void Location_is_unknown_for_any_preference_with_no_places()
    {
        var job = Job(new JobRequirementsV1 { Remote = new RemoteRequirement { Type = WorkArrangement.Remote, Quote = "This role is remote" } });

        var result = MatchScorer.Score(job, Profile(), Settings(), Today);

        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Location, Reason: UnknownReason.ProfileNotSet });
    }

    [Fact]
    public void An_onsite_job_in_a_preferred_country_is_met()
    {
        var job = OnsiteBerlinJob();

        var result = MatchScorer.Score(job, Profile(remote: RemotePreference.Onsite, locations: [new PreferredLocation { Country = "DE" }]), Settings(), Today);

        Assert.Equal(DimensionStatus.Met, Dimension(result, MatchDimension.Location).Status);
    }

    [Fact]
    public void A_city_preference_needs_the_same_city()
    {
        var result = MatchScorer.Score(OnsiteBerlinJob(), Profile(remote: RemotePreference.Onsite, locations: [new PreferredLocation { City = "Munich", Country = "DE" }]), Settings(), Today);

        Assert.Equal(DimensionStatus.Missed, Dimension(result, MatchDimension.Location).Status);
        Assert.False(result.HasBlocker);
    }

    // covers: AC-5
    [Fact]
    public void An_onsite_job_outside_every_place_blocks_a_remote_only_profile()
    {
        var result = MatchScorer.Score(OnsiteBerlinJob(), Profile(remote: RemotePreference.Remote, locations: [new PreferredLocation { Country = "PK" }]), Settings(), Today);

        Assert.True(result.HasBlocker);
        Assert.Equal(DimensionStatus.Blocker, Dimension(result, MatchDimension.Location).Status);
        var blocker = Assert.Single(result.Explanation.Blockers);
        Assert.Equal(MatchDimension.Location, blocker.Dimension);
        Assert.Equal("The office is in Berlin, Germany", blocker.JobQuote);
    }

    // covers: AC-5
    [Fact]
    public void Refused_sponsorship_caps_a_strong_fit_at_the_blocker_cap()
    {
        var job = Job(new JobRequirementsV1
        {
            Skills = [Skill("C#", CSharpQuote)],
            Locations = [new LocationRequirement { City = "Berlin", Country = "DE", Quote = "The office is in Berlin, Germany" }],
            Sponsorship = new SponsorshipRequirement { Offered = false, Quote = "We do not offer visa sponsorship" },
        });
        var profile = Profile(skills: ["C#"], roles: ["Backend Engineer"], remote: RemotePreference.Onsite, locations: [new PreferredLocation { Country = "DE" }], authorized: ["PK"]);

        var result = MatchScorer.Score(job, profile, Settings(), Today);

        Assert.True(result.HasBlocker);
        Assert.Equal(20, result.Score);
        var blocker = Assert.Single(result.Explanation.Blockers);
        Assert.Equal(MatchDimension.WorkAuthorization, blocker.Dimension);
        Assert.Equal(0, Dimension(result, MatchDimension.WorkAuthorization).Earned);
    }

    [Fact]
    public void A_lower_cap_from_config_applies()
    {
        var job = Job(new JobRequirementsV1
        {
            Skills = [Skill("C#", CSharpQuote)],
            Locations = [new LocationRequirement { Country = "DE", Quote = "The office is in Berlin, Germany" }],
            Sponsorship = new SponsorshipRequirement { Offered = false, Quote = "We do not offer visa sponsorship" },
        });

        var result = MatchScorer.Score(job, Profile(skills: ["C#"]), new MatchingSettings { BlockerCap = 5 }, Today);

        Assert.Equal(5, result.Score);
    }

    [Fact]
    public void An_authorized_country_is_never_blocked_by_a_no_sponsorship_line()
    {
        var job = Job(new JobRequirementsV1
        {
            Locations = [new LocationRequirement { Country = "Germany", Quote = "The office is in Berlin, Germany" }],
            Sponsorship = new SponsorshipRequirement { Offered = false, Quote = "We do not offer visa sponsorship" },
        });

        var result = MatchScorer.Score(job, Profile(authorized: ["DE"]), Settings(), Today);

        Assert.False(result.HasBlocker);
        Assert.Equal(DimensionStatus.Met, Dimension(result, MatchDimension.WorkAuthorization).Status);
    }

    [Theory]
    [InlineData(false, true, DimensionStatus.Met)]   // no sponsorship needed
    [InlineData(true, true, DimensionStatus.Met)]    // sponsorship offered
    [InlineData(true, null, DimensionStatus.Unknown)] // posting silent
    public void Work_authorization_follows_the_spec_order(bool needsSponsorship, bool? offered, DimensionStatus expected)
    {
        var job = Job(new JobRequirementsV1
        {
            Locations = [new LocationRequirement { Country = "DE", Quote = "The office is in Berlin, Germany" }],
            Sponsorship = offered is null ? null : new SponsorshipRequirement { Offered = offered.Value, Quote = "We do not offer visa sponsorship" },
        });

        var result = MatchScorer.Score(job, Profile(needsSponsorship: needsSponsorship), Settings(), Today);

        Assert.Equal(expected, Dimension(result, MatchDimension.WorkAuthorization).Status);
    }

    // ---------- salary, education, job type ----------

    [Theory]
    [InlineData(150000, "USD", SalaryPeriod.Year, DimensionStatus.Met)]
    [InlineData(170000, "USD", SalaryPeriod.Year, DimensionStatus.Missed)]
    [InlineData(100000, "EUR", SalaryPeriod.Year, DimensionStatus.Unknown)]
    [InlineData(100000, "USD", SalaryPeriod.Hour, DimensionStatus.Unknown)]
    public void Salary_compares_only_same_currency_yearly_amounts(decimal minimum, string currency, SalaryPeriod period, DimensionStatus expected)
    {
        var job = Job(new JobRequirementsV1
        {
            Salary = new SalaryRequirement { Min = 120000, Max = 160000, Currency = currency, Period = period, Quote = "The salary range is $120,000 - $160,000 per year" },
        });

        var result = MatchScorer.Score(job, Profile(minSalary: minimum, currency: "USD"), Settings(), Today);

        Assert.Equal(expected, Dimension(result, MatchDimension.Salary).Status);
    }

    [Fact]
    public void Education_accepts_equivalent_experience_when_the_years_are_met()
    {
        var job = Job(new JobRequirementsV1
        {
            MinYears = new YearsRequirement { Value = 5, Quote = CSharpQuote },
            Degree = new DegreeRequirement { Level = DegreeLevel.Bachelor, OrEquivalentExperience = true, Quote = "A Bachelor degree in Computer Science or equivalent experience is required" },
        });
        var profile = Profile(experiences: [Experience("2015-01-01", null)], educations: [new EducationInput(Guid.NewGuid(), "Diploma", "IT", DegreeLevel.Associate)]);

        var result = MatchScorer.Score(job, profile, Settings(), Today);

        Assert.Equal(DimensionStatus.Met, Dimension(result, MatchDimension.Education).Status);
    }

    [Fact]
    public void Education_is_unknown_when_every_row_is_level_none()
    {
        var job = Job(new JobRequirementsV1 { Degree = new DegreeRequirement { Level = DegreeLevel.Bachelor, Quote = "A Bachelor degree in Computer Science or equivalent experience is required" } });

        var result = MatchScorer.Score(job, Profile(educations: [new EducationInput(Guid.NewGuid(), "Course", "IT", DegreeLevel.None)]), Settings(), Today);

        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Education, Reason: UnknownReason.ProfileNotSet });
    }

    [Theory]
    [InlineData(JobType.FullTime, DimensionStatus.Met)]
    [InlineData(JobType.Contract, DimensionStatus.Missed)]
    public void Job_type_is_met_when_listed(JobType accepted, DimensionStatus expected)
    {
        var job = Job(new JobRequirementsV1 { JobType = new JobTypeRequirement { Value = JobType.FullTime, Quote = "This is a full time position" } });

        var result = MatchScorer.Score(job, Profile(jobTypes: [accepted]), Settings(), Today);

        Assert.Equal(expected, Dimension(result, MatchDimension.JobType).Status);
    }

    // ---------- the fallback when extraction failed (AC-8) ----------

    // covers: AC-8
    [Fact]
    public void A_failed_extraction_scores_only_the_job_columns_with_low_confidence()
    {
        var requirements = new JobRequirementsV1 { Skills = [Skill("C#", CSharpQuote)] };
        var job = Job(requirements, location: "Berlin, Germany", failed: true);
        var profile = Profile(skills: ["C#"], roles: ["Backend Engineer"], remote: RemotePreference.Onsite, locations: [new PreferredLocation { Country = "DE" }], minSalary: 1, currency: "USD");

        var result = MatchScorer.Score(job, profile, Settings(), Today);

        Assert.True(result.Explanation.ExtractionFailed);
        Assert.Equal(MatchConfidence.Low, result.Confidence);
        Assert.Equal([MatchDimension.Title, MatchDimension.Location], result.Explanation.Dimensions.Where(d => d.Status != DimensionStatus.Unknown).Select(d => d.Name));
        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Skills, Reason: UnknownReason.ExtractionFailed });
        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Salary, Reason: UnknownReason.ExtractionFailed });
        var location = Dimension(result, MatchDimension.Location).Items.Single();
        Assert.Null(location.JobQuote);
        Assert.True(location.Verified);
    }

    // covers: AC-8
    [Fact]
    public void An_unparseable_fallback_location_is_unknown()
    {
        var result = MatchScorer.Score(Job(null, location: "San Francisco, CA", failed: true), Profile(remote: RemotePreference.Onsite, locations: [new PreferredLocation { Country = "US" }]), Settings(), Today);

        Assert.Contains(result.Explanation.Unknown, u => u is { Dimension: MatchDimension.Location, Reason: UnknownReason.Unparseable });
    }

    [Fact]
    public void The_fallback_reads_a_remote_type_column()
    {
        var result = MatchScorer.Score(Job(null, remoteType: RemoteTypes.Remote, failed: true), Profile(remote: RemotePreference.Remote), Settings(), Today);

        Assert.Equal(DimensionStatus.Met, Dimension(result, MatchDimension.Location).Status);
    }

    // ---------- fingerprint (AC-7) ----------

    // covers: AC-7
    [Fact]
    public void The_fingerprint_is_stable_and_changes_with_every_input()
    {
        var job = Job(new JobRequirementsV1(), location: "Berlin, Germany");
        var profile = Profile(skills: ["C#"]);
        var stamp = new RequirementsStamp("hash", 1, RequirementsStatus.Extracted);
        string Print(RequirementsStamp s, JobMatchInput j, MatchProfileInput p, MatchingSettings m, int day = 3) =>
            MatchFingerprint.Compute(s, j, p.Fingerprint(), m, p.HasOpenEndedExperience, new DateOnly(2026, 10, day));

        var baseline = Print(stamp, job, profile, Settings());

        Assert.Equal(baseline, Print(stamp, job, Profile(skills: ["C#"]), Settings()));
        Assert.NotEqual(baseline, Print(stamp with { ContentHash = "other" }, job, profile, Settings()));
        Assert.NotEqual(baseline, Print(stamp with { ExtractorVersion = 2 }, job, profile, Settings()));
        Assert.NotEqual(baseline, Print(stamp with { Status = RequirementsStatus.Failed }, job, profile, Settings()));
        Assert.NotEqual(baseline, Print(stamp, job with { Location = "Paris, France" }, profile, Settings()));
        Assert.NotEqual(baseline, Print(stamp, job with { RemoteType = "Remote" }, profile, Settings()));
        Assert.NotEqual(baseline, Print(stamp, job, Profile(skills: ["C#"], threshold: 71), Settings()));
        Assert.NotEqual(baseline, Print(stamp, job, profile, new MatchingSettings { BlockerCap = 19 }));
        Assert.NotEqual(baseline, Print(stamp, job, profile, new MatchingSettings { SkillAliases = new() { ["js"] = "javascript" } }));
    }

    [Fact]
    public void Today_enters_the_fingerprint_only_with_an_open_ended_experience()
    {
        var job = Job(new JobRequirementsV1());
        var stamp = new RequirementsStamp("hash", 1, RequirementsStatus.Extracted);
        var closed = Profile(experiences: [Experience("2020-01-01", "2021-01-01")]);
        var open = Profile(experiences: [Experience("2020-01-01", null)]);
        string Print(MatchProfileInput p, int day) =>
            MatchFingerprint.Compute(stamp, job, p.Fingerprint(), Settings(), p.HasOpenEndedExperience, new DateOnly(2026, 10, day));

        Assert.Equal(Print(closed, 3), Print(closed, 4));
        Assert.NotEqual(Print(open, 3), Print(open, 4));
    }

    // ---------- settings (AC-16) ----------

    // covers: AC-16
    [Fact]
    public void The_default_settings_are_valid()
    {
        Assert.Empty(new MatchingSettings().Validate());
    }

    // covers: AC-16
    [Fact]
    public void Weights_summing_to_99_and_other_bad_values_are_rejected()
    {
        var settings = new MatchingSettings
        {
            Weights = new MatchWeights { Skills = 29 },
            BlockerCap = 101,
            Confidence = new ConfidenceThresholds { High = 0.5m, Medium = 0.5m },
            MaxDescriptionChars = 0,
        };

        var errors = settings.Validate();

        Assert.Contains(errors, e => e.Contains("sum to 100, not 99", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("BlockerCap", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Confidence", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("MaxDescriptionChars", StringComparison.Ordinal));
    }

    [Fact]
    public void A_negative_weight_is_rejected_even_when_the_sum_is_100()
    {
        var errors = new MatchingSettings { Weights = new MatchWeights { Skills = 40, Title = 5, Experience = -5, Location = 30 } }.Validate();

        Assert.Contains(errors, e => e.Contains("negative", StringComparison.Ordinal));
    }

    // covers: AC-12
    [Fact]
    public void The_description_is_cut_at_the_configured_length()
    {
        var settings = new MatchingSettings { MaxDescriptionChars = 10 };

        Assert.Equal("0123456789", settings.Truncate("0123456789abcdef"));
        Assert.Equal("short", settings.Truncate("short"));
        Assert.Equal(string.Empty, settings.Truncate(null));
    }

    // ---------- requirements document bounds (AC-12) ----------

    // covers: AC-12
    [Fact]
    public void Sixty_one_skills_break_the_bounds()
    {
        var requirements = new JobRequirementsV1 { Skills = Enumerable.Range(0, 61).Select(i => Skill($"skill{i}", CSharpQuote)).ToList() };

        Assert.Contains("61 skills", requirements.FindBoundsError());
    }

    // covers: AC-12
    [Fact]
    public void A_301_character_quote_breaks_the_bounds_but_a_short_one_does_not()
    {
        Assert.NotNull(new JobRequirementsV1 { Skills = [Skill("C#", new string('a', 301))] }.FindBoundsError());
        Assert.Null(new JobRequirementsV1 { Skills = [Skill("C#", "short")] }.FindBoundsError());
        Assert.Null(new JobRequirementsV1 { Skills = [Skill("C#", new string('a', 300))] }.FindBoundsError());
    }

    [Fact]
    public void A_degree_of_level_none_or_a_missing_quote_breaks_the_bounds()
    {
        Assert.NotNull(new JobRequirementsV1 { Degree = new DegreeRequirement { Level = DegreeLevel.None, Quote = CSharpQuote } }.FindBoundsError());
        Assert.NotNull(new JobRequirementsV1 { Skills = [new SkillRequirement { Name = "C#", Quote = null! }] }.FindBoundsError());
        Assert.NotNull(new JobRequirementsV1 { V = 2 }.FindBoundsError());
    }

    // ---------- requirements row lifecycle (AC-8, AC-10) ----------

    // covers: AC-8
    [Fact]
    public void The_third_failed_attempt_marks_the_row_failed()
    {
        var row = JobRequirement.Start(Guid.NewGuid(), "hash", DateTimeOffset.UnixEpoch);

        Assert.False(row.RecordFailure("one"));
        Assert.False(row.RecordFailure("two"));
        Assert.True(row.RecordFailure("three"));

        Assert.Equal(RequirementsStatus.Failed, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal("three", row.FailureReason);
    }

    [Fact]
    public void Going_pending_again_keeps_the_count_for_the_same_content_and_resets_it_otherwise()
    {
        var row = JobRequirement.Start(Guid.NewGuid(), "hash", DateTimeOffset.UnixEpoch);
        row.RecordFailure("one");

        row.MarkPending("hash", DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.Equal(1, row.Attempts);

        row.MarkPending("new-hash", DateTimeOffset.UnixEpoch.AddHours(2));
        Assert.Equal(0, row.Attempts);
        Assert.Equal("new-hash", row.ContentHash);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(2), row.PendingSince);
    }

    // covers: AC-10
    [Fact]
    public void A_rescore_moves_an_extracted_row_back_to_pending_and_clears_it()
    {
        var row = JobRequirement.Start(Guid.NewGuid(), "hash", DateTimeOffset.UnixEpoch);
        row.MarkExtracted("{}", "fake", DateTimeOffset.UnixEpoch);
        Assert.True(row.IsCurrentFor("hash"));

        row.MarkPending("hash", DateTimeOffset.UnixEpoch.AddDays(1));

        Assert.Equal(RequirementsStatus.Pending, row.Status);
        Assert.Null(row.Requirements);
        Assert.False(row.IsCurrentFor("hash"));
    }

    [Fact]
    public void Only_a_pending_row_can_be_extracted_or_fail()
    {
        var row = JobRequirement.Start(Guid.NewGuid(), "hash", DateTimeOffset.UnixEpoch);
        row.MarkExtracted("{}", "fake", DateTimeOffset.UnixEpoch);

        Assert.Throws<InvalidOperationException>(() => row.MarkExtracted("{}", "fake", DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => row.RecordFailure("x"));
    }

    // ---------- match profile rules (AC-11) ----------

    private static MatchPreferencesDraft Preferences(
        IReadOnlyList<string>? roles = null,
        string? currency = null,
        decimal? minSalary = null,
        IReadOnlyList<string>? countries = null,
        IReadOnlyList<PreferredLocation>? locations = null,
        int threshold = 70) =>
        new(roles ?? [], RemotePreference.Any, locations ?? [], [], minSalary, currency, countries ?? [], true, threshold);

    // covers: AC-11
    [Fact]
    public void A_valid_draft_has_no_errors()
    {
        var draft = new MatchProfileDraft(
            Preferences(roles: ["Backend Engineer"], currency: "eur", minSalary: 50000, countries: ["de"], locations: [new PreferredLocation { City = "Berlin", Country = "de" }]),
            ["C#"],
            [new ExperienceDraft(null, "Acme", "Engineer", new DateOnly(2020, 1, 1), null, null)],
            [new EducationDraft(null, "Uni", "BS", "CS", DegreeLevel.Bachelor, new DateOnly(2014, 1, 1), new DateOnly(2018, 1, 1))]);

        Assert.Empty(MatchProfileRules.Validate(draft));
    }

    // covers: AC-11
    [Fact]
    public void Each_broken_rule_names_its_field()
    {
        var draft = new MatchProfileDraft(
            Preferences(roles: [.. Enumerable.Repeat("Role", 21), ""], currency: "XXQ", minSalary: -1, countries: ["ZZ"], threshold: 101),
            [new string('x', 101)],
            [new ExperienceDraft(null, "", "Engineer", new DateOnly(2020, 5, 1), new DateOnly(2020, 1, 1), null)],
            [new EducationDraft(null, "Uni", "BS", "CS", DegreeLevel.Bachelor, new DateOnly(2018, 1, 1), new DateOnly(2014, 1, 1))]);

        var errors = MatchProfileRules.Validate(draft);

        Assert.Contains("targetRoles", errors.Keys);
        Assert.Contains("targetRoles[21]", errors.Keys);
        Assert.Contains("salaryCurrency", errors.Keys);
        Assert.Contains("minSalary", errors.Keys);
        Assert.Contains("authorizedCountries[0]", errors.Keys);
        Assert.Contains("strongMatchThreshold", errors.Keys);
        Assert.Contains("skills[0]", errors.Keys);
        Assert.Contains("experiences[0].company", errors.Keys);
        Assert.Contains("experiences[0].endDate", errors.Keys);
        Assert.Contains("educations[0].endDate", errors.Keys);
    }

    [Theory]
    [InlineData(1000, null, "salaryCurrency")]
    [InlineData(null, "USD", "minSalary")]
    public void Salary_and_currency_go_together(int? minSalary, string? currency, string field)
    {
        var errors = MatchProfileRules.Validate(new MatchProfileDraft(Preferences(minSalary: minSalary, currency: currency), [], [], []));

        Assert.Contains(field, errors.Keys);
    }

    [Fact]
    public void Apply_trims_upper_cases_and_drops_duplicates()
    {
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Founder" };
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        MatchProfileRules.Apply(
            profile,
            new MatchPreferencesDraft(
                [" Backend Engineer ", "backend engineer"],
                RemotePreference.Remote,
                [new PreferredLocation { City = " Berlin ", Country = "de" }, new PreferredLocation { City = "berlin", Country = "DE" }],
                [JobType.Contract, JobType.FullTime, JobType.FullTime],
                100,
                " usd ",
                ["pk", "PK", "ae"],
                false,
                55),
            now);

        Assert.Equal(["Backend Engineer"], profile.TargetRoles);
        Assert.Single(profile.PreferredLocations);
        Assert.Equal("DE", profile.PreferredLocations[0].Country);
        Assert.Equal([JobType.FullTime, JobType.Contract], profile.JobTypes);
        Assert.Equal("USD", profile.SalaryCurrency);
        Assert.Equal(["AE", "PK"], profile.AuthorizedCountries);
        Assert.False(profile.NeedsSponsorshipElsewhere);
        Assert.Equal(55, profile.StrongMatchThreshold);
        Assert.Equal(now, profile.UpdatedAt);
    }

    [Fact]
    public void Apply_refuses_an_invalid_draft()
    {
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Founder" };

        Assert.Throws<InvalidOperationException>(() => MatchProfileRules.Apply(profile, Preferences(threshold: 500), DateTimeOffset.UnixEpoch));
    }

    // ---------- places and ISO codes ----------

    [Theory]
    [InlineData("Berlin, Germany", "berlin", "DE")]
    [InlineData("London, UK", "london", "GB")]
    [InlineData("New York, NY, USA", "new york", "US")]
    [InlineData("Germany", null, "DE")]
    [InlineData("Lahore, PK", "lahore", "PK")]
    public void Job_locations_parse_city_and_country(string text, string? city, string country)
    {
        var place = PlaceText.ParseJobLocation(text);

        Assert.NotNull(place);
        Assert.Equal(city, place.City);
        Assert.Equal(country, place.Country);
    }

    [Theory]
    [InlineData("San Francisco, CA")]
    [InlineData("Remote")]
    [InlineData("Anywhere, Earth")]
    [InlineData("")]
    [InlineData(null)]
    public void Ambiguous_or_unknown_locations_do_not_parse(string? text) =>
        Assert.Null(PlaceText.ParseJobLocation(text));

    [Fact]
    public void Iso_tables_know_common_codes_and_names()
    {
        Assert.True(IsoCodes.IsCountry("PK"));
        Assert.False(IsoCodes.IsCountry("ZZ"));
        Assert.True(IsoCodes.IsCurrency("EUR"));
        Assert.False(IsoCodes.IsCurrency("XXQ"));
        Assert.Equal("US", IsoCodes.FindCountry("United States"));
        Assert.Equal("NL", IsoCodes.FindCountry("the netherlands"));
        Assert.Null(IsoCodes.FindCountry("Atlantis"));
    }

    private static JobMatchInput OnsiteBerlinJob() => Job(new JobRequirementsV1
    {
        Remote = new RemoteRequirement { Type = WorkArrangement.Onsite, Quote = "The office is in Berlin, Germany" },
        Locations = [new LocationRequirement { City = "Berlin", Country = "DE", Quote = "The office is in Berlin, Germany" }],
    });
}
