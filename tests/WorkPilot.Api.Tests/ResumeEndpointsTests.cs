using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Domain.Modules.Profile;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Api.Tests;

// Integration tests for resume management (spec 0009) against the real
// Postgres: the /internal/resumes endpoints, the Postgres file store, and the
// resume_versions trigger that makes a locked version immutable even to raw
// SQL (AC-5). Each test seeds its own profiles and removes everything it wrote.
// Needs WORKPILOTDB_CONNECTION (see supabase/.env).
[Collection("Api")]
public class ResumeEndpointsTests(SharedApiFactory factory) : IAsyncLifetime
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% test resume\n%%EOF\n");
    private static readonly byte[] DocxBytes = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5];

    private readonly HttpClient _client = factory.CreateClient();
    private Guid _profileA;
    private Guid _profileB;

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        var a = new WorkPilot.Domain.Modules.Profile.Profile { AuthUserId = Guid.NewGuid(), Name = "Resume Tester A" };
        var b = new WorkPilot.Domain.Modules.Profile.Profile { AuthUserId = Guid.NewGuid(), Name = "Resume Tester B" };
        db.Profiles.AddRange(a, b);
        await db.SaveChangesAsync();
        (_profileA, _profileB) = (a.Id, b.Id);
    }

    public async Task DisposeAsync()
    {
        // Locked versions can't be deleted (that is the point of AC-5), so the
        // cleanup turns triggers off for its own transaction only.
        await using var db = CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        Guid[] profiles = [_profileA, _profileB];
        await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM app.resume_files f USING app.resume_versions v, app.resumes r
            WHERE v."ResumeId" = r."Id" AND r."ProfileId" = ANY({0}) AND v."StorageUrl" = 'db:' || f."Id"::text
            """,
            profiles);
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM app.resume_versions WHERE "ResumeId" IN (SELECT "Id" FROM app.resumes WHERE "ProfileId" = ANY({0}))""",
            profiles);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM app.resumes WHERE "ProfileId" = ANY({0})""", profiles);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM app.profiles WHERE "Id" = ANY({0})""", profiles);
        await tx.CommitAsync();
        _client.Dispose();
    }

    [Fact]
    public async Task Create_MakesABaseResumeWithAnUnlockedDraftV1_ThatTheListShows()
    {
        // covers AC-1
        var response = await CreateAsync(_profileA, "General resume", "Experience: C#", "first", ("resume.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ResumeDetailDto>())!;
        Assert.Equal($"/internal/resumes/{created.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Base", created.Kind);
        var v1 = Assert.Single(created.Versions);
        Assert.Equal(1, v1.VersionNumber);
        Assert.False(v1.IsLocked);
        Assert.Equal("resume.pdf", v1.FileName);
        Assert.Equal("application/pdf", v1.FileContentType);
        Assert.Equal(PdfBytes.Length, v1.FileSizeBytes);

        var list = await _client.GetFromJsonAsync<List<ResumeSummaryDto>>($"/internal/resumes?profileId={_profileA}");
        var summary = Assert.Single(list!);
        Assert.Equal((created.Id, "General resume", 1, false, 1), (summary.Id, summary.Name, summary.LatestVersionNumber, summary.LatestIsLocked, summary.VersionCount));
    }

    [Fact]
    public async Task List_WithoutAProfileId_Returns400()
    {
        // covers AC-9
        var response = await _client.GetAsync("/internal/resumes");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Revise_OnADraft_UpdatesItInPlace()
    {
        // covers AC-2
        var created = await CreateOkAsync(_profileA);
        var v1 = created.Versions[0];

        var result = await ReviseOkAsync(_profileA, created.Id, "Experience: C#, Postgres", "added Postgres");

        Assert.Equal(("UpdatedDraft", 1), (result.Outcome, result.VersionNumber));
        var stored = Assert.Single(result.Resume.Versions);
        Assert.Equal(v1.Id, stored.Id);
        Assert.Equal("Experience: C#, Postgres", stored.Content);
        Assert.Equal(Micros(v1.CreatedAt), Micros(stored.CreatedAt));
        Assert.True(stored.UpdatedAt >= v1.UpdatedAt);
        Assert.Equal(1, await CountVersionsAsync(created.Id));
    }

    [Fact]
    public async Task Lock_SetsTheApplication_AndLockingAgainKeepsTheFirstOne()
    {
        // covers AC-3
        var created = await CreateOkAsync(_profileA);
        var versionId = created.Versions[0].Id;
        var appA = Guid.NewGuid();

        var first = await LockAsync(_profileA, versionId, appA);
        var second = await LockAsync(_profileA, versionId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var locked = (await first.Content.ReadFromJsonAsync<ResumeVersionDto>())!;
        Assert.True(locked.IsLocked);
        Assert.Equal(appA, locked.LockedByApplicationId);
        var again = (await second.Content.ReadFromJsonAsync<ResumeVersionDto>())!;
        Assert.Equal(appA, again.LockedByApplicationId);
        Assert.Equal(Micros(locked.LockedAt!.Value), Micros(again.LockedAt!.Value));
    }

    [Fact]
    public async Task Lock_WithAnEmptyApplicationId_Returns400()
    {
        // covers AC-3, AC-8
        var created = await CreateOkAsync(_profileA);

        var response = await LockAsync(_profileA, created.Versions[0].Id, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Revise_AfterALock_CreatesVersionTwoCarryingTheFile_AndTheLockedRowIsUnchanged()
    {
        // covers AC-4
        var created = await CreateOkAsync(_profileA, file: ("resume.pdf", PdfBytes));
        var v1Id = created.Versions[0].Id;
        await LockAsync(_profileA, v1Id, Guid.NewGuid());
        var before = await RowFingerprintAsync(v1Id);

        var result = await ReviseOkAsync(_profileA, created.Id, "Rewritten", "v2");

        Assert.Equal(("CreatedVersion", 2), (result.Outcome, result.VersionNumber));
        Assert.Equal([2, 1], result.Resume.Versions.Select(v => v.VersionNumber));
        Assert.Equal("resume.pdf", result.Resume.Versions[0].FileName);
        Assert.Equal(before, await RowFingerprintAsync(v1Id));
        Assert.Equal(PdfBytes, await DownloadAsync(_profileA, result.Resume.Versions[0].Id));
    }

    [Fact]
    public async Task RawSql_CannotUpdateOrDeleteALockedVersion()
    {
        // covers AC-5: the trigger holds even when the app is bypassed
        var created = await CreateOkAsync(_profileA);
        var v1Id = created.Versions[0].Id;
        await LockAsync(_profileA, v1Id, Guid.NewGuid());
        await using var db = CreateDbContext();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""UPDATE app.resume_versions SET "Content" = 'tampered' WHERE "Id" = {0}""", v1Id));
        var unlock = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""UPDATE app.resume_versions SET "LockedAt" = NULL WHERE "Id" = {0}""", v1Id));
        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("""DELETE FROM app.resume_versions WHERE "Id" = {0}""", v1Id));

        Assert.All([update, unlock, delete], ex => Assert.Equal("WP409", ex.SqlState));
        Assert.Equal("Experience: C#", (await GetOkAsync(_profileA, created.Id)).Versions[0].Content);
    }

    [Fact]
    public async Task RawSql_CanStillEditAnUnlockedDraft()
    {
        // covers AC-5: the trigger only guards locked rows
        var created = await CreateOkAsync(_profileA);
        await using var db = CreateDbContext();

        var rows = await db.Database.ExecuteSqlRawAsync(
            """UPDATE app.resume_versions SET "Note" = 'edited' WHERE "Id" = {0}""", created.Versions[0].Id);

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Revise_ThatLosesARaceToALock_Returns409AndLeavesTheLockedVersionUnchanged()
    {
        // covers AC-5: the service still believes v1 is a draft, but it was locked meanwhile
        var created = await CreateOkAsync(_profileA);
        var v1Id = created.Versions[0].Id;
        using var scope = factory.Services.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IResumeService>();
        await scopedDb.Resumes.Include(r => r.Versions).FirstAsync(r => r.Id == created.Id); // tracked as an unlocked draft
        await LockAsync(_profileA, v1Id, Guid.NewGuid());

        var result = await service.ReviseAsync(
            new ReviseResumeCommand(_profileA, created.Id, "raced edit", null, null, false), CancellationToken.None);

        Assert.Equal(ResumeResultStatus.Conflict, result.Status);
        var stored = (await GetOkAsync(_profileA, created.Id)).Versions.Single();
        Assert.Equal("Experience: C#", stored.Content);
        Assert.True(stored.IsLocked);
    }

    [Fact]
    public async Task Lock_ThatLosesARaceToAnotherLock_KeepsTheFirstApplication()
    {
        // covers AC-3, AC-5: the service still believes v1 is unlocked, but another application locked it meanwhile
        var created = await CreateOkAsync(_profileA);
        var v1Id = created.Versions[0].Id;
        using var scope = factory.Services.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<WorkPilotDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IResumeService>();
        await scopedDb.ResumeVersions.FirstAsync(v => v.Id == v1Id); // tracked as unlocked
        var winner = Guid.NewGuid();
        await LockAsync(_profileA, v1Id, winner);

        var result = await service.LockVersionAsync(_profileA, v1Id, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ResumeResultStatus.Ok, result.Status);
        Assert.Equal(winner, result.Value!.LockedByApplicationId);
        Assert.Equal(winner, (await GetOkAsync(_profileA, created.Id)).Versions.Single().LockedByApplicationId);
    }

    [Fact]
    public async Task Tailor_CopiesTheSourceVersionIntoASeparateResume_AndLeavesTheSourceUnchanged()
    {
        // covers AC-6
        var source = await CreateOkAsync(_profileA, file: ("resume.docx", DocxBytes));
        var sourceVersion = source.Versions[0];
        await LockAsync(_profileA, sourceVersion.Id, Guid.NewGuid());
        var before = await RowFingerprintAsync(sourceVersion.Id);

        var response = await TailorAsync(_profileA, sourceVersion.Id, "Acme resume", "Acme");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tailored = (await response.Content.ReadFromJsonAsync<ResumeDetailDto>())!;
        Assert.NotEqual(source.Id, tailored.Id);
        Assert.Equal(("Tailored", "Acme", sourceVersion.Id), (tailored.Kind, tailored.TargetCompany, tailored.SourceVersionId));
        var v1 = Assert.Single(tailored.Versions);
        Assert.False(v1.IsLocked);
        Assert.Equal(sourceVersion.Content, v1.Content);
        Assert.Equal(DocxBytes, await DownloadAsync(_profileA, v1.Id));
        Assert.Equal(before, await RowFingerprintAsync(sourceVersion.Id));
        Assert.Single((await GetOkAsync(_profileA, source.Id)).Versions);
    }

    [Fact]
    public async Task Get_ListsEveryVersionNewestFirst_WithLockDetails_AndEachFileDownloads()
    {
        // covers AC-7
        var created = await CreateOkAsync(_profileA, file: ("resume.pdf", PdfBytes));
        var app1 = Guid.NewGuid();
        await LockAsync(_profileA, created.Versions[0].Id, app1);
        await ReviseOkAsync(_profileA, created.Id, "v2 text", "second", ("resume.docx", DocxBytes));

        var detail = await GetOkAsync(_profileA, created.Id);

        Assert.Equal([2, 1], detail.Versions.Select(v => v.VersionNumber));
        Assert.Equal(("second", "resume.docx", false), (detail.Versions[0].Note, detail.Versions[0].FileName, detail.Versions[0].IsLocked));
        Assert.Equal(("first", "resume.pdf", true, app1), (detail.Versions[1].Note, detail.Versions[1].FileName, detail.Versions[1].IsLocked, detail.Versions[1].LockedByApplicationId));
        Assert.Equal(DocxBytes, await DownloadAsync(_profileA, detail.Versions[0].Id));
        Assert.Equal(PdfBytes, await DownloadAsync(_profileA, detail.Versions[1].Id));
    }

    [Fact]
    public async Task Download_ServesTheFileAsAnAttachment()
    {
        // covers AC-7
        var created = await CreateOkAsync(_profileA, file: ("resume.pdf", PdfBytes));

        var response = await _client.GetAsync($"/internal/resumes/versions/{created.Versions[0].Id}/file?profileId={_profileA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("resume.pdf", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Download_ForAVersionWithNoFile_Returns404()
    {
        // covers AC-7
        var created = await CreateOkAsync(_profileA);

        var response = await _client.GetAsync($"/internal/resumes/versions/{created.Versions[0].Id}/file?profileId={_profileA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<string, string, string, string?, int> InvalidCreates => new()
    {
        { "blank name", " ", "text", null, 0 },
        { "name too long", new string('n', 201), "text", null, 0 },
        { "content too long", "name", new string('c', 100_001), null, 0 },
        { "note too long", "name", "text", new string('o', 501), 0 },
        { "no content and no file", "name", "  ", null, 0 },
        { "bad file type", "name", "text", null, 1 },
        { "file too large", "name", "text", null, 2 },
    };

    [Theory]
    [MemberData(nameof(InvalidCreates))]
    public async Task Create_WithInvalidInput_Returns400AndWritesNothing(string _, string name, string content, string? note, int fileCase)
    {
        // covers AC-8
        (string, byte[])? file = fileCase switch
        {
            1 => ("resume.exe", PdfBytes),
            2 => ("resume.pdf", new byte[ResumeRules.FileMaxBytes + 1]),
            _ => null,
        };
        var filesBefore = await CountFilesAsync();

        var response = await CreateAsync(_profileA, name, content, note, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<List<ResumeSummaryDto>>($"/internal/resumes?profileId={_profileA}"))!);
        Assert.Equal(filesBefore, await CountFilesAsync());
    }

    [Fact]
    public async Task Create_AcceptsAFileOfExactlyFiveMegabytes()
    {
        // covers AC-8 boundary
        var response = await CreateAsync(_profileA, "name", "", null, ("resume.pdf", new byte[ResumeRules.FileMaxBytes]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_AndRevise_WithABodyOverTheUploadLimit_Return413AndWriteNothing()
    {
        // covers AC-8: an oversized upload is refused before it is read, not after
        var created = await CreateOkAsync(_profileA);
        var tooBig = ("resume.pdf", new byte[WorkPilot.Api.Endpoints.ResumeEndpoints.MaxUploadRequestBytes + 1]);
        var filesBefore = await CountFilesAsync();

        var create = await CreateAsync(_profileA, "name", "text", null, tooBig);
        var revise = await PostReviseAsync(_profileA, created.Id, "text", null, tooBig);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, create.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, revise.StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<List<ResumeSummaryDto>>($"/internal/resumes?profileId={_profileA}"))!);
        Assert.Equal(filesBefore, await CountFilesAsync());
        Assert.Equal(1, await CountVersionsAsync(created.Id));
    }

    [Fact]
    public async Task Create_ForAnUnknownProfile_Returns404()
    {
        // covers AC-9
        var response = await CreateAsync(Guid.NewGuid(), "name", "text", null, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EveryOperationOnAnotherProfilesResume_Returns404()
    {
        // covers AC-9
        var created = await CreateOkAsync(_profileA, file: ("resume.pdf", PdfBytes));
        var versionId = created.Versions[0].Id;

        var get = await _client.GetAsync($"/internal/resumes/{created.Id}?profileId={_profileB}");
        var revise = await PostReviseAsync(_profileB, created.Id, "hijack", null, null);
        var lockIt = await LockAsync(_profileB, versionId, Guid.NewGuid());
        var download = await _client.GetAsync($"/internal/resumes/versions/{versionId}/file?profileId={_profileB}");
        var tailor = await TailorAsync(_profileB, versionId, "stolen", "Acme");
        var listB = await _client.GetFromJsonAsync<List<ResumeSummaryDto>>($"/internal/resumes?profileId={_profileB}");

        Assert.All([get, revise, lockIt, download, tailor], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Empty(listB!);
        var stored = (await GetOkAsync(_profileA, created.Id)).Versions.Single();
        Assert.Equal(("Experience: C#", false), (stored.Content, stored.IsLocked));
    }

    [Fact]
    public async Task Get_AnUnknownResume_Returns404()
    {
        // covers AC-9
        var response = await _client.GetAsync($"/internal/resumes/{Guid.NewGuid()}?profileId={_profileA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revise_IdenticalToTheNewestVersion_IsANoOp_OnADraftAndOnALockedVersion()
    {
        // covers AC-10
        var created = await CreateOkAsync(_profileA);
        var before = await RowFingerprintAsync(created.Versions[0].Id);

        var onDraft = await ReviseOkAsync(_profileA, created.Id, "Experience: C#", "first");
        Assert.Equal(("Unchanged", 1), (onDraft.Outcome, onDraft.VersionNumber));
        Assert.Equal(before, await RowFingerprintAsync(created.Versions[0].Id));

        await LockAsync(_profileA, created.Versions[0].Id, Guid.NewGuid());
        var onLocked = await ReviseOkAsync(_profileA, created.Id, "Experience: C#", "first");
        Assert.Equal(("Unchanged", 1), (onLocked.Outcome, onLocked.VersionNumber));
        Assert.Equal(1, await CountVersionsAsync(created.Id));
    }

    // Postgres keeps microseconds; a write's response carries the in memory 100 ns value.
    private static long Micros(DateTimeOffset value) => value.UtcTicks / 10;

    private WorkPilotDbContext CreateDbContext() =>
        factory.Services.CreateScope().ServiceProvider.GetRequiredService<WorkPilotDbContext>();

    private async Task<HttpResponseMessage> CreateAsync(Guid profileId, string name, string content, string? note, (string Name, byte[] Bytes)? file)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(profileId.ToString()), "profileId" },
            { new StringContent(name), "name" },
            { new StringContent(content), "content" },
        };
        if (note is not null)
        {
            form.Add(new StringContent(note), "note");
        }

        AddFile(form, file);
        return await _client.PostAsync("/internal/resumes", form);
    }

    private async Task<ResumeDetailDto> CreateOkAsync(Guid profileId, (string, byte[])? file = null)
    {
        var response = await CreateAsync(profileId, "General resume", "Experience: C#", "first", file);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResumeDetailDto>())!;
    }

    private async Task<HttpResponseMessage> PostReviseAsync(Guid profileId, Guid resumeId, string content, string? note, (string, byte[])? file)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(profileId.ToString()), "profileId" },
            { new StringContent(content), "content" },
        };
        if (note is not null)
        {
            form.Add(new StringContent(note), "note");
        }

        AddFile(form, file);
        return await _client.PostAsync($"/internal/resumes/{resumeId}/revisions", form);
    }

    private async Task<ReviseResumeResultDto> ReviseOkAsync(Guid profileId, Guid resumeId, string content, string? note, (string, byte[])? file = null)
    {
        var response = await PostReviseAsync(profileId, resumeId, content, note, file);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReviseResumeResultDto>())!;
    }

    private Task<HttpResponseMessage> LockAsync(Guid profileId, Guid versionId, Guid applicationId) =>
        _client.PostAsJsonAsync($"/internal/resumes/versions/{versionId}/lock", new { profileId, applicationId });

    private Task<HttpResponseMessage> TailorAsync(Guid profileId, Guid sourceVersionId, string name, string targetCompany) =>
        _client.PostAsJsonAsync("/internal/resumes/tailored", new { profileId, sourceVersionId, name, targetCompany });

    private async Task<ResumeDetailDto> GetOkAsync(Guid profileId, Guid resumeId) =>
        (await _client.GetFromJsonAsync<ResumeDetailDto>($"/internal/resumes/{resumeId}?profileId={profileId}"))!;

    private async Task<byte[]> DownloadAsync(Guid profileId, Guid versionId) =>
        await _client.GetByteArrayAsync($"/internal/resumes/versions/{versionId}/file?profileId={profileId}");

    private static void AddFile(MultipartFormDataContent form, (string Name, byte[] Bytes)? file)
    {
        if (file is { } f)
        {
            var part = new ByteArrayContent(f.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", f.Name);
        }
    }

    // The whole row as text, so "unchanged" means every column, byte for byte.
    private async Task<string> RowFingerprintAsync(Guid versionId)
    {
        await using var db = CreateDbContext();
        return await db.Database
            .SqlQueryRaw<string>("""SELECT md5(v::text) AS "Value" FROM app.resume_versions v WHERE v."Id" = {0}""", versionId)
            .SingleAsync();
    }

    private async Task<int> CountVersionsAsync(Guid resumeId)
    {
        await using var db = CreateDbContext();
        return await db.ResumeVersions.CountAsync(v => v.ResumeId == resumeId);
    }

    private async Task<int> CountFilesAsync()
    {
        await using var db = CreateDbContext();
        return await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM app.resume_files""").SingleAsync();
    }
}
