using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkPilot.AI.Agent;
using WorkPilot.AI.Matching;
using WorkPilot.AI.Providers;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs.Matching;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Modules.Applications;
using WorkPilot.Infrastructure.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Jobs.Matching;
using WorkPilot.Infrastructure.Persistence;
using WorkPilot.Workers.Jobs;

namespace WorkPilot.Api.Tests;

// Job matching (spec 0019) against the real Postgres: events from ingestion, merge and split,
// extraction with the Fake and with failing or oversized answers, scoring with the raw SQL upsert
// and its fingerprint, JobMatched on a threshold crossing, the sweep, the match profile endpoints
// with their ETag, and the list and match panel endpoints. Every test seeds its own source, jobs
// and profiles and removes them afterwards. Needs WORKPILOTDB_CONNECTION.
[Collection("Api")]
public class JobMatchingTests(SharedApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    private const string MatchingDescription = "You need C# and PostgreSQL every day. You have 3+ years of experience.";

    private readonly string _company = $"Matchco {Guid.NewGuid():N}";
    private readonly List<Guid> _sources = [];
    private readonly List<Guid> _profiles = [];
    private readonly ScriptedSource _board = new("MatchBoard");
    private readonly RecordingScheduler _scheduler = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        var jobIds = await db.JobSourceLinks.Where(l => _sources.Contains(l.JobSourceId)).Select(l => l.JobId).Distinct().ToListAsync();
        foreach (var id in jobIds.Concat(_profiles))
        {
            await OutboxTestHelpers.DeleteEventsMentioningAsync(db, id);
        }

        await db.AuditLogs.IgnoreQueryFilters().Where(a => jobIds.Contains(a.TargetId) || _sources.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.JobMatches.Where(m => jobIds.Contains(m.JobId) || _profiles.Contains(m.ProfileId)).ExecuteDeleteAsync();
        await db.Jobs.IgnoreQueryFilters().Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync();
        await db.JobSources.Where(s => _sources.Contains(s.Id)).ExecuteDeleteAsync();
        await db.Profiles.IgnoreQueryFilters().Where(p => _profiles.Contains(p.Id)).ExecuteDeleteAsync();
    }

    // ---------- the thin thread: event, extraction, score, panel (AC-1, AC-2, AC-10, AC-13) ----------

    // covers: AC-1, AC-2, AC-10, AC-13
    [Fact]
    public async Task A_new_job_raises_content_changed_and_the_fake_extraction_scores_it_with_evidence()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#", "PostgreSQL"], roles: ["Backend Engineer"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);

        await using (var db = CreateDbContext())
        {
            var events = await OutboxTestHelpers.EventsMentioningAsync(db, jobId);
            Assert.Contains(events, e => e.EventName == "jobs.job-content-changed.v1");
        }

        Assert.Equal(ExtractionOutcome.Extracted, await ExtractAsync(jobId, FakeExtractor()));
        await ScoreAsync(jobId);

        await using (var db = CreateDbContext())
        {
            var row = await db.JobRequirements.SingleAsync(r => r.JobId == jobId);
            Assert.Equal(RequirementsStatus.Extracted, row.Status);
            Assert.Equal("fake", row.Model);
        }

        using var client = factory.CreateClient();
        var match = await client.GetFromJsonAsync<JobMatchDetailDto>($"/internal/jobs/{jobId}/match?profileId={profileId}");
        Assert.Equal(100, match!.Score);
        Assert.Equal("Extracted", match.RequirementsStatus);
        var skill = match.Explanation.Dimensions.Single(d => d.Name == "Skills").Items.First(i => i.Label.StartsWith("C#", StringComparison.Ordinal));
        Assert.Equal("You need C# and PostgreSQL every day", skill.JobQuote);
        Assert.Equal("Skill", skill.ProfileRef!.Kind);
        Assert.Equal("c#", skill.ProfileRef.Label);
        Assert.Equal($"https://matchboard.test/{jobId:N}"[..22], match.JobUrl[..22]);
    }

    // covers: AC-1 (review finding: a job joined in the run that created it)
    [Fact]
    public async Task Two_same_key_postings_in_one_run_raise_one_content_changed_for_their_new_job()
    {
        var sourceId = await SeedSourceAsync();

        await IngestAsync(sourceId, T0, Posting("1", "Platform Engineer", "Paris", MatchingDescription), Posting("2", "Platform Engineer", "Paris", MatchingDescription));

        await using var db = CreateDbContext();
        var jobId = Assert.Single(await JobIdsAsync(db));
        Assert.Equal(2, await db.JobSourceLinks.CountAsync(l => l.JobId == jobId));
        Assert.Single(await OutboxTestHelpers.EventsMentioningAsync(db, jobId), e => e.EventName == "jobs.job-content-changed.v1");
    }

    // covers: AC-1
    [Fact]
    public async Task Extraction_is_skipped_when_the_content_is_already_extracted()
    {
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        Assert.Equal(ExtractionOutcome.Extracted, await ExtractAsync(jobId, FakeExtractor()));

        var counting = new ScriptedExtractor(_ => throw new InvalidOperationException("must not be called"));

        Assert.Equal(ExtractionOutcome.AlreadyCurrent, await ExtractAsync(jobId, counting));
        Assert.Equal(0, counting.Calls);
    }

    [Fact]
    public async Task A_missing_job_is_a_quiet_no_op()
    {
        Assert.Equal(ExtractionOutcome.NoJob, await ExtractAsync(Guid.CreateVersion7(), FakeExtractor()));
    }

    // ---------- idempotency and concurrency (AC-7) ----------

    // covers: AC-7
    [Fact]
    public async Task Scoring_again_unchanged_writes_nothing_and_parallel_runs_leave_one_row()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#"], roles: ["Backend Engineer"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        await ExtractAsync(jobId, FakeExtractor());
        await ScoreAsync(jobId);
        var first = await MatchAsync(jobId, profileId);

        var again = await ScoreAsync(jobId);

        Assert.Equal(0, again.Written);
        Assert.Equal(first.RankedAt, (await MatchAsync(jobId, profileId)).RankedAt);

        await SaveProfileAsync(profileId, d => d with { StrongMatchThreshold = 71 });
        await Task.WhenAll(ScoreAsync(jobId), ScoreAsync(jobId));

        await using var db = CreateDbContext();
        var rows = await db.JobMatches.Where(m => m.JobId == jobId && m.ProfileId == profileId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(first.Id, row.Id);
        Assert.NotEqual(first.InputsFingerprint, row.InputsFingerprint);
    }

    // covers: AC-7
    [Fact]
    public async Task Rescoring_a_profile_rewrites_only_what_its_change_touched()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        await ExtractAsync(jobId, FakeExtractor());

        await RescoreProfileAsync(profileId);
        var unchanged = await RescoreProfileAsync(profileId);
        await SaveProfileAsync(profileId, d => d with { Skills = ["C#", "PostgreSQL"] });
        var changed = await RescoreProfileAsync(profileId);

        Assert.Equal(0, unchanged.Written);
        Assert.True(changed.Written >= 1);
        Assert.Equal(100, (await MatchAsync(jobId, profileId)).Score);
    }

    // ---------- extraction failure, fallback and recovery (AC-8, AC-10) ----------

    // covers: AC-8, AC-10
    [Fact]
    public async Task Three_failed_attempts_fall_back_to_the_job_columns_and_a_rescore_recovers()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#"], roles: ["Backend Engineer"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription, location: "Berlin, Germany");
        var failing = new ScriptedExtractor(_ => throw new InvalidOperationException("provider down"));

        Assert.Equal(ExtractionOutcome.RetryLater, await ExtractAsync(jobId, failing));
        Assert.Equal(ExtractionOutcome.RetryLater, await ExtractAsync(jobId, failing));
        Assert.Equal(ExtractionOutcome.Failed, await ExtractAsync(jobId, failing));
        await ScoreAsync(jobId);

        using var client = factory.CreateClient();
        var fallback = await client.GetFromJsonAsync<JobMatchDetailDto>($"/internal/jobs/{jobId}/match?profileId={profileId}");
        Assert.Equal("Failed", fallback!.RequirementsStatus);
        Assert.Contains("provider down", fallback.FailureReason);
        Assert.True(fallback.Explanation.ExtractionFailed);
        Assert.Equal("Low", fallback.Confidence);

        var queued = await client.PostAsJsonAsync($"/internal/jobs/{jobId}/match/rescore", new { profileId });
        var again = await client.PostAsJsonAsync($"/internal/jobs/{jobId}/match/rescore", new { profileId });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.True((await queued.Content.ReadFromJsonAsync<RescoreMatchResponse>())!.Queued);
        Assert.False((await again.Content.ReadFromJsonAsync<RescoreMatchResponse>())!.Queued);

        Assert.Equal(ExtractionOutcome.Extracted, await ExtractAsync(jobId, FakeExtractor(), force: true));
        await ScoreAsync(jobId);
        var recovered = await MatchDetailAsync(client, jobId, profileId);
        Assert.False(recovered.Explanation.ExtractionFailed);
        Assert.Equal("Extracted", recovered.RequirementsStatus);
    }

    // covers: AC-10, AC-15
    [Fact]
    public async Task Rescore_answers_404_for_an_unknown_job_or_profile()
    {
        var profileId = await SeedProfileAsync(Document());
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        using var client = factory.CreateClient();

        var unknownJob = await client.PostAsJsonAsync($"/internal/jobs/{Guid.CreateVersion7()}/match/rescore", new { profileId });
        var unknownProfile = await client.PostAsJsonAsync($"/internal/jobs/{jobId}/match/rescore", new { profileId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownProfile.StatusCode);
    }

    // ---------- what the model is sent and what it may answer (AC-12) ----------

    // covers: AC-12
    [Fact]
    public async Task The_model_gets_only_the_cut_description_with_no_tools_and_no_profile_data()
    {
        await SeedProfileAsync(Document(skills: ["Zyxwvut Secret Skill"], roles: ["Hidden Role Name"]));
        var description = "You need C# daily. " + new string('x', 30000);
        var jobId = await IngestOneAsync("Backend Engineer", description);
        var chat = new CapturingChatClient(_ => """{"v":1,"skills":[],"locations":[]}""");
        var extractor = new ChatClientJobRequirementExtractor(chat, new ResolvedAiPurpose(AiPurposes.JobExtraction, "Fake", null));

        Assert.Equal(ExtractionOutcome.Extracted, await ExtractAsync(jobId, extractor));

        var prompt = Assert.Single(chat.Prompts);
        Assert.StartsWith(ChatClientJobRequirementExtractor.Marker, prompt);
        Assert.Equal(new MatchingSettings().MaxDescriptionChars, ChatClientJobRequirementExtractor.DescriptionOf(prompt)!.Length);
        Assert.DoesNotContain("Zyxwvut", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Role Name", prompt, StringComparison.Ordinal);
        Assert.Null(chat.Options!.Tools);
        Assert.NotNull(chat.Options.ResponseFormat);
    }

    // covers: AC-12
    [Theory]
    [InlineData("tooManySkills", "61 skills")]
    [InlineData("longQuote", "longer than 300")]
    [InlineData("notJson", "not valid requirements JSON")]
    public async Task An_answer_outside_the_schema_counts_as_a_failed_attempt(string answer, string reason)
    {
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        var json = answer switch
        {
            "tooManySkills" => JsonSerializer.Serialize(new { v = 1, skills = Enumerable.Range(0, 61).Select(i => new { name = $"s{i}", importance = "Required", quote = "You need C# and PostgreSQL every day" }), locations = Array.Empty<object>() }),
            "longQuote" => JsonSerializer.Serialize(new { v = 1, skills = new[] { new { name = "C#", importance = "Required", quote = new string('q', 301) } }, locations = Array.Empty<object>() }),
            _ => "this is not json",
        };
        var extractor = new ChatClientJobRequirementExtractor(new CapturingChatClient(_ => json), new ResolvedAiPurpose(AiPurposes.JobExtraction, "Fake", null));

        Assert.Equal(ExtractionOutcome.RetryLater, await ExtractAsync(jobId, extractor));

        await using var db = CreateDbContext();
        var row = await db.JobRequirements.SingleAsync(r => r.JobId == jobId);
        Assert.Equal(1, row.Attempts);
        Assert.Contains(reason, row.FailureReason);
    }

    // ---------- JobMatched on a threshold crossing (AC-17) ----------

    // covers: AC-17
    [Theory]
    [InlineData(null, false, 75, false, true)]   // first score above the threshold
    [InlineData(60, false, 75, false, true)]     // crossing up
    [InlineData(80, false, 75, false, false)]    // already strong
    [InlineData(80, true, 75, false, true)]      // a blocker cleared
    [InlineData(60, false, 75, true, false)]     // blocked now
    [InlineData(60, false, 65, false, false)]    // still below
    [InlineData(70, false, 70, false, false)]    // already at the threshold
    public void The_threshold_rule_needs_a_new_unblocked_crossing(int? previous, bool previousBlocked, int score, bool blocked, bool expected)
    {
        var stored = previous is null && !previousBlocked ? null : new StoredMatch(previous, previousBlocked, "old");
        var result = new MatchResult(score, MatchConfidence.High, blocked, new MatchExplanation());

        Assert.Equal(expected, JobMatchingService.CrossesThreshold(stored, result, threshold: 70));
    }

    // covers: AC-17
    [Fact]
    public async Task A_crossing_publishes_job_matched_once_and_rescoring_while_strong_publishes_nothing()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#", "PostgreSQL"], roles: ["Backend Engineer"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        await ExtractAsync(jobId, FakeExtractor());

        await ScoreAsync(jobId);
        await SaveProfileAsync(profileId, d => d with { StrongMatchThreshold = 71 });
        await ScoreAsync(jobId);

        await using var db = CreateDbContext();
        var matched = (await OutboxTestHelpers.EventsMentioningAsync(db, jobId))
            .Where(e => e.EventName == "jobs.job-matched.v1" && e.Payload.Contains(profileId.ToString(), StringComparison.Ordinal))
            .ToList();
        var single = Assert.Single(matched);
        using var payload = JsonDocument.Parse(single.Payload);
        Assert.Equal(100, payload.RootElement.GetProperty("score").GetInt32());
    }

    // ---------- merge and split (AC-1, AC-14) ----------

    // covers: AC-1, AC-14
    [Fact]
    public async Task A_merge_keeps_one_match_per_profile_and_drops_the_removed_jobs_requirements()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#"]));
        var sourceId = await SeedSourceAsync();
        await IngestAsync(sourceId, T0, Posting("1", "Platform Engineer", "Paris", MatchingDescription), Posting("2", "Data Engineer", "Paris", MatchingDescription));
        Guid older, newer;
        await using (var db = CreateDbContext())
        {
            var ids = await JobIdsAsync(db);
            (older, newer) = (ids[0], ids[1]);
        }

        foreach (var id in new[] { older, newer })
        {
            await ExtractAsync(id, FakeExtractor());
            await ScoreAsync(id);
        }

        int before;
        await using (var db = CreateDbContext())
        {
            before = (await OutboxTestHelpers.EventsMentioningAsync(db, older)).Count(e => e.EventName == "jobs.job-content-changed.v1");
        }

        // Posting 2 alone is seen again, retitled into posting 1's key: the reconcile merges it, and
        // its more recently seen link becomes the survivor's primary, so the survivor is read again.
        await IngestAsync(sourceId, T0.AddDays(1), Posting("2", "Platform Engineer", "Paris", MatchingDescription + " Updated."));
        await ReconcileAsync();

        await using var check = CreateDbContext();
        Assert.False(await check.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == newer));
        Assert.False(await check.JobRequirements.AnyAsync(r => r.JobId == newer));
        Assert.Equal(1, await check.JobMatches.CountAsync(m => m.JobId == older && m.ProfileId == profileId));
        var survivorEvents = await OutboxTestHelpers.EventsMentioningAsync(check, older);
        Assert.Equal(before + 1, survivorEvents.Count(e => e.EventName == "jobs.job-content-changed.v1"));
    }

    // covers: AC-1
    [Fact]
    public async Task A_split_raises_content_changed_for_both_jobs()
    {
        var sourceId = await SeedSourceAsync();
        var other = new ScriptedSource("MatchAts");
        var otherId = await SeedSourceAsync(other);
        await IngestAsync(sourceId, T0, Posting("1", "Platform Engineer", "Paris", MatchingDescription));
        await IngestAsync(otherId, T0, other, Posting("a", "Platform Engineer", "Paris", MatchingDescription));
        Guid jobId, linkId;
        await using (var db = CreateDbContext())
        {
            var job = await db.Jobs.Include(j => j.Links).SingleAsync(j => j.Links.Any(l => l.JobSourceId == otherId));
            jobId = job.Id;
            linkId = job.Links.Single(l => l.JobSourceId == otherId).Id;
        }

        int before;
        await using (var db = CreateDbContext())
        {
            before = (await OutboxTestHelpers.EventsMentioningAsync(db, jobId)).Count(e => e.EventName == "jobs.job-content-changed.v1");
        }

        var result = await SplitAsync(jobId, linkId, other);

        await using var check = CreateDbContext();
        Assert.Equal(SplitOutcome.Split, result.Outcome);
        Assert.Equal(before + 1, (await OutboxTestHelpers.EventsMentioningAsync(check, jobId)).Count(e => e.EventName == "jobs.job-content-changed.v1"));
        Assert.Single(await OutboxTestHelpers.EventsMentioningAsync(check, result.NewJobId!.Value), e => e.EventName == "jobs.job-content-changed.v1");
    }

    // ---------- the sweep (AC-1, AC-7, AC-8) ----------

    // covers: AC-7 (regression: a Matching config change applies on Api start)
    [Fact]
    public async Task The_start_sweep_queues_a_rescore_for_every_profile_and_the_hourly_one_only_for_what_it_finds()
    {
        var profileId = await SeedProfileAsync(Document());

        await using var db = CreateDbContext();
        var everyProfile = await db.Profiles.Select(p => p.Id).ToListAsync();
        var onStart = new RecordingScheduler();
        await Service(db, FakeExtractor(), onStart).SweepAsync(rescoreEveryProfile: true, CancellationToken.None);
        var hourly = new RecordingScheduler();
        var found = await Service(db, FakeExtractor(), hourly).SweepAsync(rescoreEveryProfile: false, CancellationToken.None);

        Assert.Equal(everyProfile.Order(), onStart.Rescores.Order());
        Assert.Contains(profileId, onStart.Rescores);
        Assert.Equal(found.ProfilesToRescore, hourly.Rescores);
    }

    // covers: AC-1, AC-8
    [Fact]
    public async Task The_sweep_finds_jobs_with_no_lost_or_due_failed_requirements()
    {
        var sourceId = await SeedSourceAsync();
        await IngestAsync(sourceId, T0, Posting("none", "Role A", "Paris"), Posting("lost", "Role B", "Paris"), Posting("due", "Role C", "Paris"), Posting("recent", "Role D", "Paris"), Posting("done", "Role E", "Paris"));
        Dictionary<string, Guid> ids;
        await using (var db = CreateDbContext())
        {
            ids = await db.JobSourceLinks.Where(l => l.JobSourceId == sourceId).ToDictionaryAsync(l => l.ExternalId, l => l.JobId);
        }

        await ExtractAsync(ids["done"], FakeExtractor());
        var failing = new ScriptedExtractor(_ => throw new InvalidOperationException("down"));
        foreach (var key in new[] { "due", "recent" })
        {
            for (var i = 0; i < 3; i++)
            {
                await ExtractAsync(ids[key], failing);
            }
        }

        await using var sql = CreateDbContext();
        await sql.JobRequirements.Where(r => r.JobId == ids["due"]).ExecuteUpdateAsync(s => s.SetProperty(r => r.PendingSince, DateTimeOffset.UtcNow.AddDays(-2)));
        sql.JobRequirements.Add(JobRequirement.Start(ids["lost"], "hash", DateTimeOffset.UtcNow.AddHours(-1)));
        await sql.SaveChangesAsync();

        var work = await sql.Database.CreateExecutionStrategy().ExecuteAsync(() => new MatchRepository(sql).FindSweepWorkAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains(ids["none"], work.JobsToExtract);
        Assert.Contains(ids["lost"], work.JobsToExtract);
        Assert.Contains(ids["due"], work.JobsToExtract);
        Assert.DoesNotContain(ids["recent"], work.JobsToExtract);
        Assert.DoesNotContain(ids["done"], work.JobsToExtract);
    }

    // ---------- match profile endpoints (AC-11) ----------

    // covers: AC-11
    [Fact]
    public async Task The_match_profile_needs_a_current_etag_and_valid_fields()
    {
        var profileId = await SeedProfileAsync(Document());
        using var client = factory.CreateClient();
        var (document, etag) = await GetProfileAsync(client, profileId);

        var missing = await PutProfileAsync(client, profileId, document, null);
        var stale = await PutProfileAsync(client, profileId, document, "\"1\"");
        var invalid = await PutProfileAsync(client, profileId, document with { SalaryCurrency = "XXQ", MinSalary = 10, AuthorizedCountries = ["ZZ"], StrongMatchThreshold = 101 }, etag);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        var fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("salaryCurrency", fields);
        Assert.Contains("authorizedCountries[0]", fields);
        Assert.Contains("strongMatchThreshold", fields);
        Assert.Equal(etag, (await GetProfileAsync(client, profileId)).ETag);
    }

    // covers: AC-11
    [Fact]
    public async Task Saving_replaces_the_profile_normalizes_skills_and_raises_match_profile_changed()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["Rust"], experiences: [new ExperienceDto(null, "Acme", "Engineer", new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1), null), new ExperienceDto(null, "Old", "Intern", new DateOnly(2018, 1, 1), new DateOnly(2018, 6, 1), null)]));
        using var client = factory.CreateClient();
        var (document, etag) = await GetProfileAsync(client, profileId);
        var keep = document.Experiences.Single(e => e.Company == "Acme");

        var response = await PutProfileAsync(client, profileId, document with
        {
            Skills = [".NET 8", ".net", "C#"],
            Experiences = [keep with { Title = "Senior Engineer" }, new ExperienceDto(null, "Globex", "Lead", new DateOnly(2021, 2, 1), null, null)],
            Educations = [new EducationDto(null, "Uni", "MS", "CS", "Master", new DateOnly(2014, 1, 1), new DateOnly(2016, 1, 1))],
            RemotePreference = "Remote",
            JobTypes = ["FullTime"],
        }, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag!.ToString());
        var saved = (await response.Content.ReadFromJsonAsync<MatchProfileDocument>())!;
        Assert.Equal([".net", "c#"], saved.Skills);
        Assert.Equal(["Lead", "Senior Engineer"], saved.Experiences.Select(e => e.Title).Order());
        Assert.Equal(keep.Id, saved.Experiences.Single(e => e.Title == "Senior Engineer").Id);
        Assert.Equal("Master", Assert.Single(saved.Educations).DegreeLevel);
        Assert.Equal("Remote", saved.RemotePreference);

        await using var db = CreateDbContext();
        var events = await OutboxTestHelpers.EventsMentioningAsync(db, profileId);
        Assert.Contains(events, e => e.EventName == "profile.match-profile-changed.v1");
    }

    // covers: AC-11 (review finding: inputs past the database limits)
    [Fact]
    public async Task Inputs_past_the_database_limits_are_field_errors_not_server_errors()
    {
        var profileId = await SeedProfileAsync(Document());
        using var client = factory.CreateClient();
        var (document, etag) = await GetProfileAsync(client, profileId);

        var huge = await PutProfileAsync(client, profileId, document with { MinSalary = 10_000_000_000m, SalaryCurrency = "USD" }, etag);
        var nullRow = await PutProfileAsync(client, profileId, document with { Skills = ["C#", null!] }, etag);

        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Contains("minSalary", await huge.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, nullRow.StatusCode);
        Assert.Contains("skills[1]", await nullRow.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_profile_has_no_match_profile()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/profile/{Guid.CreateVersion7()}/match-profile");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- list and panel (AC-9, AC-15) ----------

    // covers: AC-9
    [Fact]
    public async Task The_list_puts_scored_first_blocked_after_unblocked_and_unscored_last()
    {
        var profileId = await SeedProfileAsync(Document());
        var sourceId = await SeedSourceAsync();
        await IngestAsync(sourceId, T0, Posting("a", "Role A", "Paris"), Posting("b", "Role B", "Paris"), Posting("c", "Role C", "Paris"), Posting("d", "Role D", "Paris"), Posting("e", "Role E", "Paris"));
        Dictionary<string, Guid> ids;
        await using (var db = CreateDbContext())
        {
            ids = await db.JobSourceLinks.Where(l => l.JobSourceId == sourceId).ToDictionaryAsync(l => l.ExternalId, l => l.JobId);
            db.JobMatches.AddRange(
                SeededMatch(ids["a"], profileId, 80, false),
                SeededMatch(ids["b"], profileId, 20, true),
                SeededMatch(ids["c"], profileId, null, false),
                SeededMatch(ids["e"], profileId, 95, false));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var all = new List<JobListItemDto>();
        MatchListDto page;
        var number = 1;
        do
        {
            page = (await client.GetFromJsonAsync<MatchListDto>($"/internal/matches?profileId={profileId}&page={number++}&pageSize=100"))!;
            all.AddRange(page.Items);
        }
        while (all.Count < page.Total);

        var ours = all.Where(i => ids.ContainsValue(i.JobId)).Select(i => ids.Single(x => x.Value == i.JobId).Key).ToList();
        var unscored = new[] { ids["c"], ids["d"] }.Order().Select(id => ids.Single(x => x.Value == id).Key);
        Assert.Equal(["e", "a", "b", .. unscored], ours);
        Assert.True(page.ProfileIncomplete);
        Assert.True(all.Single(i => i.JobId == ids["b"]).HasBlocker);
    }

    // covers: AC-9
    [Fact]
    public async Task The_list_pages_by_25_and_rejects_bad_paging()
    {
        var profileId = await SeedProfileAsync(Document());
        using var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<MatchListDto>($"/internal/matches?profileId={profileId}");

        Assert.Equal(25, first!.PageSize);
        Assert.Equal(1, first.Page);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/internal/matches?profileId={profileId}&pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/internal/matches?profileId={profileId}&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/internal/matches")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/matches?profileId={Guid.CreateVersion7()}")).StatusCode);
    }

    // covers: AC-9
    [Fact]
    public async Task A_profile_with_skills_and_experience_is_not_incomplete()
    {
        var profileId = await SeedProfileAsync(Document(skills: ["C#"], experiences: [new ExperienceDto(null, "Acme", "Engineer", new DateOnly(2020, 1, 1), null, null)]));
        using var client = factory.CreateClient();

        var list = await client.GetFromJsonAsync<MatchListDto>($"/internal/matches?profileId={profileId}");

        Assert.False(list!.ProfileIncomplete);
    }

    // covers: AC-15
    [Fact]
    public async Task A_match_is_only_returned_for_its_own_profile()
    {
        var mine = await SeedProfileAsync(Document(skills: ["C#"]));
        var jobId = await IngestOneAsync("Backend Engineer", MatchingDescription);
        await ExtractAsync(jobId, FakeExtractor());
        await ScoreAsync(jobId);
        using var client = factory.CreateClient();

        var own = await client.GetAsync($"/internal/jobs/{jobId}/match?profileId={mine}");
        var stranger = await client.GetAsync($"/internal/jobs/{jobId}/match?profileId={Guid.CreateVersion7()}");
        var unknownJob = await client.GetAsync($"/internal/jobs/{Guid.CreateVersion7()}/match?profileId={mine}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownJob.StatusCode);
        Assert.Equal("application/problem+json", stranger.Content.Headers.ContentType?.MediaType);
    }

    // ---------- startup validation and handlers (AC-16, AC-1) ----------

    // covers: AC-16
    [Fact]
    public void Bad_matching_config_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Matching:Weights:Skills"] = "29" })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddJobsModule(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MatchingSettings>>().Value);

        Assert.Contains("sum to 100, not 99", error.Message);
    }

    // covers: AC-1, AC-7
    [Fact]
    public async Task The_handlers_queue_extraction_and_profile_rescore()
    {
        var scheduler = new RecordingScheduler();
        var jobId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();

        await new EnqueueRequirementExtraction(scheduler).HandleAsync(new JobContentChanged(jobId, "hash"), CancellationToken.None);
        await new EnqueueProfileRescore(scheduler).HandleAsync(new MatchProfileChanged(profileId), CancellationToken.None);

        Assert.Equal([(jobId, false)], scheduler.Extractions);
        Assert.Equal([profileId], scheduler.Rescores);
    }

    // ---------- helpers ----------

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private JobMatchingService Service(WorkPilotDbContext db, IJobRequirementExtractor extractor, RecordingScheduler? scheduler = null) =>
        new(new MatchRepository(db), extractor, new EventPublisher(db, TimeProvider.System), scheduler ?? _scheduler, new MatchingSettings(), TimeProvider.System);

    private static IJobRequirementExtractor FakeExtractor() =>
        new ChatClientJobRequirementExtractor(new FakeChatClient(), new ResolvedAiPurpose(AiPurposes.JobExtraction, "Fake", null));

    private async Task<ExtractionOutcome> ExtractAsync(Guid jobId, IJobRequirementExtractor extractor, bool force = false)
    {
        await using var db = CreateDbContext();
        return await Service(db, extractor).ExtractAsync(jobId, force, CancellationToken.None);
    }

    private async Task<ScoringSummary> ScoreAsync(Guid jobId)
    {
        await using var db = CreateDbContext();
        return await Service(db, FakeExtractor()).ScoreJobAsync(jobId, CancellationToken.None);
    }

    private async Task<ScoringSummary> RescoreProfileAsync(Guid profileId)
    {
        await using var db = CreateDbContext();
        return await Service(db, FakeExtractor()).RescoreProfileAsync(profileId, CancellationToken.None);
    }

    private async Task<JobMatch> MatchAsync(Guid jobId, Guid profileId)
    {
        await using var db = CreateDbContext();
        return await db.JobMatches.AsNoTracking().SingleAsync(m => m.JobId == jobId && m.ProfileId == profileId);
    }

    private static async Task<JobMatchDetailDto> MatchDetailAsync(HttpClient client, Guid jobId, Guid profileId) =>
        (await client.GetFromJsonAsync<JobMatchDetailDto>($"/internal/jobs/{jobId}/match?profileId={profileId}"))!;

    private static MatchProfileDocument Document(
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<ExperienceDto>? experiences = null) =>
        new(roles ?? [], "Any", [], [], null, null, [], true, 70, skills ?? [], experiences ?? [], []);

    // A profile row, then its match profile saved through the real endpoint.
    private async Task<Guid> SeedProfileAsync(MatchProfileDocument document)
    {
        Guid profileId;
        await using (var db = CreateDbContext())
        {
            var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Matching test" };
            db.Profiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
            _profiles.Add(profileId);
        }

        using var client = factory.CreateClient();
        var (_, etag) = await GetProfileAsync(client, profileId);
        var response = await PutProfileAsync(client, profileId, document, etag);
        response.EnsureSuccessStatusCode();
        return profileId;
    }

    private async Task SaveProfileAsync(Guid profileId, Func<MatchProfileDocument, MatchProfileDocument> change)
    {
        using var client = factory.CreateClient();
        var (document, etag) = await GetProfileAsync(client, profileId);
        (await PutProfileAsync(client, profileId, change(document), etag)).EnsureSuccessStatusCode();
    }

    private static async Task<(MatchProfileDocument Document, string ETag)> GetProfileAsync(HttpClient client, Guid profileId)
    {
        var response = await client.GetAsync($"/internal/profile/{profileId}/match-profile");
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<MatchProfileDocument>())!, response.Headers.ETag!.ToString());
    }

    private static Task<HttpResponseMessage> PutProfileAsync(HttpClient client, Guid profileId, MatchProfileDocument document, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/profile/{profileId}/match-profile") { Content = JsonContent.Create(document) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return client.SendAsync(request);
    }

    private static JobMatch SeededMatch(Guid jobId, Guid profileId, int? score, bool blocked) => new()
    {
        JobId = jobId,
        ProfileId = profileId,
        Score = score,
        HasBlocker = blocked,
        Confidence = "High",
        Explanation = "{}",
        InputsFingerprint = "seeded",
        RankedAt = T0,
    };

    private async Task<Guid> SeedSourceAsync(ScriptedSource? adapter = null)
    {
        adapter ??= _board;
        await using var db = CreateDbContext();
        var source = new JobSource { Type = adapter.SourceType, Name = $"{adapter.SourceType}:{Guid.NewGuid():N}", Config = "{}" };
        db.JobSources.Add(source);
        await db.SaveChangesAsync();
        _sources.Add(source.Id);
        return source.Id;
    }

    private async Task<Guid> IngestOneAsync(string title, string description, string? location = "Paris")
    {
        var sourceId = await SeedSourceAsync();
        await IngestAsync(sourceId, T0, Posting("1", title, location, description));
        await using var db = CreateDbContext();
        return await db.JobSourceLinks.Where(l => l.JobSourceId == sourceId).Select(l => l.JobId).SingleAsync();
    }

    private RawJobPosting Posting(string id, string title, string? location, string description = "Build it.") =>
        new(id, string.Empty, title, _company, location, $"<p>{description}</p>", T0.AddDays(-3), string.Empty);

    private Task IngestAsync(Guid sourceId, DateTimeOffset now, params RawJobPosting[] postings) =>
        IngestAsync(sourceId, now, _board, postings);

    private async Task IngestAsync(Guid sourceId, DateTimeOffset now, ScriptedSource source, params RawJobPosting[] postings)
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        var audit = new AuditService(db);
        IJobSource[] adapters = [_board, source];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), audit, adapters.Distinct());
        var service = new JobIngestionService(adapters.Distinct(), repository, merger, audit, new EventPublisher(db, TimeProvider.System), new FixedTime(now));
        source.Next = postings.Select(source.Stamp).ToArray();
        await service.IngestAsync(sourceId, null, null, CancellationToken.None);
    }

    private async Task ReconcileAsync()
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), new AuditService(db), [_board]);
        await new JobDedupService([_board], repository, merger, new EventPublisher(db, TimeProvider.System), new FixedTime(T0.AddDays(2))).ReconcileAsync(CancellationToken.None);
    }

    private async Task<SplitResult> SplitAsync(Guid jobId, Guid linkId, ScriptedSource other)
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        IJobSource[] adapters = [_board, other];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), new AuditService(db), adapters);
        return await new JobDedupService(adapters, repository, merger, new EventPublisher(db, TimeProvider.System), new FixedTime(T0.AddDays(2))).SplitAsync(jobId, linkId, CancellationToken.None);
    }

    private async Task<List<Guid>> JobIdsAsync(WorkPilotDbContext db) =>
        await db.JobSourceLinks.Where(l => _sources.Contains(l.JobSourceId)).Select(l => l.JobId).Distinct().OrderBy(id => id).ToListAsync();

    private sealed class ScriptedSource(string type) : IJobSource
    {
        private readonly AsyncLocal<RawJobPosting[]> _next = new();

        public RawJobPosting[] Next { set => _next.Value = value; }

        public string SourceType => type;

        public decimal ProvenanceConfidence => 0.9m;

        public JobSourceDefinition? Describe(string board) => new($"{type}:{board}", "{}");

        public Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RawJobPosting>>(_next.Value ?? []);

        public RawJobPosting ParseStored(JobSource source, string rawContent) =>
            JsonSerializer.Deserialize<RawJobPosting>(rawContent)! with { RawContent = rawContent };

        public RawJobPosting Stamp(RawJobPosting posting)
        {
            var stamped = posting with { SourceUrl = $"https://{type.ToLowerInvariant()}.test/{posting.ExternalId}", RawContent = string.Empty };
            return stamped with { RawContent = JsonSerializer.Serialize(stamped) };
        }
    }

    private sealed class ScriptedExtractor(Func<string, JobRequirementExtraction> answer) : IJobRequirementExtractor
    {
        public int Calls { get; private set; }

        public Task<JobRequirementExtraction> ExtractAsync(string description, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(description));
        }
    }

    private sealed class RecordingScheduler : IMatchJobScheduler
    {
        public List<(Guid JobId, bool Force)> Extractions { get; } = [];

        public List<Guid> Rescores { get; } = [];

        public void EnqueueExtraction(Guid jobId, bool force) => Extractions.Add((jobId, force));

        public void EnqueueProfileRescore(Guid profileId) => Rescores.Add(profileId);
    }

    // Records each prompt and the options it came with, and answers with scripted text.
    private sealed class CapturingChatClient(Func<string, string> answer) : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Concat(messages.Select(m => m.Text));
            Prompts.Add(prompt);
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(prompt))) { ModelId = "capture" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
