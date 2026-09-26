using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Jobs;
using WorkPilot.Domain.Modules.Jobs;
using WorkPilot.Infrastructure.Modules.Agent;
using WorkPilot.Infrastructure.Modules.Applications;
using WorkPilot.Infrastructure.Modules.Jobs;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// Job deduplication (spec 0017) against the real Postgres: merges across and
// within sources, the primary link, split, reconcile, rename, revive, the
// advisory lock under two concurrent runs, and the read endpoints. Two
// scripted sources of different types (and confidences) stand in for
// Greenhouse and Lever. Every test uses its own company, so nothing here
// merges with another test's jobs. Needs WORKPILOTDB_CONNECTION.
[Collection("Api")]
public class JobDeduplicationTests(SharedApiFactory factory) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly string _company = $"Acme {Guid.NewGuid():N}";
    private readonly List<Guid> _sources = [];
    private readonly ScriptedSource _board = new("ScriptedBoard", 0.9m);
    private readonly ScriptedSource _ats = new("ScriptedAts", 1.0m);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = CreateDbContext();
        var jobIds = await db.JobSourceLinks.Where(l => _sources.Contains(l.JobSourceId)).Select(l => l.JobId).Distinct().ToListAsync();
        await db.AuditLogs.IgnoreQueryFilters().Where(a => jobIds.Contains(a.TargetId) || _sources.Contains(a.TargetId)).ExecuteDeleteAsync();
        await db.JobMatches.Where(m => jobIds.Contains(m.JobId)).ExecuteDeleteAsync();
        await db.Jobs.IgnoreQueryFilters().Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync();
        await db.JobSources.Where(s => _sources.Contains(s.Id)).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_second_source_with_the_same_role_joins_the_job_and_the_higher_confidence_link_fills_it()
    {
        // covers AC-2, AC-4, AC-5, AC-12
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);

        await IngestAsync(_board, board, T0, Posting("b1", "Sr. Backend Engineer", "Berlin", "Board text."));
        var summary = await IngestAsync(_ats, ats, T0.AddHours(1), Posting("a1", "Senior Backend Engineer", "Berlin", "ATS text."));

        Assert.Equal(0, summary!.Created);
        Assert.Equal(1, summary.Merged);

        await using var db = CreateDbContext();
        var job = Assert.Single(await JobsAsync(db));
        Assert.Equal(2, job.Links.Count);
        Assert.Equal(2, job.Snapshots.Count);
        var primary = job.PrimaryLink!;
        Assert.Equal(ats, primary.JobSourceId);
        Assert.Equal("Senior Backend Engineer", job.Title);
        Assert.Equal("ATS text.", job.Description);
        Assert.Equal("https://ats.test/a1", job.Provenance.SourceUrl);
        Assert.Equal(T0, job.Provenance.RetrievedAt);
        Assert.Equal(T0.AddHours(1), job.Provenance.VerifiedAt);
        Assert.Equal(1.0m, job.Provenance.Confidence);
        Assert.False(job.IsStale);

        // The board changes its text: only its own link gets a snapshot, the ATS link still fills the job.
        var again = await IngestAsync(_board, board, T0.AddDays(1), Posting("b1", "Sr. Backend Engineer", "Berlin", "Board text v2."));
        Assert.Equal(1, again!.Updated);
        await using var db2 = CreateDbContext();
        job = Assert.Single(await JobsAsync(db2));
        Assert.Equal(2, job.Snapshots.Count(s => s.ExternalId == "b1"));
        Assert.Single(job.Snapshots, s => s.ExternalId == "a1");
        Assert.Equal("ATS text.", job.Description);

        var merged = await db2.AuditLogs.Where(a => a.TargetId == job.Id && a.Action == JobMerger.MergedAction).ToListAsync();
        using var payload = JsonDocument.Parse(Assert.Single(merged).Payload!);
        Assert.Equal("ingest", payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_different_location_or_a_missing_one_stays_a_separate_job_and_a_repost_merges()
    {
        // covers AC-2, AC-3
        var board = await SeedSourceAsync(_board);

        var first = await IngestAsync(_board, board, T0,
            Posting("1", "Data Engineer", "Berlin"),
            Posting("2", "Data Engineer", "Munich"),
            Posting("3", "Data Engineer", null),
            Posting("4", "Data  Engineer!", "berlin"));

        Assert.Equal(3, first!.Created);
        Assert.Equal(1, first.Merged);
        await using var db = CreateDbContext();
        var jobs = await JobsAsync(db);
        Assert.Equal(3, jobs.Count);
        Assert.Equal(["1", "4"], jobs.Single(j => j.Links.Count == 2).Links.Select(l => l.ExternalId).Order());
    }

    [Fact]
    public async Task A_split_moves_the_link_into_its_own_job_and_it_is_never_merged_back()
    {
        // covers AC-8, AC-12
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, T0, Posting("b1", "QA Engineer", "Remote"));
        await IngestAsync(_ats, ats, T0.AddHours(1), Posting("a1", "QA Engineer", "Remote", "ATS text."));

        Guid jobId, atsLinkId;
        await using (var db = CreateDbContext())
        {
            var job = Assert.Single(await JobsAsync(db));
            jobId = job.Id;
            atsLinkId = job.Links.Single(l => l.JobSourceId == ats).Id;
            db.JobMatches.Add(new JobMatch { JobId = job.Id, ProfileId = Guid.NewGuid(), Score = 0.5m });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        // The split itself runs through the use case: only it knows the scripted
        // sources' snapshot format (the live 200 path is in verify.md).
        var body = await SplitAsync(jobId, atsLinkId);
        Assert.Equal(SplitOutcome.Split, body.Outcome);
        Assert.Equal(jobId, body.JobId);

        await using (var db = CreateDbContext())
        {
            var jobs = await JobsAsync(db);
            var original = jobs.Single(j => j.Id == jobId);
            var split = jobs.Single(j => j.Id == body.NewJobId!.Value);
            Assert.Equal(board, Assert.Single(original.Links).JobSourceId);
            Assert.Equal("https://board.test/b1", original.Provenance.SourceUrl);
            Assert.Equal(0.9m, original.Provenance.Confidence);
            Assert.All(original.Snapshots, s => Assert.Equal("b1", s.ExternalId));
            Assert.NotNull(Assert.Single(split.Links).SplitAt);
            Assert.All(split.Snapshots, s => Assert.Equal("a1", s.ExternalId));
            Assert.Equal("ATS text.", split.Description);
            Assert.Equal(1, await db.JobMatches.CountAsync(m => m.JobId == jobId));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.TargetId == jobId && a.Action == JobMerger.SplitAction));
        }

        // Both keep their key; seeing the ATS posting again leaves it on its own job.
        await IngestAsync(_ats, ats, T0.AddDays(1), Posting("a1", "QA Engineer", "Remote", "ATS text."));
        await using (var db = CreateDbContext())
        {
            Assert.Equal(2, (await JobsAsync(db)).Count);
        }

        var notOnJob = await client.PostAsync($"/internal/jobs/{jobId}/links/{atsLinkId}/split", null);
        Assert.Equal(HttpStatusCode.NotFound, notOnJob.StatusCode);
        var onlyLink = await client.PostAsync($"/internal/jobs/{body.NewJobId}/links/{atsLinkId}/split", null);
        Assert.Equal(HttpStatusCode.BadRequest, onlyLink.StatusCode);
        var unknown = await client.PostAsync($"/internal/jobs/{Guid.NewGuid()}/links/{atsLinkId}/split", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_title_change_goes_stale_and_the_reconcile_merges_it_moving_matches_once()
    {
        // covers AC-9, AC-12
        var board = await SeedSourceAsync(_board);
        await IngestAsync(_board, board, T0, Posting("1", "Platform Engineer", "Paris"), Posting("2", "Infra Engineer", "Paris"));

        var profile = Guid.NewGuid();
        Guid older, newer;
        await using (var db = CreateDbContext())
        {
            var jobs = await JobsAsync(db);
            older = jobs.Single(j => j.Links[0].ExternalId == "1").Id;
            newer = jobs.Single(j => j.Links[0].ExternalId == "2").Id;
            Assert.True(older.CompareTo(newer) < 0);
            db.JobMatches.Add(new JobMatch { JobId = newer, ProfileId = profile, Score = 0.7m });
            db.JobMatches.Add(new JobMatch { JobId = newer, ProfileId = Guid.NewGuid(), Score = 0.4m });
            db.JobMatches.Add(new JobMatch { JobId = older, ProfileId = profile, Score = 0.6m });
            await db.SaveChangesAsync();
        }

        // Posting 2 is retitled into posting 1's key: no merge inside the run, the job goes stale.
        var changed = await IngestAsync(_board, board, T0.AddDays(1), Posting("1", "Platform Engineer", "Paris"), Posting("2", "Platform Engineer", "Paris"));
        Assert.True(changed!.ReconcileNeeded);
        Assert.Equal(0, changed.Merged);

        Assert.True(await ReconcileAsync() >= 1);
        Assert.Equal(0, await ReconcileAsync());

        await using (var db = CreateDbContext())
        {
            var job = Assert.Single(await JobsAsync(db));
            Assert.Equal(older, job.Id);
            Assert.Equal(2, job.Links.Count);
            Assert.False(job.IsStale);
            Assert.Equal(2, await db.JobMatches.CountAsync(m => m.JobId == older));
            Assert.Equal(0.6m, (await db.JobMatches.SingleAsync(m => m.JobId == older && m.ProfileId == profile)).Score);
            var audits = await db.AuditLogs.Where(a => a.TargetId == older && a.Action == JobMerger.MergedAction).ToListAsync();
            Assert.Contains(audits, a => a.Payload!.Contains("\"reconcile\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_company_rename_rematches_the_sources_jobs_and_merges_the_collision()
    {
        // covers AC-7
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, T0, Posting("b1", "ML Engineer", "London"));
        await IngestAsync(_ats, ats, T0, PostingFor("other-slug", "a1", "ML Engineer", "London"));

        await using (var db = CreateDbContext())
        {
            Assert.Equal(2, (await JobsAsync(db)).Count);
        }

        var renamed = await IngestAsync(_ats, ats, T0.AddHours(2), companyName: _company, PostingFor("other-slug", "a1", "ML Engineer", "London"));
        Assert.Equal(1, renamed!.Merged);
        Assert.False(renamed.ReconcileNeeded);

        await using (var db = CreateDbContext())
        {
            var job = Assert.Single(await JobsAsync(db));
            Assert.Equal(_company, job.Company);
            Assert.Equal(2, job.Links.Count);
            Assert.Equal(_company, (await db.JobSources.SingleAsync(s => s.Id == ats)).CompanyName);
            var audits = await db.AuditLogs.Where(a => a.TargetId == job.Id && a.Action == JobMerger.MergedAction).ToListAsync();
            Assert.Contains(audits, a => a.Payload!.Contains("\"rename\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_match_on_a_soft_deleted_job_revives_it()
    {
        // covers AC-11
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, T0, Posting("b1", "SRE", "Oslo"));
        await using (var db = CreateDbContext())
        {
            var job = Assert.Single(await JobsAsync(db));
            job.SoftDelete(T0);
            await db.SaveChangesAsync();
        }

        var summary = await IngestAsync(_ats, ats, T0.AddHours(1), Posting("a1", "SRE", "Oslo"));

        Assert.Equal(1, summary!.Merged);
        await using var check = CreateDbContext();
        var revived = Assert.Single(await JobsAsync(check));
        Assert.False(revived.IsDeleted);
        Assert.Equal(2, revived.Links.Count);
    }

    [Fact]
    public async Task Two_concurrent_runs_seeing_the_same_new_role_create_one_job()
    {
        // covers AC-10
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);

        for (var round = 0; round < 3; round++)
        {
            var title = $"Race Engineer {round}";
            await Task.WhenAll(
                IngestAsync(_board, board, T0, Posting($"b{round}", title, "Rome")),
                IngestAsync(_ats, ats, T0, Posting($"a{round}", title, "Rome")));
        }

        await using var db = CreateDbContext();
        var jobs = await JobsAsync(db);
        Assert.Equal(3, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(2, j.Links.Count));
    }

    [Fact]
    public async Task The_read_endpoints_show_every_source_and_link()
    {
        // covers AC-13
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, T0, Posting("b1", "Designer", "Lisbon"));
        await IngestAsync(_ats, ats, T0.AddHours(1), Posting("a1", "Designer", "Lisbon"));
        using var client = factory.CreateClient();

        var list = await client.GetFromJsonAsync<List<ListBody>>($"/internal/jobs?jobSourceId={board}");
        var item = Assert.Single(list!);
        Assert.Equal([board, ats], item.Sources.Select(s => s.JobSourceId));

        var detail = await client.GetFromJsonAsync<DetailBody>($"/internal/jobs/{item.Id}");
        Assert.Equal(2, detail!.Links.Count);
        var primary = Assert.Single(detail.Links, l => l.IsPrimary);
        Assert.Equal(("ScriptedAts", "a1"), (primary.SourceType, primary.ExternalId));
        Assert.Equal(2, detail.SnapshotCount);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task After_a_split_a_new_posting_joins_the_oldest_live_job_on_the_key()
    {
        // covers the "which job a new link joins" rule (spec 0017, Decision; AC-11)
        var board = await SeedSourceAsync(_board);
        var ats = await SeedSourceAsync(_ats);
        await IngestAsync(_board, board, T0, Posting("b1", "Data Scientist", "Madrid"));
        await IngestAsync(_ats, ats, T0.AddHours(1), Posting("a1", "Data Scientist", "Madrid"));

        Guid original, split;
        await using (var db = CreateDbContext())
        {
            var job = Assert.Single(await JobsAsync(db));
            original = job.Id;
            split = (await SplitAsync(job.Id, job.Links.Single(l => l.JobSourceId == ats).Id)).NewJobId!.Value;
        }

        // Two live jobs now share the key: a repost joins the older one (lower id).
        Assert.True(original.CompareTo(split) < 0);
        await IngestAsync(_board, board, T0.AddDays(1), Posting("b1", "Data Scientist", "Madrid"), Posting("b2", "Data Scientist", "Madrid"));
        await using (var db = CreateDbContext())
        {
            Assert.Contains((await JobsAsync(db)).Single(j => j.Id == original).Links, l => l.ExternalId == "b2");
            (await db.Jobs.SingleAsync(j => j.Id == original)).SoftDelete(T0.AddDays(1));
            await db.SaveChangesAsync();
        }

        // With the older one soft deleted, the next repost joins the live split job instead.
        await IngestAsync(_board, board, T0.AddDays(2), Posting("b3", "Data Scientist", "Madrid"));
        await using (var db = CreateDbContext())
        {
            var jobs = await JobsAsync(db);
            Assert.Contains(jobs.Single(j => j.Id == split).Links, l => l.ExternalId == "b3");
            Assert.True(jobs.Single(j => j.Id == original).IsDeleted);
        }
    }

    [Fact]
    public async Task A_unit_whose_job_changed_underneath_it_runs_again_instead_of_overwriting()
    {
        // covers the split vs ingestion race (review of spec 0017): the job's xmin
        // turns a lost update into a retry of the whole unit on fresh data.
        var board = await SeedSourceAsync(_board);
        await IngestAsync(_board, board, T0, Posting("b1", "Staff Engineer", "Vienna", "First text."));
        Guid jobId;
        await using (var db = CreateDbContext())
        {
            jobId = Assert.Single(await JobsAsync(db)).Id;
        }

        await using var unitDb = CreateDbContext();
        var repository = new JobRepository(unitDb);
        var attempts = 0;
        await repository.InTransactionAsync(async ct =>
        {
            attempts++;
            var job = await repository.GetJobAsync(jobId, ct);
            if (attempts == 1)
            {
                // Another writer commits a change to the same job after this unit read it.
                await IngestAsync(_board, board, T0.AddDays(1), Posting("b1", "Staff Engineer", "Vienna", "Second text."));
            }

            job!.SalaryRangeMin = 100_000m;
            await repository.SaveChangesAsync(ct);
            return 0;
        }, CancellationToken.None);

        Assert.Equal(2, attempts);
        await using var check = CreateDbContext();
        var saved = Assert.Single(await JobsAsync(check));
        Assert.Equal("Second text.", saved.Description);
        Assert.Equal(100_000m, saved.SalaryRangeMin);
    }

    private RawJobPosting Posting(string id, string title, string? location, string description = "Build it.") =>
        PostingFor(_company, id, title, location, description);

    private static RawJobPosting PostingFor(string company, string id, string title, string? location, string description = "Build it.") =>
        new(id, string.Empty, title, company, location, $"<p>{description}</p>", T0.AddDays(-3), string.Empty);

    private Task<JobIngestionSummary?> IngestAsync(ScriptedSource source, Guid sourceId, DateTimeOffset now, params RawJobPosting[] postings) =>
        IngestAsync(source, sourceId, now, null, postings);

    private async Task<JobIngestionSummary?> IngestAsync(ScriptedSource source, Guid sourceId, DateTimeOffset now, string? companyName, params RawJobPosting[] postings)
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        var audit = new AuditService(db);
        IJobSource[] adapters = [_board, _ats];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), audit, adapters);
        var service = new JobIngestionService(adapters, repository, merger, audit, new FixedTime(now));
        source.Next = postings.Select(source.Stamp).ToArray();
        return await service.IngestAsync(sourceId, null, companyName, CancellationToken.None);
    }

    private async Task<SplitResult> SplitAsync(Guid jobId, Guid linkId)
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        IJobSource[] adapters = [_board, _ats];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), new AuditService(db), adapters);
        return await new JobDedupService(adapters, repository, merger, new FixedTime(T0.AddDays(2))).SplitAsync(jobId, linkId, CancellationToken.None);
    }

    private async Task<int> ReconcileAsync()
    {
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        IJobSource[] adapters = [_board, _ats];
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), new AuditService(db), adapters);
        return await new JobDedupService(adapters, repository, merger, new FixedTime(T0.AddDays(2))).ReconcileAsync(CancellationToken.None);
    }

    private async Task<List<Job>> JobsAsync(WorkPilotDbContext db) =>
        await db.Jobs.IgnoreQueryFilters()
            .Include(j => j.Links)
            .Include(j => j.Snapshots)
            .Where(j => j.Links.Any(l => _sources.Contains(l.JobSourceId)))
            .AsSplitQuery()
            .ToListAsync();

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<Guid> SeedSourceAsync(ScriptedSource adapter)
    {
        await using var db = CreateDbContext();
        var source = new JobSource { Type = adapter.SourceType, Name = $"{adapter.SourceType}:{Guid.NewGuid():N}", Config = "{}" };
        db.JobSources.Add(source);
        await db.SaveChangesAsync();
        _sources.Add(source.Id);
        return source.Id;
    }

    // A source whose next fetch the test scripts. Each run's postings are held
    // per async flow, so two concurrent runs of different sources don't mix.
    private sealed class ScriptedSource(string type, decimal confidence) : IJobSource
    {
        private readonly AsyncLocal<RawJobPosting[]> _next = new();

        public RawJobPosting[] Next { set => _next.Value = value; }

        public string SourceType => type;

        public decimal ProvenanceConfidence => confidence;

        public JobSourceDefinition? Describe(string board) => new($"{type}:{board}", "{}");

        public Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RawJobPosting>>(_next.Value ?? []);

        public RawJobPosting ParseStored(JobSource source, string rawContent) =>
            JsonSerializer.Deserialize<RawJobPosting>(rawContent)! with { RawContent = rawContent };

        // Gives the posting this source's URL and its own JSON as raw content.
        public RawJobPosting Stamp(RawJobPosting posting)
        {
            var host = type == "ScriptedAts" ? "ats" : "board";
            var stamped = posting with { SourceUrl = $"https://{host}.test/{posting.ExternalId}", RawContent = string.Empty };
            return stamped with { RawContent = JsonSerializer.Serialize(stamped) };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ListSource(Guid JobSourceId, string ExternalId);

    private sealed record ListBody(Guid Id, List<ListSource> Sources);

    private sealed record DetailLink(Guid Id, string SourceType, string ExternalId, bool IsPrimary, DateTimeOffset? SplitAt);

    private sealed record DetailBody(Guid Id, int SnapshotCount, List<DetailLink> Links);
}
