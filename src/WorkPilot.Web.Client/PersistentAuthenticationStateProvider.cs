using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace WorkPilot.Web.Client;

/// <summary>
/// The WASM side of Auto render mode's identity handoff: reads the identity
/// the server already persisted into the page (see WorkPilot.Web's
/// PersistingAuthenticationStateProvider), no HTTP call and no cookie read
/// involved. Docs/specs/0004-auth-app-shell.md.
/// </summary>
public sealed class PersistentAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> UnauthenticatedTask =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    private readonly Task<AuthenticationState> _authenticationStateTask;

    public PersistentAuthenticationStateProvider(PersistentComponentState state)
    {
        if (!state.TryTakeFromJson<PersistedAuthState>(PersistedAuthState.StateKey, out var persisted) || persisted is null)
        {
            _authenticationStateTask = UnauthenticatedTask;
            return;
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, persisted.AuthUserId),
            new Claim(ClaimTypes.Email, persisted.Email),
            new Claim(PersistedAuthState.ProfileIdClaimType, persisted.ProfileId),
        };
        var identity = new ClaimsIdentity(claims, authenticationType: nameof(PersistentAuthenticationStateProvider));
        _authenticationStateTask = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _authenticationStateTask;
}
