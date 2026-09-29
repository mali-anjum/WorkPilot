# 0018. Module contracts for the remaining features

**Date**: 2026-09-26
**Status**: In Progress

## Summary

This spec fixes, once, how every remaining feature (#10 to #33) fits into the codebase, so later features can be designed in batches and built unattended without stepping on each other. It says which module owns and writes which tables, how modules talk (direct interface calls when an answer is needed now, durable events through an outbox table when something just happened), how each module plugs into `Program.cs` with two lines, one error pattern for every endpoint, and the full page route map. It ends with the order the remaining features are built in, wave by wave, and the rule for small decisions the AI makes on its own during those runs.

## Decision

**Chosen option**: Option 1: a module contract standard, enforced going forward, with one small groundwork change built first. Full options and reasoning: see [rationale.md](rationale.md).

Every module owns its tables and is the only one that writes them. Any module may read any table. Modules call each other through the owner's Application interface when the caller needs a result now, and publish domain events (facts that something happened) through a transactional outbox when others merely react.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Standard definition

### 1. Module ownership (who writes what)

The 13 modules from `AGENTS.md` own the tables spec 0002 created. "Owner" means: the only module whose code creates, updates or deletes rows in that table, and whose EF configuration file describes it. Every other module may **read** it (plain LINQ over `WorkPilotDbContext`, `AsNoTracking`), never write it.

| Module | Owns (writes) | Remaining features it hosts |
|---|---|---|
| Identity | `Profile` row creation only | none |
| Profile | `Profile` fields, `Skill`, `ProfileSkill`, `Experience`, `Education`, `Resume`, `ResumeVersion`, `CoverLetter`, `CoverLetterVersion` | #15 cover letters, #33 onboarding |
| Jobs | `JobSource`, `Job`, `JobSnapshot`, `JobSourceLink` (spec 0017), `JobMatch` | #10 dedup, #11 matching, #12 jobs list/detail |
| Applications | `JobApplication`, `ApplicationAnswer`, `ApplicationEvent`, plus a new `Interview` table (#29) | #16 prep flow, #17 pipeline, #18 execution engine, #29 interview lifecycle |
| Universities | `University`, `Program`, `Professor`, `ResearchArea`, `ProfessorResearchArea`, `Scholarship`, plus a new research match table (#22) | #21 discovery, #22 research matching |
| Outreach | `OutreachContact`, `OutreachMessage`, `EmailThread`, `FollowUp` | #24 composer, #25 reply detection, #26 follow ups |
| Calendar | `CalendarEvent` | #27 calendar |
| Tasks | `Task` | #28 tasks |
| Agent | `WorkflowInstance`, `WorkflowStep`, `WorkflowEvent`, `AgentRun`, `AgentStep`, `ToolCall` | none new (every feature adds tools) |
| Approvals | `Approval` | none new |
| Integrations | `Integration`, `OAuthConnection` (tokens) | #23 Gmail connection, #27 Google Calendar connection, #31 integrations hub |
| Notifications | `Notification` | #20 notifications |
| Audit | `AuditLog`, `OutboxMessage` (new, section 3) | #19 activity feed |

Features with no table of their own: #13 dashboard, #30 settings and #32 command palette are **compositions** in `WorkPilot.Web`. They read through other modules' query interfaces and own nothing.

**Shared entity rules** (the ones two modules both care about):
- **JobApplication status** changes only through `IApplicationService` in Applications. The execution engine (#18), reply detection (#25) and interview lifecycle (#29) ask Applications to move it. They never set it themselves. The state machine stays in the entity (spec 0002, AC-4).
- **Professor vs OutreachContact**: Universities owns `Professor` (a discovered, sourced record). Outreach owns `OutreachContact` (someone you actually email), linked by the nullable `ProfessorId`. Outreach creates the contact when you first compose to a professor.
- **Gmail and Calendar tokens**: Integrations alone reads and refreshes `OAuthConnection`. It exposes `IGmailClient` and `IGoogleCalendarClient` (Application interfaces, Infrastructure adapters). Outreach and Calendar never see a token.
- **Tasks linked to other work**: Tasks owns `Task`. `LinkedApplicationId` and `LinkedOutreachMessageId` are set by Tasks' own event handlers (for example "follow up due" creates a task), never by the source module.
- **Audit and activity**: `AuditLog` is written through `IAuditService` in the same unit of work as the change (unchanged from spec 0005: audit is never an event, it must commit atomically). The activity feed (#19) is a **read view** over `AuditLog`, with no table of its own.
- **Settings**: each module keeps its own settings in its own table or columns and exposes `ISettingsSection` (section 5). No shared settings table.
- **Schema changes to a table** go through the owner module. A feature in another module that needs a new column on it adds the column in the owner's EF configuration file, in that feature's own migration.

### 2. Talking across modules

Two mechanisms, chosen by one question: **does the caller need the answer to continue?**

- **Yes: call an interface.** The owner module publishes an interface in `WorkPilot.Application/Modules/<Owner>/` (for example `IApplicationService.MoveToAsync(...)`, `IJobMatchQuery.GetScoreAsync(...)`). The caller injects it. Only the Application interface crosses the module line, never an Infrastructure class and never an entity. Returned values are DTOs or `Result<T>` (section 4).
- **No, others just react: publish a domain event.** The source module raises a fact about its own data, and does not know who listens.
- **Inside one module, never use an event.** A module hands work to itself with a direct call or by enqueuing its own Hangfire job (for example ingestion enqueues matching directly). Events exist only to cross a module boundary.

**Domain events**:
- An event is a plain `sealed record` implementing the marker `IDomainEvent` (in `WorkPilot.Domain/Common/`, no framework code). It lives in `WorkPilot.Domain/Modules/<Source>/Events.cs`. It is named in the past tense and carries ids and small values only, never an entity. It declares a stable name, `public static string EventName => "<module>.<event-kebab>.v1"` (for example `applications.status-changed.v1`), which is what the outbox stores. Renaming the class never changes the name; changing the payload shape bumps the version.
- Publish with `IEventPublisher.Publish(evt)` (Application; implemented by `EventPublisher` in `WorkPilot.Infrastructure/Modules/Audit/`). It serializes the event itself with System.Text.Json web defaults (the jsonb payload is separate from Hangfire's own argument serializer). Like `IAuditService.Record`, it only adds an `OutboxMessage` row to the current unit of work. The caller's own `SaveChangesAsync` commits the change and the event together, or neither.
- A handler implements `IEventHandler<TEvent>` in `WorkPilot.Application/Modules/<Subscriber>/Handlers/`, declares a stable `HandlerKey` (`<module>.<handler-kebab>`, for example `notifications.on-approval-requested`), and writes only its own module's tables. Handlers only write to the database. Anything with a real world side effect (sending, submitting, calling a provider) goes through an agent tool and the approval engine, never a handler.
- **Registry**: at startup an `EventRegistry` is built from the DI registrations: `EventName` to CLR type, and `HandlerKey` to a non generic invoker (`IEventHandlerInvoker.HandleAsync(object evt, CancellationToken)`) that resolves the typed handler from DI. Startup throws on a duplicate `EventName` or `HandlerKey`, or on a stored name with no type.
- Delivery is at least once, and idempotency is **structural**, not a convention: see `outbox_deliveries` in section 3.

**Event catalog** (the events the remaining features need; a feature may add one, see section 8):

| Event | Raised by (feature) | Handled by (feature) |
|---|---|---|
| `JobMatched(jobId, profileId, score)` | Jobs (#11) | Notifications (#20), when over the profile's threshold |
| `ApplicationStatusChanged(applicationId, from, to)` | Applications (#16, #17, #18) | Notifications (#20), Tasks (#28), Applications interview lifecycle (#29) |
| `ApplicationSubmissionFailed(applicationId, reason)` | Applications (#18) | Notifications (#20), Tasks (#28) |
| `ApprovalRequested(approvalId, targetType, targetId)` | Approvals (#8, existing code raises it once groundwork lands) | Notifications (#20) |
| `ApprovalDecided(approvalId, status)` | Approvals (#8) | Notifications (#20) |
| `AgentRunFailed(agentRunId, reason)` | Agent (#6): raised at every place a run becomes Failed (`PlanRunJob` planning failure, the policy violation transition, `AdvanceRunJob` step failure); the existing audit records stay | Notifications (#20) |
| `OutreachMessageSent(messageId, contactId)` | Outreach (#24) | Outreach follow ups (#26) |
| `EmailReplyReceived(threadId, messageId, linkedApplicationId?)` | Outreach (#25) | Notifications (#20), Outreach follow ups (#26, cancels pending), Applications (#29, may move to Interviewing through its own service) |
| `FollowUpDue(followUpId, messageId)` | Outreach (#26) | Tasks (#28), Notifications (#20) |
| `CalendarEventsSynced(profileId, createdIds, changedIds)` | Calendar (#27) | Applications interview lifecycle (#29) |
| `InterviewScheduled(interviewId, applicationId, startsAt)` | Applications (#29) | Tasks (#28, prep task), Notifications (#20) |
| `TaskDue(taskId)` | Tasks (#28) | Notifications (#20) |
| `IntegrationDisconnected(integrationId, provider, reason)` | Integrations (#23, #27, #31) | Notifications (#20) |

### 3. The outbox (durable event delivery)

- **Table** `app.outbox_messages`, owned by Audit: `Id` (GUID v7), `EventName` (text, the stable name), `Payload` (jsonb), `OccurredAt` (timestamptz), `DispatchedAt` (timestamptz, nullable). Partial index on `OccurredAt` where `DispatchedAt IS NULL`. Hard deleted, not soft deleted: dispatched rows older than `Outbox:RetentionDays` (default 30) are removed by the sweep.
- **Table** `app.outbox_deliveries`, owned by Audit: primary key (`MessageId`, `HandlerKey`), `HandledAt` (timestamptz). This is what makes handlers idempotent.
- **Interceptor**: a singleton `OutboxSaveChangesInterceptor` (it uses Hangfire's singleton `IBackgroundJobClient`). In `SavingChanges` it notes whether any `OutboxMessage` is being added; in `SavedChanges` it enqueues one `DispatchOutboxJob`. Inside an explicit transaction `SavedChanges` can fire before the commit. That is harmless: the dispatcher only sees committed rows, and the sweep catches anything it missed.
- **Dispatcher** `DispatchOutboxJob` (in `WorkPilot.Workers/Audit/`, marked `[DisableConcurrentExecution]`): in one transaction it claims undispatched rows oldest first with `FOR UPDATE SKIP LOCKED` (batch of 100), enqueues one `HandleEventJob(messageId, handlerKey)` per registered handler of that event (both arguments are strings or ids, never a CLR type, so a class rename never breaks queued jobs), and sets `DispatchedAt`. A crash after enqueuing but before commit re-enqueues that message's handlers later; `outbox_deliveries` makes the repeat a no op.
- **Handler run** `HandleEventJob`: in one transaction it inserts the `outbox_deliveries` row (if the key already exists, it returns without running the handler), runs the handler, and commits both together. Each handler therefore retries on its own (Hangfire's retries) without rerunning the others, and a repeat never double writes.
- **Safety sweep**: a recurring job runs `DispatchOutboxJob` every minute, so a crash between commit and enqueue loses nothing. It also deletes rows past the retention window.
- A handler that exhausts its retries lands in Hangfire's failed list (visible at `/hangfire`). It never blocks other handlers or other events.
- **Ordering** is not guaranteed across events. A handler that cares reads current state instead of trusting event order.

### 4. Errors: `Result<T>` plus ProblemDetails

**Canonical pattern**:

```csharp
// WorkPilot.Application/Common/Result.cs
public enum ResultStatus { Ok, NotFound, Invalid, Conflict, Forbidden }
public sealed record Result<T>(ResultStatus Status, T? Value, IReadOnlyDictionary<string, string[]>? Errors = null, string? Detail = null)
{
    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value);
    public static Result<T> NotFound(string? detail = null) => new(ResultStatus.NotFound, default, null, detail);
    public static Result<T> Invalid(string field, string message) => new(ResultStatus.Invalid, default, new Dictionary<string, string[]> { [field] = [message] });
    public static Result<T> Conflict(string detail) => new(ResultStatus.Conflict, default, null, detail);
    public static Result<T> Forbidden(string? detail = null) => new(ResultStatus.Forbidden, default, null, detail);
}

// Use case (Application): expected failures are values, not exceptions.
public async Task<Result<TaskDto>> CompleteAsync(Guid profileId, Guid taskId, CancellationToken ct)
{
    var task = await repo.FindAsync(profileId, taskId, ct);
    if (task is null) return Result<TaskDto>.NotFound();
    if (task.IsDone) return Result<TaskDto>.Conflict("That task is already done.");
    task.Complete(clock.GetUtcNow());
    await repo.SaveAsync(ct);
    return Result<TaskDto>.Ok(task.ToDto());
}

// Endpoint (Api): one shared mapper, no try/catch.
group.MapPost("/{id:guid}/complete", async (Guid id, [FromBody] ProfileRequest body, ITaskService tasks, CancellationToken ct) =>
    (await tasks.CompleteAsync(body.ProfileId, id, ct)).ToHttp(Results.Ok));
// The caller's profile id arrives as an explicit value (query, form or body), exactly as today:
// the Web host reads it from the signed in user's profile_id claim and passes it on (spec 0004).
```

- `ResultHttpExtensions.ToHttp` (in `WorkPilot.Api/Common/`) maps `Ok` to the given success result, `NotFound` to 404, `Invalid` to a 400 validation ProblemDetails, `Conflict` to 409, and `Forbidden` to 403. Every error body is RFC 7807 ProblemDetails (the standard JSON error shape with `title`, `status`, `detail`).
- `builder.Services.AddProblemDetails()` plus `app.UseExceptionHandler()` in the Api turn any **unexpected** exception into a logged 500 ProblemDetails. Throwing is reserved for bugs and broken invariants (a domain entity rejecting an invalid transition is one: that is a programming error at the endpoint, because the use case should have checked).
- Postgres `WP409` (the resume lock trigger) and unique violations the use case expects are caught **inside** the use case's repository and returned as `Conflict`, not left to the global handler.
- Web side: one shared `ApiResult<T>` reader in `WorkPilot.Web/Features/Common/`. It reads ProblemDetails `detail` (or the validation errors) into a message the page shows. It replaces the per feature readers (`ResumeApiResult<T>`).

**Replaces**: `ResumeResult<T>` and its private `ToHttp` in `ResumeEndpoints`; nullable returns turned into `Results.NotFound()` inline in `Program.cs`; per feature Web result readers.

### 5. Plugging a module in (collision free wiring)

Each module adds **exactly two lines** to the Api's `Program.cs` and at most two to the Web's `Program.cs`, placed in **alphabetical order by module** inside a marked block, so parallel branches edit different lines:

```csharp
// === Modules (alphabetical; one Add and one Map line each, spec 0018) ===
builder.Services.AddAgentModule(builder.Configuration);
builder.Services.AddApplicationsModule(builder.Configuration);
builder.Services.AddJobsModule(builder.Configuration);
// ...
app.MapAgentEndpoints();
app.MapApplicationsEndpoints();
app.MapJobsEndpoints();
```

- `Add<Module>Module(IServiceCollection, IConfiguration)` lives in `<Module>Module.cs`. Put it in `WorkPilot.Workers/<Module>/` when the module has Hangfire jobs, otherwise in `WorkPilot.Infrastructure/Modules/<Module>/`. It registers **everything** the module has: repositories, services, query interfaces, event handlers, agent tools (`ITool`), recurring jobs (as `IRecurringJobDefinition` registrations; one neutral `app.ApplyRecurringJobs()` call in the Hangfire setup of `Program.cs`, from `WorkPilot.Workers/Common/`, applies them all, so no module touches Hangfire setup), settings sections, search providers, validated options.
- `Map<Module>Endpoints(IEndpointRouteBuilder)` lives in `WorkPilot.Api/Endpoints/<Module>Endpoints.cs` and maps one group `/internal/<module-kebab>/...`. Every Api endpoint stays internal (spec 0004). Endpoints over per profile data take the caller's `profileId` and scope every query to it; shared catalogs (jobs, job sources, universities) follow their own spec (spec 0017: jobs are shared, not scoped to a profile).
- **Stays outside the modules block** (host plumbing, not a module): `AddServiceDefaults`, `AddDataProtection`, the DbContext, Hangfire and its server, `AddWorkPilotAi`, `AddProblemDetails`/`UseExceptionHandler`, `ApplyRecurringJobs`, the health endpoints, and Identity's `/internal/identity/profile`.
- Web: `Add<Module>Web()` in `WorkPilot.Web/Features/<Module>/<Module>WebExtensions.cs` registers the typed Api client. Pages live in `WorkPilot.Web/Components/Pages/<Module>/`.
- **DTOs that cross Api and Web**: new ones go in `WorkPilot.Contracts/<Module>/`. DTOs used only inside the Api stay in Application. Existing ones (the resume DTOs in Application) stay where they are until a feature touches them. Web keeps its current references to Application and Infrastructure (needed for `IAuthService`).
- **Composition contracts** (implemented by each module, collected by Web):
  - Keys below (`ISettingsSection.Key`, `ISearchProvider.Kind`) are prefixed with the module (`jobs.matching`), and startup throws on a duplicate.
  - `ISettingsSection { string Key; string Title; int Order; }` plus the module's own typed get and save endpoints under `/internal/<module>/settings`. The Settings page (#30) lists the sections and renders each module's own settings component.
  - `ISearchProvider { string Kind; Task<IReadOnlyList<SearchHit>> SearchAsync(Guid profileId, string query, int limit, CancellationToken ct); }` in Application. `GET /internal/search?q=` fans out to every provider (#32).
  - `IDashboardTile` query per module (for example `IJobDashboardQuery.GetSummaryAsync(profileId)`), read by `/` (#13).
- EF configurations for new tables go in `WorkPilot.Infrastructure/Modules/<Module>/Persistence/`. `ApplyConfigurationsFromAssembly` already picks them up. Existing files in `Persistence/Configurations/` stay where they are.

### 6. Migrations

- One migration per feature branch, named `Add<FeatureName>` (for example `AddJobMatching`), regenerated after every rebase on `main`. Merge one branch at a time.
- Hand written SQL inside a generated migration (triggers, backfills, partial indexes EF cannot express) is fenced with `// HAND WRITTEN (spec NNNN): keep when regenerating` and listed in that feature's spec, so regeneration keeps it.
- The groundwork change (section 9) adds `AddModuleContracts` (the outbox table).

### 7. Route map (every page, now and planned)

All pages render InteractiveServer (spec 0016) inside the app shell unless noted. Sidebar entries (`NavRoutes`) are marked **nav**.

| Route | Feature | Notes |
|---|---|---|
| `/` **nav** | #13 | Dashboard, composed from dashboard queries |
| `/jobs` **nav** · `/jobs/{id:guid}` | #12 | List with filters and score; detail shows links (#10), match breakdown (#11), "Prepare application" |
| `/applications` **nav** · `/applications/{id:guid}` | #17 | Pipeline board and detail with events, interview section (#29) |
| `/applications/{id:guid}/prepare` | #16 | Prepare flow: resume version, cover letter, answers, submit for approval |
| `/resumes` **nav** · `/resumes/{id:guid}` | #14 | Exists |
| `/cover-letters` **nav** · `/cover-letters/{id:guid}` | #15 | New nav item under Work, after Resumes |
| `/universities` **nav** · `/universities/{id:guid}` · `/universities/professors/{id:guid}` · `/universities/scholarships` | #21, #22 | Research match shown on professor detail |
| `/outreach` **nav** · `/outreach/compose` · `/outreach/{messageId:guid}` | #24, #25, #26 | Detail shows thread and follow ups |
| `/calendar` **nav** | #27 | |
| `/tasks` **nav** | #28 | |
| `/activity` **nav** | #19 | New nav item under Agent, first in that section |
| `/agent/runs` **nav** · `/agent/runs/{id:guid}` | #6 | Detail page added when a feature needs it |
| `/approvals` **nav** | #8 | Exists |
| `/notifications` | #20 | Full list; the TopBar bell opens a drawer with the latest, no nav item |
| `/settings` **nav** · `/settings/{section}` | #30 | `{section}` is an `ISettingsSection.Key` |
| `/integrations` **nav** · `/integrations/{provider}` | #31 | OAuth callbacks are Web endpoints `/integrations/{provider}/callback`, not pages |
| `/onboarding` | #33 | Outside the sidebar layout; the shell redirects here while the profile is incomplete |
| (overlay, no route) | #32 | Command palette, `Ctrl+K` |

### 8. Rules for the unattended build runs

- A feature spec may add an event, a query interface, a column, or a route **without** a new cross cutting decision, as long as it follows sections 1 to 7. It records the addition in its own spec.
- Changing an ownership row in section 1, adding a new module, or a second way to talk across modules is load bearing. The run stops and routes to `/architect`.
- A small decision no spec covers: the AI picks the option most consistent with the specs, records it under **"Decisions made without the engineer (please review)"** in that feature's spec, and keeps going.
- The run always stops for credentials or OAuth consent, anything tagged GA before it sends or submits for real, a failing test it cannot fix at the root, and a load bearing decision as above.

### Enforcement, rollout, exceptions

**Enforcement**:
- Compile time: `Result<T>`, `IEventPublisher`, `IEventHandler<T>` and `ISettingsSection`/`ISearchProvider` are types, so a use case that ignores them stands out in review. Entities keep private setters and methods for their invariants, so a foreign module cannot set `JobApplication.Status` directly.
- Test: an architecture test (Follow-up) fails when a module's code references another module's repository, service implementation or EF configuration. It **cannot** see a direct `db.<OtherModuleTable>.Add(...)` on the shared DbContext.
- Review: that direct write case is enforced by `/check review` only, which checks every feature's writes against the ownership table.
- Review: `/check review` checks every feature against sections 1 to 6 (write ownership, event idempotency, wiring lines, error mapping).

**Rollout**: new code immediately. Existing code converges once, in the Wave 0 groundwork branch (section 9). No gradual migration.

**Exceptions**:
- `IAuditService` stays a direct same transaction call, never an event.
- Identity's profile provisioning (`/internal/identity/profile`) keeps its current shape; it is sign in plumbing, not a module feature.
- Health endpoints (`/health/db`, `/health/ai`) stay in `Program.cs`.

### 9. Build order (waves)

A **wave** is designed in one `/architect` sitting (all its specs written together), then built unattended **one feature at a time, in order** (parallel builds only when you explicitly ask, and then the one at a time merge rule applies): develop, verify, test, review, document, sync, merge. The next wave is designed only after the previous one is merged, so its specs use what was learned.

| Wave | Features, in build order | Why this order |
|---|---|---|
| 0: groundwork | `feat/module-contracts`: `Result<T>` plus ProblemDetails; the outbox tables, interceptor, dispatcher, `EventRegistry`, `IEventPublisher`/`IEventHandler<T>`; `IAuditService` moved to the Audit module; `ApplyRecurringJobs`; both `Program.cs` module blocks, with every existing registration moved: the inline agent registrations and `/internal/agent/runs` endpoints become `AddAgentModule`/`MapAgentEndpoints` (Workers/Agent), `AddApprovalEngine` becomes `AddApprovalsModule` (Workers/Approvals), `AddJobIngestion` moves to `Workers/Jobs/JobsModule.cs` as `AddJobsModule`, `AddResumeManagement` becomes `AddProfileModule` (Infrastructure/Modules/Profile); `ResumeResult<T>` and `ResumeApiResult<T>` migrated; Approvals and Agent raise their catalog events | Every later feature builds on these; small, no product behavior change |
| 1: job core loop (next) | #10 dedup (spec 0017) → #11 matching → #19 activity feed (spec 0011) → #20 notifications → #12 jobs list/detail → #13 dashboard | Matching needs dedup; the list shows scores; feed and notifications are ready before the pages that link to them; dashboard composes everything |
| 2: applications | #15 cover letters → #16 prep flow → #17 pipeline → #28 tasks (spec 0010) → #30 settings (spec 0015) | Prep needs cover letters; tasks react to pipeline events; settings last so every section exists |
| 3: execution and integrations (GA) | #31 integrations hub → #23 Gmail (spec 0013) → #18 execution engine | The hub hosts the Gmail connection; execution is the riskiest and goes last |
| 4: university agent | #21 discovery (spec 0012) → #22 research matching → #24 outreach composer (GA) → #25 reply detection → #26 follow ups | Each step needs the one before it |
| 5: personal and system | #27 calendar (spec 0014) → #29 interview lifecycle → #32 command palette → #33 onboarding | Interviews need calendar sync; palette and onboarding need every module in place |

These wave numbers replace the old parallel build's "Wave 1" label; its unbuilt features are placed in the waves above. Specs 0010 to 0015 stay reserved for the features named above. New specs take the next free number after 0018.

## Consequences

**Positive**:
- Features are built one at a time, so collisions are rare; even when two are built in parallel they can only collide on the migration snapshot and adjacent `Program.cs` lines, both handled by the merge rule.
- Notifications, tasks, the activity feed and interview automation are added later without editing the modules whose events they react to.
- Expected failures look the same on every endpoint, and the Web shows them the same way.
- Later waves need far fewer questions, because this spec answers the structural ones.

**Negative / tradeoffs**:
- The outbox adds two tables, an interceptor, a registry and two Hangfire jobs. Idempotency is structural (`outbox_deliveries`), but a handler that reaches outside the database would break it, which is why handlers may only write to the database.
- Event effects are eventual (usually under a second, at worst about a minute via the sweep). A page that shows a notification or task right after an action may briefly show stale data.
- "Read any table" lets a module couple to another's schema through queries. A column rename in the owner can break a reader, and only tests catch it.
- "Write own only" is a review rule plus entity encapsulation, not a compiler check, and the architecture test cannot see a direct DbSet write. An unattended run could break it, so `/check review` must look for it.
- The groundwork wave touches files merged in #7 to #14, so it must land before the next feature branch starts.

**Neutral**:
- `IAuditService` changes namespace (Agent to Audit). Callers change their `using` only.
- The activity feed needs no new table, but its query needs an index on `AuditLog (OccurredAt DESC)`, added by #19.

## Follow-up

- [ ] Build the Wave 0 groundwork (`feat/module-contracts`) before any other feature; the scope has no row for it, so `/scope` can enroll one (for example "Module contracts groundwork", phase Foundation).
- [ ] After groundwork merges, `/sync` should replace the "error handling pattern not yet chosen" rule in `AGENTS.md` with a pointer to this spec, and add the module wiring and outbox rules.
- [ ] Add an architecture test (reflection over the Application assembly, in `WorkPilot.Domain.Tests` or a new test project) that fails when an `IEventHandler<T>` or service in module A references a repository or configuration of module B. It covers the part of "write own" a test can see.
- [ ] Design Wave 1 next: `/architect` the job core loop (#11, #19, #20, #12, #13; #10 already has spec 0017) in one sitting.
- [ ] Spec 0002's follow up about deleting old `AgentStep`/`ToolCall` rows can reuse the recurring job registration from section 5 when it is built.

## Rationale

Reasoning and options: see [rationale.md](rationale.md).
