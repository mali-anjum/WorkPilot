using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Application.Modules.Profile.MatchProfile;
using WorkPilot.Web.Client;
using WorkPilot.Web.Components.Pages;
using WorkPilot.Web.Features.Common;
using WorkPilot.Web.Features.Profile;

namespace WorkPilot.Web.Tests;

// The /profile page (spec 0019, AC-11), rendered with a fake IMatchProfileApiClient standing in
// for the internal Api and a signed in session carrying the profile_id claim. The validation and
// ETag rules themselves are covered in Api.Tests/JobMatchingTests.
public class ProfileTests : BunitContext
{
    private static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-000000000001");

    private readonly FakeMatchProfileApiClient _api = new();

    public ProfileTests()
    {
        Services.AddSingleton<IMatchProfileApiClient>(_api);
        var auth = this.AddAuthorization();
        auth.SetAuthorized("founder@example.com");
        auth.SetClaims(new Claim(PersistedAuthState.ProfileIdClaimType, ProfileId.ToString()));
    }

    private static readonly MatchProfileDocument Stored = new(
        ["Backend Engineer"],
        "Remote",
        [new PreferredLocationDto("Berlin", "DE"), new PreferredLocationDto(null, "PK")],
        ["FullTime"],
        100000,
        "USD",
        ["PK"],
        true,
        70,
        ["c#", "go"],
        [new ExperienceDto(Guid.NewGuid(), "Acme", "Engineer", new DateOnly(2020, 1, 1), null, null)],
        [new EducationDto(Guid.NewGuid(), "Uni", "BS", "CS", "Bachelor", new DateOnly(2014, 1, 1), new DateOnly(2018, 1, 1))]);

    // covers: AC-11
    [Fact]
    public void Loads_the_saved_profile_into_the_form()
    {
        var cut = Render<Profile>();

        Assert.Equal("Backend Engineer", cut.FindAll("textarea")[0].GetAttribute("value"));
        Assert.Equal("Berlin, DE\nPK", cut.FindAll("textarea")[1].GetAttribute("value"));
        Assert.Equal("c#, go", cut.FindAll("textarea")[2].GetAttribute("value"));
        Assert.Equal("USD", cut.Find("input[placeholder=USD]").GetAttribute("value"));
        Assert.Single(cut.FindAll(".wp-match__row-form"), r => r.TextContent.Contains("Company", StringComparison.Ordinal));
    }

    // covers: AC-11
    [Fact]
    public void Saving_sends_the_edited_document_with_the_etag_and_confirms()
    {
        var cut = Render<Profile>();
        cut.FindAll("textarea")[2].Change("C#, Go, Rust");
        cut.FindAll("textarea")[1].Change("Lahore, PK\nAE");

        cut.Find("form").Submit();

        var (etag, document) = Assert.Single(_api.Saves);
        Assert.Equal("\"1\"", etag);
        Assert.Equal(["C#", "Go", "Rust"], document.Skills);
        Assert.Equal([new PreferredLocationDto("Lahore", "PK"), new PreferredLocationDto(null, "AE")], document.PreferredLocations);
        Assert.Equal(Stored.Experiences[0].Id, document.Experiences[0].Id);
        Assert.Contains("Saved.", cut.Find(".wp-match__message").TextContent);
    }

    // covers: AC-11
    [Fact]
    public void A_stale_save_says_so_and_keeps_the_edits_on_screen()
    {
        _api.SaveResult = new MatchProfileSaveResult(null, "Your profile changed elsewhere.", Stale: true);
        var cut = Render<Profile>();
        cut.Find("input[placeholder=USD]").Change("EUR");

        cut.Find("form").Submit();

        Assert.Contains("changed elsewhere", cut.Find(".wp-match__banner[role=alert]").TextContent);
        Assert.Equal("EUR", cut.Find("input[placeholder=USD]").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".wp-match__error"));
    }

    // covers: AC-11
    [Fact]
    public void Field_errors_are_shown_and_the_edits_stay()
    {
        _api.SaveResult = new MatchProfileSaveResult(null, "Currency must be a 3 letter ISO 4217 code, like EUR or USD.", Stale: false);
        var cut = Render<Profile>();
        cut.Find("input[placeholder=USD]").Change("XXQ");

        cut.Find("form").Submit();

        Assert.Equal("Currency must be a 3 letter ISO 4217 code, like EUR or USD.", cut.Find(".wp-match__error").TextContent);
        Assert.Equal("XXQ", cut.Find("input[placeholder=USD]").GetAttribute("value"));
    }

    // covers: AC-11
    [Fact]
    public void Reload_after_a_stale_save_brings_back_the_latest_version()
    {
        _api.SaveResult = new MatchProfileSaveResult(null, "stale", Stale: true);
        var cut = Render<Profile>();
        cut.Find("input[placeholder=USD]").Change("EUR");
        cut.Find("form").Submit();

        cut.Find(".wp-match__banner button").Click();

        Assert.Equal("USD", cut.Find("input[placeholder=USD]").GetAttribute("value"));
        Assert.Empty(cut.FindAll(".wp-match__banner[role=alert]"));
    }

    [Fact]
    public void Experience_rows_can_be_added_and_removed()
    {
        var cut = Render<Profile>();

        cut.FindAll("button").Single(b => b.TextContent == "Add experience").Click();
        Assert.Equal(2, cut.FindAll(".wp-match__row-form").Count(r => r.TextContent.Contains("Company", StringComparison.Ordinal)));

        cut.FindAll(".wp-match__row-form")[0].QuerySelector("button")!.Click();
        cut.Find("form").Submit();

        Assert.Single(_api.Saves[0].Document.Experiences);
        Assert.Null(_api.Saves[0].Document.Experiences[0].Id);
    }

    private sealed class FakeMatchProfileApiClient : IMatchProfileApiClient
    {
        public MatchProfileSaveResult? SaveResult { get; set; }
        public List<(string ETag, MatchProfileDocument Document)> Saves { get; } = [];

        public Task<ApiResult<VersionedMatchProfile>> GetAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ApiResult<VersionedMatchProfile>.Ok(new VersionedMatchProfile(Stored, "\"1\"")));

        public Task<MatchProfileSaveResult> SaveAsync(Guid profileId, string etag, MatchProfileDocument document, CancellationToken cancellationToken = default)
        {
            Saves.Add((etag, document));
            return Task.FromResult(SaveResult ?? new MatchProfileSaveResult(new VersionedMatchProfile(document, "\"2\""), null, false));
        }
    }
}
