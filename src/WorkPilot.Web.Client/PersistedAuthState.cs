namespace WorkPilot.Web.Client;

/// <summary>
/// The founder's identity, carried across from the server's cookie auth to
/// the WASM half of Auto render mode via Blazor's persistent component
/// state, never via the (HttpOnly) session cookie itself, which WASM never
/// reads. Docs/specs/0004-auth-app-shell.md.
/// </summary>
public sealed record PersistedAuthState(string AuthUserId, string Email, string ProfileId)
{
    public const string StateKey = "workpilot-auth";
    public const string ProfileIdClaimType = "profile_id";
}
