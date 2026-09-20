# Personal Work Agent — Master Product Specification

Project codename: **PersonalWorkAgent** (repo: `WorkPilot`)

This is the single product in scope. It is a personal AI powered work execution system for finding jobs, analyzing and matching them, preparing and (where permitted) automating applications, tracking outcomes, finding universities, programs, professors, and scholarships, preparing outreach, sending approved emails, monitoring replies, scheduling follow ups, and managing calendar and tasks.

## 1. Product vision

The user should be able to say "Find opportunities that match me and handle the repetitive work" and have the system turn that into:

INTENTION -> UNDERSTAND -> PLAN -> SEARCH -> ANALYZE -> MATCH -> PREPARE -> ASK APPROVAL -> EXECUTE -> VERIFY -> TRACK -> FOLLOW UP

The product's primary value is execution, not conversation. It is not a chatbot.

## 2. Three agent domains

- **Job Agent**: discovery, normalization, dedup, matching, scoring, resume selection/tailoring, cover letters, application prep/execution/verification/tracking, interview and rejection tracking, follow ups.
- **University Agent**: university/program/professor/research-area discovery, research matching, publication analysis, scholarships, outreach generation, email sending, reply detection, conversation tracking, follow up scheduling.
- **Personal Agent**: Gmail, Calendar, Tasks, Reminders, Notifications, general automation.

## 3. Navigation / IA

```
Overview
WORK: Jobs, Applications, Universities, Outreach
PERSONAL: Calendar, Tasks
AGENT: Agent, Approvals, Activity
SYSTEM: Integrations, Settings
```

## 4. Design system

Visual language: professional, technical, premium, calm, information dense. No excessive gradients/shadows/rounded cards/illustrations/animation/neon.

- Sidebar 240px (72px collapsed), top bar 64px.
- Font: Inter (system-ui fallback). Sizes 32/28/22/18/14/13/12.
- Spacing scale: 4/8/12/16/20/24/32/40/48/64/80. Page padding 32/24/16 by breakpoint.
- Radius: inputs/buttons 8px, cards 10px, dialogs 12px, large panels 14px.
- Color tokens (light/dark) as specified in full spec — background, surface, border, text primary/secondary/muted, primary, success, warning, danger, info. All colors go through design tokens, no arbitrary values.
- Components: AppShell, Sidebar, TopBar, PageHeader, Card, Button, Input, Select, Combobox, Tabs, Badge, StatusBadge, Avatar, Dropdown, Modal, Drawer, Toast, Tooltip, DataTable, Pagination, EmptyState, Skeleton, Timeline, Stepper, ProgressBar, CommandPalette, ApprovalCard, AgentRun, ActivityItem.
- Accessibility target: WCAG 2.2 AA. Primary resolution target 1440x900, also 1920x1080/1280x800/1024x768. Desktop-first.

## 5. Core screens

Dashboard (`/`), Jobs (`/jobs` + detail), Application preparation (`/applications/new`), Application pipeline (`/applications` + detail), Approval center (`/approvals`), Universities (`/universities` with tabs: Universities/Programs/Professors/Scholarships/Research Areas), Professor detail, Outreach (`/outreach`), Email composer, Gmail integration (`/integrations/gmail`), Email tracking/threads, Calendar (`/calendar`), Tasks (`/tasks`), Agent (`/agent` + run detail), Activity (`/activity`), Integrations (`/integrations`), Settings (profile, preferences, job prefs, university prefs, AI, integrations, notifications, security, privacy, automation, appearance, data).

Full layout ASCII mockups and copy are documented in the original spec conversation (2026-09-20) and should be treated as authoritative for wireframing during `/architect` and `/develop`.

## 6. Application state machine

```
DISCOVERED -> MATCHED -> SHORTLISTED -> PREPARING -> READY_FOR_APPROVAL
  -> APPROVED -> SUBMITTING -> CONFIRMATION_PENDING -> APPLIED -> INTERVIEW
Terminal: REJECTED, WITHDRAWN, FAILED
```

Every application must retain the exact resume/cover-letter versions and submission evidence used.

## 7. Approval policy

- **Auto allowed**: read, search, analyze, classify, deduplicate, generate, prepare, monitor, detect, suggest.
- **Approval required**: send email, submit application, withdraw application, create important external action, connect external account.
- **Explicit confirmation**: delete data, change security settings, change permissions, destructive operations.

## 8. Job matching engine

Inputs: skills, experience, education, location, remote preference, salary, technology, job type, work authorization, other configured preferences. Output: match score, confidence, evidence, missing requirements, unknown information. Must be explainable (show "why it matches").

## 9. Job source architecture

`IJobSource` interface -> raw job -> normalizer -> deduplicator -> canonical job (with source list) -> matcher.

## 10. Application automation architecture

Application Engine composed of: supported ATS integrations, provider adapters, and a browser worker (Playwright-based) for navigation, form discovery/mapping, uploads, submission, screenshots, and result extraction. The Agent (not the browser) owns planning, policy, approval, state, verification, audit.

Failure modes to handle explicitly: timeout, login required, CAPTCHA, unsupported form, upload failure, network failure, rate limit, website error, submission uncertain. Anti-bot/CAPTCHA barriers route to human intervention, never bypass.

**LinkedIn is a special case**: never assume unauthorized automation is required or default. Support automatic execution OR user-assisted execution OR manual execution per-platform, based on what is actually permitted. LinkedIn discovery/analysis/matching/prep is automated; final submission may be user-guided.

