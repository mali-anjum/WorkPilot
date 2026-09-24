# Epic: Foundation

Everything later features stand on: the stack, the shared data model, the design system, the app shell, and the agent orchestrator's core machinery (planner, policy, tools, workflow, approvals, verification). Nothing in Slice 1 starts until the walking skeleton here boots.

### 1. Stack & architecture · done
.NET Aspire modular monolith (Blazor Web App, Auto render mode, ASP.NET Core backend, EF Core) sharing one self hosted Supabase Postgres database (Supabase owns auth/storage/simple CRUD, the .NET backend owns the Agent/workflows/approvals/browser automation), Hangfire for durable jobs, Playwright for browser automation, Microsoft.Extensions.AI for the provider abstraction, all self hosted on one VPS via Docker Compose.
**Done when:** the stack is recorded in a spec, the empty scaffold boots locally (`dotnet build` + `aspire run`), and the modular monolith module boundaries (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) are reflected in the folder structure.
- [x] Decide the stack (spec): `/architect stack & architecture`
- [x] Build it: `/develop stack & architecture`
   - [x] Scaffold the .NET Aspire solution (AppHost, Web, Application, Domain, Infrastructure, Workers, Contracts, AI projects) (AC-1, AC-3, AC-4)
   - [x] Stand up self hosted Supabase (Postgres, GoTrue, Storage) via Docker Compose and connect EF Core to the same database (AC-2, AC-5)
   - [x] Wire Hangfire against the shared Postgres database and confirm its dashboard (AC-6)
- [x] Verify it: `/check verify stack & architecture`
- [x] Test it: `/test stack & architecture`
- [x] Review it: `/check review stack & architecture`
Spec 0001 · code in `./`

