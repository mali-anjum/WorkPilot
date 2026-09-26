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

// Integration tests for job source ingestion (spec 0008) against the real
// Postgres. The ingestion use case runs with the real repository and audit
// service but a scripted IJobSource: a live third party board changing its
// postings must not make the suite flaky (the live Greenhouse run is in
// docs/specs/0008-job-source-ingestion/verify.md). Hangfire's worker is
// disabled in the test host, so the endpoint tests cover the synchronous
// trigger and read paths. Needs WORKPILOTDB_CONNECTION (see supabase/.env).
[Collection("Api")]
public class JobIngestionTests(SharedApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // Unique per test: jobs with the same company, title and location merge
    // (spec 0017), so no test may share one with another test or old data.
    private readonly string _company = $"Acme {Guid.NewGuid():N}";

    [Fact]
    public async Task Ingest_StoresCanonicalJobsWithProvenanceSnapshotsAndOneAuditRow()
    {
        // covers AC-2, AC-5
        var source = new ScriptedSource(Posting("1", "Backend Engineer"), Posting("2", "Platform Engineer", location: "Remote, EU"));
        var sourceId = await SeedSourceAsync();

        try
        {
            var summary = await IngestAsync(source, sourceId, keywords: null, T0);

            Assert.Equal(new JobIngestionSummary(sourceId, 2, 2, 2, 0, 0, 0, 0), summary);

            await using var db = CreateDbContext();
            var jobs = await JobsOfAsync(db, sourceId);
            Assert.Equal(["1", "2"], jobs.Select(j => j.Links.Single().ExternalId));
            Assert.All(jobs, j =>
            {
                Assert.Equal($"https://boards.test/jobs/{j.Links.Single().ExternalId}", j.Provenance.SourceUrl);
                Assert.Equal(T0, j.Provenance.RetrievedAt);
                Assert.Equal(T0, j.Provenance.VerifiedAt);
                Assert.Equal(0.9m, j.Provenance.Confidence);
                Assert.Equal("Build it.", j.Description);
                var snapshot = Assert.Single(j.Snapshots);
                Assert.Equal(sourceId, snapshot.JobSourceId);
                Assert.Equal(j.Links.Single().ExternalId, snapshot.ExternalId);
                Assert.Equal(T0, snapshot.Provenance.RetrievedAt);
                Assert.Equal(T0, snapshot.Provenance.VerifiedAt);
            });
            Assert.Equal(RemoteTypes.Remote, jobs[1].RemoteType);

            var audit = Assert.Single(await db.AuditLogs.Where(a => a.TargetId == sourceId).ToListAsync());
            Assert.Equal(("Agent", "JobsIngested", "JobSource"), (audit.Actor, audit.Action, audit.TargetType));
            using var payload = JsonDocument.Parse(audit.Payload!);
            Assert.Equal(2, payload.RootElement.GetProperty("created").GetInt32());
        }
        finally
        {
            await CleanupAsync(sourceId);
        }
    }

    [Fact]
    public async Task Ingest_AppliesTheKeywordFilterAndSkipsUnusablePostings()
    {
        // covers AC-2, AC-6: a posting missing its title, or repeating an id, is skipped and counted
        var source = new ScriptedSource(
            Posting("1", "Backend Engineer"),
            Posting("2", "Sales Manager"),
            Posting("3", "  "),
            Posting("1", "Backend Engineer"));
        var sourceId = await SeedSourceAsync();

        try
        {
            var summary = await IngestAsync(source, sourceId, keywords: "engineer", T0);

            // "  " matches no keyword either, so only the two "Backend Engineer" copies match; the second is a repeat.
            Assert.Equal(new JobIngestionSummary(sourceId, 4, 2, 1, 0, 0, 1, 0), summary);

            var summaryAll = await IngestAsync(new ScriptedSource(Posting("3", "  ")), sourceId, keywords: null, T0);
            Assert.Equal(new JobIngestionSummary(sourceId, 1, 1, 0, 0, 0, 1, 0), summaryAll);

            await using var db = CreateDbContext();
            Assert.Equal(1, await db.Jobs.CountAsync(j => j.Links.Any(l => l.JobSourceId == sourceId)));
        }
        finally
        {
            await CleanupAsync(sourceId);
        }
    }

    [Fact]
    public async Task Ingest_TwiceIsIdempotent_AndChangedContentAddsASnapshot()
    {
        // covers AC-4
        var sourceId = await SeedSourceAsync();
        var t1 = T0.AddHours(6);
        var t2 = T0.AddDays(1);

        try
        {
            await IngestAsync(new ScriptedSource(Posting("1", "Backend Engineer"), Posting("2", "Data Engineer")), sourceId, null, T0);

            var again = await IngestAsync(new ScriptedSource(Posting("1", "Backend Engineer"), Posting("2", "Data Engineer")), sourceId, null, t1);
            Assert.Equal(new JobIngestionSummary(sourceId, 2, 2, 0, 0, 2, 0, 0), again);

            var changed = await IngestAsync(new ScriptedSource(Posting("1", "Staff Backend Engineer"), Posting("2", "Data Engineer")), sourceId, null, t2);
            // The new title is a new match key, so the job goes stale for the reconcile (spec 0017).
            Assert.Equal(new JobIngestionSummary(sourceId, 2, 2, 0, 1, 1, 0, 0) { ReconcileNeeded = true }, changed);

            await using var db = CreateDbContext();
            var jobs = await JobsOfAsync(db, sourceId);
            Assert.Equal(2, jobs.Count);

            var updated = jobs[0];
            Assert.Equal("Staff Backend Engineer", updated.Title);
            Assert.Equal(2, updated.Snapshots.Count);
            Assert.Equal(T0, updated.Provenance.RetrievedAt);
            Assert.Equal(t2, updated.Provenance.VerifiedAt);

            var unchanged = jobs[1];
            var snapshot = Assert.Single(unchanged.Snapshots);
            Assert.Equal(T0, snapshot.Provenance.RetrievedAt);
            Assert.Equal(t2, snapshot.Provenance.VerifiedAt);
            Assert.Equal(t2, unchanged.Provenance.VerifiedAt);

            Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.TargetId == sourceId));
        }
        finally
        {
            await CleanupAsync(sourceId);
        }
    }

    [Fact]
    public async Task Ingest_WhenTheSourceFails_StoresNothing()
    {
        // covers AC-6: the exception propagates (so Hangfire retries) and nothing partial is saved
        var sourceId = await SeedSourceAsync();

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => IngestAsync(new ScriptedSource(), sourceId, null, T0, fail: true));

            await using var db = CreateDbContext();
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.Links.Any(l => l.JobSourceId == sourceId)));
            Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.TargetId == sourceId));
        }
        finally
        {
            await CleanupAsync(sourceId);
        }
    }

    [Fact]
    public async Task Ingest_ForAnUnknownSource_ReturnsNull()
    {
        Assert.Null(await IngestAsync(new ScriptedSource(), Guid.NewGuid(), null, T0));
    }

    [Theory]
    [InlineData("""{"source":"linkedin","boardToken":"acme"}""")]
    [InlineData("""{"source":"greenhouse","boardToken":"../evil.test/x"}""")]
    [InlineData("""{"source":"greenhouse","boardToken":""}""")]
    [InlineData("""{"source":"greenhouse"}""")]
    [InlineData("""{"source":"lever","boardToken":"../evil.test/x"}""")]
    [InlineData("""{"source":"greenhouse","boardToken":"acme","companyName":"   "}""")]
    [InlineData("""{"boardToken":"acme"}""")]
    public async Task TriggerIngestion_WithAnUnknownSourceOrBadBoard_Returns400(string body)
    {
        // covers AC-1
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/internal/jobs/ingestions", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TriggerIngestion_RejectsACompanyNameOver200Chars_AndAcceptsALeverSite()
    {
        // covers spec 0017, AC-6, AC-7: the name is validated before anything is stored
        using var client = factory.CreateClient();
        var site = $"wp-test-{Guid.NewGuid():N}"[..30];

        var tooLong = await client.PostAsJsonAsync("/internal/jobs/ingestions", new { Source = "lever", BoardToken = site, CompanyName = new string('x', 201) });
        var lever = await client.PostAsJsonAsync("/internal/jobs/ingestions", new { Source = "lever", BoardToken = site, CompanyName = new string('x', 200) });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, lever.StatusCode);
        var body = await lever.Content.ReadFromJsonAsync<TriggerBody>();
        try
        {
            await using var db = CreateDbContext();
            var source = await db.JobSources.SingleAsync(s => s.Id == body!.JobSourceId);
            Assert.Equal(("Lever", $"lever:{site}"), (source.Type, source.Name));

            // The name is applied by the ingestion job, not stored by the trigger.
            Assert.Null(source.CompanyName);
        }
        finally
        {
            await CleanupAsync(body!.JobSourceId);
        }
    }

    [Fact]
    public async Task TriggerIngestion_FindsOrCreatesTheSourceAndEnqueuesAJob()
    {
        // covers AC-1: the same board (any case) maps to one JobSource row
        using var client = factory.CreateClient();
        var token = $"wp-test-{Guid.NewGuid():N}"[..30];

        var first = await client.PostAsJsonAsync("/internal/jobs/ingestions", new { Source = "greenhouse", BoardToken = token, Keywords = "engineer" });
        var second = await client.PostAsJsonAsync("/internal/jobs/ingestions", new { Source = "Greenhouse", BoardToken = token.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<TriggerBody>();
        var b = await second.Content.ReadFromJsonAsync<TriggerBody>();
        Assert.Equal(a!.JobSourceId, b!.JobSourceId);
        Assert.False(string.IsNullOrEmpty(a.BackgroundJobId));
        Assert.NotEqual(a.BackgroundJobId, b.BackgroundJobId);

        try
        {
            await using var db = CreateDbContext();
            var source = await db.JobSources.SingleAsync(s => s.Id == a.JobSourceId);
            Assert.Equal(("Greenhouse", $"greenhouse:{token}"), (source.Type, source.Name));
            Assert.Contains(token, source.Config);
        }
        finally
        {
            await CleanupAsync(a.JobSourceId);
        }
    }

    [Fact]
    public async Task ListJobs_ReturnsJobsWithProvenance_AndCapsTake()
    {
        // covers AC-7
        var sourceId = await SeedSourceAsync();

        try
        {
            await IngestAsync(new ScriptedSource(Posting("1", "Backend Engineer"), Posting("2", "Data Engineer")), sourceId, null, T0);
            using var client = factory.CreateClient();

            var all = await client.GetFromJsonAsync<List<JobBody>>($"/internal/jobs?jobSourceId={sourceId}");
            var one = await client.GetFromJsonAsync<List<JobBody>>($"/internal/jobs?jobSourceId={sourceId}&take=1");

            Assert.Equal(2, all!.Count);
            Assert.Single(one!);
            Assert.All(all, j =>
            {
                Assert.Equal($"https://boards.test/jobs/{Assert.Single(j.Sources).ExternalId}", j.SourceUrl);
                Assert.Equal(T0, j.RetrievedAt);
                Assert.Equal(T0, j.VerifiedAt);
                Assert.Equal(1, j.SnapshotCount);
            });
        }
        finally
        {
            await CleanupAsync(sourceId);
        }
    }

    [Fact]
    public async Task ListJobs_ForAnUnknownSource_Returns404()
    {
        // covers AC-7
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/jobs?jobSourceId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private RawJobPosting Posting(string id, string title, string? location = "Berlin") =>
        ScriptedSource.Posting(id, title, _company, location, T0.AddDays(-3));

    private static async Task<List<Job>> JobsOfAsync(WorkPilotDbContext db, Guid sourceId) =>
        (await db.Jobs.Include(j => j.Links).Include(j => j.Snapshots).Where(j => j.Links.Any(l => l.JobSourceId == sourceId)).ToListAsync())
            .OrderBy(j => j.Links.Min(l => l.ExternalId), StringComparer.Ordinal)
            .ToList();

    private async Task<JobIngestionSummary?> IngestAsync(ScriptedSource source, Guid sourceId, string? keywords, DateTimeOffset now, bool fail = false)
    {
        source.Fail = fail;
        await using var db = CreateDbContext();
        var repository = new JobRepository(db);
        var audit = new AuditService(db);
        var merger = new JobMerger(repository, new JobApplicationReassigner(db), audit, [source]);
        var service = new JobIngestionService([source], repository, merger, audit, new FixedTime(now));
        return await service.IngestAsync(sourceId, keywords, null, CancellationToken.None);
    }

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<Guid> SeedSourceAsync()
    {
        await using var db = CreateDbContext();
        var source = new JobSource { Type = ScriptedSource.Type, Name = $"scripted:{Guid.NewGuid():N}", Config = "{}" };
        db.JobSources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private async Task CleanupAsync(Guid sourceId)
    {
        await using var db = CreateDbContext();
        await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TargetId == sourceId).ExecuteDeleteAsync();
        // Deleting a job cascades to its links and snapshots.
        await db.Jobs.IgnoreQueryFilters().Where(j => j.Links.Any(l => l.JobSourceId == sourceId)).ExecuteDeleteAsync();
        await db.JobSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
    }

    // A job source whose postings the test scripts; can also fail like a dead board.
    private sealed class ScriptedSource(params RawJobPosting[] postings) : IJobSource
    {
        public const string Type = "Scripted";

        public bool Fail { get; set; }

        public string SourceType => Type;

        public decimal ProvenanceConfidence => 0.9m;

        public JobSourceDefinition? Describe(string board) => new($"scripted:{board}", "{}");

        public Task<IReadOnlyList<RawJobPosting>> FetchAsync(JobSource source, CancellationToken cancellationToken) =>
            Fail
                ? throw new HttpRequestException("board is down")
                : Task.FromResult<IReadOnlyList<RawJobPosting>>(postings);

        // The raw content is the posting itself as JSON, so it round trips.
        public RawJobPosting ParseStored(JobSource source, string rawContent) =>
            JsonSerializer.Deserialize<RawJobPosting>(rawContent)! with { RawContent = rawContent };

        public static RawJobPosting Posting(string id, string title, string company, string? location, DateTimeOffset postedAt)
        {
            var posting = new RawJobPosting(id, $"https://boards.test/jobs/{id}", title, company, location, "<p>Build it.</p>", postedAt, string.Empty);
            return posting with { RawContent = JsonSerializer.Serialize(posting) };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TriggerBody(Guid JobSourceId, string BackgroundJobId);

    private sealed record JobBody(string SourceUrl, DateTimeOffset RetrievedAt, DateTimeOffset? VerifiedAt, int SnapshotCount, List<SourceBody> Sources);

    private sealed record SourceBody(Guid JobSourceId, string ExternalId);
}
