using System.Reflection;
using WorkPilot.Web.Features.Auth;

namespace WorkPilot.Web.Tests;

// AuthEndpoints maps its handlers with MapGet/MapPost, so there is no public
// surface to call directly without standing up a full host (the sign in flow
// also needs a live GoTrue and the internal Api's profile endpoint; that is
// exercised live by /check verify, not duplicated here as a mock-heavy
// integration test). What is unit testable, and security load bearing enough
// to lock in, are the two pure helpers the handlers all route through:
// SafeLocalRedirectTarget (open redirect prevention) and Html (XSS-safe
// encoding of untrusted form input echoed back into a page). Both are private,
// so they are invoked via reflection rather than by changing their
// accessibility just for tests.
public class AuthEndpointsTests
{
    private static readonly MethodInfo SafeLocalRedirectTargetMethod = typeof(AuthEndpoints)
        .GetMethod("SafeLocalRedirectTarget", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AuthEndpoints.SafeLocalRedirectTarget not found; the method may have been renamed.");

    private static readonly MethodInfo HtmlMethod = typeof(AuthEndpoints)
        .GetMethod("Html", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AuthEndpoints.Html not found; the method may have been renamed.");

    private static string SafeLocalRedirectTarget(string? returnUrl) =>
        (string)SafeLocalRedirectTargetMethod.Invoke(null, [returnUrl])!;

    private static string Html(string? value) =>
        (string)HtmlMethod.Invoke(null, [value])!;

    // covers: spec 0004 API surface (POST /login redirect target) and AC-1
    // (lands back on the originally requested route)
    [Fact]
    public void SafeLocalRedirectTarget_returns_the_returnUrl_when_it_is_a_local_path()
    {
        Assert.Equal("/jobs", SafeLocalRedirectTarget("/jobs"));
    }

    [Fact]
    public void SafeLocalRedirectTarget_falls_back_to_the_shell_root_when_returnUrl_is_null()
    {
        Assert.Equal("/", SafeLocalRedirectTarget(null));
    }

    [Fact]
    public void SafeLocalRedirectTarget_falls_back_to_the_shell_root_when_returnUrl_is_empty()
    {
        Assert.Equal("/", SafeLocalRedirectTarget(""));
    }

    // covers: spec 0004 API surface, "only if Url.IsLocalUrl and no
    // protocol-relative // prefix" (open redirect prevention)
    [Theory]
    [InlineData("https://evil.example.com/phish")]
    [InlineData("http://evil.example.com")]
    [InlineData("//evil.example.com")]
    [InlineData("//evil.example.com/jobs")]
    [InlineData("jobs")]
    [InlineData("  /jobs")]
    public void SafeLocalRedirectTarget_rejects_anything_that_is_not_a_bare_local_path(string returnUrl)
    {
        Assert.Equal("/", SafeLocalRedirectTarget(returnUrl));
    }

    // covers: spec 0004 Key invariants (no data sourced from a lookup leaks
    // into responses) and general XSS hardening of untrusted form input
    // echoed back into the sign in / reset password pages
    [Fact]
    public void Html_encodes_angle_brackets_and_quotes_so_untrusted_input_cannot_break_out_of_an_attribute_or_tag()
    {
        var encoded = Html("""<script>alert('xss')</script>""");

        Assert.DoesNotContain("<script>", encoded);
        Assert.Contains("&lt;script&gt;", encoded);
    }

    [Fact]
    public void Html_returns_an_empty_string_for_null_input()
    {
        Assert.Equal("", Html(null));
    }

    [Fact]
    public void Html_returns_an_empty_string_for_empty_input()
    {
        Assert.Equal("", Html(""));
    }

    [Fact]
    public void Html_leaves_plain_text_unchanged()
    {
        Assert.Equal("founder@example.com", Html("founder@example.com"));
    }
}
