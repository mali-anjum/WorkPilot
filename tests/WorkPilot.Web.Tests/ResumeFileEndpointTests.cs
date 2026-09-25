using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Web.Client;
using WorkPilot.Web.Features.Resumes;

namespace WorkPilot.Web.Tests;

// GET /resumes/versions/{id}/file (spec 0009, AC-7, AC-9): the Web host's
// download proxy, hosted in a minimal in process server with the real endpoint
// mapping and a header based test sign in standing in for the session cookie.
// The internal Api is replaced by a recording IResumesApiClient that only
// serves files to their owner, like the real Api.
public sealed class ResumeFileEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("01a0da9c-0d42-7000-8000-00000000000a");
    private static readonly Guid VersionId = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000f1");
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 test resume");

    private readonly RecordingClient _api = new();
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(HeaderAuthHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.Scheme, null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IResumesApiClient>(_api);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapResumeFileEndpoints();
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private HttpClient CreateClient(string? profileClaim)
    {
        var client = _app.GetTestClient();
        if (profileClaim is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthHandler.Header, profileClaim);
        }

        return client;
    }

    // covers: AC-7
    [Fact]
    public async Task The_owner_gets_the_file_as_an_attachment_with_its_name_and_type()
    {
        using var client = CreateClient(Owner.ToString());

        var response = await client.GetAsync($"/resumes/versions/{VersionId}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PdfBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("resume.pdf", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal([(Owner, VersionId)], _api.Calls);
    }

    // covers: AC-9: the profile always comes from the session, so another profile gets 404
    [Fact]
    public async Task Another_profile_gets_404()
    {
        var other = Guid.NewGuid();
        using var client = CreateClient(other.ToString());

        var response = await client.GetAsync($"/resumes/versions/{VersionId}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal([(other, VersionId)], _api.Calls);
    }

    // covers: AC-9
    [Fact]
    public async Task A_signed_out_request_is_challenged_and_never_reaches_the_api()
    {
        using var client = CreateClient(null);

        var response = await client.GetAsync($"/resumes/versions/{VersionId}/file");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_api.Calls);
    }

    // covers: AC-9
    [Theory]
    [InlineData("none")] // signed in, but no profile_id claim
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_session_without_a_valid_profile_is_forbidden_and_never_reaches_the_api(string profileClaim)
    {
        using var client = CreateClient(profileClaim);

        var response = await client.GetAsync($"/resumes/versions/{VersionId}/file");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_api.Calls);
    }

    private sealed class RecordingClient : IResumesApiClient
    {
        public List<(Guid ProfileId, Guid VersionId)> Calls { get; } = [];

        public Task<HttpResponseMessage?> DownloadAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken = default)
        {
            Calls.Add((profileId, versionId));
            if (profileId != Owner || versionId != VersionId)
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            var content = new ByteArrayContent(PdfBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "resume.pdf" };
            return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        public Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the download endpoint.");

        public Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the download endpoint.");

        public Task<ResumeApiResult<ResumeDetailDto>> CreateAsync(Guid profileId, string name, string content, string? note, ResumeUpload? file, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the download endpoint.");

        public Task<ResumeApiResult<ReviseResumeResultDto>> ReviseAsync(Guid profileId, Guid resumeId, string content, string? note, ResumeUpload? file, bool removeFile, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the download endpoint.");

        public Task<ResumeApiResult<ResumeDetailDto>> TailorAsync(Guid profileId, Guid sourceVersionId, string name, string targetCompany, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by the download endpoint.");
    }

    // Signs the request in with the profile_id claim named in a header ("none"
    // means signed in with no profile claim), like the real session cookie.
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "TestSession";
        public const string Header = "X-Test-Profile-Claim";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, "01a0da9c-0d42-7000-8000-0000000000aa")];
            if (value.ToString() != "none")
            {
                claims.Add(new Claim(PersistedAuthState.ProfileIdClaimType, value.ToString()));
            }

            var identity = new ClaimsIdentity(claims, Scheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }
}
