# WorkPilot

Personal AI work execution system (finds and matches jobs/academic opportunities, prepares applications and outreach, executes permitted parts with approval, tracks outcomes). Single user (the founder) today, designed so it could serve more later.

## Stack

- **Language / Runtime**: C# / .NET 10
- **Framework**: .NET Aspire (orchestration) + Blazor Web App (InteractiveServer render mode, app wide; docs/specs/0016-blazor-server-render-mode) + ASP.NET Core backend
- **Key dependencies**: EF Core (Npgsql), Hangfire (background jobs), Microsoft.Extensions.AI (`IChatClient`), Playwright for .NET
- **Database**: PostgreSQL, self hosted via Supabase (Postgres + GoTrue auth + Storage, Docker Compose); EF Core owns only the product schema, never `auth.*`/`storage.*`
- **Package manager**: NuGet (`dotnet` CLI); every package version lives in `Directory.Packages.props` (central package management), so a `PackageReference` never carries a `Version`

## Build approach

**Tracer Bullet**: each feature is built end to end through every layer and left working, rather than built in horizontal layers.

## Commands

```bash
# Install
dotnet restore

# Dev server (Aspire orchestrates Web + Api + Postgres together)
dotnet run --project src/WorkPilot.AppHost

# Build
dotnet build WorkPilot.slnx

# Test (all test projects)
dotnet test WorkPilot.slnx
```

`WorkPilot.Api.Tests` run against a real Postgres and fail fast without `WORKPILOTDB_CONNECTION`: start it with `docker compose up -d db` in `supabase/`, then e.g. `export WORKPILOTDB_CONNECTION="Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=<POSTGRES_PASSWORD from supabase/.env>"`. The Api applies EF Core migrations itself on startup, so an empty database works. Stop any dev Api running against the same database first: its Hangfire worker picks up jobs the tests enqueue and makes them flaky.

Self hosted Supabase stack (Postgres, GoTrue, Storage): `docker compose up` in `supabase/` (see `supabase/.env.example`).

Running outside Aspire (e.g. for `/check verify`): `docker compose up -d db auth` in `supabase/`, then the Api with `ConnectionStrings__workpilotdb` set and the Web with `services__api__http__0=<api url>`, `Supabase__Url=http://localhost:<GOTRUE_PORT>` and `Supabase__AnonKey`, each via `dotnet run --no-build --no-launch-profile --project <project>` (running the dll directly breaks the content root). `SERVICE_ROLE_KEY` in `supabase/.env` is not a JWT; GoTrue admin calls need an HS256 `service_role` token signed with `JWT_SECRET`.

## Specs

Stored in `docs/specs/`. Format: `docs/specs/NNNN-title.md`.

## Rules

