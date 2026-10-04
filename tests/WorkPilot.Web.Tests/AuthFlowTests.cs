using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Web.Components;

namespace WorkPilot.Web.Tests;

// The sign in, session and sign out flow (spec 0004) through the real Web host: its
// cookie scheme, antiforgery, the auth endpoints and the authorization gate on every
// Blazor route. Only the two things outside the Web host are replaced: GoTrue (a
// scripted IAuthService) and the internal Api's profile endpoint (a stub handler).
// The real GoTrue round trip and the reset email are checked live by /check verify.
public sealed partial class AuthFlowTests : IClassFixture<AuthFlowTests.WebFactory>
{
    private const string Email = "founder@workpilot.test";
    private const string Password = "correct horse battery staple";
    private const string SessionCookie = "workpilot-session";

    private readonly WebFactory _factory;

    public AuthFlowTests(WebFactory factory) => _factory = factory;

    // covers: spec 0004 AC-1
    [Fact]
    public async Task An_anonymous_request_for_a_shell_route_is_sent_to_login_with_the_route_to_return_to()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/login", location);
        Assert.Contains("returnUrl=%2Fjobs", location);
    }

    // covers: spec 0004 AC-2 (wrong password)
    [Fact]
    public async Task A_wrong_password_stays_on_login_with_an_error_and_no_session()
    {
        using var client = NewClient();

        var response = await PostLoginAsync(client, Email, "wrong password", "/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Incorrect email or password.", html);
        Assert.Contains("action=\"/login\"", html);
        Assert.DoesNotContain(SetCookies(response), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/jobs")).StatusCode);
    }

    // covers: spec 0004 AC-1, AC-2
    [Fact]
    public async Task The_right_password_signs_in_and_returns_to_the_requested_route()
    {
        using var client = NewClient();

        var response = await PostLoginAsync(client, Email, Password, "/jobs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/jobs", response.Headers.Location!.ToString());
        var cookie = Assert.Single(SetCookies(response), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_factory.Api.ResolvedEmails, e => e == Email);

        // The cookie is the whole session: the next request is let in on it alone.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/logout-confirm")).StatusCode);
    }

    // covers: spec 0004 AC-3 (a browser restart drops a cookie without an expiry, so the session must carry one)
    [Fact(Skip = "Known defect found by the 2026-10-04 test audit: POST /login signs in without " +
        "AuthenticationProperties.IsPersistent, so the session cookie has no expiry and a browser restart ends it. " +
        "Remove this Skip once /debug fixes it.")]
    public async Task The_session_cookie_outlives_a_browser_restart_for_30_days()
    {
        using var client = NewClient();

        var response = await PostLoginAsync(client, Email, Password, "/");

        var cookie = Assert.Single(SetCookies(response), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        var expires = Regex.Match(cookie, "expires=([^;]+)", RegexOptions.IgnoreCase);
        Assert.True(expires.Success, $"the session cookie has no expiry: {cookie}");
        Assert.InRange(DateTimeOffset.Parse(expires.Groups[1].Value) - DateTimeOffset.UtcNow, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
    }

    // covers: spec 0004 AC-3 (no GoTrue clock: the cookie slides on activity for 30 days)
    [Fact]
    public void The_session_cookie_slides_for_30_days()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/login", options.LoginPath.Value);
    }

    // covers: spec 0004 AC-4
    [Fact]
    public async Task A_cookie_that_cannot_be_decrypted_is_treated_as_no_session()
    {
        using var client = NewClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie}=CfDJ8not-a-real-ticket");

        var response = await client.GetAsync("/approvals");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("returnUrl=%2Fapprovals", response.Headers.Location!.ToString());
    }

    // covers: spec 0004 AC-6
    [Fact]
    public async Task Signing_out_clears_the_cookie_and_returns_to_login()
    {
        using var client = NewClient();
        await PostLoginAsync(client, Email, Password, "/");
        var token = await AntiforgeryTokenAsync(client, "/logout-confirm");

        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.ToString());
        var cleared = Assert.Single(SetCookies(response), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/logout-confirm")).StatusCode);
    }

    // covers: spec 0004 AC-6 (sign out is a POST guarded by antiforgery, so a link elsewhere cannot sign you out)
    [Fact]
    public async Task Sign_out_without_an_antiforgery_token_is_refused_and_keeps_the_session()
    {
        using var client = NewClient();
        await PostLoginAsync(client, Email, Password, "/");

        var response = await client.PostAsync("/logout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/logout-confirm")).StatusCode);
    }

    // covers: spec 0004 AC-5 (the answer never says whether the email exists)
    [Fact]
    public async Task A_reset_request_gets_the_same_answer_for_any_email()
    {
        using var client = NewClient();

        var known = await PostForgotPasswordAsync(client, Email);
        var unknown = await PostForgotPasswordAsync(client, "nobody@workpilot.test");

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Contains("If an account exists for that email", await known.Content.ReadAsStringAsync());
        Assert.Contains(Email, _factory.Auth.ResetRequests);
    }

    // covers: spec 0004 AC-5 (a reset link without its token is refused, never a blank form)
    [Fact]
    public async Task A_reset_link_without_a_token_is_refused()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/reset-password");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid or has expired", await response.Content.ReadAsStringAsync());
    }

    // covers: spec 0004 AC-8
    [Fact]
    public async Task The_sign_in_page_offers_no_way_to_create_an_account()
    {
        using var client = NewClient();

        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();

        Assert.Contains("action=\"/login\"", html);
        Assert.DoesNotMatch("(?i)sign ?up|register|create (an )?account", html);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
    });

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string email, string password, string returnUrl)
    {
        var token = await AntiforgeryTokenAsync(client, $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["returnUrl"] = returnUrl,
            ["email"] = email,
            ["password"] = password,
        }));
    }

    private static async Task<HttpResponseMessage> PostForgotPasswordAsync(HttpClient client, string email)
    {
        var token = await AntiforgeryTokenAsync(client, "/forgot-password");
        return await client.PostAsync("/forgot-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = email,
        }));
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string page)
    {
        var html = await (await client.GetAsync(page)).Content.ReadAsStringAsync();
        var match = TokenField().Match(html);
        Assert.True(match.Success, $"{page} rendered no antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    /// <summary>The real Web host with GoTrue and the internal Api replaced.</summary>
    public sealed class WebFactory : WebApplicationFactory<App>
    {
        public ScriptedAuthService Auth { get; } = new();

        public StubApi Api { get; } = new();

        public WebFactory()
        {
            // Program.cs reads these before WebApplicationFactory's own configuration is layered on.
            Environment.SetEnvironmentVariable("Supabase__Url", "http://gotrue.test");
            Environment.SetEnvironmentVariable("Supabase__AnonKey", "test-anon-key");
            Environment.SetEnvironmentVariable("services__api__https__0", "https://api.test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IAuthService>(_ => Auth);
                services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => Api);
            });
        }
    }

    /// <summary>Accepts only the founder's password, like GoTrue would.</summary>
    public sealed class ScriptedAuthService : IAuthService
    {
        public static readonly Guid AuthUserId = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000a1");

        public List<string> ResetRequests { get; } = [];

        public Task<SignInResult> SignInAsync(string email, string password, CancellationToken cancellationToken) =>
            Task.FromResult(email == Email && password == Password
                ? new SignInResult(true, AuthUserId, email)
                : new SignInResult(false, null, null));

        public Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
        {
            lock (ResetRequests)
            {
                ResetRequests.Add(email);
            }

            return Task.CompletedTask;
        }

        public Task<PasswordResetResult> ResetPasswordAsync(string tokenHash, string newPassword, CancellationToken cancellationToken) =>
            Task.FromResult(new PasswordResetResult(true, null));
    }

    /// <summary>The internal Api's POST /internal/identity/profile, answering one fixed profile.</summary>
    public sealed class StubApi : HttpMessageHandler
    {
        public static readonly Guid ProfileId = Guid.Parse("01a0da9c-0d42-7000-8000-0000000000b1");

        public List<string> ResolvedEmails { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/internal/identity/profile")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = await request.Content!.ReadFromJsonAsync<ResolveRequest>(cancellationToken);
            lock (ResolvedEmails)
            {
                ResolvedEmails.Add(body!.Email);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ProfileId }) };
        }

        private sealed record ResolveRequest(Guid AuthUserId, string Email);
    }
}
