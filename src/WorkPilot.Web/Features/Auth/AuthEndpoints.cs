using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using WorkPilot.Application.Modules.Identity;

namespace WorkPilot.Web.Features.Auth;

/// <summary>
/// Sign in, sign out, and password reset, mapped as plain minimal API
/// endpoints rather than routed Blazor components. This is deliberate: the
/// app's Blazor <c>Routes</c> component renders under InteractiveServer (see
/// <c>App.razor</c>), and an interactive circuit cannot write a <c>Set-Cookie</c>
/// response, only a real HTTP request/response cycle can issue the session
/// cookie. Docs/specs/0004-auth-app-shell.md.
/// </summary>
public static class AuthEndpoints
{
    private const string CookieName = "workpilot-session";
    private const string ClaimProfileId = "profile_id";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (string? returnUrl, HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
            return Results.Content(RenderAuthPage(
                title: "Sign in",
                bodyHtml: $"""
                <form method="post" action="/login">
                  <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                  <input type="hidden" name="returnUrl" value="{Html(returnUrl)}" />
                  <label class="wp-field">
                    <span>Email</span>
                    <input type="email" name="email" required autofocus autocomplete="username" />
                  </label>
                  <label class="wp-field">
                    <span>Password</span>
                    <input type="password" name="password" required autocomplete="current-password" />
                  </label>
                  <button type="submit" class="wp-button wp-button--primary wp-button--md">Sign in</button>
                  <p class="wp-auth-link"><a href="/forgot-password">Forgot your password?</a></p>
                </form>
                """), "text/html");
        });

        app.MapPost("/login", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            IAuthService authService,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAntiforgeryValidAsync(ctx, antiforgery))
            {
                return Results.BadRequest();
            }

            var form = await ctx.Request.ReadFormAsync(cancellationToken);
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var result = await authService.SignInAsync(email, password, cancellationToken);
            if (!result.Succeeded || result.AuthUserId is not { } authUserId || result.Email is null)
            {
                var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
                return Results.Content(RenderAuthPage(
                    title: "Sign in",
                    bodyHtml: $"""
                    <p class="wp-auth-error" role="alert">Incorrect email or password.</p>
                    <form method="post" action="/login">
                      <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                      <input type="hidden" name="returnUrl" value="{Html(returnUrl)}" />
                      <label class="wp-field">
                        <span>Email</span>
                        <input type="email" name="email" value="{Html(email)}" required autofocus autocomplete="username" />
                      </label>
                      <label class="wp-field">
                        <span>Password</span>
                        <input type="password" name="password" required autocomplete="current-password" />
                      </label>
                      <button type="submit" class="wp-button wp-button--primary wp-button--md">Sign in</button>
                      <p class="wp-auth-link"><a href="/forgot-password">Forgot your password?</a></p>
                    </form>
                    """), "text/html", statusCode: StatusCodes.Status401Unauthorized);
            }

            var api = httpClientFactory.CreateClient("api");
            var resolveResponse = await api.PostAsJsonAsync(
                "/internal/identity/profile",
                new { AuthUserId = authUserId, Email = result.Email },
                cancellationToken);
            resolveResponse.EnsureSuccessStatusCode();
            var resolved = await resolveResponse.Content.ReadFromJsonAsync<ProfileIdResponse>(cancellationToken)
                ?? throw new InvalidOperationException("The internal profile resolution endpoint returned no body.");

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, authUserId.ToString()),
                new(ClaimTypes.Email, result.Email),
                new(ClaimProfileId, resolved.ProfileId.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Redirect(SafeLocalRedirectTarget(returnUrl));
        });

        app.MapPost("/logout", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            if (!await IsAntiforgeryValidAsync(ctx, antiforgery))
            {
                return Results.BadRequest();
            }

            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        // A small statically rendered stop on the way to POST /logout (linked
        // from the interactive TopBar), so the antiforgery token needed by
        // that POST comes from a real server request rather than requiring
        // an interactive component to reach into request scoped services it
        // no longer has once it is running as WASM.
        app.MapGet("/logout-confirm", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
            return Results.Content(RenderAuthPage(
                title: "Sign out",
                bodyHtml: $"""
                <form method="post" action="/logout">
                  <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                  <button type="submit" class="wp-button wp-button--primary wp-button--md">Sign out</button>
                </form>
                """), "text/html");
        }).RequireAuthorization();

        app.MapGet("/forgot-password", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
            return Results.Content(RenderAuthPage(
                title: "Reset your password",
                bodyHtml: $"""
                <form method="post" action="/forgot-password">
                  <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                  <label class="wp-field">
                    <span>Email</span>
                    <input type="email" name="email" required autofocus autocomplete="username" />
                  </label>
                  <button type="submit" class="wp-button wp-button--primary wp-button--md">Send reset email</button>
                  <p class="wp-auth-link"><a href="/login">Back to sign in</a></p>
                </form>
                """), "text/html");
        });

        app.MapPost("/forgot-password", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            IAuthService authService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAntiforgeryValidAsync(ctx, antiforgery))
            {
                return Results.BadRequest();
            }

            var form = await ctx.Request.ReadFormAsync(cancellationToken);
            var email = form["email"].ToString();
            await authService.RequestPasswordResetAsync(email, cancellationToken);

            // Same fixed message whether the email exists, is unknown, or hit
            // GoTrue's own rate limit: nothing here is sourced from a lookup
            // (spec 0004, AC-5, Value sourcing).
            return Results.Content(RenderAuthPage(
                title: "Reset your password",
                bodyHtml: """
                <p>If an account exists for that email, a reset link is on its way.</p>
                <p class="wp-auth-link"><a href="/login">Back to sign in</a></p>
                """), "text/html");
        });

        app.MapGet("/reset-password", (string? token_hash, HttpContext ctx, IAntiforgery antiforgery) =>
        {
            if (string.IsNullOrEmpty(token_hash))
            {
                return Results.Content(RenderAuthPage(
                    title: "Reset your password",
                    bodyHtml: """<p class="wp-auth-error" role="alert">This reset link is invalid or has expired.</p>"""),
                    "text/html", statusCode: StatusCodes.Status400BadRequest);
            }

            var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
            return Results.Content(RenderAuthPage(
                title: "Reset your password",
                bodyHtml: $"""
                <form method="post" action="/reset-password">
                  <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                  <input type="hidden" name="tokenHash" value="{Html(token_hash)}" />
                  <label class="wp-field">
                    <span>New password</span>
                    <input type="password" name="newPassword" required autofocus autocomplete="new-password" />
                  </label>
                  <button type="submit" class="wp-button wp-button--primary wp-button--md">Set new password</button>
                </form>
                """), "text/html");
        });

        app.MapPost("/reset-password", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            IAuthService authService,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAntiforgeryValidAsync(ctx, antiforgery))
            {
                return Results.BadRequest();
            }

            var form = await ctx.Request.ReadFormAsync(cancellationToken);
            var tokenHash = form["tokenHash"].ToString();
            var newPassword = form["newPassword"].ToString();

            var result = await authService.ResetPasswordAsync(tokenHash, newPassword, cancellationToken);
            if (!result.Succeeded)
            {
                var token = antiforgery.GetAndStoreTokens(ctx).RequestToken;
                return Results.Content(RenderAuthPage(
                    title: "Reset your password",
                    bodyHtml: $"""
                    <p class="wp-auth-error" role="alert">{Html(result.ErrorMessage)}</p>
                    <form method="post" action="/reset-password">
                      <input type="hidden" name="__RequestVerificationToken" value="{Html(token)}" />
                      <input type="hidden" name="tokenHash" value="{Html(tokenHash)}" />
                      <label class="wp-field">
                        <span>New password</span>
                        <input type="password" name="newPassword" required autofocus autocomplete="new-password" />
                      </label>
                      <button type="submit" class="wp-button wp-button--primary wp-button--md">Set new password</button>
                    </form>
                    """), "text/html", statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Redirect("/login");
        });
    }

    private static async Task<bool> IsAntiforgeryValidAsync(HttpContext ctx, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Only ever redirects to a local path (spec 0004, API surface): rejects
    /// an absolute URL and a protocol-relative <c>//host</c> prefix, which
    /// browsers still treat as a cross origin redirect.
    /// </summary>
    private static string SafeLocalRedirectTarget(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        ? returnUrl
        : "/";

    private static string Html(string? value) =>
        string.IsNullOrEmpty(value) ? "" : System.Net.WebUtility.HtmlEncode(value);

    private static string RenderAuthPage(string title, string bodyHtml) => $"""
        <!DOCTYPE html>
        <html lang="en" data-theme="dark">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>{Html(title)} · WorkPilot</title>
          <link rel="stylesheet" href="/css/tokens.css" />
          <link rel="stylesheet" href="/app.css" />
          <link rel="stylesheet" href="/css/auth.css" />
          <link rel="icon" type="image/png" href="/favicon.png" />
        </head>
        <body class="wp-auth-body">
          <main class="wp-auth-card">
            <h1>{Html(title)}</h1>
            {bodyHtml}
          </main>
        </body>
        </html>
        """;

    private sealed record ProfileIdResponse(Guid ProfileId);
}
