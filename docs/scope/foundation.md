# Epic: Foundation

Everything later features stand on: the stack, the shared data model, the design system, the app shell, and the agent orchestrator's core machinery (planner, policy, tools, workflow, approvals, verification). Nothing in Slice 1 starts until the walking skeleton here boots.

### 1. Stack & architecture · in-progress
.NET Aspire modular monolith (Blazor Web App, Auto render mode, ASP.NET Core backend, EF Core) sharing one self hosted Supabase Postgres database (Supabase owns auth/storage/simple CRUD, the .NET backend owns the Agent/workflows/approvals/browser automation), Hangfire for durable jobs, Playwright for browser automation, Microsoft.Extensions.AI for the provider abstraction, all self hosted on one VPS via Docker Compose.
**Done when:** the stack is recorded in a spec, the empty scaffold boots locally (`dotnet build` + `aspire run`), and the modular monolith module boundaries (Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit) are reflected in the folder structure.
- [x] Decide the stack (spec): `/architect stack & architecture`
- [ ] Build it: `/develop stack & architecture`
   - [ ] Scaffold the .NET Aspire solution (AppHost, Web, Application, Domain, Infrastructure, Workers, Contracts, AI projects) (AC-1, AC-3, AC-4)
   - [ ] Stand up self hosted Supabase (Postgres, GoTrue, Storage) via Docker Compose and connect EF Core to the same database (AC-2, AC-5)
   - [ ] Wire Hangfire against the shared Postgres database and confirm its dashboard (AC-6)
- [ ] Verify it: `/check verify stack & architecture`
- [ ] Test it: `/test stack & architecture`
Spec 0001 · code in `./`

### 2. Coding standards & tooling
Capture conventions (lint, format, commit hooks, test runner) from the real scaffolded project.
**Done when:** root `AGENTS.md` reflects the real stack, and lint/format/pre-commit run clean.
- [ ] Capture conventions + tooling: `/audit`

### 3. Data model · needs a decision
Core entities from the product spec: Users, Profiles, Skills, Experiences, Education, Resumes/Versions, CoverLetters/Versions, Jobs, JobSources, JobSnapshots, JobMatches, JobApplications, ApplicationAnswers, ApplicationEvents, Universities, Programs, Professors, ResearchAreas, Scholarships, OutreachContacts, OutreachMessages, EmailThreads, FollowUps, Tasks, CalendarEvents, AgentRuns, AgentSteps, ToolCalls, Approvals, AuditLogs, Workflows, WorkflowSteps, WorkflowEvents, Integrations, OAuthConnections, Notifications.
**Done when:** the schema supports every entity above with real relationships, migrations apply cleanly, and provenance fields (source URL, retrieved/verified timestamps, confidence) exist on every externally sourced record.
- [ ] Design it (spec): `/architect data model`

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
