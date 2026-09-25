using System.Net;
using System.Security.Claims;
using System.Text;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages;
using WorkPilot.Web.Features.Resumes;

namespace WorkPilot.Web.Tests;

// The /resumes and /resumes/{id} pages (spec 0009, AC-1, AC-2, AC-4, AC-6,
// AC-7, AC-9), rendered with a fake IResumesApiClient standing in for the
// internal Api and a signed in session carrying the profile_id claim. The
// real Api behavior is covered in Api.Tests/ResumeEndpointsTests.
public class ResumesTests : TestContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");
    private static readonly DateTimeOffset At = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeResumesApiClient _api = new();

    public ResumesTests()
    {
        Services.AddSingleton<IResumesApiClient>(_api);
    }

    private void SignIn(Guid? profileId = null)
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, (profileId ?? ProfileId).ToString()));
    }

    private static ResumeVersionDto Version(int number, bool locked = false, string? fileName = null, string content = "Experience: C#", string? note = null, Guid? lockedBy = null) =>
        new(Guid.NewGuid(), Guid.Empty, number, content, note, fileName, fileName is null ? null : "application/pdf", fileName is null ? null : 20_125,
            At.AddDays(number), At.AddDays(number), locked, locked ? At.AddDays(number).AddHours(1) : null, locked ? lockedBy ?? Guid.NewGuid() : null);

    private static ResumeDetailDto Detail(params ResumeVersionDto[] newestFirst) =>
        new(Guid.NewGuid(), "General resume", "Base", null, null, At, newestFirst);

    // covers: AC-1
    [Fact]
    public void List_shows_the_empty_state_when_there_are_no_resumes()
    {
        SignIn();

        var cut = RenderComponent<Resumes>();

        Assert.Equal("No resumes yet", cut.Find(".wp-empty-state__title").TextContent);
        Assert.Equal([ProfileId], _api.ListedFor);
    }

    // covers: AC-1
    [Fact]
    public void List_shows_each_resume_with_its_kind_version_and_lock_state()
    {
        SignIn();
        var baseId = Guid.NewGuid();
        _api.Summaries =
        [
            new(baseId, "General resume", "Base", null, 2, true, 2, At),
            new(Guid.NewGuid(), "Acme resume", "Tailored", "Acme", 1, false, 1, At),
        ];

        var cut = RenderComponent<Resumes>();

        var items = cut.FindAll(".wp-resumes__item");
        Assert.Equal(2, items.Count);
        Assert.Equal($"/resumes/{baseId}", items[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("v2 · 2 version(s)", items[0].TextContent);
        Assert.Contains("Locked", items[0].TextContent);
        Assert.Contains("for Acme", items[1].TextContent);
        Assert.Contains("Draft", items[1].TextContent);
    }

    // covers: AC-1
    [Fact]
    public void Create_sends_the_form_for_the_signed_in_profile_and_opens_the_new_resume()
    {
        SignIn();
        var created = Detail(Version(1));
        _api.CreateResult = new(created, null);
        var cut = RenderComponent<Resumes>();

        cut.Find("input[required]").Change("General resume");
        cut.Find("textarea").Change("Experience: C#");
        cut.Find("form").Submit();

        Assert.Equal((ProfileId, "General resume", "Experience: C#"), _api.Created);
        Assert.EndsWith($"/resumes/{created.Id}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    // covers: AC-8: an Api validation message reaches the user
    [Fact]
    public void Create_shows_the_error_when_the_api_rejects_it()
    {
        SignIn();
        _api.CreateResult = new(null, "The name must be 1 to 200 characters.");
        var cut = RenderComponent<Resumes>();

        cut.Find("form").Submit();

        Assert.Equal("The name must be 1 to 200 characters.", cut.Find("[role=alert]").TextContent);
        Assert.EndsWith("/", Services.GetRequiredService<NavigationManager>().Uri);
    }

    // covers: AC-9
    [Fact]
    public void List_without_a_profile_claim_asks_to_sign_in_again_and_calls_nothing()
    {
        var auth = this.AddTestAuthorization();
        auth.SetAuthorized("founder@example.com");

        var cut = RenderComponent<Resumes>();

        Assert.Contains("sign in again", cut.Find("[role=alert]").TextContent);
        Assert.Empty(_api.ListedFor);
    }

    // covers: AC-9
    [Fact]
    public void Detail_for_a_resume_the_api_does_not_return_shows_not_found()
    {
        SignIn();

        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, Guid.NewGuid()));

        Assert.Equal("This resume doesn't exist", cut.Find(".wp-empty-state__title").TextContent);
    }

    // covers: AC-7
    [Fact]
    public void Detail_lists_every_version_newest_first_with_lock_and_download_details()
    {
        SignIn();
        var app = Guid.NewGuid();
        var v2 = Version(2, note: "second");
        var v1 = Version(1, locked: true, fileName: "resume.pdf", note: "first", lockedBy: app);
        var resume = Detail(v2, v1);
        _api.Details[resume.Id] = resume;

        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        var versions = cut.FindAll(".wp-resumes__version");
        Assert.Equal(["2", "1"], versions.Select(v => v.GetAttribute("data-version")));
        Assert.Contains("Draft", versions[0].TextContent);
        Assert.Contains("second", versions[0].TextContent);
        Assert.Empty(versions[0].QuerySelectorAll("a[download]"));
        Assert.Contains("Locked", versions[1].TextContent);
        Assert.Contains($"used by application {app}", versions[1].TextContent);
        var link = versions[1].QuerySelector("a[download]")!;
        Assert.Equal($"/resumes/versions/{v1.Id}/file", link.GetAttribute("href"));
        Assert.Equal("resume.pdf", link.TextContent);
    }

    // covers: AC-2
    [Fact]
    public void Detail_explains_that_saving_a_draft_updates_it_in_place()
    {
        SignIn();
        var resume = Detail(Version(1));
        _api.Details[resume.Id] = resume;

        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        Assert.Contains("v1 is a draft. Saving updates it in place", cut.Markup);
    }

    // covers: AC-4
    [Fact]
    public void Detail_explains_that_saving_over_a_locked_version_makes_a_new_one()
    {
        SignIn();
        var resume = Detail(Version(1, locked: true));
        _api.Details[resume.Id] = resume;

        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        Assert.Contains("v1 was used by an application and is locked. Saving creates v2.", cut.Markup);
    }

    // covers: AC-4
    [Fact]
    public void Save_over_a_locked_version_reports_the_new_version_and_shows_it()
    {
        SignIn();
        var v1 = Version(1, locked: true);
        var resume = Detail(v1);
        _api.Details[resume.Id] = resume;
        var v2 = Version(2, content: "Rewritten");
        _api.ReviseResult = new(new ReviseResumeResultDto("CreatedVersion", 2, resume with { Versions = [v2, v1] }), null);
        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        cut.Find("textarea").Change("Rewritten");
        cut.FindAll("form")[0].Submit();

        Assert.Equal((ProfileId, resume.Id, "Rewritten"), _api.Revised);
        Assert.Equal("Saved as new version v2 (the previous version is locked).", cut.Find("[role=status]").TextContent);
        Assert.Equal(2, cut.FindAll(".wp-resumes__version").Count);
    }

    // covers: AC-5: a lost race surfaces as the Api's conflict message
    [Fact]
    public void Save_that_the_api_rejects_shows_its_message()
    {
        SignIn();
        var resume = Detail(Version(1));
        _api.Details[resume.Id] = resume;
        _api.ReviseResult = new(null, "This version was just locked by an application. Reload and save again to create a new version.");
        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        cut.FindAll("form")[0].Submit();

        Assert.Contains("just locked", cut.Find("[role=alert]").TextContent);
    }

    // covers: AC-6
    [Fact]
    public void Tailor_copies_the_chosen_version_by_default_the_newest_and_opens_the_new_resume()
    {
        SignIn();
        var v2 = Version(2);
        var resume = Detail(v2, Version(1, locked: true));
        _api.Details[resume.Id] = resume;
        var tailored = Detail(Version(1)) with { Kind = "Tailored", TargetCompany = "Acme" };
        _api.TailorResult = new(tailored, null);
        var cut = RenderComponent<ResumeDetail>(p => p.Add(x => x.Id, resume.Id));

        var tailorForm = cut.FindAll("form")[1];
        tailorForm.QuerySelectorAll("input")[0].Change("Acme");
        cut.FindAll("form")[1].QuerySelectorAll("input")[1].Change("Acme resume");
        cut.FindAll("form")[1].Submit();

        Assert.Equal((ProfileId, v2.Id, "Acme resume", "Acme"), _api.Tailored);
        Assert.EndsWith($"/resumes/{tailored.Id}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    private sealed class FakeResumesApiClient : IResumesApiClient
    {
        public List<ResumeSummaryDto> Summaries { get; set; } = [];
        public Dictionary<Guid, ResumeDetailDto> Details { get; } = [];
        public ResumeApiResult<ResumeDetailDto> CreateResult { get; set; } = new(null, "not set up");
        public ResumeApiResult<ReviseResumeResultDto> ReviseResult { get; set; } = new(null, "not set up");
        public ResumeApiResult<ResumeDetailDto> TailorResult { get; set; } = new(null, "not set up");

        public List<Guid> ListedFor { get; } = [];
        public (Guid, string, string)? Created { get; private set; }
        public (Guid, Guid, string)? Revised { get; private set; }
        public (Guid, Guid, string, string)? Tailored { get; private set; }

        public Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            ListedFor.Add(profileId);
            return Task.FromResult<IReadOnlyList<ResumeSummaryDto>>(Summaries);
        }

        public Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(profileId == ProfileId && Details.TryGetValue(resumeId, out var detail) ? detail : null);

        public Task<ResumeApiResult<ResumeDetailDto>> CreateAsync(Guid profileId, string name, string content, string? note, ResumeUpload? file, CancellationToken cancellationToken = default)
        {
            Created = (profileId, name, content);
            return Task.FromResult(CreateResult);
        }

        public Task<ResumeApiResult<ReviseResumeResultDto>> ReviseAsync(Guid profileId, Guid resumeId, string content, string? note, ResumeUpload? file, bool removeFile, CancellationToken cancellationToken = default)
        {
            Revised = (profileId, resumeId, content);
            return Task.FromResult(ReviseResult);
        }

        public Task<ResumeApiResult<ResumeDetailDto>> TailorAsync(Guid profileId, Guid sourceVersionId, string name, string targetCompany, CancellationToken cancellationToken = default)
        {
            Tailored = (profileId, sourceVersionId, name, targetCompany);
            return Task.FromResult(TailorResult);
        }

        public Task<HttpResponseMessage?> DownloadAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<HttpResponseMessage?>(null);
    }
}

// ResumesApiClient (spec 0009): how the Web host turns the internal Api's
// responses into what the resume pages show. The Api is replaced at the HTTP
// boundary by a stub handler.
public class ResumesApiClientTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static (ResumesApiClient Client, StubHandler Handler) Create(HttpStatusCode status, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        return (new ResumesApiClient(new StubFactory(handler)), handler);
    }

    // covers: AC-8
    [Fact]
    public async Task A_validation_problem_becomes_its_field_messages()
    {
        var (client, _) = Create(HttpStatusCode.BadRequest, """{"errors":{"name":["The name must be 1 to 200 characters."]}}""");

        var result = await client.CreateAsync(ProfileId, "", "text", null, null);

        Assert.False(result.Succeeded);
        Assert.Equal("The name must be 1 to 200 characters.", result.Error);
    }

    // covers: AC-5
    [Fact]
    public async Task A_conflict_becomes_its_message()
    {
        var (client, _) = Create(HttpStatusCode.Conflict, """{"errors":{"resume":["This version was just locked."]}}""");

        var result = await client.ReviseAsync(ProfileId, Guid.NewGuid(), "text", null, null, false);

        Assert.Equal("This version was just locked.", result.Error);
    }

    // covers: AC-9
    [Fact]
    public async Task Not_found_says_the_resume_no_longer_exists()
    {
        var (client, _) = Create(HttpStatusCode.NotFound);

        var result = await client.TailorAsync(ProfileId, Guid.NewGuid(), "Acme resume", "Acme");

        Assert.Equal("That resume no longer exists.", result.Error);
    }

    [Fact]
    public async Task An_unreadable_error_body_falls_back_to_a_generic_message()
    {
        var (client, _) = Create(HttpStatusCode.BadRequest, "not json");

        var result = await client.CreateAsync(ProfileId, "name", "text", null, null);

        Assert.Equal("The request was rejected.", result.Error);
    }

    // covers: AC-1, AC-9: the create goes to the internal endpoint as multipart, carrying the profile and the file
    [Fact]
    public async Task Create_posts_a_multipart_form_with_the_profile_and_file()
    {
        var (client, handler) = Create(HttpStatusCode.BadRequest, """{"errors":{}}""");

        await client.CreateAsync(ProfileId, "General resume", "Experience", "first", new ResumeUpload("resume.pdf", Encoding.ASCII.GetBytes("%PDF")));

        Assert.Equal((HttpMethod.Post, "/internal/resumes"), (handler.Method, handler.Path));
        Assert.StartsWith("multipart/form-data", handler.ContentType);
        Assert.Contains(ProfileId.ToString(), handler.Body);
        Assert.Contains("filename=resume.pdf", handler.Body);
        Assert.Contains("%PDF", handler.Body);
    }

    // covers: AC-9
    [Fact]
    public async Task Get_returns_null_for_not_found()
    {
        var (client, handler) = Create(HttpStatusCode.NotFound);
        var resumeId = Guid.NewGuid();

        var detail = await client.GetAsync(ProfileId, resumeId);

        Assert.Null(detail);
        Assert.Equal($"/internal/resumes/{resumeId}?profileId={ProfileId}", handler.PathAndQuery);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api") };
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? ContentType { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            PathAndQuery = request.RequestUri.PathAndQuery;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
