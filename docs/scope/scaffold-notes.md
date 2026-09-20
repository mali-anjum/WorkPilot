# Scaffold notes (feature 1: Stack & architecture)

Working notes captured while running the scaffold task from
`docs/specs/0001-stack-architecture.md`. Superseded once `docs/scope/foundation.md`
is updated with the real checklist state — kept here only as a pointer to
where things live.

## Solution layout

```
WorkPilot.slnx
src/
  WorkPilot.AppHost/            Aspire orchestration (Web + Api + Postgres)
  WorkPilot.ServiceDefaults/    OTel, health checks, service discovery
  WorkPilot.Web/                Blazor Web App server host (render mode Auto)
  WorkPilot.Web.Client/         Blazor WebAssembly client project
  WorkPilot.Api/                ASP.NET Core backend host (Web layer), Hangfire dashboard
  WorkPilot.Application/        Application layer, Modules/<ModuleName>/README.md per module
  WorkPilot.Domain/             Domain layer, Modules/<ModuleName>/README.md per module
  WorkPilot.Infrastructure/     EF Core DbContext, Hangfire Postgres storage wiring
  WorkPilot.Workers/            Background/worker layer (placeholder)
  WorkPilot.Contracts/          Cross-cutting DTOs/contracts (placeholder)
  WorkPilot.AI/                 Microsoft.Extensions.AI wiring (placeholder, no provider yet)
supabase/
  docker-compose.yml            Postgres + GoTrue + Storage (minimal self-hosted stack)
  .env.example                  Placeholder env vars for the compose stack
```

Modules reflected as folders under `WorkPilot.Domain/Modules/*` and
`WorkPilot.Application/Modules/*`: Identity, Profile, Jobs, Applications,
Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations,
Notifications, Audit.
