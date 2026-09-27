# 0018. Module contracts: rationale

## Context

> ⚠️ Premise note: the goal behind this spec was to design all 24 remaining features up front, so one long unattended run could build everything without questions. Designing every feature in detail now would rest on guesses about features not yet built (the render mode issue found while building #14, spec 0016, is the kind of discovery that would invalidate them), and some questions (OAuth consent, whether to submit or send for real) can only be answered at build time. This spec narrows the goal to what can safely be fixed now: the structure every feature plugs into. Feature detail is designed wave by wave (section 9 of index.md).

Features 1 to 9 and 14 are built. Each was designed on its own, and the codebase now shows three ways of wiring a module:
- inline registrations in `Program.cs` (the agent orchestrator)
- an extension method in Infrastructure (`AddJobIngestion`, `AddResumeManagement`)
- an extension method in Workers (`AddApprovalEngine`)

It also shows three ways of returning an error:
- a per feature `ResumeResult<T>` with a private `ToHttp` mapper
- nullable returns turned into `Results.NotFound()` inline
- handler outcome types in Approvals

`AGENTS.md` itself records that the error pattern is "not yet chosen".

The remaining 24 features interact far more than the first ten did. An application moving to Interviewing matters to notifications, tasks, the activity feed and interview automation. A reply to an outreach email matters to follow ups, notifications and possibly an application. If each feature decides for itself how to reach the others, every new feature edits the ones before it, and branches built close together collide on the same files. The last parallel build already collided on the EF model snapshot, `Program.cs` and the scope table.

The founder wants to answer the structural questions once and then let the AI build waves of features unattended. That only works if the rules a builder would otherwise invent (who may write a table, how to react to another module, where registrations go, what an error looks like) are written down before the builds start. The system is a single user modular monolith on one VPS with one Postgres database and Hangfire already running. Any mechanism chosen must fit that, not add infrastructure.

## Options considered

### Option 1: A module contract standard, enforced going forward, with a small groundwork change first

Write the ownership map, the two communication mechanisms (interfaces for request and response, outbox backed domain events for reactions), the error pattern, the wiring pattern, the route map and the build order into one spec. Then build a small groundwork branch that adds the shared pieces (the Result type, the outbox, event interfaces, wiring blocks) and moves the existing modules onto them.

**Pros**:
- Every later feature has one obvious way to do each structural thing, so unattended builds stay consistent.
- The existing code converges early, while it is still only ten features.
- Uses only what is already running (Postgres, Hangfire, EF Core).

**Cons**:
- The outbox adds moving parts and demands idempotent handlers.
- One groundwork branch must land before any feature work continues.

### Option 2: Document the standard only, no groundwork

Write the same spec but let each feature adopt the pieces as it needs them: the first feature that needs events builds the outbox, and old modules keep their own patterns until touched.

**Pros**:
- No pause before feature work resumes.
- No churn in code that already works.

**Cons**:
- The first feature that needs events also carries the outbox, so it is bigger and riskier, and two parallel branches may both try to build it.
- Old and new patterns coexist for a long time, and an unattended builder copying nearby code will copy the old ones.

### Option 3: Full detailed design of all 24 features up front

Run `/architect` on every remaining feature now, write all specs, then build everything in one long run.

**Pros**:
- Questions are answered in one period, as the founder hoped.
- Every spec exists before any build.

**Cons**:
- Specs for later features rest on guesses about earlier ones and go stale as those ship. Rewriting them costs about as much as writing them late.
- Several decisions (OAuth consent, real submission and sending) cannot be settled until the feature exists.
- A very long unattended run on a 6 GB machine with no CI has a large failure blast radius.

## Rationale

Option 1 is chosen because the forces in Context are structural, not feature level. Collisions come from shared wiring files and from modules reaching into each other, and repeated questions come from undecided structure. Both are removed by fixing the structure once. Option 3 tries to remove questions by answering feature detail early, which fails for the reason the premise note gives. Option 2 fixes the structure on paper but leaves the code with three patterns for each thing. An unattended builder learns from the code around it more than from a spec, so the groundwork branch is what makes the standard stick.

For module communication, interfaces plus events won over interfaces only because, with interfaces only, every source module must know every module that cares about it. Submitting an application would call notifications, tasks and interviews directly, so each of those features would reopen Applications' code: the exact collision this spec exists to prevent. Events only was rejected because many interactions are real questions ("what is this job's score?", "move this application"), which are awkward and hard to trace as events.

The outbox won over dispatching events in memory after save because a lost event is silent. A crash between the commit and the dispatch would drop a notification or a follow up task with no trace, and in a system that acts on your behalf, a missing "reply received" is worse than a slightly late one. The outbox reuses Postgres and Hangfire, which are already operated, so it adds a table and two jobs, not a new broker. It follows the same "add to the unit of work, the caller saves" shape as `IAuditService`, so it is familiar.

"Read any table, write only your own" keeps list, dashboard and search queries simple on a single database while keeping invariants (the application state machine, the resume lock) in exactly one module. Strict isolation even for reads would require dozens of query interfaces for a single user app with no scaling pressure.

`Result<T>` plus ProblemDetails won over exceptions plus a global handler because expected failures (not found, conflict, invalid input) then appear in method signatures, which is easy for an unattended builder and a reviewer to check. It generalizes `ResumeResult<T>`, the pattern the most recent feature already chose.

Per module settings and in app only notifications with a channel field both keep ownership inside one module and avoid a dependency on Gmail before #23 exists.

### Recommendations made while writing (not asked)

- **Module wiring in alphabetical marked blocks**, two lines per module. Runner up: reflection based module discovery (no `Program.cs` edits at all), rejected because it hides what is registered and makes startup failures harder to read.
- **Registration file location by need** (Workers when the module has jobs, else Infrastructure). Runner up: always Workers, rejected because it would make Workers depend on modules with no jobs for no reason.
- **Activity feed as a read view over `AuditLog`.** Runner up: a separate activity table fed by events, rejected because it duplicates audit data and can drift from it.
- **Cover letters under Profile**, next to resumes, since spec 0009 put resumes there and they share the versioning and lock rules. Runner up: an Applications owned table, rejected because a cover letter can exist before any application.
- **Interview lifecycle (#29) inside Applications** with its own `Interview` table. Runner up: a new module, rejected because the 13 module list in `AGENTS.md` is fixed and an interview is a stage of an application.
- **Tokens only inside Integrations**, exposed as `IGmailClient` and `IGoogleCalendarClient`. Runner up: Outreach and Calendar reading `OAuthConnection` directly, rejected because token refresh and encryption belong in one place.
- **Wave order**: job core loop first, as the founder chose. Within it, feed and notifications come before the jobs pages so the pages link to working destinations.