Architecture: Clean Architecture. Layers: `Domain` (entities, value objects) → `Application` (use cases) → `Infrastructure` (DB, APIs, frameworks) → `Web`/`Api` (presentation). Dependency rule: outer layers depend on inner layers, never the reverse; `Domain` has zero external imports.
- Use cases are thin orchestrators: they call domain logic and infrastructure interfaces, never implement business rules themselves.
- Infrastructure implements interfaces defined in the domain/application layer (dependency inversion).
- No framework or ORM code inside `Domain`/`Application`. Entities are plain objects with no ORM decorators, enforcing their own invariants.
- No domain entities leak into the presentation layer; cross boundary communication uses DTOs.
- Domain and application layers are unit tested with no infrastructure mocks; infrastructure is integration tested against real systems (e.g. `WebApplicationFactory` against a live Postgres, not a mock).
- Folder by feature: each of the 13 domain modules (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) is its own subfolder inside each layer (`Domain/Modules/<Area>`, `Application/Modules/<Area>`), not grouped by technical layer alone.
- Nullable reference types are strict everywhere (`<Nullable>enable</Nullable>` in every `.csproj`); no nullability warnings suppressed.
- Document public APIs (controllers/endpoints, services) with XML doc comments.
- One consistent error handling pattern across the API (not ad hoc try/catch per endpoint): use cases return `Result<T>` (`Application/Common/Result.cs`) and endpoints map it with `ToHttp` (`Api/Common/ResultHttpExtensions.cs`), so every failure is ProblemDetails; the Web reads them through `ApiResultReader` (`Web/Features/Common/ApiResult.cs`) (docs/specs/0018-module-contracts).
- Modules (docs/specs/0018-module-contracts): each module owns and is the only writer of its tables (any module may read); it plugs in with one `Add<Module>Module` and one `Map<Module>Endpoints` line in the alphabetical module blocks of `Api/Program.cs` (and `Add<Module>Web`/`Map<Module>WebEndpoints` in `Web/Program.cs`); recurring jobs register with `AddRecurringJob<TJob>` and are applied once by `app.ApplyRecurringJobs()`.
- Cross module reactions go through domain events, never direct writes: an event is an `IDomainEvent` record with a stable versioned `EventName` (`<module>.<event>.v1`), raised with `IEventPublisher.Publish` before the caller's own save (the transactional outbox, `app.outbox_messages`), and handled by an `IEventHandler<T>` with a stable `HandlerKey`, registered with `AddEventHandler<TEvent, THandler>()`. Handlers run inside `HandleEventJob`'s transaction (never open another) and must be safe to run again. Never rename a shipped `EventName`: startup fails while an undispatched row names an unknown event.
- Validate configuration/env vars at startup; fail fast rather than a null reference deep in a request.
- AI: each purpose (`Default`, `Planner`, ...) picks a provider and model in the `Ai` section of `src/WorkPilot.Api/appsettings.json`; switching is config only (`Ai__Purposes__<purpose>__Provider`/`__Model`). The committed default is the `Fake` provider, so no key is needed to build or test. Keys never go in a tracked file: `dotnet user-secrets set "Ai:Providers:<name>:ApiKey" <key> --project src/WorkPilot.Api` in dev, `Ai__Providers__<name>__ApiKey` env vars in prod. `GET /health/ai` checks the Default provider on demand (docs/specs/0006-ai-provider-abstraction).
- Jobs: every external job source plugs in behind `IJobSource` (Application), one adapter per source in `Infrastructure/Modules/Jobs/Sources/`; nothing outside the adapter knows the source (docs/specs/0008-job-source-ingestion). Sources today: Greenhouse and Lever. An adapter also implements `ParseStored` (rebuild one posting from its snapshot) and keeps the raw company the source's own, never `JobSource.CompanyName`: the raw posting feeds the content hash, and the company name is applied later by `JobSources.WithCompany`.
- Job dedup (docs/specs/0017-job-deduplication): a job's identity is its `JobSourceLink`s (unique `(JobSourceId, ExternalId)`), not columns on `Job`; a new posting joins the job with the same `JobDedupKey` (exact normalized company, title, location). Bump `JobDedupKey.CurrentRuleVersion` whenever the normalization changes: every job goes stale and `ReconcileJobsJob` (enqueued on Api start) merges again. Code that changes jobs runs in `IJobRepository.InTransactionAsync` and takes `LockDedupKeysAsync` before it reads the jobs it changes; `jobs.xmin` is a concurrency token, and `InTransactionAsync` reruns the whole unit on fresh data when another writer changed one of its jobs first, so a unit must load everything it touches itself. `jobs.PrimaryLinkId` has a hand written `DEFERRABLE INITIALLY DEFERRED` foreign key in `AddJobDeduplication`, absent from the EF model on purpose; keep that SQL if the migration is ever regenerated. Merges move applications through `IJobApplicationReassigner` (Applications owns `job_applications`).
- Job matching (docs/specs/0019-job-matching-engine): AI only extracts requirements (`IJobRequirementExtractor` on the `JobExtraction` purpose, no tools, never any profile data); the score comes from the pure Domain `MatchScorer`, configured by the `Matching` section (weights must sum to 100, validated at startup). Bump `MatchScorer.ScoringVersion` when a scoring rule changes and `JobRequirement.CurrentExtractorVersion` when the prompt or schema changes (that re-extracts every job, an AI cost). `job_matches` is written only by `MatchRepository.UpsertMatchAsync` (raw SQL, skipped when `InputsFingerprint` is unchanged). Code that creates a job or changes its primary content stamps the jobs with `JobContentEvents` before the change and publishes `JobContentChanged` before the save. The match profile ETag is `profiles.xmin` (`Result<T>` maps stale to 412, missing `If-Match` to 428). Keep the hand written column defaults in `AddJobMatching` if it is ever regenerated.
- Jobs list (docs/specs/0021-jobs-list-detail): the `/jobs` search is one SQL query in `MatchQueries` (filters, sort and page all come from the URL, bad input is a per field ProblemDetails). `job_dismissals` is per profile and written only by `JobDismissalService` (idempotent `ON CONFLICT DO NOTHING`; a job removed by a racing merge answers 404); a merge moves dismissals to the surviving job through `IJobRepository.ReassignDismissalsAsync`.
- Notifications (docs/specs/0020-notifications): only the Notifications module writes `notifications`, always from an `IEventHandler` (a new kind of notification is one more handler in `Application/Modules/Notifications/Handlers/`, registered in `AddNotificationsModule`). A `Link` must be app relative (the Domain refuses anything else). The daily digest relies on the partial unique index `UX_notifications_open_digest` (one open digest per profile and UTC day) plus `FOR UPDATE` and the Hangfire retry of `HandleEventJob`; keep that index and the hand written backfill SQL in `AddNotifications` if it is ever regenerated. `Notifications__ReadRetentionDays` (1 to 3650, default 90) is validated at startup.
- Outbound HTTP: the ServiceDefaults standard resilience handler (10 s per attempt) ignores options named per client; a typed client that needs other timeouts must replace that pipeline for itself (see `JobIngestionServiceCollectionExtensions`).
- Blazor: every routed page lives in `WorkPilot.Web` and renders InteractiveServer (set once on `<Routes>` in `App.razor`), so pages may inject server services; a component that calls JS interop in `DisposeAsync` must catch `JSDisconnectedException`, since every full page load closes a circuit. Shell controls go into `AppShell`'s `TopBarActions` slot from `MainLayout`; a component under `Web/Features/<Area>/` sits outside `Components/_Imports.razor`, so it needs its own `@using` lines.
- Resumes: a version an application used is locked forever, backed by the `resume_versions_block_locked_changes` Postgres trigger (SQLSTATE `WP409`); keep that hand written SQL if `AddResumeManagement` is ever regenerated. Files go through `IResumeFileStore` (Postgres backed until Supabase Storage runs) (docs/specs/0009-resume-management).
- Format with `dotnet format` + `.editorconfig`; a `.githooks/pre-commit` hook runs `dotnet format` on staged `.cs` files before commit (format only, not a full lint/typecheck gate yet). One time per clone: `git config core.hooksPath .githooks`.
- Testing gate: unit + integration tests with xUnit (`WebApplicationFactory` for integration tests against a real Postgres, never a mock of the database).
- Guard tests: `ArchitectureTests` (Api.Tests) enforces the layer dependency rule and that every event name and handler key sits in its own module; Web.Tests can host the real Web app with `WebApplicationFactory<App>` and only GoTrue and the Api replaced (see `AuthFlowTests`). Tag each test `// covers: spec NNNN AC-N`.
- No CI configured yet.

