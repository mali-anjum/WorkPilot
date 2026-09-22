# 0004. Auth and app shell gating — Rationale

## Context

Feature 4 built the app shell (sidebar, top bar, command palette) and eleven stub routes, but nothing gates them: any visitor who reaches the app today sees every screen. This project already runs Supabase's GoTrue as its auth service (spec 0001), and the data model (spec 0002) already models `Profile` as a one to one link to `auth.users`, the table GoTrue owns. So the identity provider and the core data shape are already decided; what is still open is how the ASP.NET Core backend and the Blazor Web App (in Auto render mode, meaning it first renders on the server, then upgrades to run in the browser) actually use GoTrue to sign someone in and keep them signed in across page loads.

The product is single user today (the founder only), but AGENTS.md records it is meant to grow to serve more people later, so the shape chosen here (real sign in, real session, gated shell) should not paint the project into a single user corner, even though the signup surface itself stays closed for now.

## Options considered

### Option 1a: Server-side cookie session, backend stores and refreshes GoTrue's own tokens

The ASP.NET Core backend calls GoTrue's REST API directly (via the official Supabase .NET client) to sign in, then keeps GoTrue's access and refresh tokens inside its own encrypted cookie, refreshing the access token on every request as it nears expiry.

**Pros**:
- If a later feature needs to call GoTrue or Supabase's PostgREST layer authenticated as the founder, the token is already on hand.

**Cons**:
- Pulls in GoTrue's refresh-token rotation semantics: two concurrent requests refreshing at once can each get a token, leaving one holding a now-revoked token and forcing a spurious sign out.
- An interactive Blazor Server circuit runs over a persistent WebSocket, which cannot send a `Set-Cookie` response mid-circuit, so a token refreshed there cannot be written back to the cookie without an extra server-side ticket store.
- Nothing in this feature's scope, or anything already decided in spec 0001, has the backend calling GoTrue or PostgREST as the signed in founder after login, so the complexity buys nothing here.

### Option 1b: Server-side cookie session, backend issues its own plain identity cookie (recommended)

The ASP.NET Core backend calls GoTrue once at sign in to check the password (and once per password reset), then forgets GoTrue's tokens entirely. It issues a normal ASP.NET Core authentication cookie carrying only the founder's identity (the GoTrue user id and email), with its own sliding expiry. No refresh token, no per-request call to GoTrue, no token ever stored.

**Pros**:
- Works correctly under Auto render mode's server-first page loads, before any WASM has downloaded.
- No GoTrue token of any kind is ever stored or reaches the browser, matching AGENTS.md's rule that the LLM/client layer never receives passwords, tokens, or session cookies for external services, and going further than Option 1a by not persisting them server-side either.
- Removes the refresh-token rotation race and the WebSocket cookie-write problem entirely, because there is no token to refresh.
- Reuses standard, well understood ASP.NET Core primitives (`AddAuthentication().AddCookie()`, `[Authorize]`), not a hand rolled mechanism.

**Cons**:
- If a future feature needs to act as the founder against GoTrue or PostgREST directly, that feature will need to add its own token handling then; this spec does not build that path in advance.

### Option 2: Client-side Supabase session (browser holds the tokens)

A JavaScript Supabase client (or a browser-side .NET GoTrue client) runs in the browser, signs in directly against GoTrue, and stores the access and refresh tokens in browser storage (`localStorage` or an in-memory JS state).

**Pros**:
- Common pattern for single page apps; less server-side session code to write.

**Cons**:
- Puts refresh tokens in the browser, directly against AGENTS.md's rule that the AI/client layer never receives session tokens or credentials.
- Fights Auto render mode: the first server render has no client-side session available yet, so gating a server-rendered page requires a second mechanism anyway.

### Option 3: Roll a fully custom auth system (no GoTrue)

Build password hashing, session tokens, and reset flows directly against the `app` schema, bypassing GoTrue entirely.

**Pros**:
- Full control over every detail of the auth flow.

**Cons**:
- Reimplements a solved, security sensitive problem (password storage, token rotation, reset flows) that GoTrue already handles correctly, for no benefit; the project already runs GoTrue specifically so it would own this (spec 0001).
- Throws away the `auth.users` foreign key relationship the data model (spec 0002) already built `Profile` on top of.

## Rationale

Auto render mode is the deciding force between server-side and client-side sessions: a page can be served fully from the server before any WASM has loaded, so any gating mechanism that only exists in browser JavaScript (Option 2) cannot protect the first render. A server-side cookie the backend checks on every request protects both render paths uniformly. AGENTS.md's rule that the LLM and client layer never receive passwords, OAuth tokens, refresh tokens, or session cookies for external services rules out putting GoTrue's tokens in the browser at all, which further rules out Option 2 regardless of Auto render.

Between the two server-side shapes, nothing in this feature, or in anything spec 0001 already decided about how the .NET backend and Supabase divide responsibility, has the backend calling GoTrue or PostgREST as the founder after sign in; Supabase is used here purely to check the password and send reset emails. Storing and refreshing GoTrue's own tokens (Option 1a) would only earn its complexity if that changed, and it introduces two problems with no offsetting benefit today: GoTrue's refresh token rotation can race under concurrent requests and revoke a token still in use, and an interactive Blazor Server circuit cannot write a refreshed cookie mid-circuit at all. A plain, self issued identity cookie (Option 1b) sidesteps both, and can be upgraded to hold a real GoTrue token later if a feature genuinely needs one.

Option 3 is rejected because spec 0001 already chose GoTrue specifically so the team does not have to own password and token security itself; reimplementing it here would contradict that decision and orphan the `Profile` to `auth.users` relationship spec 0002 already built.
