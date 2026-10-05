using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Common;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Contracts.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Modules.Applications;
using WorkPilot.Infrastructure.Modules.Audit;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;
using WorkPilot.Infrastructure.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// The jobs list and job detail (spec 0021) against the real Postgres: the widened
// GET /internal/matches (sorts, every filter, paging, match status), facets, the detail with
// soft deleted jobs, dismissals and how merges move them, the sources summary from the audit log,
// Run now, and the ingestion trigger's ProblemDetails. Every test seeds its own company, sources,
// jobs and profiles and removes them afterwards. Needs WORKPILOTDB_CONNECTION.
[Collection("Api")]
public class JobsListTests(SharedApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _company = $"Listco {Guid.NewGuid():N}";
    private readonly List<Guid> _sources = [];
    private readonly List<Guid> _profiles = [];
    private readonly List<Guid> _skills = [];
    private readonly ScriptedSource _board = new("ListBoard");
    private readonly ScriptedSource _ats = new("ListAts");

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
        await db.JobDismissals.Where(d => jobIds.Contains(d.JobId) || _profiles.Contains(d.ProfileId)).ExecuteDeleteAsync();
        await db.JobMatches.Where(m => jobIds.Contains(m.JobId) || _profiles.Contains(m.ProfileId)).ExecuteDeleteAsync();
        await db.Jobs.IgnoreQueryFilters().Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync();
        await db.JobSources.Where(s => _sources.Contains(s.Id)).ExecuteDeleteAsync();
        await db.Profiles.IgnoreQueryFilters().Where(p => _profiles.Contains(p.Id)).ExecuteDeleteAsync();
        await db.Skills.Where(s => _skills.Contains(s.Id)).ExecuteDeleteAsync();
    }

    // ---------- order and paging (AC-1) ----------

    // covers: AC-1, AC-3
    [Fact]
    public async Task Best_match_pages_by_25_without_repeats_and_newest_sorts_by_posted_date()
    {
        var profileId = await SeedProfileAsync();
        var sourceId = await SeedSourceAsync(_board);
        await IngestAsync(_board, sourceId, Enumerable.Range(0, 30).Select(i => Posting($"{i}", $"Role {i:00}", "Paris", postedAt: Now.AddHours(-i))).ToArray());
        var ids = await JobIdsByExternalIdAsync(sourceId);
        await SeedMatchAsync(ids["5"], profileId, 90, false);
        await SeedMatchAsync(ids["6"], profileId, 40, false);
        await SeedMatchAsync(ids["7"], profileId, 95, true);
        using var client = factory.CreateClient();

        var first = await ListAsync(client, profileId, "");
        var second = await ListAsync(client, profileId, "&page=2");

        Assert.Equal(30, first.Total);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        var all = first.Items.Concat(second.Items).Select(i => i.JobId).ToList();
        Assert.Equal(30, all.Distinct().Count());

        // Scored before unscored, unblocked before blocked, best first; then the newest posting first.
        Assert.Equal([ids["5"], ids["6"], ids["7"], ids["0"], ids["1"]], all.Take(5));
        Assert.Equal(ids["29"], all[^1]);

        var newest = await ListAsync(client, profileId, "&sort=newest");
        Assert.Equal(Enumerable.Range(0, 25).Select(i => ids[$"{i}"]), newest.Items.Select(i => i.JobId));

        var row = first.Items[0];
        Assert.Equal((90, "High", JobMatchStatuses.Scored), (row.Score, row.Confidence, row.MatchStatus));
        Assert.Equal(["ListBoard"], row.SourceTypes);
        Assert.False(row.Dismissed);
    }

    // covers: AC-3, AC-7
    [Fact]
    public async Task Each_row_says_why_it_has_no_score()
    {
        var incomplete = await SeedProfileAsync();
        var sourceId = await SeedSourceAsync(_board);
        await IngestAsync(_board, sourceId, Posting("n", "Null Score", "Paris"), Posting("p", "No Row", "Paris"));
        var ids = await JobIdsByExternalIdAsync(sourceId);
        await SeedMatchAsync(ids["n"], incomplete, null, false);
        await using (var db = CreateDbContext())
        {
            // Skills and experience make the profile complete, so a missing row is just pending.
            db.Experiences.Add(new Experience { ProfileId = incomplete, Company = "Acme", Title = "Engineer", StartDate = new DateOnly(2020, 1, 1) });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var stillIncomplete = await ListAsync(client, incomplete, "");
        Assert.True(stillIncomplete.ProfileIncomplete);
        Assert.Equal(JobMatchStatuses.NotEnoughInfo, stillIncomplete.Items.Single(i => i.JobId == ids["n"]).MatchStatus);
        Assert.Equal(JobMatchStatuses.ProfileIncomplete, stillIncomplete.Items.Single(i => i.JobId == ids["p"]).MatchStatus);

        await using (var db = CreateDbContext())
        {
            var skill = new Skill { Name = $"skill-{Guid.NewGuid():N}" };
            db.Skills.Add(skill);
            _skills.Add(skill.Id);
            db.Set<ProfileSkill>().Add(new ProfileSkill { ProfileId = incomplete, SkillId = skill.Id });
            await db.SaveChangesAsync();
        }

        var complete = await ListAsync(client, incomplete, "");
        Assert.False(complete.ProfileIncomplete);
        Assert.Equal(JobMatchStatuses.Pending, complete.Items.Single(i => i.JobId == ids["p"]).MatchStatus);
    }

    // ---------- filters (AC-2) ----------

    // covers: AC-2
    [Fact]
    public async Task Every_filter_narrows_the_list()
    {
        var profileId = await SeedProfileAsync();
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board,
            Posting("remote", "Backend Engineer", "Remote, Europe", postedAt: Now.AddHours(-2)),
            Posting("hybrid", "Data_Analyst 100%", "Berlin (Hybrid)", postedAt: Now.AddDays(-5)),
            Posting("old", "Frontend Engineer", "Berlin", postedAt: Now.AddDays(-40)));
        await IngestAsync(_ats, ats, Posting("ats", "Platform Lead", "Lisbon", postedAt: Now.AddDays(-20)));
        var ids = await JobIdsAsync();
        var remote = await JobIdOfAsync(board, "remote");
        var hybrid = await JobIdOfAsync(board, "hybrid");
        var old = await JobIdOfAsync(board, "old");
        var lisbon = await JobIdOfAsync(ats, "ats");
        await SeedMatchAsync(remote, profileId, 85, false);
        await SeedMatchAsync(hybrid, profileId, 72, true);
        await SeedMatchAsync(old, profileId, 50, false);
        await using (var db = CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == remote).ExecuteUpdateAsync(s => s.SetProperty(j => j.SalaryRangeMin, 80000m).SetProperty(j => j.SalaryRangeMax, 120000m));
            await db.Jobs.Where(j => j.Id == hybrid).ExecuteUpdateAsync(s => s.SetProperty(j => j.SalaryRangeMin, 95000m));
        }

        using var client = factory.CreateClient();
        async Task<List<Guid>> Ids(string filters) => (await ListAsync(client, profileId, filters)).Items.Select(i => i.JobId).ToList();

        Assert.Equal(4, ids.Count);
        Assert.Equal([remote], await Ids("&q=BACKEND"));
        Assert.Equal([hybrid], await Ids("&q=_"));
        Assert.Equal([hybrid], await Ids("&q=%25"));
        Assert.Equal([old, hybrid], await Ids("&location=berlin"));
        Assert.Equal([remote], await Ids("&remoteType=Remote"));
        Assert.Equal([lisbon], await Ids($"&sourceId={ats}"));
        Assert.Equal([remote, hybrid], await Ids("&minScore=70"));
        Assert.Equal([remote], await Ids("&minScore=70&hideBlocked=true"));
        Assert.Equal([remote], await Ids("&postedWithinDays=1"));
        Assert.Equal([remote, hybrid], await Ids("&postedWithinDays=7"));
        Assert.Equal([remote, hybrid, lisbon], await Ids("&postedWithinDays=30"));
        Assert.Equal([remote, hybrid], await Ids("&salaryMin=90000"));
        Assert.Equal([remote], await Ids("&salaryMin=100000"));
        var otherCase = await client.GetFromJsonAsync<MatchListDto>($"/internal/matches?profileId={profileId}&company={Uri.EscapeDataString(_company.ToUpperInvariant())}");
        Assert.DoesNotContain(otherCase!.Items, i => ids.Contains(i.JobId));
    }

    // covers: AC-8
    [Theory]
    [InlineData("pageSize=500", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("minScore=101", "minScore")]
    [InlineData("postedWithinDays=3", "postedWithinDays")]
    [InlineData("salaryMin=-1", "salaryMin")]
    [InlineData("sort=oldest", "sort")]
    public async Task A_bad_parameter_is_a_400_naming_it(string query, string field)
    {
        var profileId = await SeedProfileAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/matches?profileId={profileId}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    // covers: AC-2
    [Fact]
    public async Task Facets_list_the_companies_remote_types_and_sources_present()
    {
        var profileId = await SeedProfileAsync();
        var board = await SeedSourceAsync(_board);
        var empty = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, Posting("1", "Backend Engineer", "Remote, Europe"));
        using var client = factory.CreateClient();

        var facets = (await client.GetFromJsonAsync<JobFacetsDto>($"/internal/jobs/facets?profileId={profileId}"))!;

        Assert.Contains(_company, facets.Companies);
        Assert.Contains("Remote", facets.RemoteTypes);
        Assert.Contains(facets.Sources, s => s.Id == board);
        Assert.DoesNotContain(facets.Sources, s => s.Id == empty);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/internal/jobs/facets")).StatusCode);
    }

    // ---------- dismissal (AC-4) ----------

    // covers: AC-4
    [Fact]
    public async Task Dismiss_hides_a_job_show_dismissed_marks_it_and_undo_restores_it()
    {
        var profileId = await SeedProfileAsync();
        var other = await SeedProfileAsync();
        var sourceId = await SeedSourceAsync(_board);
        await IngestAsync(_board, sourceId, Posting("1", "Backend Engineer", "Paris"), Posting("2", "Frontend Engineer", "Paris"));
        var jobId = await JobIdOfAsync(sourceId, "1");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/internal/jobs/{jobId}/dismissal", new DismissJobRequest(profileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/internal/jobs/{jobId}/dismissal", new DismissJobRequest(profileId))).StatusCode);

        Assert.DoesNotContain((await ListAsync(client, profileId, "")).Items, i => i.JobId == jobId);
        Assert.Single((await ListAsync(client, profileId, "&includeDismissed=true")).Items, i => i.JobId == jobId && i.Dismissed);
        Assert.Contains((await ListAsync(client, other, "")).Items, i => i.JobId == jobId && !i.Dismissed);
        var detail = (await client.GetFromJsonAsync<JobDetailView>($"/internal/jobs/{jobId}?profileId={profileId}"))!;
        Assert.True(detail.Dismissed);
        await using (var db = CreateDbContext())
        {
            Assert.Equal(1, await db.JobDismissals.CountAsync(d => d.JobId == jobId));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/internal/jobs/{jobId}/dismissal?profileId={profileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/internal/jobs/{jobId}/dismissal?profileId={profileId}")).StatusCode);
        Assert.Contains((await ListAsync(client, profileId, "")).Items, i => i.JobId == jobId && !i.Dismissed);
    }

    // covers: AC-4
    [Fact]
    public async Task Dismissing_an_unknown_job_or_for_an_unknown_profile_is_a_404()
    {
        var profileId = await SeedProfileAsync();
        var sourceId = await SeedSourceAsync(_board);
        await IngestAsync(_board, sourceId, Posting("1", "Backend Engineer", "Paris"));
        var jobId = await JobIdOfAsync(sourceId, "1");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/internal/jobs/{Guid.CreateVersion7()}/dismissal", new DismissJobRequest(profileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/internal/jobs/{jobId}/dismissal", new DismissJobRequest(Guid.CreateVersion7()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/internal/jobs/{Guid.CreateVersion7()}/dismissal?profileId={profileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/internal/jobs/{jobId}/dismissal")).StatusCode);
    }

    // covers: spec 0021 AC-4 (review finding: a merge removing the job after the existence check is a 404, not a 500)
    [Fact]
    public async Task Dismissing_a_job_a_merge_removed_after_the_check_is_a_404()
    {
        var profileId = await SeedProfileAsync();
        await using var db = CreateDbContext();
        // The job "existed" when checked, then a merge deleted it before the insert ran.
        var service = new JobDismissalService(new ExistedWhenChecked(new JobDismissalRepository(db)), TimeProvider.System);

        var result = await service.DismissAsync(Guid.CreateVersion7(), profileId, CancellationToken.None);

        Assert.Equal(ResultStatus.NotFound, result.Status);
        Assert.False(await db.JobDismissals.AnyAsync(d => d.ProfileId == profileId));
    }

    private sealed class ExistedWhenChecked(IJobDismissalRepository inner) : IJobDismissalRepository
    {
        public Task<bool> JobExistsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> ProfileExistsAsync(Guid profileId, CancellationToken cancellationToken) => inner.ProfileExistsAsync(profileId, cancellationToken);

        public Task<bool> AddAsync(JobDismissal dismissal, CancellationToken cancellationToken) => inner.AddAsync(dismissal, cancellationToken);

        public Task RemoveAsync(Guid profileId, Guid jobId, CancellationToken cancellationToken) => inner.RemoveAsync(profileId, jobId, cancellationToken);
    }

    // covers: Key invariants (a dismissal follows its job through a merge)
    [Fact]
    public async Task A_dismissed_job_that_merges_stays_dismissed_on_the_kept_job()
    {
        var profileId = await SeedProfileAsync();
        var both = await SeedProfileAsync();
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, Posting("b1", "Designer", "Lisbon"));
        await IngestAsync(_ats, ats, Posting("a1", "Product Designer", "Lisbon"));
        var kept = await JobIdOfAsync(board, "b1");
        var merged = await JobIdOfAsync(ats, "a1");
        Assert.NotEqual(kept, merged);
        using var client = factory.CreateClient();
        (await client.PutAsJsonAsync($"/internal/jobs/{merged}/dismissal", new DismissJobRequest(profileId))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/internal/jobs/{merged}/dismissal", new DismissJobRequest(both))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/internal/jobs/{kept}/dismissal", new DismissJobRequest(both))).EnsureSuccessStatusCode();

        // The ATS posting is retitled to the board's title, so the reconcile run merges them.
        await IngestAsync(_ats, ats, Posting("a1", "Designer", "Lisbon"));
        await ReconcileAsync();

        await using var db = CreateDbContext();
        Assert.Equal(kept, await JobIdOfAsync(ats, "a1"));
        Assert.False(await db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == merged));
        Assert.Equal([kept], await db.JobDismissals.Where(d => d.ProfileId == profileId).Select(d => d.JobId).ToListAsync());
        Assert.Equal([kept], await db.JobDismissals.Where(d => d.ProfileId == both).Select(d => d.JobId).ToListAsync());
    }

    // ---------- detail (AC-5) ----------

    // covers: AC-5
    [Fact]
    public async Task The_detail_has_salary_and_a_soft_deleted_job_is_returned_marked()
    {
        var sourceId = await SeedSourceAsync(_board);
        await IngestAsync(_board, sourceId, Posting("1", "Backend Engineer", "Paris"));
        var jobId = await JobIdOfAsync(sourceId, "1");
        await using (var db = CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SalaryRangeMax, 100000m).SetProperty(j => j.IsDeleted, true));
        }

        using var client = factory.CreateClient();
        var detail = (await client.GetFromJsonAsync<JobDetailView>($"/internal/jobs/{jobId}"))!;

        Assert.True(detail.IsDeleted);
        Assert.False(detail.Dismissed);
        Assert.Equal((null, 100000m), (detail.SalaryRangeMin, detail.SalaryRangeMax));
        Assert.Single(detail.Links);
        var profileId = await SeedProfileAsync();
        Assert.DoesNotContain((await ListAsync(client, profileId, "&includeDismissed=true")).Items, i => i.JobId == jobId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/jobs/{Guid.CreateVersion7()}")).StatusCode);
    }

    // ---------- sources (AC-6) ----------

    // covers: AC-6
    [Fact]
    public async Task Sources_show_their_job_count_and_latest_run_or_never_run()
    {
        var board = await SeedSourceAsync(_board);
        var never = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, Posting("1", "Backend Engineer", "Paris"), Posting("2", "Frontend Engineer", "Paris"));
        await IngestAsync(_board, board, Posting("1", "Backend Engineer", "Paris"), Posting("2", "Frontend Engineer II", "Paris"), Posting("3", "Designer", "Paris"));
        using var client = factory.CreateClient();

        var sources = (await client.GetFromJsonAsync<List<JobSourceSummaryDto>>("/internal/jobs/sources"))!;

        var ran = sources.Single(s => s.Id == board);
        Assert.Equal(3, ran.JobCount);
        Assert.NotNull(ran.LastRunAt);
        Assert.Equal((1, 1), (ran.LastRunCreated, ran.LastRunUpdated));
        var idle = sources.Single(s => s.Id == never);
        Assert.Equal((0, null, null), (idle.JobCount, idle.LastRunAt, idle.LastRunCreated));
    }

    // covers: AC-6
    [Fact]
    public async Task Run_now_queues_a_stored_source_and_404s_an_unknown_one()
    {
        var sourceId = await SeedSourceAsync(_board);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/internal/jobs/sources/{sourceId}/ingestions", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(sourceId, (await response.Content.ReadFromJsonAsync<TriggerJobIngestionResponse>())!.JobSourceId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/internal/jobs/sources/{Guid.CreateVersion7()}/ingestions", null)).StatusCode);
    }

    // covers: AC-6
    [Theory]
    [InlineData("""{"source":"workday","boardToken":"acme"}""", "source")]
    [InlineData("""{"boardToken":"acme"}""", "source")]
    [InlineData("""{"source":"lever","boardToken":"Not A Board!"}""", "boardToken")]
    [InlineData("""{"source":"greenhouse"}""", "boardToken")]
    [InlineData("""{"source":"greenhouse","boardToken":"acme","companyName":"   "}""", "companyName")]
    public async Task A_bad_board_is_a_problem_naming_the_field(string body, string field)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/internal/jobs/ingestions", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    // ---------- helpers ----------

    private async Task<MatchListDto> ListAsync(HttpClient client, Guid profileId, string filters) =>
        (await client.GetFromJsonAsync<MatchListDto>($"/internal/matches?profileId={profileId}&company={Uri.EscapeDataString(_company)}{filters}"))!;

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<Guid> SeedProfileAsync()
    {
        await using var db = CreateDbContext();
        var profile = new Profile { AuthUserId = Guid.NewGuid(), Name = "Jobs list test" };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        _profiles.Add(profile.Id);
        return profile.Id;
    }

    private async Task SeedMatchAsync(Guid jobId, Guid profileId, int? score, bool blocked)
    {
        await using var db = CreateDbContext();
        db.JobMatches.Add(new JobMatch
        {
            JobId = jobId,
            ProfileId = profileId,
            Score = score,
            HasBlocker = blocked,
            Confidence = "High",
            Explanation = "{}",
            InputsFingerprint = "seeded",
            RankedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSourceAsync(ScriptedSource adapter)
    {
        await using var db = CreateDbContext();
        var source = new JobSource { Type = adapter.SourceType, Name = $"{adapter.SourceType}:{Guid.NewGuid():N}", Config = "{}" };
        db.JobSources.Add(source);
        await db.SaveChangesAsync();
        _sources.Add(source.Id);
        return source.Id;
    }

    private RawJobPosting Posting(string id, string title, string? location, DateTimeOffset? postedAt = null) =>
        new(id, string.Empty, title, _company, location, "<p>Build it.</p>", postedAt, string.Empty);

    private async Task IngestAsync(ScriptedSource source, Guid sourceId, params RawJobPosting[] postings)
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        var audit = new AuditService(db);
        IJobSource[] adapters = [_board, _ats];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), audit, adapters);
        var service = new JobIngestionService(adapters, repository, merger, audit, new EventPublisher(db, TimeProvider.System), TimeProvider.System);
        source.Next = postings.Select(source.Stamp).ToArray();
        await service.IngestAsync(sourceId, null, null, CancellationToken.None);
    }

    private async Task ReconcileAsync()
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        IJobSource[] adapters = [_board, _ats];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), new AuditService(db), adapters);
        await new JobDedupService(adapters, repository, merger, new EventPublisher(db, TimeProvider.System), TimeProvider.System).ReconcileAsync(CancellationToken.None);
    }

    private async Task<Guid> JobIdOfAsync(Guid sourceId, string externalId)
    {
        await using var db = CreateDbContext();
        return await db.JobSourceLinks.Where(l => l.JobSourceId == sourceId && l.ExternalId == externalId).Select(l => l.JobId).SingleAsync();
    }

    private async Task<Dictionary<string, Guid>> JobIdsByExternalIdAsync(Guid sourceId)
    {
        await using var db = CreateDbContext();
        return await db.JobSourceLinks.Where(l => l.JobSourceId == sourceId).ToDictionaryAsync(l => l.ExternalId, l => l.JobId);
    }

    private async Task<List<Guid>> JobIdsAsync()
    {
        await using var db = CreateDbContext();
        return await db.JobSourceLinks.Where(l => _sources.Contains(l.JobSourceId)).Select(l => l.JobId).Distinct().ToListAsync();
    }

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
}
