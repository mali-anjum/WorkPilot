# Scope: Personal Work Agent

A personal AI work execution system that finds and matches jobs and academic opportunities, prepares applications and outreach, executes the permitted parts with approval, and tracks everything through to outcome. Built for a single user (the founder), designed so it could later serve more.

**Build approach:** Tracer Bullet (each feature is built end to end through every layer and left working, rather than built in horizontal layers).
**Workflow:** Beta (after `/develop`: `/check verify` then `/test`). Features that send email or submit an application on the user's behalf are bumped to `GA` (adds a fresh model `/check review` and `/document`) because they are external and hard to reverse. `/architect` is still the required first stop for anything with a real decision, regardless of tier.

_These are recommendations to keep the build orderly, not requirements. Skip anything that does not fit; you decide when a feature is done._

**Decisions confirmed (spec 0001):** .NET Aspire modular monolith — Blazor Web App (Auto render mode) + ASP.NET Core backend + EF Core, sharing one self hosted Supabase Postgres database (Supabase owns auth/storage/simple CRUD; the .NET backend owns the Agent orchestrator/workflows/approvals/browser automation), Hangfire for durable jobs, Playwright for browser automation, Microsoft.Extensions.AI for the provider abstraction, all self hosted on one VPS via Docker Compose. This repo does **not** deploy to Vercel; see [specs/0001-stack-architecture.md](../specs/0001-stack-architecture.md) for the full decision record and consequences.

**Assumptions made while planning (flag if wrong):** MVP boundary is the Job Agent's core loop (discover, match, prepare, approve, track) before University Agent or Personal Agent depth; LinkedIn automation is scoped to discovery and assisted preparation only, never assumed-authorized auto-submission.

## At a glance

| # | Feature | Phase | Status |
|---|---------|-------|--------|
| 1 | Stack & architecture | Foundation | done |
| 2 | Coding standards & tooling | Foundation | done |
| 3 | Data model | Foundation | done |
| 4 | Design system & UI foundation | Foundation | done |
| 5 | Auth & app shell | Foundation | done |
| 6 | Agent orchestrator core | Foundation | done |
| 7 | AI provider abstraction | Foundation | in-progress |
| 8 | Approval engine & Approval center | Foundation | planned |
| 9 | Job source ingestion & normalization | Slice 1: Job Agent core loop | planned |
| 10 | Job deduplication | Slice 1: Job Agent core loop | planned |
| 11 | Job matching engine & scoring | Slice 1: Job Agent core loop | planned |
| 12 | Jobs list & job detail | Slice 1: Job Agent core loop | planned |
| 13 | Dashboard overview | Slice 1: Job Agent core loop | planned |
| 14 | Resume management | Slice 1: Job Agent core loop | planned |
| 15 | Cover letter generation | Slice 1: Job Agent core loop | planned |
| 16 | Application preparation flow | Slice 1: Job Agent core loop | planned |
| 17 | Application pipeline & tracking | Slice 1: Job Agent core loop | planned |
| 18 | Application execution engine (browser worker / ATS) | Slice 1: Job Agent core loop · GA | planned |
| 19 | Activity feed & audit log | Slice 1: Job Agent core loop | planned |
| 20 | Notifications | Slice 1: Job Agent core loop | planned |
| 21 | University/program/professor/scholarship discovery | Slice 2: University Agent | planned |
| 22 | Research matching engine | Slice 2: University Agent | planned |
| 23 | Gmail integration (OAuth) | Slice 2: University Agent · GA | planned |
| 24 | Outreach pipeline & email composer | Slice 2: University Agent · GA | planned |
| 25 | Email thread tracking & reply detection | Slice 2: University Agent | planned |
| 26 | Follow up scheduling | Slice 2: University Agent | planned |
| 27 | Calendar integration & screen | Slice 3: Personal Agent | planned |
| 28 | Tasks screen & task engine | Slice 3: Personal Agent | planned |
| 29 | Interview lifecycle automation | Slice 3: Personal Agent | planned |
| 30 | Settings | System | planned |
| 31 | Integrations hub | System | planned |
| 32 | Command palette & global search | System | planned |
| 33 | Onboarding flow | System | planned |

## Deferred

- **LinkedIn auto submission**: full unattended submission on LinkedIn · needs a decision · not assumed authorized, discovery/prep only for now
- **Multi language / i18n**: out of scope for a single user MVP
- **Mobile layout**: desktop first per spec; responsive polish deferred
- **Multi user / team accounts**: product is single user for now
- **ReAct style Planner & cross run memory**: revisit once a real domain agent's goals show the plan then execute, cold start model is limiting (from spec 0005 follow up)
- **Approval expiry**: pending approvals never expire today; revisit once real usage shows them piling up (from spec 0005 follow up)

## Epics

- [foundation.md](foundation.md) — stack, tooling, data model, design system, shell, agent core, approvals
- [job-agent.md](job-agent.md) — job discovery through application execution and tracking
- [university-agent.md](university-agent.md) — universities, professors, research matching, outreach, Gmail
- [personal-agent.md](personal-agent.md) — calendar, tasks, interview lifecycle
- [system.md](system.md) — settings, integrations hub, command palette, onboarding

## /scope plan · Personal Work Agent

**33 features planned (0 already on the scope, 4 deferred), build approach Tracer Bullet, workflow Beta (GA on send/submit features).**
Next: `/clear`, then `/architect stack & architecture`
Heads up: the original product notes sketch a .NET/Blazor stack, but this repo's environment (Vercel, JS Mastery skills) points to Next.js/TypeScript — resolve this explicitly in spec 1, don't let it default silently. Application execution (browser automation against real job sites) and Gmail sending are the highest risk features in the whole scope; both are tagged GA and both need `/architect` before any code.
Scope written to `docs/scope/index.md` (+ epic files).
