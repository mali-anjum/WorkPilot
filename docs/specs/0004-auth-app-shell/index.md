# 0004. Auth and app shell gating

**Date**: 2026-09-22
**Status**: Accepted

## Summary

This decision wires real sign in to the app shell that feature 4 already built. A single founder account signs in with email and password checked against GoTrue (Supabase's auth service, already running in this project's Docker stack). The ASP.NET Core backend checks the password with GoTrue once at sign in, then tracks the session itself with a plain, self issued cookie; it never stores or re-uses GoTrue's own access or refresh tokens, and browser code never sees any of it. Every screen behind the shell requires sign in; a visitor who is not signed in only ever sees the sign in page.

## Requirements

**User stories**:
- As the founder, I want to sign in with my email and password so that only I can reach my job search data.
- As the founder, I want to stay signed in for weeks at a time so that I am not repeatedly logging back in on a tool I use daily.
- As the founder, I want to reset my password by email if I forget it, without needing direct database access.
- As a visitor who is not signed in, I should never see any page of the app except the sign in and password reset pages.

**Acceptance criteria** (the contract, each criterion is IDed and independently checkable):
- **AC-1**: A visitor who is not signed in and requests any shell route (e.g. `/jobs`) is redirected to `/login`; after a successful sign in they land back on the route they originally requested.
- **AC-2**: A visitor who submits the correct email and password on `/login` is signed in and lands inside the shell; a visitor who submits the wrong password sees an error and stays on `/login`.
- **AC-3**: Once signed in, the session survives a browser restart for up to 30 days without requiring the founder to sign in again, sliding forward on activity, with no GoTrue token to refresh (the cookie's own expiry is the only clock).
- **AC-4**: If the session cookie itself is expired, invalid, or missing (including after a server restart that lost its signing key), the founder is redirected to `/login`, with the return-to-original-route behavior from AC-1 still applying.
- **AC-5**: A visitor can request a password reset email from `/forgot-password` by entering their email, and complete the reset on `/reset-password` using the link GoTrue emails them; a real recovery link always leads to a successful reset, and the same email address cannot be used to flood the founder's inbox with reset emails.
- **AC-6**: A signed in founder can sign out from anywhere in the shell (`/logout`), which clears the session cookie and redirects to `/login`.
- **AC-7**: No GoTrue access token, refresh token, or password ever reaches client-side (WASM/browser) code; all calls to GoTrue happen from the ASP.NET Core backend only, and the backend itself never persists a GoTrue access or refresh token past the single sign in or reset request that used it.
- **AC-8**: There is no public account creation surface anywhere in the app; the one account is created directly against GoTrue outside the application.

## Decision

**Chosen option**: Option 1b: Server-side cookie session, backend issues its own plain identity cookie.

The ASP.NET Core backend is the only thing that ever talks to GoTrue, and it only talks to GoTrue at the moment of a sign in or a password reset, through the official Supabase .NET client. It never stores GoTrue's access or refresh tokens; instead it tracks the signed in founder with its own plain ASP.NET Core authentication cookie (identity claims only, no external token inside it). Blazor's shell renders nothing behind that cookie until it is present and valid.

**Implementation skills**: `supabase` (`supabase/agent-skills`, `.agents/skills/supabase/`)

## Rationale

Reasoning and options: see [rationale.md](rationale.md).

## Feature design

**Data model sketch**:
No new entities. GoTrue already owns `auth.users` (out of this project's migration control, per spec 0002 AC-7); `Profile` (already modeled, `app.profiles`) already carries the 1:1 `AuthUserId` foreign key to it. This feature adds no columns and no migration.

**State transitions**:
Session state machine, held entirely in the ASP.NET Core auth cookie, not a database entity, and not tied to any GoTrue token lifetime:
`signed out` → (GoTrue confirms the email+password once) → `signed in, cookie issued with a sliding expiry` → (30 days pass with no activity, or explicit sign out, or the cookie fails to decrypt e.g. after a key change) → `signed out`.

**API surface**:
| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| /login | GET | returnUrl (optional, query) | login form (statically rendered, not an interactive Blazor component, so it can set cookies during Auto render's server phase) | anonymous | none |
| /login | POST | email (req), password (req), returnUrl (optional), antiforgery token | redirect to returnUrl (only if `Url.IsLocalUrl` and no protocol-relative `//` prefix) or shell root, sets session cookie carrying claims `sub` (GoTrue user id), `email`, and `profileId` (`Profile.Id`) | anonymous | 401 invalid email or password (one identical message whether the email is unknown or the password is wrong, and whether or not the GoTrue account is unconfirmed, so no distinction leaks); 400 missing antiforgery token |
| /logout | POST | antiforgery token | redirect to /login, clears session cookie | authenticated | none |
| /forgot-password | GET | none | request form | anonymous | none |
| /forgot-password | POST | email (req) | confirmation message (always shown, identical whether or not the email exists or the rate limit was hit) | anonymous | none surfaced to the caller (GoTrue's own per-email rate limit silently drops excess requests; see Key invariants) |
| /reset-password | GET | token_hash (req, query, from GoTrue's emailed link) | new password form | anonymous | 400 invalid/expired token_hash |
| /reset-password | POST | token_hash (req), newPassword (req, must meet GoTrue's configured minimum length) | redirect to /login on success | anonymous | 400 invalid/expired token_hash, 422 password shorter than the configured minimum (message names the minimum) |
| (all shell routes) | GET | — | shell content, or redirect to /login if unauthenticated | authenticated | 302 to /login with returnUrl on missing/invalid/expired session cookie |

**Value sourcing** (name the source of every value each action produces, computes, or displays; a required value with no named source is an undecided input to resolve now, never one for the build to invent):
| Action | Value produced / displayed | Source |
|---|---|---|
| Redirect to /login on a gated route | returnUrl | the originally requested route's path, captured by the auth middleware before the redirect |
| POST /login success | redirect target | returnUrl if present, a local path (`Url.IsLocalUrl`), and not protocol-relative; else the shell root (`/`) |
| POST /login | the session cookie's claims | `sub`/`email` from GoTrue's sign-in response; `profileId` from looking up (or, on a first ever sign in, creating) the `Profile` row whose `AuthUserId` matches `sub` |
| POST /login | the 401 error message shown | a single fixed string, not sourced from GoTrue's specific error reason, so unknown-email vs wrong-password vs unconfirmed-account are never distinguished |
| Every request | whether to silently extend the cookie's expiry | ASP.NET Core cookie authentication's built in `SlidingExpiration`, not custom logic |
| GET /reset-password | the reset token | the `token_hash` query parameter of the link GoTrue emailed (GoTrue's `{{ .TokenHash }}` recovery template value, verified server-side against GoTrue's `/verify` endpoint, never GoTrue's default fragment-based link, which the server never receives) |
| POST /reset-password | the 422 minimum length shown | `GOTRUE_PASSWORD_MIN_LENGTH`, read from the same GoTrue config this project's Docker Compose already sets |
| POST /forgot-password | confirmation message shown | a fixed, static message; never varies by whether the email exists or GoTrue's rate limit was hit, so no data is sourced from a lookup |

**Key invariants**:
- No page under the shell renders any content, even partially, for a request without a valid, current session cookie.
- The session cookie is set with `HttpOnly` and `Secure`, and is never readable by browser-side JavaScript or WASM code.
- The account password, and GoTrue's own access/refresh tokens, never appear in any response body, log line, client-side state, or the session cookie itself; the cookie carries only the founder's identity claims (AC-7).
- Exactly one account exists in `auth.users` for the lifetime of this feature; nothing in the app can create another.
- All state-changing POSTs (`/login`, `/logout`, `/reset-password`, `/forgot-password`) carry ASP.NET Core's antiforgery token; a request missing or failing it is rejected before touching GoTrue or the session.
- The ASP.NET Core Data Protection key ring the cookie is encrypted with is persisted outside the container's writable layer (see Configuration required), so a routine container restart or redeploy does not silently invalidate every signed in session.

**Security model**:
Single role today (the founder); no role or ownership model needed yet since there is exactly one account. Every shell route requires the ASP.NET Core cookie authentication scheme (`RequireAuthorization()` on `MapRazorComponents`, so no route can be added later without inheriting the gate). `/login`, `/forgot-password`, and `/reset-password` are the only anonymous-reachable routes. Failed sign in attempts and reset email volume are GoTrue's own responsibility (already running, already rate limits its auth and recovery endpoints); this feature adds no custom lockout or rate limit tracking, and its 401/reset responses stay generic enough (a single fixed message; see API surface) not to reveal which case GoTrue rejected. Login and logout forms are statically rendered (never an interactive Blazor component), because only a statically rendered page can set or clear the auth cookie during Auto render's server phase; the client-side (WASM) half of the app receives the signed in state via Blazor's persistent component state, not by reading the cookie itself.

**Configuration required**:
- `Supabase__Url`: the self hosted GoTrue/Supabase stack's base URL, already used by other parts of this project per spec 0001.
- `Supabase__AnonKey`: the anon key the Supabase .NET client authenticates its own requests with, already generated per spec 0001.
- The ASP.NET Core Data Protection key ring (the key material the session cookie is encrypted with): built using the same default local file system key storage `WorkPilot.Api` already uses for `OAuthConnection` tokens (`~/.aspnet/DataProtection-Keys`), since this project deploys as plain processes on one VPS (`AddProject` in AppHost, not swappable containers), so the default survives an ordinary restart; only a container image swap without a mounted volume would lose it, which does not apply here.
- GoTrue's own config (already set in this project's `supabase/docker-compose.yml` and `.env` per spec 0001, not new to this feature, but load bearing for AC-5 and the reset flow's 422 message): `GOTRUE_PASSWORD_MIN_LENGTH`, `GOTRUE_SITE_URL` and its allowed `redirect_to` list (must include this app's `/reset-password` route), the recovery email template (set to use `{{ .TokenHash }}`, see Value sourcing), and outbound SMTP settings so reset emails actually send.

**Critical test scenarios** (each maps to an acceptance criterion in ## Requirements):
- Happy path: founder enters correct email and password on /login, lands on the shell root with the sidebar and top bar visible, verifies **AC-2**
- Failure case: an anonymous request to /jobs redirects to /login?returnUrl=/jobs, and a subsequent successful sign in lands back on /jobs, verifies **AC-1**
- Failure case: a session cookie that is missing, expired, or fails to decrypt (simulating a Data Protection key change) redirects to /login with the returnUrl preserved, with no GoTrue call involved at all, verifies **AC-3**, **AC-4**
- Failure case: a real GoTrue recovery link completes a password reset end to end, and a second reset request for the same email within GoTrue's rate limit window still returns the same generic confirmation message without sending a second email, verifies **AC-5**
- Auth/permission: an unauthenticated request to any shell route never renders shell content, even partially, before redirecting, verifies **AC-1**, **AC-7**
- Auth/permission: signing out clears the cookie and a subsequent request to any shell route redirects to /login, verifies **AC-6**
- Auth/permission: no route in the app accepts an unauthenticated request that creates an `auth.users` row, verifies **AC-8**

## Build plan

1. [x] Add ASP.NET Core cookie authentication (`AddAuthentication().AddCookie()` with `SlidingExpiration` and a 30 day window) and persist its Data Protection key ring to Postgres (or a mounted volume), with startup validation that the key store is reachable, satisfies **AC-3**, **AC-4** — built with the same default local key storage `WorkPilot.Api` already uses for `OAuthConnection` (survives an ordinary process restart on this VPS deployment model; revisit only if the deployment moves to swappable containers)
2. [x] Wire the Supabase .NET client (GoTrue) into the backend for sign in and password reset calls only, reading `Supabase__Url` / `Supabase__AnonKey` with startup validation, and confirm it never persists a GoTrue token past the request that used it, satisfies **AC-7**
3. [x] Build `/login` (GET + POST, statically rendered, antiforgery protected), checking the password against GoTrue, resolving/creating the `Profile` row, and issuing the plain identity cookie on success, with the validated returnUrl redirect behavior and the single generic 401 message, satisfies **AC-1**, **AC-2**, **AC-7**
4. [x] Gate the shell: require authentication at the shell's root layout so every current and future route under it inherits the gate, and wire Blazor's authentication state persistence so the WASM half of Auto render mode sees the signed in state without touching the cookie itself, satisfies **AC-1**, **AC-7**
5. [x] Build `/logout` (statically rendered, antiforgery protected), clearing the session cookie, satisfies **AC-6**
6. [x] Build `/forgot-password` and `/reset-password`, calling GoTrue's reset endpoints against the `token_hash`/`/verify` flow (not GoTrue's default fragment link), and configure GoTrue's recovery email template, `GOTRUE_SITE_URL`/`redirect_to`, `GOTRUE_PASSWORD_MIN_LENGTH`, and SMTP settings, satisfies **AC-5**
7. [x] Confirm no public signup route exists anywhere in the routed pages, and document creating the one account directly via GoTrue's admin API/Supabase Studio, satisfies **AC-8** — `GOTRUE_DISABLE_SIGNUP` now defaults to `true`

## Consequences

**Positive**:
- Every screen built after this feature is safe by default: a new route under the shell is gated automatically, nothing has to remember to add `[Authorize]`.
- Tokens and passwords never reach browser code, closing off a whole class of client-side credential leaks before any domain feature is built.
- Reuses infrastructure (GoTrue) already running for this project; no new service to operate.

**Negative / tradeoffs**:
- The backend now owns real session plumbing (cookie issuance, sliding expiry, Data Protection key persistence); a bug here locks the one founder out of their own tool.
- A 30 day silently renewed session is more convenient but a stolen or left-open device stays signed in longer than a short session would; acceptable for a single-user personal tool per the confirmed requirement, but worth remembering if the product later serves people beyond the founder.
- Because the backend never stores a GoTrue token, any future feature that needs to call GoTrue or Supabase's PostgREST layer as the founder (not needed by anything in scope today) will have to add that token handling itself rather than reusing this session.

**Neutral**:
- No public signup route exists; onboarding a second user later (per AGENTS.md's "designed so it could serve more later") will need a deliberate follow-up decision, not an assumption this feature makes for free.

## Follow-up

- [ ] When the product moves beyond a single founder account, revisit the "no public signup" decision (AC-8) and the single-role security model; both were deliberately scoped to one user today.
- [ ] `supabase` and `supabase-postgres-best-practices` skill conventions are already referenced in root AGENTS.md; no new AGENTS.md update needed for this feature.