## 11. Gmail / outreach automation

Flow: professor discovered -> research analyzed -> email generated with evidence -> user approval -> Gmail OAuth send -> thread tracked -> reply detected -> follow-up scheduled -> approval -> sent. OAuth only; the Agent/LLM never receives passwords, OAuth tokens, refresh tokens, client secrets, or session cookies directly — those live in Infrastructure behind the tool layer.

## 12. Provenance

Every external fact retains: source URL, source provider, RetrievedAt, LastVerifiedAt, confidence. Never present stale scraped data as current without qualification.

## 13. Agent architecture

```
AgentOrchestrator
  Planner, Policy Engine, Tool Registry, Workflow Engine,
  Approval Engine, Memory, Execution Engine, Verification Engine
Specialized agents: JobAgent, UniversityAgent, PersonalAgent
```

AI provider abstraction `IAiProvider` (OpenAI, DeepSeek, future providers) — never hardcode a single vendor into domain logic. Flow: user request -> planner -> structured plan -> validation -> policy check -> tool execution -> result -> verification -> state update. The LLM never directly mutates important DB state — it goes through tools.

Every tool declares: name, description, input/output schema, required permissions, risk level, approval requirement, timeout, retry policy, audit policy.

## 14. Workflow engine

Every long-running workflow is persisted step by step (never relies on an in-memory process surviving). Retries use exponential backoff with a hard limit, then route to human review, never retry forever.

## 15. Verification & audit

Every external action must answer "did it actually happen?" with real evidence (message IDs, event IDs, confirmation pages/screenshots). Every meaningful action is audited: who, what, when, why, target, result, evidence. States are distinct: Prepared, Approved, Attempted, Submitted, Verified, Failed, Unknown — never collapse "AI said done" into "done".

## 16. Data model (core entities)

Users, Profiles, Skills, Experiences, Education, Resumes/ResumeVersions, CoverLetters/CoverLetterVersions, Jobs, JobSources, JobSnapshots, JobMatches, JobApplications, ApplicationAnswers, ApplicationEvents, Universities, Programs, Professors, ResearchAreas, ProfessorResearchAreas, Scholarships, OutreachContacts, OutreachMessages, EmailThreads, FollowUps, Tasks, CalendarEvents, AgentRuns, AgentSteps, ToolCalls, Approvals, AuditLogs, Workflows, WorkflowSteps, WorkflowEvents, Integrations, OAuthConnections, Notifications.

## 17. Project structure (target)

```
src/
  PersonalWorkAgent.Web
  PersonalWorkAgent.Application
  PersonalWorkAgent.Domain
  PersonalWorkAgent.Infrastructure
  PersonalWorkAgent.Workers
  PersonalWorkAgent.Contracts
  PersonalWorkAgent.AI
tests/
  Domain.Tests, Application.Tests, Infrastructure.Tests, Integration.Tests, Web.Tests
orchestration/
  PersonalWorkAgent.AppHost
```

Modular monolith first (Modules: Identity, Profile, Jobs, Applications, Universities, Outreach, Calendar, Tasks, Agent, Approvals, Integrations, Notifications, Audit). No microservices at start.

Note: the original spec sketches a .NET-flavored structure (Blazor, .sln). This will be revisited during `/architect` against the team's actual stack decision (see Sprint 0) — the working directory and available tooling (Node/Next.js on Vercel, per environment context) may push toward a TypeScript/Next.js stack instead. That is an explicit open decision for `/architect`, not assumed here.

## 18. Security & permissions

Standard: authn/authz, secure sessions, OAuth, least privilege, encryption, secret management, input/output validation, rate limiting, audit logging, security headers. Fine-grained permissions per tool, e.g. `jobs.read`, `applications.submit`, `gmail.send`, `calendar.create`, `profile.write`.

External actions require idempotency keys (ApplicationSubmissionId, EmailSendId, CalendarOperationId) to survive worker crashes without duplicating.

## 19. UX system rules

Every screen needs a loading (skeleton) state, an empty state, and an error state that explains what happened, why, what the system knows, and what the user can do next. Command palette (Ctrl+K) and global search across jobs/applications/universities/professors/emails/tasks/agent runs/activity.

## 20. Six-month build sequence (reference only — superseded by sprint plan)

Month 1: foundation, design system, auth, profile, DB, shell, settings, deployment, logging.
Month 2: agent core, tool registry, workflow engine, approvals, AI abstraction, audit, scheduler, notifications.
Month 3: Job Agent — discovery, normalization, dedup, matching, resume, cover letter, applications.
Month 4: application execution — ATS integrations, browser worker, Playwright, form mapping, uploads, verification, recovery.
Month 5: University Agent — programs, professors, research matching, scholarships, Gmail, outreach, replies, follow-ups.
Month 6: production hardening — security, testing, observability, performance, accessibility, backups, deployment, docs, polish.

## 21. Definition of done (per feature)

UI, responsive layout, loading/empty/error states, validation, authorization, logging, audit, tests, failure recovery, accessibility, DB migration, docs. For anything touching an external system additionally: permission check, approval policy, idempotency, timeout, retry, verification, evidence.

## 22. The core rule

```
INTENTION -> PLAN -> ACTION -> VERIFICATION -> STATE
```
Never `INTENTION -> AI says "done"`.
