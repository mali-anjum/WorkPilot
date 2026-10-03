# 0020. Notifications

**Date**: 2026-09-29
**Status**: In Progress

## Summary

The product tells you when something needs you. Three events now create a notification: an approval was requested (Action Required), an agent run failed (Error), and jobs crossed your strong match threshold (Info). Strong matches are grouped into one "N new strong matches today" notification per day instead of one per job. A bell in the top bar shows your unread count and refreshes every 15 seconds; clicking it opens the latest 10, and `/notifications` holds the full list with mark read, mark all read and dismiss. Read notifications are cleaned up after 90 days. Notifications are written only by event handlers (spec 0018 outbox), so no module ever writes the table directly, and later features (application outcomes, replies, interviews) add one handler each.

The decision record (context, options considered, rationale) is in [rationale.md](rationale.md). Verify steps are in [verify.md](verify.md).

## Requirements

**User stories**:
- As the founder, I want to be told as soon as an approval waits for me, so the Agent is not blocked for hours.
- As the founder, I want to know when an agent run fails, with the reason, so I can fix or rerun it.
- As the founder, I want one daily note about strong new matches, not a flood, so good jobs reach me without noise.

**Acceptance criteria**:
- **AC-1**: When `ApprovalRequested` is published, the run's profile gets a notification with priority `ActionRequired`, type `approvals.requested`, title "Approval needed: {tool name}", body the run's goal (up to 300 chars), link `/approvals`, within about a minute (usually seconds).
- **AC-2**: When `AgentRunFailed` is published, the run's profile gets priority `Error`, type `agent.run-failed`, title "Agent run failed", body the readable reason (from `AgentRunFailureReasons`: for example `provider_error` → "The AI provider failed") and the goal, link `/agent/runs`.
- **AC-3**: When `JobMatched` is published, the profile's unread digest for the current UTC day (`GroupKey` `jobs.strong-matches:{yyyy-MM-dd}`) is created or updated: title "{N} new strong match(es) today", payload holds the count and the top 3 jobs by score (job id, title, company, score), link `/jobs?sort=score&minScore={threshold}`, priority `Info`, type `jobs.strong-matches`. A job already in today's digest is not counted twice. If today's digest was already read, a new unread one starts.
- **AC-4**: The top bar shows a bell with your unread count (hidden at 0, `99+` above 99). It refreshes every 15 seconds while the page is open and on every navigation, and stops when the circuit closes. Clicking it opens a drawer with the latest 10 notifications (unread highlighted); clicking one marks it read and navigates to its link; "Mark all read" and "See all" (`/notifications`) are in the drawer.
- **AC-5**: `/notifications` lists your notifications newest first, 25 per page with page numbers, with an "Unread only" toggle (both in the URL). Each row shows priority, title, body, relative time (in your browser's time zone) and actions: mark read/unread, dismiss. "Mark all read" marks every unread one.
- **AC-6**: Handlers are safe to run again: replaying the same event creates no duplicate notification and does not double count a digest (structural through `outbox_deliveries`, plus the job id check in the digest).
- **AC-7**: A daily recurring job `notifications.cleanup` soft deletes read notifications whose `ReadAt` is older than 90 days; unread ones are never removed.
- **AC-8**: Every endpoint is scoped to `profileId`: acting on a notification of another profile returns 404 ProblemDetails and changes nothing.
- **AC-9**: Loading, empty ("You're all caught up") and error states render in both the drawer and the page; a failed bell refresh keeps the last count and tries again on the next tick.

## Decision

**Chosen option**: Option 1: in app notifications written by outbox event handlers, a polling bell (15 seconds), a daily digest for matches, and a 90 day cleanup of read ones.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Feature design

**Data model sketch** (`Notification`, Notifications module, table `notifications`, migration `AddNotifications`):

| Field | Type | Notes |
|---|---|---|
| `Id`, `IsDeleted`, `DeletedAt` | existing | soft delete (base class) |
| `ProfileId` | uuid, required | existing; FK → profiles |
| `Type` | varchar(100), required | existing; e.g. `approvals.requested` |
| `Priority` | varchar(20), required | new; `Info`, `Success`, `Warning`, `ActionRequired`, `Error` (enum as string) |
| `Title` | varchar(200), required | new |
| `Body` | varchar(1000), null | new |
| `Link` | varchar(500), null | new; an app relative path starting with `/` |
| `GroupKey` | varchar(200), null | new; digest key |
| `Payload` | jsonb, null | existing (switch to jsonb if it is text) |
| `ReadAt` | timestamptz, null | existing |
| `CreatedAt` | timestamptz, required | new |

Indexes: (`ProfileId`, `CreatedAt` desc) where not deleted; (`ProfileId`) where `ReadAt IS NULL` and not deleted (unread count); unique (`ProfileId`, `GroupKey`) where `GroupKey IS NOT NULL AND ReadAt IS NULL AND NOT IsDeleted`.

Domain (`WorkPilot.Domain/Modules/Notifications/`): `Notification.Create(...)`, `MarkRead(now)`, `MarkUnread()`, `Dismiss(now)`, and `StrongMatchDigest` (adds a job to the payload unless present, keeps the top 3 by score, recomputes the title). Title and body are trimmed to their limits.

**Handlers** (`WorkPilot.Application/Modules/Notifications/Handlers/`, registered with `AddEventHandler` in `AddNotificationsModule`):

| Handler key | Event | Profile resolved from |
|---|---|---|
| `notifications.on-approval-requested` | `ApprovalRequested` | `Approval` → its `AgentStep` → `AgentRun.ProfileId` (read only); tool name and goal from the step and run |
| `notifications.on-agent-run-failed` | `AgentRunFailed` | `AgentRun.ProfileId`; goal from the run |
| `notifications.on-job-matched` | `JobMatched` | `JobMatched.ProfileId`; title/company from `jobs`, threshold from `profiles.StrongMatchThreshold` (spec 0019) |

The digest handler loads today's unread digest with `SELECT … FOR UPDATE` (row lock inside the handler transaction) and updates it, or inserts one. A concurrent insert that hits the partial unique index throws, the handler's Hangfire retry then finds the row and updates it. A missing approval, run or job (deleted since) makes the handler return without writing; it does not fail.

**Recurring job**: `NotificationsCleanupJob` registered with `AddRecurringJob` as `notifications.cleanup`, daily at 03:00 UTC, one set based `UPDATE`.

**State transitions**: unread (`ReadAt` null) ⇄ read; any → dismissed (soft deleted, final). A digest stops receiving jobs once read or dismissed.

**API surface** (group `/internal/notifications`, `MapNotificationsEndpoints`; DTOs in `WorkPilot.Contracts/Notifications/`):

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/notifications` | GET | `profileId` (req), `unreadOnly`: bool (opt), `page`: int ≥ 1 (opt), `pageSize`: 1 to 50 (opt, default 25) | `{ items: NotificationDto[], total, page, pageSize }` | internal | 400 bad paging |
| `/internal/notifications/unread-count` | GET | `profileId` (req) | `{ count }` | internal | none |
| `/internal/notifications/{id}/read` | POST | body `{ profileId, read: bool }` | `NotificationDto` | internal | 404 not yours or missing |
| `/internal/notifications/read-all` | POST | body `{ profileId }` | `{ updated }` | internal | none |
| `/internal/notifications/{id}` | DELETE | `profileId` (req) | 204 | internal | 404 not yours or missing |

`NotificationDto { id, type, priority, title, body?, link?, payload?, createdAt, readAt? }`.

Web: `AddNotificationsWeb()` registers `NotificationsApiClient`; `NotificationBell.razor` (with drawer) in `WorkPilot.Web/Features/Notifications/`; `AppShell` and `TopBar` (in `WorkPilot.Web.Client/Shared`) gain a `TopBarActions` `RenderFragment` parameter, and `MainLayout` passes `<NotificationBell />` into it. The bell uses a `PeriodicTimer` (15 s), refreshes on `NavigationManager.LocationChanged`, and disposes both (catching `JSDisconnectedException` if it touches JS, per AGENTS.md). Page: `WorkPilot.Web/Components/Pages/Notifications/Notifications.razor` at `/notifications` (no nav item, spec 0018 route map).

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Approval notification | profile, tool name, goal | `approvals` → `agent_steps` → `agent_runs` (read only) |
| Run failed notification | profile, reason, goal | event `Reason` mapped by a Domain `AgentRunFailureReasons.Describe`; `agent_runs.Goal`, `ProfileId` |
| Digest | day | the event handling time in UTC (`TimeProvider`), `yyyy-MM-dd` |
| Digest | jobs listed, score | event `JobId`, `Score`; `jobs.Title`, `jobs.Company` |
| Digest link | threshold | `profiles.StrongMatchThreshold` (spec 0019, default 70) |
| Bell and list | profile | Web claim (`ProfileWebExtensions.ProfileId`), never user input |
| Relative time | local time | `BrowserTimeZone` (spec 0011) |
| Cleanup | cutoff | now (UTC) minus 90 days, `Notifications:ReadRetentionDays` option (default 90, validated 1 to 3650 at start) |

**Key invariants**:
- At most one unread, non dismissed digest per (profile, day) (partial unique index).
- A job id appears at most once in a digest's payload.
- Only the Notifications module writes `notifications`; handlers never send, submit or call a provider.
- `Link` is always an app relative path (starts with `/`, never `//` or a scheme), so a notification cannot navigate off site.

**Security model**: single user, internal Api (spec 0004). Every query and mutation filters by `profileId`, which the Web takes from the authenticated claim. A notification id of another profile behaves exactly like a missing one (404). Payloads hold ids, titles and scores, no secrets.

**Configuration required**:
- `Notifications__ReadRetentionDays` (optional, default 90): how long read notifications are kept.

**Critical test scenarios**:
- Happy path: an approval required step suspends, `ApprovalRequested` dispatches, and the bell count goes to 1 within one poll; clicking marks it read and opens `/approvals`; verifies **AC-1**, **AC-4**.
- Run failure: a planner provider error creates an Error notification with the readable reason; verifies **AC-2**.
- Digest: five `JobMatched` events for the same day produce one notification with count 5 and the top 3; replaying one event keeps count 5; verifies **AC-3**, **AC-6**.
- Concurrency: two digest handlers for the same profile and day at once end with one row counting both jobs; verifies **AC-3**, **Key invariants**.
- Scoping: marking another profile's notification returns 404 and changes nothing; verifies **AC-8**.
- Cleanup: a read notification 91 days old is removed, an unread 200 day old one is kept; verifies **AC-7**.
- Failure: the Api is down during a bell tick, the last count stays and the next tick recovers; verifies **AC-9**.

## Build plan

Tracer Bullet: one event all the way to the bell first, then the rest.

1. [x] Thin thread: migration `AddNotifications` (new columns and indexes); `Notification` domain methods; `AddNotificationsModule` with the `notifications.on-approval-requested` handler; `GET /internal/notifications/unread-count`; `TopBarActions` slot and `NotificationBell` polling the count. Satisfies **AC-1**, **AC-4** (count), **AC-6**.
2. [x] Drawer and list: `GET /internal/notifications`, read, read all, dismiss endpoints on `Result<T>`; drawer with the latest 10; `/notifications` page with paging and the unread toggle. Satisfies **AC-4**, **AC-5**, **AC-8**.
3. [x] More sources: `notifications.on-agent-run-failed` with readable reasons; `notifications.on-job-matched` digest with row lock and dedupe. Satisfies **AC-2**, **AC-3**, **AC-6**.
4. [x] Cleanup recurring job and its option. Satisfies **AC-7**.
5. [x] States and tests: loading, empty and error states; Domain unit tests (digest, link validation, reason text); Api integration tests (real Postgres) for handlers through `HandleEventJob`, replay, concurrency, scoping and cleanup; bUnit tests for the bell. Satisfies **AC-1** to **AC-9**.

## Consequences

**Positive**:
- Approvals and failures reach you from any page within seconds.
- Future modules add a notification by writing one handler; nothing in #20 changes.

**Negative / tradeoffs**:
- Polling costs one indexed count query per open tab every 15 seconds.
- Delivery can lag up to a minute when the outbox falls back to its sweep.
- The digest day is the UTC day, so it can roll over in your evening or morning rather than at your midnight.
- Application outcome notifications (the second half of the scope's done bar) arrive only with #16 to #18, each as one handler.

**Neutral**:
- `AppShell`/`TopBar` gain a generic `TopBarActions` slot, reusable by later features.
- No email or push delivery; revisit after Gmail (#23).

## Follow-up

- [ ] Add handlers for `ApplicationStatusChanged` and `ApplicationSubmissionFailed` when #16 to #18 raise them (the scope's application outcome part of the done bar).
- [ ] Per type notification preferences (mute a type) with Settings (#30).
- [ ] Decisions made without the engineer (please review): `ApprovalDecided` is not notified (you just made the decision); digest day in UTC; cleanup at 03:00 UTC; notifications for a deleted target are skipped silently.
