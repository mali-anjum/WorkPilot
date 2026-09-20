# 0001. Adopt .NET Aspire modular monolith with Supabase for commodity plumbing

**Date**: 2026-09-20
**Status**: Proposed

## Summary

This decision fixes the foundation the whole Personal Work Agent is built on. The application is a .NET solution (Blazor Web App on ASP.NET Core, orchestrated with .NET Aspire) organized as a modular monolith, matching the module boundaries already named in the product spec (Jobs, Applications, Universities, Agent, and so on). Commodity plumbing (the user's login, file storage for resumes, and simple settings CRUD) is offloaded to Supabase, which is really just a managed Postgres database with an auth and storage layer bolted on; the .NET backend keeps ownership of everything that is actually the product (the Agent orchestrator, workflows, approvals, browser automation, Gmail). Everything is self hosted on one VPS with Docker Compose, so there is no cloud vendor lock in beyond the AI provider itself.

## Context

The product is a single user, agent driven system whose hardest parts (planning, tool execution, approval gating, browser automation, provenance tracking) have no shortcut through a backend as a service; a BaaS can, however, remove a lot of the boring, well solved parts (accounts, sessions, password resets, file storage) so build effort goes into the parts that make this product valuable rather than reinventing login forms.

The engineer already fixed the top of the stack before this spec: .NET, Blazor, ASP.NET Core, and a Clean Architecture project layout with an `AppHost` orchestration project, which is the standard shape of a .NET Aspire solution. The remaining forces to settle are the database and how much of it a BaaS should own, the render mode, hosting (constrained: this repository's working environment is tuned for Vercel, which does not run ASP.NET Core or Blazor Server, so hosting had to be decided explicitly rather than inherited from the environment), the durable workflow engine behind the Agent's long running runs, the AI provider abstraction, and browser automation for the highest risk feature in the whole product (submitting real job applications).

The data model itself (30+ entities: Users, Jobs, JobMatches, Applications, Universities, Professors, AgentRuns, Approvals, AuditLogs, and their relationships) is heavily relational, with real foreign keys and several many to many join tables (for example ProfessorResearchAreas). That shape is a strong forcing function on the database choice below.

## Requirements

This is a decision only spec (ARCHITECTURE mode); there is no independent build spec here beyond the chosen stack itself. The buildable unit this spec unlocks is the "Stack & architecture" foundation feature (`docs/scope/foundation.md`, item 1), whose only task is `/develop stack & architecture`: scaffold the solution from the `## Proposed stack` below and confirm it boots.

**Acceptance criteria for the scaffold** (checked when that feature is built):
- **AC-1**: `dotnet build` succeeds across the whole solution from a clean checkout.
- **AC-2**: `aspire run` (or the AppHost project) starts the Web project, the .NET backend API, and a local Postgres container together with one command.
- **AC-3**: The Blazor Web App renders at least one page end to end (a health or landing page) in Auto (Server + WebAssembly) render mode.
- **AC-4**: The solution folder structure matches the modules named in `## Proposed stack` (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit), each as a folder or project under a Clean Architecture layer, not a flat pile of files.
- **AC-5**: A Supabase project (self hosted, per this decision) is reachable from the .NET backend over the same Postgres connection string used by EF Core migrations, proving both sides share one database.
- **AC-6**: Hangfire's own tables are created in that same Postgres database on first run, and its dashboard is reachable.

## Options considered

### Option 1: Full custom .NET backend, no BaaS (Supabase or Firebase)

The .NET backend owns everything: auth (ASP.NET Core Identity), file storage (local disk or a self hosted object store like MinIO), and every CRUD endpoint, with Blazor talking only to that backend.

**Pros**:
- One system to reason about, no second vendor's auth/session model to bridge into .NET's own.
- No dependency on Supabase's own uptime or self hosted stack, on top of the VPS already being self hosted.

**Cons**:
- Rebuilds solved problems (password reset flows, email verification, session/token handling, a file upload service with resumable uploads) that a BaaS gives for free, for no product benefit; this is exactly the "reinventing auth" failure pattern.
- More surface area to secure and patch (auth is one of the highest consequence places to get security wrong).

### Option 2: Supabase (self hosted) for auth, storage, and simple CRUD; .NET backend for the Agent and everything sensitive

Supabase's Postgres database is the single source of truth. Supabase's GoTrue (auth), Storage, and PostgREST/Realtime layers handle login, resumes/cover letter file storage, and simple settings reads the Blazor client can call directly. The .NET backend (ASP.NET Core, orchestrated by Aspire) owns the Agent orchestrator, tool registry, workflow engine (Hangfire), approval engine, browser automation worker, and Gmail OAuth token handling, connecting to the same Postgres database via EF Core.

**Pros**:
- Matches the actually relational data model directly; Supabase is Postgres, so EF Core migrations and Supabase's own schema coexist in one database with no translation layer.
- Removes the highest risk, most security sensitive commodity work (auth, session handling, password resets) from the custom codebase entirely.
- Engineering effort concentrates on what makes this product valuable: the Agent, approvals, and automation, not CRUD forms.

**Cons**:
- Two systems now touch the same database (Supabase's own migrations/policies and EF Core's migrations), which needs a clear ownership line (below) so they never fight over the same tables.
- Self hosting all of Supabase (chosen by the engineer over its managed cloud) means operating Postgres, GoTrue, PostgREST, Realtime, Storage, and Kong yourself on the VPS, on top of the .NET backend; this is materially more ops than using Supabase's managed cloud would have been.

### Option 3: Firebase (self hosted emulator or managed) for auth and storage

Firebase Authentication and Cloud Storage handle the same commodity concerns as Option 2, with Firestore (a NoSQL document store) as the primary database.

**Pros**:
- Very fast to stand up for simple auth and file storage needs.
- Wide client SDK support if a mobile client is ever added.

**Cons**:
- Firestore is a document store; this product's data model (jobs, matches, applications, pipeline states, audit trails, many to many joins) is deeply relational, so Firestore would force denormalization or client side joins on every matching, pipeline, and audit query. This is the "NoSQL for relational data" failure pattern from first principles, not a stylistic preference.
- Firebase's own Cloud Functions run on Node.js or Python, not .NET, so it does not naturally extend into the same backend that runs the Agent orchestrator the way Supabase's shared Postgres does.

## Decision

**Chosen option**: Option 2: Supabase (self hosted) for auth, storage, and simple CRUD; .NET backend for the Agent and everything sensitive.

Build a .NET Aspire orchestrated modular monolith (Blazor Web App, Auto render mode, ASP.NET Core backend, EF Core, Hangfire) that shares one self hosted Supabase Postgres database with Supabase's own auth, storage, and simple CRUD layer, all deployed together on a single self hosted VPS via Docker Compose.

**Implementation skills**: `aspire` (`managedcode/dotnet-skills`, `.claude/skills/aspire/`) · `microsoft-extensions-ai` (`managedcode/dotnet-skills`, `.claude/skills/microsoft-extensions-ai/`) · `scaffold-dotnet-test-project` (`dotnet/skills`, `.claude/skills/scaffold-dotnet-test-project/`)

## Rationale

The engineer's confusion between Supabase and Firebase was really a confusion about whether a document store or a relational store fits this data model; it does not survive contact with the actual entity list (Jobs, JobMatches, Applications with a real state machine, Universities, Professors, AuditLogs, and their foreign keys), which is why Option 3 is ruled out on the data shape alone, not on vendor preference.

Between Option 1 and Option 2, the deciding force is where this product's engineering effort should go: the spec itself is explicit that the product's value is execution (the Agent, approvals, verification), not another login form. Paying custom backend effort to rebuild auth and file storage would be time not spent on the Agent orchestrator, which is the one part no vendor can build for this product. Supabase being Postgres, not a separate database technology, means this is additive (one more well scoped consumer of the same database) rather than a second source of truth to reconcile.

The engineer chose to self host all of Supabase rather than use its managed cloud, trading lower operational cost in engineering time for full control and no third party dependency; this is a legitimate call for a solo project, and is recorded here explicitly because it is materially more to operate (Postgres, GoTrue, PostgREST, Realtime, Storage, and Kong, all on one VPS) than the managed alternative would have been. To keep the two systems from fighting over the same tables: Supabase (via its Studio or SQL) owns Auth-related tables (`auth.*`), Storage tables (`storage.*`), and any table the Blazor client is meant to read or write directly through Supabase's client library or PostgREST; every other table in the product's own schema (Jobs, Applications, AgentRuns, Approvals, AuditLogs, and so on) is owned and migrated exclusively by EF Core from the .NET backend. This split is a Follow-up item below to record explicitly once the data model spec is written, since crossing it in either direction (Supabase writing to an EF-owned table, or EF Core migrating an `auth.*` table) is the actual risk of the two-systems tradeoff named above.

## Proposed stack

| Layer | Choice | Reason |
|---|---|---|
| Language | C# (.NET 9 LTS candidate at time of writing; confirm current LTS at scaffold time) | Matches the engineer's confirmed .NET/Blazor decision. |
| Application type / orchestration | .NET Aspire (AppHost project orchestrating Web + backend + Postgres locally) | The `AppHost` project name already in the original notes is Aspire's own convention; it gives one command local orchestration for a modular monolith without hand rolled Docker Compose for dev. |
| Frontend framework | Blazor Web App, render mode Auto (Server + WebAssembly) | Fast first paint over SignalR, then WASM for snappy interactivity; the engineer confirmed this as the best fit for an information dense internal tool. |
| Backend framework | ASP.NET Core, Clean Architecture layering (Web / Application / Domain / Infrastructure / Workers / Contracts / AI), modular monolith by domain (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) | Matches the product spec's own project structure (section 17) and the modular monolith mandate (section 57); no microservices until a real bottleneck forces it. |
| Primary database | PostgreSQL, self hosted via Supabase's own Postgres container, migrated by EF Core for the product schema | Relational data model with real FKs and join tables; Supabase's own layer only reaches tables it owns (`auth.*`, `storage.*`, and any table the Blazor client reads directly). |
| Auth | Supabase Auth (GoTrue), self hosted | Removes password reset, session, and token handling from custom code entirely; the engineer confirmed the BaaS split explicitly. |
| File storage | Supabase Storage, self hosted | Resumes and cover letter files; object storage, never stored in the database, per the spec's own resume immutability requirement. |
| ORM | Entity Framework Core (product schema only, never `auth.*`/`storage.*`) | Standard .NET ORM; complex matching/reporting queries still get raw SQL per general EF guidance, not forced through LINQ. |
| Background jobs / workflows | Hangfire, storage in the same Postgres database | Confirmed by the engineer; battle tested retry/scheduling/dashboard out of the box, avoids hand building the Workflow/WorkflowStep/WorkflowEvent retry machinery from scratch. |
| AI provider abstraction | Microsoft.Extensions.AI (`IChatClient`) with provider specific clients behind it | Confirmed by the engineer; official Microsoft abstraction built exactly for swapping OpenAI-compatible and DeepSeek-compatible providers via configuration, without adopting a heavier framework (Semantic Kernel) that would overlap the product's own Agent Orchestrator design. |
| Browser automation | Playwright for .NET | Confirmed by the engineer; the standard, actively maintained .NET browser automation library, needed for the application execution engine's form filling, uploads, and screenshot evidence. |
| Hosting | Self hosted VPS, Docker Compose (running the .NET backend/Web containers alongside a fully self hosted Supabase stack: Postgres, GoTrue, PostgREST, Realtime, Storage, Kong) | Confirmed by the engineer over Azure App Service and Fly.io; full control and no cloud vendor cost, at the cost of owning all patching, TLS, backups, and scaling. |
| Observability | Serilog structured logging shipped to a self hosted Grafana + Loki (or Seq) instance on the same VPS | Confirmed by the engineer; keeps the whole observability stack self hosted rather than adding a third party vendor dependency. |

## Consequences

**Positive**:
- Auth, session handling, and file storage are removed from the custom codebase entirely, concentrating build effort on the Agent orchestrator, approvals, and automation, which is where this product's actual value is.
- One shared Postgres database keeps the relational data model coherent across both Supabase's own tables and the product's EF Core managed schema, with no data sync or duplication between two databases.
- Every layer of the stack (Postgres, GoTrue, Storage, the .NET backend, Hangfire, observability) is self hosted on one VPS the engineer fully controls, with no recurring per seat or per request cloud bill beyond the AI provider itself.

**Negative / tradeoffs**:
- Self hosting the entire Supabase stack (rather than its managed cloud) means the engineer personally operates Postgres, GoTrue, PostgREST, Realtime, Storage, and Kong, on top of the .NET backend and Hangfire, all on one VPS; this is a real, ongoing operational load for a solo project, not a one time setup cost.
- Two systems (Supabase and EF Core) now touch one database; the table ownership split in Rationale must be enforced by convention (documented in `AGENTS.md` and the data model spec), since Postgres itself will not stop either side from touching the other's tables.
- ASP.NET Core/Blazor Server on a self hosted VPS means the engineer, not a managed platform, owns TLS certificate renewal, container restarts, disk space, and backups for the database; there is no managed control plane absorbing that operational work.
- Vercel, the environment this session runs in, cannot host this stack; any tooling that assumes a Vercel deployment target does not apply to this project going forward.

**Neutral**:
- The single VPS becomes a single point of failure for the whole product (frontend, backend, database, and auth all on one machine) unless a backup/restore and monitoring plan is put in place; this is worth a dedicated Follow-up rather than solving here.

## Follow-up

- [ ] Data model spec (`docs/scope/foundation.md` item 3) must explicitly record the table ownership split named in Rationale: which tables live under Supabase's management (`auth.*`, `storage.*`, any client-read table) versus EF Core's exclusive migration ownership (the product schema), so `/develop` never crosses that line by accident.
- [ ] Decide and document a backup/restore plan for the self hosted VPS (single point of failure for Postgres, GoTrue, and Storage together) before the Application execution engine (the highest risk feature) goes live.
- [ ] Confirm the current .NET LTS version at scaffold time (`dotnet --list-sdks` / the .NET release page); this spec assumes .NET 9 as the likely LTS at the time of writing but the scaffold task should verify rather than assume.
- [ ] `microsoft-extensions-ai` and `aspire` skills are installed but not yet referenced in a root `AGENTS.md` (none exists yet); `/audit` should add a `## Agent skills` section pointing to `.claude/skills/aspire/` and `.claude/skills/microsoft-extensions-ai/` once it runs.

## References

**Project sources** (verifiable, in this repo):
- `docs/product/product-spec.md`, sections 4 (BaaS-style hybrid boundaries implied by the security model), 17 (original project structure and AppHost naming), 57 (modular monolith mandate), 59 to 61 (security, permission model, idempotency), 62 (retry policy)
- `docs/scope/index.md` and `docs/scope/foundation.md`, feature 1 (this spec's linked scope row)

**Practices & standards**:
- Monolith first, extract services only when a specific bottleneck forces it
- A relational database as the default for data with real relational structure, reserving document stores for genuinely schemaless or extreme-scale key value workloads
- Never build authentication from scratch when a proven provider is available
- Idempotency keys for external, hard to reverse operations (named in the product spec itself for application submissions and email sends, and load bearing for how Hangfire jobs must be written)