## Git

- integration: on
- branch prefix: feat/
- commit: per-milestone

## Agent skills

- [aspire](.claude/skills/aspire/): `.NET Aspire`, orchestration conventions for the AppHost/ServiceDefaults setup
- [microsoft-extensions-ai](.claude/skills/microsoft-extensions-ai/): `Microsoft.Extensions.AI`, the `IChatClient` provider abstraction used by the AI provider layer
- [scaffold-dotnet-test-project](.claude/skills/scaffold-dotnet-test-project/): scaffolding new xUnit test projects wired into `WorkPilot.slnx`
- [ef-core](.agents/skills/ef-core/): `github/awesome-copilot`, EF Core development patterns and best practices
- [supabase](.agents/skills/supabase/): `supabase/agent-skills`, official Supabase conventions (database, auth, storage)
- [supabase-postgres-best-practices](.agents/skills/supabase-postgres-best-practices/): `supabase/agent-skills`, Postgres best practices for the self hosted stack
- [csharp-xunit](.agents/skills/csharp-xunit/): `github/awesome-copilot`, xUnit testing patterns and best practices

Declined: Hangfire (no dedicated skill found; the closest match, abpframework/abp-skills@background-jobs-and-events, isn't Hangfire specific and has low adoption)

## Context files

<!-- Nested AGENTS.md files are listed here as they are created -->

_Drafted by /audit from the repo, worth a quick human pass. Edit freely: once a line stops matching this draft, later runs treat it as curated and will flag rather than overwrite it._
