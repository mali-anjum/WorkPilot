# WorkPilot

Personal AI work execution system (finds and matches jobs/academic opportunities, prepares applications and outreach, executes permitted parts with approval, tracks outcomes). Single user (the founder) today, designed so it could serve more later.

## Stack

- **Language / Runtime**: C# / .NET 10
- **Framework**: .NET Aspire (orchestration) + Blazor Web App (Auto render mode) + ASP.NET Core backend
- **Key dependencies**: EF Core (Npgsql), Hangfire (background jobs), Microsoft.Extensions.AI (`IChatClient`), Playwright for .NET
- **Database**: PostgreSQL, self hosted via Supabase (Postgres + GoTrue auth + Storage, Docker Compose); EF Core owns only the product schema, never `auth.*`/`storage.*`
- **Package manager**: NuGet (`dotnet` CLI)

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

# Test
dotnet test tests/WorkPilot.Api.Tests/WorkPilot.Api.Tests.csproj
```

Self hosted Supabase stack (Postgres, GoTrue, Storage): `docker compose up` in `supabase/` (see `supabase/.env.example`).

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
- One consistent error handling pattern across the API (not ad hoc try/catch per endpoint); not yet chosen, decide and record when the first real error path is built.
- Validate configuration/env vars at startup; fail fast rather than a null reference deep in a request.
- Format with `dotnet format` + `.editorconfig`; a `.githooks/pre-commit` hook runs `dotnet format` on staged `.cs` files before commit (format only, not a full lint/typecheck gate yet). One time per clone: `git config core.hooksPath .githooks`.
- Testing gate: unit + integration tests with xUnit (`WebApplicationFactory` for integration tests against a real Postgres, never a mock of the database).
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