**/check review (2026-09-20): Changes requested → fixed.** Reviewed by claude-opus-5 (author: sonnet). 1 blocker, 3 major, all fixed same session (full findings: `docs/reviews/2026-09-20-main.md`):
- Blocker: a real Supabase Postgres password was hardcoded in `HealthDbEndpointTests.cs`. Removed; tests now require `WORKPILOTDB_CONNECTION` and fail loudly if unset. Confirmed the value never reached git history (the file was untracked), so no rotation was needed.
- Major: `Database.MigrateAsync()` was running on every `/health/db` request (DDL on an unauthenticated GET). Moved to a one-shot call at startup.
- Major: the `Hangfire:DisableServer` test-only flag failed silently toward "no background processing". Added an explicit startup warning log when it's set.
- Major: EF Core's tables were landing in Postgres's shared `public` schema (what Supabase's future PostgREST layer exposes by default), contradicting the DbContext's own doc comment. Added `modelBuilder.HasDefaultSchema("app")` and regenerated the `InitialCreate` migration.
Remaining minors/nits (env var test isolation, no negative test for dashboard auth, brittle JSON string assertions, centralizing package versions) are left for the "Coding standards & tooling" scope item or a future pass; not release blockers.

**/test (2026-09-20): PASS, 2/2.** `tests/WorkPilot.Api.Tests` (xUnit + `WebApplicationFactory<Program>`), scoped to the one real integration point in the scaffold — `GET /health/db` (EF Core round-trip) and `GET /hangfire` (dashboard reachability) — since the rest of the scaffold is framework boilerplate with no behavior worth locking in yet. Run against the real self hosted Supabase Postgres (port 5433), not a mock:
```
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 2 s
```
Three real bugs found and fixed while writing these tests (not test-side workarounds):
- `GET /health/db` called `Database.EnsureCreatedAsync()`, which is a no-op once the target *database* already exists (true here — Supabase's `postgres` database already has other schemas) — so `scaffold_pings` was never actually created and every real request 500'd. Switched to EF Core migrations (`Database.MigrateAsync()`), added the `InitialCreate` migration.
- `AddHangfireServer` (the background worker/watchdog threads) doesn't cooperate with graceful host shutdown inside `WebApplicationFactory.Dispose()` — every test run hung ~60s past a `TaskCanceledException` in `Hangfire.Server.BackgroundProcessingServer.WaitForShutdownAsync`, timeout tuning alone didn't fix it. Fixed by making the worker server itself skippable via `Hangfire:DisableServer` config (off by default; only the tests set it) — the dashboard/client registration (`AddHangfire`) stays on regardless, so `/hangfire` is still exercised for real.
- The test project's transitive `Microsoft.EntityFrameworkCore.Relational` version (10.0.11, via `Microsoft.AspNetCore.Mvc.Testing`) conflicted with the app's pinned 10.0.12, breaking migrations at runtime with a silent 500. Pinned the test project to 10.0.12 to match.
Also confirmed the Hangfire dashboard's default local-only authorization filter was correctly rejecting `TestServer` requests (it never populates `RemoteIpAddress`) — not a bug, so the test simulates a loopback request via an `IStartupFilter` rather than loosening the app's real authorization.

Test tier is Beta: all boxes in this feature are now checked. Next: engineer's call whether to mark this feature `done` (and advance spec 0001 status `In Progress` → `Accepted`), or continue with `/check review` → `/document` → `/sync`.

**/check verify (2026-09-20): PASS.** All 6 acceptance criteria met, exercised fresh this run:
- AC-1: `dotnet build WorkPilot.slnx` → Build succeeded, 0 Warning(s), 0 Error(s).
- AC-2: `dotnet run --project src/WorkPilot.AppHost` → postgres, workpilotdb, api, and web resources all reached `Running` in the Aspire CLI log within ~3s of each other.
- AC-3: `curl https://localhost:44497/` (the Web resource's endpoint) → HTTP 200, real Blazor markup, `<title>Home</title>`.
- AC-4: `ls src/WorkPilot.Domain/Modules src/WorkPilot.Application/Modules` → all 13 named modules present (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit).
- AC-5: ran `src/WorkPilot.Api` standalone with `ConnectionStrings__workpilotdb` pointed at the self hosted Supabase stack's Postgres (`supabase/docker-compose.yml`, port 5433, already up and healthy: GoTrue `/health` and Storage `/status` both HTTP 200) → Api started cleanly, no connection errors, Hangfire installed against that same database.
- AC-6: Hangfire dashboard → HTTP 200 both against Aspire's dev Postgres (`/hangfire` on the AppHost run) and against the real Supabase Postgres (the standalone Api run above); `\dt hangfire.*` on both databases shows all 12 Hangfire tables created.

All started processes and dev containers (Aspire's AppHost tree, its dev postgres/pgadmin containers) were stopped and cleaned up after verification; the Supabase compose stack (`supabase/`) was left running since it's durable project infra, not a verify artifact.
Next: `/test stack & architecture`.

**Verified locally (2026-09-20):** `dotnet build` succeeds clean (0 warnings after pinning `Newtonsoft.Json` to 13.0.3 to clear a transitive advisory from Hangfire.Core). `dotnet run --project src/WorkPilot.AppHost` boots Postgres, the Api, and the Blazor Web project together; the Web home page returns HTTP 200. All 13 module folders (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) are present under `WorkPilot.Domain/Modules` and `WorkPilot.Application/Modules`.

The self hosted Supabase stack (`supabase/docker-compose.yml`: Postgres + GoTrue + Storage) is up and verified for real, not just against Aspire's own dev Postgres container: `docker compose up` in `supabase/` brings up all three healthy (GoTrue `/health` and Storage `/status` both HTTP 200), and running the Api standalone with its connection string pointed at that same Postgres (port 5433) shows EF Core and Hangfire both connecting to it live, with Hangfire's 12 tables created there and its dashboard returning HTTP 200.

Getting there required fixing a few real bugs in the original scaffold, worth knowing about:
- `supabase/.env` mounted a host folder over the image's own `/docker-entrypoint-initdb.d`, silently deleting the image's built-in role/schema bootstrap (the actual cause of every earlier failure). Fixed by mounting individual files into its `init-scripts/`/`migrations/` subdirectories instead, matching Supabase's own official compose.
- The pinned image versions (Postgres 15.8.1.060 + GoTrue v2.164.0 + Storage v1.19.3) were a known-bad combination (GoTrue's migrations conflicted with the image's own). Repinned to the versions Supabase's own compose currently ships together: Postgres 17.6.1.136, GoTrue v2.196.0, Storage v1.74.0.
- `supabase/.env`'s `ENABLE_EMAIL_AUTOCONFIRM=true` had a trailing inline `#` comment, which `.env` files don't support — it would have been read as part of the value. Moved to its own comment line.
- `ANON_KEY`/`SERVICE_ROLE_KEY` were placeholder strings, not real JWTs; generated real ones signed with the `.env`'s `JWT_SECRET`.
- Default `POSTGRES_PORT=5432` collided with a pre-existing local system Postgres on this machine; moved the stack to 5433.

**Follow-up:** none outstanding for the scaffold itself. Deferred by design (documented in the compose file header): Kong, PostgREST, Realtime, Supabase Studio — add if/when the Blazor client needs to talk to Supabase directly.

### 2. Coding standards & tooling · done
Capture conventions (lint, format, commit hooks, test runner) from the real scaffolded project.
**Done when:** root `AGENTS.md` reflects the real stack, and lint/format/pre-commit run clean.
- [x] Capture conventions + tooling: `/audit`

`/develop` (2026-09-21): wired `.editorconfig`, `dotnet format WorkPilot.slnx --verify-no-changes` passes clean, and a format only pre-commit hook (`.githooks/pre-commit`, `git config core.hooksPath .githooks`) runs it on staged `.cs` files. Code in `.editorconfig`, `.githooks/`.

### 3. Data model · done
Core entities from the product spec: Users, Profiles, Skills, Experiences, Education, Resumes/Versions, CoverLetters/Versions, Jobs, JobSources, JobSnapshots, JobMatches, JobApplications, ApplicationAnswers, ApplicationEvents, Universities, Programs, Professors, ResearchAreas, Scholarships, OutreachContacts, OutreachMessages, EmailThreads, FollowUps, Tasks, CalendarEvents, AgentRuns, AgentSteps, ToolCalls, Approvals, AuditLogs, Workflows, WorkflowSteps, WorkflowEvents, Integrations, OAuthConnections, Notifications.
**Done when:** the schema supports every entity above with real relationships, migrations apply cleanly, and provenance fields (source URL, retrieved/verified timestamps, confidence) exist on every externally sourced record.
- [x] Design it (spec): `/architect data model` → [specs/0002-data-model/index.md](../specs/0002-data-model/index.md)
- [x] Build it: `/develop data model`
  1. [x] Define the `Provenance` owned type and apply it to every externally sourced entity (AC-2)
  2. [x] Model Identity/Profile, Resume/CoverLetter (immutable versions), Jobs, and Applications incl. the `JobApplication` state machine (AC-1, AC-4, AC-5)
  3. [x] Model Universities, Outreach, Personal, Integrations/Notifications, Approvals/Audit (AC-1)
  4. [x] Model Agent/Workflow entities with Storage backed payload references (AC-1, AC-8)
  5. [x] Add the global soft delete query filter to every soft deleted entity (AC-3)
  6. [x] Wire ASP.NET Core Data Protection for `OAuthConnection` tokens (AC-6)
  7. [x] Generate and apply the single initial migration, confirm it targets only the `app` schema (AC-1, AC-7)
- [x] Verify it: `/check verify data model`
- [x] Test it: `/test data model`

`/develop` (2026-09-21): all 39 tables live in the self hosted Supabase Postgres `app` schema (confirmed via `\dt app.*`), `auth.*`/`storage.*` untouched. `ScaffoldPing` removed (superseded by the real model); `/health/db` now counts `profiles`. Code in `src/WorkPilot.Domain/Modules/*/Entities.cs` (+ `Applications/JobApplication.cs`), `src/WorkPilot.Infrastructure/Persistence/`.

**/check verify (2026-09-21): PASS.** All 8 acceptance criteria met, exercised live against the self hosted Supabase Postgres stack (port 5433): schema applied cleanly (`has-pending-model-changes` → none), all 39 tables present under `app`, `auth.*`/`storage.*` table count unchanged (33) confirming EF never touched them, provenance columns required non-null on all 6 externally sourced entities, both integration tests pass, and a scratch run against the live DB proved the `JobApplication` state machine rejects invalid transitions, a soft deleted `Job` disappears from default queries while its `JobApplication`/`ApplicationEvent` history stays intact, a second `ResumeVersion` never mutates the first, `OAuthConnection.AccessToken` reads back as ciphertext via raw SQL, and a stale `WorkflowEvent` is queryable by its retention cutoff. Full evidence in [specs/0002-data-model/verify.md](../specs/0002-data-model/verify.md). Next: `/test data model`.

**/test (2026-09-21): PASS, 22/22.** New `tests/WorkPilot.Domain.Tests` (xUnit, wired into `WorkPilot.slnx`, no infrastructure/database per `AGENTS.md`): the `JobApplication` state machine (every valid transition plus every skipped/terminal rejection, AC-4) and the shared `Entity`/`SoftDeletableEntity` base behavior (unique time ordered ids, soft delete flags, AC-3's domain half). The database dependent criteria (AC-1, AC-2, AC-6, AC-7, AC-8, and AC-3's query filter half) stay covered by `verify.md`'s live Postgres evidence per this project's "infrastructure is integration tested against real systems, never mocked" rule; not re-asserted here.
```
Passed!  - Failed: 0, Passed: 22, Skipped: 0, Total: 22, Duration: 80 ms
```

### 4. Design system & UI foundation · done
Token based visual language (colors, type, spacing, radius per the product spec section 4), base components (AppShell, Sidebar, TopBar, PageHeader, Card, Button, Badge/StatusBadge, EmptyState, Modal, CommandPalette now; Input, Select, Tabs, DataTable, Skeleton, Timeline, Stepper, Toast deferred to the first real screen that needs each), light and dark mode (cookie backed, no flash on Blazor Auto render).
**Done when:** `design.md` covers type/color/spacing/components, base components handle focus and keyboard, and the app shell renders the full navigation (Overview, Work, Personal, Agent, System sections) with empty pages behind each route.
- [x] Design it (spec): `/architect design system & UI foundation` → [0003](../specs/0003-design-system-ui-foundation/index.md)
- [x] Build it: `/develop design system & UI foundation`
  - [x] Tokens + cookie based theme mechanism + tracer thread (AppShell/Sidebar/TopBar/PageHeader wired through `/`) — AC-1 (partial), AC-2 (partial), AC-5, AC-6
  - [x] Static display components: Card, Badge/StatusBadge, EmptyState — AC-3
  - [x] Button, Modal, CommandPalette with full keyboard/focus support + global Cmd/Ctrl+K — AC-3, AC-4, AC-7
  - [x] Wire every remaining stub route; `/design` checklist page; `docs/design.md` — AC-1, AC-2, AC-3, AC-8
- [x] Verify it: `/check verify design system & UI foundation`
- [x] Test it: `/test design system & UI foundation`

`/develop` (2026-09-21): all 10 in-scope components built (`src/WorkPilot.Web.Client/Shared/`), the theme cookie mechanism, all 11 stub routes, the `/design` checklist page, and `docs/design.md`. A real Blazor Auto render mode bug surfaced during the build (not caught by the design gate): the render mode can't be set on a nested `AppShell` component receiving `ChildContent` across the static-to-interactive boundary (`RenderFragment` isn't serializable across it). Fixed by setting `@rendermode InteractiveAuto` once on `<Routes>` in `App.razor` instead, and forwarding the server-read theme cookie down as a named `string` cascading value (`InitialTheme`) rather than reading `HttpContext` again inside the now-interactive `MainLayout`. Verified live: dark is the default on `/`, a `workpilot-theme=light` cookie flips `data-theme` in the first server-rendered byte (no flash) confirmed via `curl`, and all 11 routes plus `/design` return 200. 8 new bUnit tests (`tests/WorkPilot.Web.Tests`, wired into `WorkPilot.slnx`) cover Modal/CommandPalette Escape-to-close and Button click; `dotnet format --verify-no-changes` passes. DOM tab order and the rendered focus ring are native browser behavior bUnit can't exercise; left for manual `/check verify`. Code in `src/WorkPilot.Web.Client/Shared/`, `src/WorkPilot.Web/Components/`, `src/WorkPilot.Web/wwwroot/css/tokens.css`, `src/WorkPilot.Web/wwwroot/js/interop.js`, `docs/design.md`.

**/check verify (2026-09-21): PASS.** All 12 verify.md behaviors confirmed with cited evidence; no AC missing or unapplied. Command line checks: `dotnet build` 0 errors, `dotnet test tests/WorkPilot.Web.Tests` 8/8 pass, `dotnet format --verify-no-changes` clean, all 11 routes + `/design` return 200, theme cookie flips `data-theme` in the first byte both ways. This session had no browser automation tool connected, so it installed Playwright (`npm install playwright` + system Chrome, no sudo needed) into a scratch dir and drove the real app with it to close the remaining interactive checks: theme toggle click flips `data-theme` with no navigation and survives reload; 20 sequential Tabs from `/` land on all 11 nav links then the theme toggle, each with a visible `outline: solid 2px` and `:focus-visible` true, in DOM order; a real `Control+k` keydown sent to `body` (not an input) on `/jobs` opens the CommandPalette with focus landing in its search input, `Escape` closes it; the `/design` page's Modal traps 15 consecutive Tabs inside `[role="dialog"]`, `Escape` closes it and returns focus to the "Open modal" button; clicking the Jobs sidebar link adds the `active` class. Full evidence and the exact steps in [specs/0003-design-system-ui-foundation/verify.md](../specs/0003-design-system-ui-foundation/verify.md). Next: `/test design system & UI foundation`.

**/test (2026-09-21): PASS, 42/42.** Focused this pass on the shared components and route data (10 changed files were already covered by earlier tests; 11 near-identical stub pages deferred, see below), extending `tests/WorkPilot.Web.Tests`: `AppShellTests` (renders Sidebar + TopBar + routed content together, CommandPalette starts closed, forwards `InitialTheme`), `SidebarTests` (all 5 sections/11 links with literal hrefs, active highlight on `/` and after navigating, covers AC-2), `TopBarTests` (starts in sync with the server resolved theme, toggle flips label/aria-label both directions), `BadgeTests` (each `StatusKind` maps to its own token class, default is neutral), `CardTests` (optional title shown/omitted, child content renders), `EmptyStateTests` (required title, optional description/icon shown or omitted, icon is `aria-hidden`), `PageHeaderTests` (title as `h1`, optional description), and `NavRoutesTests` (the 5 sections and all 11 routes match spec 0003's literal Route map exactly, no duplicate hrefs, covers AC-2). All green on the first run, no real bugs found.
```
Total tests: 42
     Passed: 42
 Total time: 1.9447 Seconds
```
`interop.js` (no JS test runner in this stack, its DOM effects are covered by the Playwright evidence in `verify.md`) and `App.razor`/`MainLayout.razor`'s theme cookie resolution (server rendering logic, covered by the curl evidence in `verify.md`, not bUnit-testable) stay out of scope for bUnit. Test tier is Beta: both boxes now checked.

**/test follow up (2026-09-21): stub page batch, PASS, 13/13 new (55/55 total).** Added `WorkPilot.Web.csproj` as a project reference to `WorkPilot.Web.Tests.csproj` (the 11 stub pages plus `Home`/`Design` live in `WorkPilot.Web`, not `WorkPilot.Web.Client`), then wrote one test per stub page (`JobsTests`, `ApplicationsTests`, `UniversitiesTests`, `OutreachTests`, `CalendarTests`, `TasksTests` (as `TasksPageTests` to keep the class name unambiguous), `AgentRunsTests`, `ApprovalsTests`, `SettingsTests`, `IntegrationsTests`, `HomeTests`) confirming its `PageHeader` title and `EmptyState` title render, plus `DesignTests` (covers AC-3: all 10 in-scope components have a card on `/design`, and the page's example Modal starts closed and opens on click). Written and run one file at a time; all green immediately, no real bugs found.
```
Total tests: 55
     Passed: 55
```
`dotnet format --verify-no-changes` stays clean. Nothing left uncovered for this feature beyond the JS-interop/server-rendering items noted above.

### 5. Auth & app shell · done
Single user authentication, secure sessions, and the persistent shell (sidebar, top bar, command palette shortcut, global search stub) every screen mounts inside.
**Done when:** the user can sign in, land on an empty dashboard inside the real shell, and every route in the nav is reachable (even if its content is a stub).
- [x] Design it (spec): `/architect auth & app shell` → [0004](../specs/0004-auth-app-shell/index.md)
- [x] Build it: `/develop auth & app shell`
  - [x] Cookie auth foundation: sliding-expiry session cookie with persisted Data Protection keys, and the GoTrue client wired for sign-in/reset only, no token storage (AC-3, AC-4, AC-7)
  - [x] Sign in and shell gating: `/login` (statically rendered, antiforgery protected, returnUrl redirect), the shell-wide `RequireAuthorization()` gate, and WASM auth state persistence (AC-1, AC-2, AC-7)
  - [x] Sign out: `/logout` (AC-6)
  - [x] Password reset: `/forgot-password` + `/reset-password` against GoTrue's `token_hash`/`/verify` flow, plus GoTrue email/recovery config (AC-5)
  - [x] Confirm no public signup surface exists; document the one-time account creation via GoTrue admin API (AC-8)
- [x] Verify it: `/check verify auth & app shell`
- [x] Test it: `/test auth & app shell`

`/check verify` (2026-09-22): PASS, all 8 acceptance criteria (AC-1 through AC-8) confirmed live against the running app, GoTrue, and the real Postgres schema; see [verify.md](../specs/0004-auth-app-shell/verify.md) for the full checklist and evidence. Reused the founder account from the earlier build session (password reset via GoTrue's admin API for a known credential), drove every flow through curl (no browser MCP connected): unauthenticated redirect with returnUrl, correct and wrong password sign in, a real end to end password reset (requested `/forgot-password`, pulled the live `recovery_token` from `auth.users` as the `token_hash`, completed `/reset-password`, signed in with the new password), sign out clearing the session, a corrupted cookie redirecting same as a missing one, and GoTrue's `/signup` rejecting with `signup_disabled`. One command step in verify.md (profile row count incrementing on a first ever sign in) was left unticked, not because it failed, it wasn't re exercised this run since the founder's profile row already existed from the earlier build session. `dotnet test` reconfirmed: `WorkPilot.Web.Tests` (55/55) and `WorkPilot.Api.Tests` (2/2) pass; the one `WorkPilot.Domain.Tests.EntityBaseTests.Entity_IdsAreTimeOrdered` failure is the same pre-existing, unrelated flaky UUIDv7 timing test noted during the build.

`/develop` (2026-09-22): server-side sign in with a plain, self issued identity cookie (no GoTrue token ever stored), per spec [0004](../specs/0004-auth-app-shell/index.md). `/login`, `/logout`, `/logout-confirm`, `/forgot-password`, `/reset-password` are plain minimal API endpoints (not routed Blazor components), since an interactive circuit can't issue a `Set-Cookie` response; every Blazor route is gated via `RequireAuthorization()` on `MapRazorComponents`, with `AuthorizeRouteView` as a second layer for a session that expires mid-circuit. The founder's identity crosses into the WASM half of Auto render mode via Blazor's `PersistentComponentState` (a hand-written `PersistingAuthenticationStateProvider`/`PersistentAuthenticationStateProvider` pair — the framework has no single built-in extension method for this, despite some docs suggesting otherwise). The Api project stays the sole owner of `WorkPilotDbContext`; Web resolves/creates the founder's `ProfileId` via a new internal-only `POST /internal/identity/profile` endpoint on Api (never externally exposed, same as the rest of Api). `supabase/docker-compose.yml` and `.env(.example)` now set `GOTRUE_DISABLE_SIGNUP=true` (AC-8, confirmed live: a signup attempt now gets `422 signup_disabled`), `GOTRUE_PASSWORD_MIN_LENGTH`, and `GOTRUE_MAILER_URLPATHS_RECOVERY=/reset-password`. Verified live end to end against the real self hosted Supabase stack (not mocked): an admin-created founder account signs in, lands back on the originally requested route, a wrong password is rejected with a generic message, `/jobs` redirects unauthenticated visitors to `/login?returnUrl=/jobs`, sign out clears the cookie, and a real password recovery email request reaches GoTrue (`user_recovery_requested` in its audit log). Code in `src/WorkPilot.Web/Features/Auth/`, `src/WorkPilot.Web/Program.cs`, `src/WorkPilot.Web/PersistingAuthenticationStateProvider.cs`, `src/WorkPilot.Web/Components/Routes.razor`, `src/WorkPilot.Web.Client/PersistentAuthenticationStateProvider.cs`, `src/WorkPilot.Application/Modules/Identity/`, `src/WorkPilot.Infrastructure/Modules/Identity/`, `src/WorkPilot.Api/Program.cs`, `supabase/`.
One pre-existing, unrelated test failure noted, not caused by this build: `WorkPilot.Domain.Tests.EntityBaseTests.Entity_IdsAreTimeOrdered` (a flaky UUIDv7 time-ordering assertion); `WorkPilot.Web.Tests` (55/55) and `WorkPilot.Api.Tests` (2/2) pass clean.

`/test` (2026-09-22): 15 new tests, all passing. `tests/WorkPilot.Api.Tests/IdentityProfileEndpointTests.cs` (2 tests, integration, against the real Postgres): `POST /internal/identity/profile` creates a `Profile` row on a first ever sign in (name derived from the email's local part) and returns the same `ProfileId` idempotently on a second call for the same `AuthUserId`, never creating a second row (spec 0004, Value sourcing: `profileId`). `tests/WorkPilot.Web.Tests/AuthEndpointsTests.cs` (13 tests, unit, via reflection since the helpers are private): `SafeLocalRedirectTarget` (open redirect prevention, AC-1 and the API surface's `Url.IsLocalUrl`/no `//`-prefix rule) and `Html` (XSS-safe encoding of untrusted form input echoed back into the sign in/reset pages). Not covered by an automated test, deferred to `/check verify`'s live steps: the sign in/sign out/reset flows themselves (need a live GoTrue, a live session cookie, and the Data Protection key ring; see `verify.md`), and AC-3/AC-7/AC-8, which are runtime/config properties rather than something a unit or integration test can pin down. `dotnet test` (full suite) and `dotnet format --verify-no-changes` both clean.

### 6. Agent orchestrator core · GA · done
The shared machinery every domain agent runs on: Planner, Policy Engine, Tool Registry, Workflow Engine, Approval Engine, Memory, Execution Engine, Verification Engine. Tools declare name, schema, required permissions, risk level, approval requirement, timeout, retry policy, audit policy. Workflows persist step by step, never rely on an in-memory process surviving. The LLM never mutates important state directly, only through tools; it never receives passwords, OAuth tokens, refresh tokens, client secrets, or session cookies.
**Done when:** a trivial end to end tool call (e.g. "list my profile") runs through Planner -> Policy Engine -> Tool Registry -> Execution -> Verification -> Audit, is durably persisted as an AgentRun/AgentSteps/ToolCalls, and survives a process restart mid run.
- [x] Design it (spec): `/architect agent orchestrator core` → [0005](../specs/0005-agent-orchestrator-core/index.md)
- [x] Build it: `/develop agent orchestrator core`
  - [x] Migration + `ITool` contract, Tool Registry, and the two milestone tools (auto-allowed "list my profile", approval-required dummy) (AC-1, AC-2, AC-3, AC-4, AC-6, AC-9)
  - [x] Planner (`PlanRun` job) + Policy Engine (upfront plan validation) (AC-1, AC-2, AC-7)
  - [x] Execution Engine (`AdvanceRun` job) + Verification Engine + Audit wiring (AC-3, AC-5, AC-8, AC-9)
  - [x] Trigger/status/decide endpoints (`/internal/agent/runs`, `/internal/agent/approvals/{id}/decide`) + Approval Engine suspend/resume (AC-4, AC-10)
  - [x] Prove the auto-allowed and approval-required threads live; AC-9's crash scenarios and AC-10's true concurrent case are code-level guarded but not yet exercised by an actual kill/race, left for `/check verify` (AC-1, AC-3, AC-4, AC-5, AC-6)
- [x] Verify it: `/check verify agent orchestrator core`
- [x] Test it: `/test agent orchestrator core`
- [x] Review it (fresh model): `/check review agent orchestrator core` → [review](../reviews/2026-09-24-feat-agent-orchestrator-core.md)
- [x] Document it: `/document agent orchestrator core`

`/test` (2026-09-24): 12 tests, all passing against the real Postgres. `tests/WorkPilot.Api.Tests/AdvanceRunJobTests.cs` (8 tests, integration, drives `AdvanceRunJob` directly with a scripted `ITool`): retry exhaustion fails the step and the run, and a later planned step never runs (AC-8); a non-idempotent `Running` step is failed on re-entry and an idempotent one re-runs safely (AC-9); every `ToolCall` writes an `AuditLog` row whose jsonb `Payload` is the tool's output on success, `null` with no output, and `{"error": "..."}` on failure, even for error text with quotes, backslashes, and unicode (AC-5; these failure cases fail against the pre-fix code, which wrote raw error text into the jsonb column). `tests/WorkPilot.Api.Tests/ChatClientPlannerTests.cs` (4 tests, unit, scripted `IChatClient`): one retry on an unparseable or empty plan, then `PlanParseException` (AC-7). Follow ups after `/check review` (same day): `PlanRunJobTests.cs` (3 tests) covers the policy check (AC-2); the decide endpoint now audits the deciding profile (AC-5); and approve leaves the step at `AwaitingApproval` so `AdvanceRunJob` moves it to `Running` itself, which lets a non idempotent approved tool run once instead of failing (AC-4, AC-9). Known gap, deferred to the API error handling decision: a `DbUpdateConcurrencyException` from `agent_runs.xmin` isn't caught yet.

`/check verify` (2026-09-24, re-run): PASS after one fix; see [verify.md](../specs/0005-agent-orchestrator-core/verify.md). Driven live against the Api and a fresh Postgres: auto-allowed run completes, approval-required run suspends, survives four Api restarts, and completes on approve (a second decide gets `409`, the decision is audited with the deciding `ProfileId`). The mid-planning `kill -9` step (AC-9) first failed: the run sat at `Planning` after restart because Hangfire.PostgreSql re-fetches a dead worker's job only after its 30 minute default `InvisibilityTimeout`. Fixed in `src/WorkPilot.Api/Program.cs` with a sliding 1 minute timeout; the stuck run then completed within 3s of restart. `dotnet test` 146/146 (Domain 47, Web 68, Api 31), `dotnet format --verify-no-changes` clean.

### 7. AI provider abstraction · planned
`IAiProvider`-style interface so no domain logic hardcodes a single vendor. Must support at least two swappable providers per the spec (e.g. OpenAI-compatible and DeepSeek-compatible), routed through the AI Gateway pattern appropriate to the chosen stack.
**Done when:** the same planning call can be swapped between two configured providers via configuration only, no code change.
- [x] Design it (spec): `/architect AI provider abstraction` → [0006](../specs/0006-ai-provider-abstraction/index.md)
- [ ] Build it: `/develop AI provider abstraction`
  - [ ] Config selected `IChatClient` (OpenAI compatible and Fake kinds) replacing the hardcoded fake (AC-1, AC-2, AC-5)
  - [ ] Startup validation and the provider log line (AC-3, AC-4, AC-5)
  - [ ] Bounded calls; a provider failure fails the run (AC-6)
  - [ ] Keys via user secrets or env vars; prove the swap live against two OpenAI compatible endpoints (AC-1, AC-2, AC-4)
- [ ] Verify it: `/check verify AI provider abstraction`
- [ ] Test it: `/test AI provider abstraction`

### 8. Approval engine & Approval center · needs a decision · GA
Enforces the three tier policy: auto allowed (read/search/analyze/classify/dedupe/generate/prepare/monitor/detect/suggest), approval required (send email, submit application, withdraw application, connect external account), explicit confirmation (delete data, security/permission changes, destructive ops). The Approval center screen (`/approvals`) shows pending actions with enough evidence to decide (resume/cover letter versions, risk, target) and Approve/Reject.
**Done when:** a tool marked "approval required" cannot execute without a recorded, user-issued approval; the Approval center lists it with full context and the decision is auditable.
- [ ] Design it (spec): `/architect approval engine & approval center`
