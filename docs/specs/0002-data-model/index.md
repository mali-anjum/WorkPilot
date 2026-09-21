# 0002. Core data model for the Personal Work Agent

**Date**: 2026-09-21
**Status**: Accepted

## Summary

This decision fixes the shared database schema every later feature builds on: around 35 entities covering the user's profile, jobs, applications, universities, outreach, the agent's own run history, approvals, and audit trail. Every record pulled from an outside source (a job posting, a university page) carries where it came from and when. History that matters (applications, audit entries) is never truly deleted, only marked hidden; high volume internal logs are deleted on a schedule instead, with any bulky content kept in file storage rather than the database. The schema lives entirely in EF Core's migrations, in its own `app` schema, and never touches the tables Supabase (the hosted login/file storage layer) owns for itself.

## Requirements

**User stories**:
- As the developer, I want a schema covering every entity the product spec names, so that every later feature has somewhere to persist its state without a schema change blocking it.
- As the developer, I want provenance fields on every externally sourced record, so that the agent can show its work and a wrong match can be traced back to its source.
- As the developer, I want application and audit history to survive record removal, so that the pipeline and audit trail (scope features 17, 19) stay intact even after a job posting or application is withdrawn.

**Acceptance criteria**:
- **AC-1**: `dotnet ef database update` applies a single initial migration cleanly against the self hosted Supabase Postgres database, creating every entity below in the `app` schema.
- **AC-2**: Every externally sourced entity (`Job`, `JobSnapshot`, `University`, `Program`, `Professor`, `ResearchArea`, `Scholarship`) has a `Provenance` value (`SourceUrl`, `RetrievedAt`, `VerifiedAt`, `Confidence`), enforced as required (non nullable) at the database level.
- **AC-3**: Soft deleting a `Job`, `JobApplication`, `University`, `Professor`, `Scholarship`, `Resume`, `CoverLetter`, `OutreachContact`, `Notification`, or `AuditLog` sets `IsDeleted`/`DeletedAt` and is excluded from default EF Core queries via a global query filter, without breaking any foreign key referencing it.
- **AC-4**: `JobApplication.Status` only permits the transitions named in `## Feature design`; the entity itself throws when an invalid transition is attempted, this is proven by a unit test with no database involved.
- **AC-5**: `ResumeVersion` and `CoverLetterVersion` rows are never updated after creation (enforced by having no setter for their content fields once persisted); a new edit always inserts a new version row and a new Storage object.
- **AC-6**: `OAuthConnection.AccessToken`/`RefreshToken` are stored encrypted (ASP.NET Core Data Protection) such that a direct `SELECT` against the column never returns the raw token value.
- **AC-7**: EF Core's migrations touch only the `app` schema; no migration in this project ever creates, alters, or drops a table under `auth.*` or `storage.*`.
- **AC-8**: `AgentStep`/`ToolCall`/`WorkflowEvent` rows older than a configurable retention window (default 90 days) are eligible for hard deletion by a scheduled job (the job itself is out of scope here, only the schema's support for it: a queryable `OccurredAt`/`Timestamp` column and no foreign key that would orphan on their removal).

## Decision

**Chosen option**: A shared `Provenance` owned type plus a hybrid deletion model (soft delete for business/audit relevant entities, hard delete on a retention window with Storage offload for high volume Agent/workflow internals). Full options considered: see [rationale.md](rationale.md).

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`)

## Feature design

**Data model sketch** (entity, key fields, relationships; `PK` = GUID v7 unless noted; `PROV` = carries the shared `Provenance` owned type; `SD` = soft delete with global query filter):

*Identity & Profile*
- `Profile` `SD` — 1:1 `auth.users` (`AuthUserId`), Name, Headline, TargetRoles (text[]), Location
- `Skill` — Name (unique), Category; M:N with `Profile` via `ProfileSkill`
- `Experience` — FK `ProfileId`, Company, Title, StartDate, EndDate (nullable)
- `Education` — FK `ProfileId`, Institution, Degree, Field, StartDate, EndDate (nullable)

*Resumes & cover letters*
- `Resume` `SD` — FK `ProfileId`, Name, IsActive
- `ResumeVersion` — FK `ResumeId`, StorageUrl, VersionNumber (unique per `ResumeId`), ParsedContent (jsonb), CreatedAt; immutable after insert (AC-5)
- `CoverLetter` `SD` — FK `ProfileId`, Name
- `CoverLetterVersion` — FK `CoverLetterId`, StorageUrl, VersionNumber (unique per `CoverLetterId`), GeneratedForApplicationId (nullable FK `JobApplication`), CreatedAt; immutable after insert

*Jobs*
- `JobSource` — Name, Type, Config (jsonb)
- `Job` `SD` `PROV` — FK `JobSourceId`, Title, Company, Location, RemoteType, SalaryRangeMin/Max (nullable), ExternalId (unique per `JobSourceId`)
- `JobSnapshot` `PROV` — FK `JobId`, RawContent, `ContentHash` (indexed, non unique), RetrievedAt
- `JobMatch` — FK `JobId`, FK `ProfileId` (unique together), Score (numeric), MatchedSkills (jsonb), RankedAt

*Applications*
- `JobApplication` `SD` — FK `JobId`, FK `ProfileId`, FK `ResumeVersionId`, FK `CoverLetterVersionId` (nullable), Status (enum, see State transitions)
- `ApplicationAnswer` — FK `JobApplicationId`, Question, Answer
- `ApplicationEvent` — FK `JobApplicationId`, EventType, Payload (jsonb), OccurredAt; append only, never deleted

*Universities*
- `University` `SD` `PROV` — Name, Website, Location
- `Program` `PROV` — FK `UniversityId`, Name, Degree, Field
- `Professor` `SD` `PROV` — FK `UniversityId`, Name, Email (nullable), ProfileUrl
- `ResearchArea` — Name (unique); M:N with `Professor` via `ProfessorResearchArea`
- `Scholarship` `SD` `PROV` — FK `UniversityId` (nullable), Name, AmountRange, DeadlineDate

*Outreach*
- `OutreachContact` `SD` — FK `ProfessorId` (nullable), Email, Name
- `OutreachMessage` — FK `OutreachContactId`, Subject, Body, Status
- `EmailThread` — FK `OutreachMessageId` (1:1), GmailThreadId (unique)
- `FollowUp` — FK `OutreachMessageId`, ScheduledFor, Status

*Personal*
- `Task` `SD` — FK `ProfileId`, Title, DueDate (nullable), Status, LinkedApplicationId (nullable), LinkedOutreachMessageId (nullable)
- `CalendarEvent` — FK `ProfileId`, ExternalCalendarId, Title, StartsAt, EndsAt

*Agent & workflow*
- `WorkflowInstance` — DefinitionName, Status (Hangfire backed durable state)
- `WorkflowStep` — FK `WorkflowInstanceId`, StepName, Status, PayloadUrl (Storage ref, nullable); hard delete after retention window (AC-8)
- `WorkflowEvent` — FK `WorkflowInstanceId`, EventType, PayloadUrl (nullable), OccurredAt; hard delete after retention window
- `AgentRun` — FK `WorkflowInstanceId` (1:1), Goal, Status
- `AgentStep` — FK `AgentRunId`, ReasoningUrl (Storage ref, nullable for short reasoning kept inline), Timestamp; hard delete after retention window
- `ToolCall` — FK `AgentStepId`, ToolName, InputPayloadUrl (nullable), OutputPayloadUrl (nullable), Success; hard delete after retention window

*Approvals & audit*
- `Approval` — TargetType, TargetId (polymorphic), RiskTier, Status, DecidedAt (nullable), DecidedBy (nullable)
- `AuditLog` `SD` — Actor, Action, TargetType, TargetId, Payload (jsonb), OccurredAt; append only

*Integrations & notifications*
- `Integration` — ProviderName, Status
- `OAuthConnection` — FK `IntegrationId`, FK `ProfileId`, AccessToken (Data Protection encrypted), RefreshToken (encrypted), ExpiresAt
- `Notification` `SD` — FK `ProfileId`, Type, Payload (jsonb), ReadAt (nullable)

Shared owned type: `Provenance { SourceUrl, RetrievedAt, VerifiedAt (nullable), Confidence (numeric, nullable) }`.

**State transitions** (`JobApplication.Status`, enforced in the entity, AC-4):

Discovered → Matched → Preparing → PendingApproval → Submitted → Interviewing → (Offered | Rejected) ; any state → Withdrawn.

No transition may skip a stage forward (e.g. Discovered → Submitted is rejected); `Withdrawn` is reachable from any non terminal state; `Offered`/`Rejected`/`Withdrawn` are terminal.

**API surface**:

This spec covers the persistence schema only; endpoints belong to the features that consume these entities (Jobs list, Application pipeline, Approval center, etc., scope features 9 to 33) and are designed in their own specs. No API surface here.

**Value sourcing**:
| Action | Value produced / displayed | Source |
|---|---|---|
| Any migration run | Which schema tables land in | Fixed as `app` via `modelBuilder.HasDefaultSchema("app")`, already set in spec 0001 |
| Insert on a `PROV` entity | `Provenance.RetrievedAt` | Set by the ingesting feature (e.g. Job source ingestion, scope feature 9) at write time, not by this schema |
| Soft delete on any `SD` entity | `DeletedAt` | Set to the current UTC time by the delete operation itself |
| `AgentStep`/`ToolCall`/`WorkflowEvent` retention sweep | The cutoff date | `OccurredAt`/`Timestamp` compared against a configured retention window (`AGENT_TRACE_RETENTION_DAYS`, default 90) |

**Key invariants**:
- Every `PROV` entity's `Provenance.SourceUrl` and `RetrievedAt` are non nullable; `VerifiedAt`/`Confidence` may be null until a verification step runs.
- `ResumeVersion.VersionNumber` and `CoverLetterVersion.VersionNumber` are unique per parent and monotonically increasing; content fields have no setter once persisted (AC-5).
- `JobApplication.Status` transitions only along the state machine above; enforced in `JobApplication`'s own methods, never via a raw setter (AC-4).
- A soft deleted row (`IsDeleted = true`) is invisible to default EF Core queries (global query filter) but its foreign keys are never physically removed, so `ApplicationEvent`/`AuditLog` history referencing it stays intact (AC-3).
- No EF Core migration in this project ever targets `auth.*` or `storage.*` (AC-7), preserving the ownership boundary spec 0001 set.

**Security model**:
- Every table lives in the `app` schema, owned and migrated exclusively by EF Core from the .NET backend; Supabase's own layer (GoTrue, Storage, PostgREST if enabled later) never writes to it.
- `OAuthConnection.AccessToken`/`RefreshToken` are encrypted with ASP.NET Core Data Protection before the value reaches EF Core, so raw tokens never exist in a Postgres column, a Supabase Studio view, or a database backup (AC-6). Only the .NET backend holds the key ring; the LLM and Supabase layer never see a raw token.
- Row level scoping to a single `Profile` (the founder, today) is enforced in the Application layer, not Postgres row level security, since the system is single user; a future multi user version would need to revisit this, noted in Follow-up.

**Configuration required**:
- `AGENT_TRACE_RETENTION_DAYS`: how many days `AgentStep`/`ToolCall`/`WorkflowEvent` rows are kept before a scheduled job may hard delete them (default `90`).
- Data Protection key ring persistence location (e.g. a Postgres backed key store or a mounted volume on the VPS) so encrypted `OAuthConnection` tokens remain decryptable across container restarts; the exact mechanism is a `/develop` time choice, not fixed here.

**Critical test scenarios**:
- Happy path: applying the initial migration against the self hosted Supabase Postgres database creates every table above with no errors, verifies **AC-1**.
- Failure case: attempting `JobApplication.Status` transition from `Discovered` directly to `Submitted` throws, with no database call made, verifies **AC-4**.
- Failure case: soft deleting a `Job` that has a `JobApplication` referencing it leaves the `JobApplication` and its `ApplicationEvent` history queryable and intact, verifies **AC-3**.
- Auth/permission: reading `OAuthConnection.AccessToken` directly via SQL (bypassing the .NET backend) returns ciphertext, not a usable token, verifies **AC-6**.

## Build plan

1. Define the `Provenance` owned type and apply it to `Job`, `JobSnapshot`, `University`, `Program`, `Professor`, `ResearchArea`, `Scholarship`, satisfies **AC-2**
2. Model the Identity/Profile, Resume/CoverLetter (with immutable version entities), Jobs, and Applications entities and relationships, including the `JobApplication` state machine enforced in the entity, satisfies **AC-1**, **AC-4**, **AC-5**
3. Model the Universities, Outreach, Personal (Task/CalendarEvent), Integrations/Notifications, and Approvals/Audit entities, satisfies **AC-1**
4. Model the Agent/Workflow entities (`WorkflowInstance`/`WorkflowStep`/`WorkflowEvent`, `AgentRun`/`AgentStep`/`ToolCall`) with Storage backed payload references, satisfies **AC-1**, **AC-8**
5. Add the global soft delete query filter and `IsDeleted`/`DeletedAt` columns to every `SD` entity, satisfies **AC-3**
6. Configure ASP.NET Core Data Protection and wire `OAuthConnection.AccessToken`/`RefreshToken` through it (EF Core value converter), satisfies **AC-6**
7. Generate and apply the single initial migration against the self hosted Supabase Postgres database, confirming it targets only the `app` schema, satisfies **AC-1**, **AC-7**

Built as one Tracer Bullet slice (the project's build approach): a single coherent migration covering the full target model, since every later feature needs the whole schema to exist before it can build against any part of it; there is no meaningful "thin thread" subset of a shared schema.

## Consequences

**Positive**:
- Every later feature (Slice 1 through System) has a complete, coherent schema to build against with no further foundational migrations expected.
- Provenance and audit requirements from the product spec are structurally enforced, not left to each feature to remember.

**Negative / tradeoffs**:
- A single large initial migration is higher risk than several small ones; a mistake in one entity's shape is more expensive to fix once other features have started building on it, mitigated by the confirm gate already run during design.
- The hybrid deletion model (soft vs hard) is one more rule engineers must apply correctly per entity rather than a single uniform rule.
- The polymorphic `Approval.TargetType`/`TargetId` pair trades referential integrity (no real foreign key) for the flexibility of approving any entity type; a bad `TargetId` is only caught in application code, not by Postgres.

**Neutral**:
- `Task`/`OutreachMessage` linking fields (`LinkedApplicationId`, etc.) are nullable and loosely coupled by design, since not every task or message originates from the Job or University Agent.

## Follow-up

- [ ] Design the Agent orchestrator core (scope feature 6) needs to confirm `AgentRun`/`AgentStep`/`ToolCall` match its actual Planner/Policy/Execution/Verification flow; this schema is a reasonable target but the orchestrator spec owns the final word.
- [ ] Decide the Data Protection key ring persistence mechanism (Postgres backed key store vs mounted volume) when `/develop` builds this feature; noted as an open implementation detail, not blocking the schema itself.
- [ ] Revisit the single user assumption in `Security model` (no Postgres row level security) if the product ever moves beyond a single user, per the scope's own "designed so it could later serve more" note.

## Rationale

Full context, options considered, and reasoning: see [rationale.md](rationale.md).

