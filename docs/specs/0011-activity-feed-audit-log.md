# 0011. Activity feed over the audit log

**Date**: 2026-09-29
**Status**: Proposed

## Summary

`/activity` becomes a timeline of everything the Agent and you did, read straight from the existing audit log (`AuditLog`), newest first, with filter chips for Agent, Jobs, Email, Calendar, System and Errors. Each entry gets a stored category (a new `Category` column set when the audit row is written, existing rows backfilled), so filtering stays fast and correct as new actions appear. Clicking an entry expands its evidence (the audit payload as readable key and value pairs), and entries about a job, a source, an approval or an agent run link to that page. No new table: the feed is a read model over `AuditLog`, owned by the Audit module.

## Context

Every meaningful action already writes an `AuditLog` row through `IAuditService.Record` in the same transaction as the change (spec 0002, spec 0018 exception list): ingestion runs, merges and splits, agent tool calls, approval requests, gate refusals, decisions, planning failures. Nothing shows them. You cannot see what the Agent did overnight, why a job merged, or which run failed, without querying Postgres.

The scope asks for a timeline filterable by domain (Agent, Jobs, Email, Calendar, System, Errors). The audit row holds `Actor`, `Action`, `TargetType`, `TargetId`, `Payload` (JSON) and `OccurredAt`, but no domain label, and actions are free strings chosen by each module (`JobsIngested`, `ApprovalApproved`, a tool name such as `list_my_profile`). New modules in later waves (outreach, calendar, tasks) add more. The table has no index on `OccurredAt`, and it grows with every ingestion run.

