# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Scaffolded the foundational stack (see spec 0001): a .NET Aspire modular monolith with a Blazor Web App (Auto render mode) and an ASP.NET Core backend, sharing one self hosted Supabase Postgres database. The 13 domain module boundaries (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) are reflected in the folder structure.
- Stood up a self hosted Supabase stack (Postgres, GoTrue, Storage) via Docker Compose, connected to the same Postgres database EF Core uses.
- Added Hangfire for durable background jobs, storing its state in the shared Postgres database, with its dashboard reachable at `/hangfire`.
- Added the repo's first integration test project (`tests/WorkPilot.Api.Tests`), covering `GET /health/db` (a real EF Core round trip against Postgres) and Hangfire dashboard reachability.

### Fixed
- `GET /health/db` now actually creates its database table on startup via EF Core migrations. It previously called `EnsureCreatedAsync`, which silently does nothing once the target Postgres database already exists (true here, since Supabase pre creates it), so the table was never created.
- Fixed the Hangfire background worker server hanging on shutdown inside test hosts (`WebApplicationFactory`); the worker server can now be disabled independently of the dashboard/client registration.

### Security
- EF Core's own tables now migrate into a dedicated `app` schema instead of Postgres's default `public` schema, keeping the product's data separate from what a future Supabase PostgREST layer would expose by default.
