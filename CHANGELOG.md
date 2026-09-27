# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Scaffolded the foundational stack (see spec 0001): a .NET Aspire modular monolith with a Blazor Web App (Auto render mode, since changed to InteractiveServer by spec 0016) and an ASP.NET Core backend, sharing one self hosted Supabase Postgres database. The 13 domain module boundaries (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) are reflected in the folder structure.
- Stood up a self hosted Supabase stack (Postgres, GoTrue, Storage) via Docker Compose, connected to the same Postgres database EF Core uses.
- Added Hangfire for durable background jobs, storing its state in the shared Postgres database, with its dashboard reachable at `/hangfire`.
- Added the repo's first integration test project (`tests/WorkPilot.Api.Tests`), covering `GET /health/db` (a real EF Core round trip against Postgres) and Hangfire dashboard reachability.
- Coding standards and tooling: `.editorconfig`, `dotnet format`, and a `.githooks/pre-commit` hook that formats staged `.cs` files.
- The product data model (spec 0002): EF Core entities and migrations for the 13 modules, in the `app` schema.
- Design system and UI foundation (spec 0003): design tokens, light and dark mode from a cookie (no flash), the app shell and base components, and bUnit tests (`tests/WorkPilot.Web.Tests`).
- Sign in and app shell gating (spec 0004): server side sign in against self hosted GoTrue with a self issued cookie; every page requires sign in; sign up is disabled.
- Agent orchestrator core (spec 0005): runs planned into steps, a tool registry with risk tiers, and one idempotent Hangfire job that advances a run step by step and survives restarts.
- AI provider abstraction (spec 0006): each purpose picks a provider and model in config, a keyless `Fake` provider by default, key redaction, and `GET /health/ai`.
- Approval engine and Approval center (spec 0007): a three tier approval policy, frozen evidence on each approval, a guarded decide path, and the `/approvals` page.
- Job source ingestion (spec 0008): the `IJobSource` seam, a Greenhouse adapter, a normalizer, snapshots, and trigger and read endpoints.
- Resume management (spec 0009): base and tailored resumes with versions, file upload and download, and versions locked forever once an application uses them.
- Job deduplication (spec 0017): jobs identified by their source links, merged across sources by a match key, with split, reconcile, and a Lever adapter.

### Changed
- The whole Blazor app now renders InteractiveServer, set once on `<Routes>` (spec 0016); Auto never worked, since every routed page lives in the server project.
- Upgraded bUnit to 2.11.3 in the Web tests, which drops the vulnerable AngleSharp 1.2.0.

### Fixed
- The agent run job no longer crashes when an approval decision changes the same run at the same moment; it re-enqueues itself and runs again on fresh data.
- The Api now fails fast at startup when `ConnectionStrings:workpilotdb` is missing, instead of failing on first use.
- `GET /health/db` now actually creates its database table on startup via EF Core migrations. It previously called `EnsureCreatedAsync`, which silently does nothing once the target Postgres database already exists (true here, since Supabase pre creates it), so the table was never created.
- Fixed the Hangfire background worker server hanging on shutdown inside test hosts (`WebApplicationFactory`); the worker server can now be disabled independently of the dashboard/client registration.

### Security
- EF Core's own tables now migrate into a dedicated `app` schema instead of Postgres's default `public` schema, keeping the product's data separate from what a future Supabase PostgREST layer would expose by default.
