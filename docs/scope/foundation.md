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

### 4. Design system & UI foundation · needs a decision
Token based visual language (colors, type, spacing, radius per the product spec section 4), base components (AppShell, Sidebar, TopBar, PageHeader, Card, Button, Input, Select, Tabs, Badge/StatusBadge, DataTable, EmptyState, Skeleton, Timeline, Stepper, Toast, Modal, CommandPalette), light and dark mode.
**Done when:** `design.md` covers type/color/spacing/components, base components handle focus and keyboard, and the app shell renders the full navigation (Overview, Work, Personal, Agent, System sections) with empty pages behind each route.
- [ ] Design it (spec): `/architect design system & UI foundation`

### 5. Auth & app shell · needs a decision
Single user authentication, secure sessions, and the persistent shell (sidebar, top bar, command palette shortcut, global search stub) every screen mounts inside.
**Done when:** the user can sign in, land on an empty dashboard inside the real shell, and every route in the nav is reachable (even if its content is a stub).
- [ ] Design it (spec): `/architect auth & app shell`

### 6. Agent orchestrator core · needs a decision · GA
The shared machinery every domain agent runs on: Planner, Policy Engine, Tool Registry, Workflow Engine, Approval Engine, Memory, Execution Engine, Verification Engine. Tools declare name, schema, required permissions, risk level, approval requirement, timeout, retry policy, audit policy. Workflows persist step by step, never rely on an in-memory process surviving. The LLM never mutates important state directly, only through tools; it never receives passwords, OAuth tokens, refresh tokens, client secrets, or session cookies.
**Done when:** a trivial end to end tool call (e.g. "list my profile") runs through Planner -> Policy Engine -> Tool Registry -> Execution -> Verification -> Audit, is durably persisted as an AgentRun/AgentSteps/ToolCalls, and survives a process restart mid run.
- [ ] Design it (spec): `/architect agent orchestrator core`

### 7. AI provider abstraction · needs a decision
`IAiProvider`-style interface so no domain logic hardcodes a single vendor. Must support at least two swappable providers per the spec (e.g. OpenAI-compatible and DeepSeek-compatible), routed through the AI Gateway pattern appropriate to the chosen stack.
**Done when:** the same planning call can be swapped between two configured providers via configuration only, no code change.
- [ ] Design it (spec): `/architect AI provider abstraction`

### 8. Approval engine & Approval center · needs a decision · GA
Enforces the three tier policy: auto allowed (read/search/analyze/classify/dedupe/generate/prepare/monitor/detect/suggest), approval required (send email, submit application, withdraw application, connect external account), explicit confirmation (delete data, security/permission changes, destructive ops). The Approval center screen (`/approvals`) shows pending actions with enough evidence to decide (resume/cover letter versions, risk, target) and Approve/Reject.
**Done when:** a tool marked "approval required" cannot execute without a recorded, user-issued approval; the Approval center lists it with full context and the decision is auditable.
- [ ] Design it (spec): `/architect approval engine & approval center`
