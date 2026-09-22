using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using WorkPilot.Web.Client;

namespace WorkPilot.Web;

/// <summary>
/// The server side of Auto render mode's identity handoff: reads the
/// founder's identity from the request's cookie authenticated
/// <c>HttpContext.User</c> (set by ASP.NET Core cookie auth), then persists
/// it into the page for the WASM half to pick up (see WorkPilot.Web.Client's
/// PersistentAuthenticationStateProvider). Never persists a GoTrue token,
/// only the three identity claims the cookie itself carries.
/// Docs/specs/0004-auth-app-shell.md.
/// </summary>
public sealed class PersistingAuthenticationStateProvider : AuthenticationStateProvider, IDisposable
{
    private readonly PersistentComponentState _state;
    private readonly PersistingComponentStateSubscription _subscription;
    private readonly Task<AuthenticationState> _authenticationStateTask;
    private readonly ClaimsPrincipal _user;

    public PersistingAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor, PersistentComponentState state)
    {
        _state = state;
        _user = httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        _authenticationStateTask = Task.FromResult(new AuthenticationState(_user));
        _subscription = _state.RegisterOnPersisting(OnPersistingAsync, RenderMode.InteractiveWebAssembly);
    }

    private Task OnPersistingAsync()
    {
        if (_user.Identity?.IsAuthenticated == true)
        {
            var authUserId = _user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = _user.FindFirst(ClaimTypes.Email)?.Value;
            var profileId = _user.FindFirst(PersistedAuthState.ProfileIdClaimType)?.Value;
            if (authUserId is not null && email is not null && profileId is not null)
            {
                _state.PersistAsJson(PersistedAuthState.StateKey, new PersistedAuthState(authUserId, email, profileId));
            }
        }

        return Task.CompletedTask;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _authenticationStateTask;

    public void Dispose() => _subscription.Dispose();
}
