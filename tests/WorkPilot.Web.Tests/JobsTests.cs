using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Jobs.Matching;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages;
using WorkPilot.Web.Features.Common;
using WorkPilot.Web.Features.Jobs;

namespace WorkPilot.Web.Tests;

// The /jobs list (spec 0019, AC-9), rendered with a fake IJobsApiClient standing in for the
// internal Api and a signed in session carrying the profile_id claim.
public class JobsTests : BunitContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");

    private readonly FakeJobsApiClient _api = new();

    public JobsTests()
    {
        Services.AddSingleton<IJobsApiClient>(_api);
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, ProfileId.ToString()));
    }

    // covers: AC-9
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = Render<Jobs>();

        Assert.Equal("Jobs", cut.Find("h1").TextContent);
        Assert.Equal("No jobs yet", cut.Find(".wp-empty-state__title").TextContent);
    }

    private sealed class FakeJobsApiClient : IJobsApiClient
    {
        public MatchListDto List { get; set; } = new([], 0, 1, 25, false);

        public Task<ApiResult<MatchListDto>> ListMatchesAsync(Guid profileId, int page, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<MatchListDto>.Ok(List));

        public Task<ApiResult<JobMatchDetailDto>> GetMatchAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<JobMatchDetailDto>.Fail("This job has no match for your profile yet."));

        public Task<ApiResult<RescoreMatchResponse>> RescoreAsync(Guid jobId, Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<RescoreMatchResponse>.Ok(new RescoreMatchResponse(true)));
    }
}