The product has one user; `AuditLog` has no profile column. The Api is internal only (spec 0004). The dashboard (#13) needs the latest few entries, so the read path must be reusable, not page code.

## Requirements

**User stories**:
- As the founder, I want one timeline of what the Agent and I did, so I can see at a glance what happened while I was away.
- As the founder, I want to filter it to one domain (for example Errors), so I find a failure without scrolling.
- As the founder, I want to open an entry and see its evidence and jump to what it touched, so the feed explains itself.

**Acceptance criteria**:
- **AC-1**: `/activity` (nav item under Agent, first in that section) lists audit entries newest first (`OccurredAt` desc, then `Id` desc), 50 at a time with a "Load more" button that fetches the next 50 by cursor. Each entry shows when (relative, with the exact time in your browser's time zone on hover), who (`You` when the actor is your profile id, `Agent` for `Agent`, otherwise `System`), a readable summary of what happened, the category, and the target.
- **AC-2**: Filter chips `All`, `Agent`, `Jobs`, `Email`, `Calendar`, `System`, `Errors` narrow the list to one category; the choice is kept in the URL (`/activity?category=errors`) so a filtered view can be linked and survives reload.
- **AC-3**: Every audit row has a `Category`. New rows get it from `AuditCategories.For(actor, action, targetType)` when `IAuditService.Record` runs; every existing row is backfilled by the migration with the same rules.
- **AC-4**: Clicking an entry expands its evidence: the payload's top level properties as key and value pairs (nested objects and arrays shown as indented JSON), a payload that is not valid JSON shown as plain text, and payloads over 10 KB shown truncated with a "Show all" toggle.
- **AC-5**: An entry whose target has a page links to it: `Job` → `/jobs/{id}`, `JobSource` → `/jobs?source={id}`, `AgentStep` → `/approvals`, `AgentRun` → `/agent/runs`. Other target types render as plain text.
- **AC-6**: Known actions show a readable summary (`JobsIngested` → "Ingested jobs from a source: 12 new, 3 updated", `JobsMerged` → "Merged duplicate jobs", `JobLinkSplit` → "Split a job link", `ApprovalRequested` → "Asked for your approval", `ApprovalApproved`/`ApprovalRejected` → "You approved/rejected a step", `ApprovalGateRefused` → "Blocked a step at the approval gate", `PlanningFailed` → "Agent planning failed"); any other action shows its raw name, so a new action is never hidden.
- **AC-7**: Loading, empty ("Nothing has happened yet" / "Nothing in this category yet") and error (message from ProblemDetails with a Retry button) states each render; a failed "Load more" keeps the entries already shown.
- **AC-8**: `IActivityQuery.GetPageAsync` is the one read path; the dashboard (#13) reuses it for its latest 8 entries.

## Options considered

### Option 1: Stored category column (chosen)

Add `AuditLog.Category`, set at write time from a Domain rule, backfilled once, indexed with `OccurredAt`.

**Pros**: filtering is one indexed equality; a new action lands in a sensible category through the fallback rules, not nowhere; the rule lives in one Domain class that unit tests pin.
**Cons**: a migration with a backfill over a table that only grows; changing the rules later needs another backfill for old rows.

### Option 2: Map categories in the query

No schema change; the feed query turns a category into a list of target types and actions.

**Pros**: no migration, no backfill, rules change instantly for old rows too.
**Cons**: an action missing from the map silently drops out of every filter; the `IN` list grows with each module and cannot use one clean index.

## Decision

**Chosen option**: Option 1: a stored `Category` on `AuditLog`, set by `IAuditService` from a Domain rule, with a cursor paged read model owned by the Audit module.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

The scope's done bar is that every meaningful action shows up. Option 2 fails that bar quietly: a module that adds an action and forgets the map makes it vanish from every filter. Option 1's fallback rules (below) always assign something, and the actor and target type are reliable signals that exist on every row. The backfill is a single `UPDATE` with the same `CASE` the Domain rule encodes, on a table that is still small, so its cost is paid once now rather than on every query. Cursor paging (keyset on `OccurredAt`, `Id`) keeps "Load more" correct while ingestion keeps inserting rows at the top, which offset paging would not.

## Feature design

**Data model sketch**:
- `AuditLog` (Audit): add `Category` varchar(20), required, default `System`. Values: `Agent`, `Jobs`, `Email`, `Calendar`, `System`, `Errors` (Domain enum `ActivityCategory`, stored as string).
- Indexes: (`OccurredAt` desc, `Id` desc) for the unfiltered feed; (`Category`, `OccurredAt` desc, `Id` desc) for a filtered one. Both partial on `IsDeleted = false`.
- Migration `AddActivityFeed`: column, indexes, and the backfill `UPDATE` fenced `// HAND WRITTEN (spec 0011): keep when regenerating`.

**Category rule** (`WorkPilot.Domain/Modules/Audit/AuditCategories.cs`, first match wins; the backfill SQL mirrors it):
1. `Errors`: action is `PlanningFailed` or `ApprovalGateRefused`, or ends with `Failed`.
2. By target type: `Job`, `JobSource`, `JobMatch` → `Jobs`; `AgentRun`, `AgentStep`, `Approval` → `Agent`; `OutreachMessage`, `EmailThread`, `OutreachContact` → `Email`; `CalendarEvent` → `Calendar`.
3. Actor is `Agent` → `Agent`.
4. Otherwise `System`.

Later modules extend rule 2 with their target types in the same class (and bump nothing: old rows keep their stored category).

**State transitions**: none (audit rows are append only).

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/audit/activity` | GET | `category`: string (opt, case insensitive), `before`: timestamptz (opt), `beforeId`: guid (opt, required with `before`), `take`: int (opt, 1 to 100, default 50) | `ActivityPageDto { items: ActivityEntryDto[], nextBefore?, nextBeforeId? }`; `ActivityEntryDto { id, occurredAt, actor, action, category, targetType, targetId, payload? }` | internal (spec 0004) | 400 ProblemDetails for an unknown category or `before` without `beforeId` |

DTOs live in `WorkPilot.Contracts/Audit/`. `IActivityQuery` (Application/Modules/Audit) is implemented in Infrastructure/Modules/Audit and registered by `AddAuditModule`; the endpoint is mapped by `MapAuditEndpoints` in the Api module block. Web: `AddAuditWeb()` registers `ActivityApiClient`; page `WorkPilot.Web/Components/Pages/Audit/Activity.razor`; `NavRoutes` gains `Activity` first under Agent.

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| List | entries, order, cursor | `audit_logs` columns; cursor = last item's `OccurredAt` and `Id` |
| Category | category | `AuditLog.Category` (write time rule or backfill) |
| "Who" | `You` / `Agent` / `System` | `Actor` compared with the viewer's profile id from the Web claim (`ProfileWebExtensions.ProfileId`) |
| "When" | local time | `OccurredAt` (UTC) converted with the browser's IANA time zone, read once per circuit by a scoped `BrowserTimeZone` service through JS (`Intl.DateTimeFormat().resolvedOptions().timeZone`); UTC when unavailable |
| Summary | readable text | Web side `ActivitySummaries` map by action, reading counts from the payload for `JobsIngested`; raw action otherwise |
| Target link | route | Web side map by `TargetType` (AC-5) |
| Evidence | key/value pairs | `Payload` parsed as JSON in Web |

**Key invariants**:
- Audit rows stay append only: the feed never updates or deletes them; only the migration's one time backfill writes `Category`.
- Every row has exactly one category.
- Pages never overlap or skip a row, even while new rows arrive (keyset cursor).

**Security model**: single user, internal Api (spec 0004); every signed in page is behind the auth gate (spec 0004), so the feed shows every audit row. `AuditLog` has no profile column today; when a second user arrives, the feed must gain a profile filter before release (Follow-up). Payloads can hold job data and approval evidence, never secrets (spec 0005 rule that tools do not log credentials still holds).

**Configuration required**: none.

**Critical test scenarios**:
- Happy path: after an ingestion and an approval decision, `/activity` shows both newest first with readable summaries and `You` on the decision; verifies **AC-1**, **AC-6**.
- Filter: `?category=errors` shows only a `PlanningFailed` row and survives reload; verifies **AC-2**.
- Category rule: unit tests cover each rule step, and the migration backfill gives the same category as the Domain rule for a fixture of existing actions; verifies **AC-3**.
- Paging under inserts: insert rows between two "Load more" calls and the second page neither repeats nor skips; verifies **AC-1**, **Key invariants**.
- Bad payload: a non JSON payload renders as text, a 20 KB one is truncated; verifies **AC-4**.
- Bad input: `category=nope` gives 400 ProblemDetails; verifies **AC-7**.

## Build plan

Tracer Bullet: a thin feed end to end first, then the filters and evidence.

1. Thin thread: `ActivityCategory` + `AuditCategories.For` in Domain; `AuditService` sets `Category`; migration `AddActivityFeed` (column, indexes, hand written backfill); `IActivityQuery` + `GET /internal/audit/activity` with cursor paging; `AddAuditWeb`, `/activity` page listing entries with Load more, nav item. Satisfies **AC-1**, **AC-3**, **AC-8**.
2. Category chips bound to the URL, and the 400 for bad input. Satisfies **AC-2**.
3. Readable summaries, `You`/`Agent`/`System`, `BrowserTimeZone`, evidence expander, target links. Satisfies **AC-4**, **AC-5**, **AC-6**.
4. Loading, empty and error states, including a failed Load more. Satisfies **AC-7**.
5. Tests: Domain unit tests for the category rule; Api integration tests (real Postgres) for paging, filter, backfill parity and errors; bUnit tests for the expander and summaries. Satisfies **AC-1** to **AC-8**.

## Consequences

**Positive**:
- Every existing and future audited action appears with no extra work in the module that writes it.
- The dashboard and later the command palette reuse `IActivityQuery`.

**Negative / tradeoffs**:
- The category rule is duplicated once in SQL for the backfill; the parity test is what keeps them honest.
- Summaries live in a Web map; a new action shows its raw name until someone adds a summary line.
- No profile column means the feed is not ready for more than one user.

**Neutral**:
- `IAuditService.Record` keeps its signature; only its implementation changes.
- `BrowserTimeZone` is a new scoped Web service that notifications (#20) and the dashboard (#13) reuse.

## Follow-up

- [ ] Add `ProfileId` to `AuditLog` (and a filter) before a second user exists.
- [ ] Full text search over the feed with the command palette (#32).
- [ ] Decisions made without the engineer (please review): the category rule order above; `AgentRun` links to the runs list until a run detail page exists; payload truncation at 10 KB.
