# 0021. Jobs list and job detail

**Date**: 2026-09-29
**Status**: In Progress
**Updated**: 2026-10-04 (AC-9: fully responsive on any device, which depends on the responsive app shell); 2026-10-02 (aligned with spec 0019 as merged: extends `GET /internal/matches` instead of a second search endpoint, blockers, `/profile`)

## Summary

`/jobs` becomes the place you browse every discovered job: 25 per page, best match first, with filters for the data the catalog actually has (text, company, location, remote type, source, minimum score, posted within, salary, hide blocked jobs), all kept in the URL. You can dismiss a job you are not interested in (hidden by default, one click to undo). `/jobs/{id}` shows the job on the left (details, description, where it was seen) and the Agent's analysis on the right (the match breakdown from spec 0019). A Job sources drawer on `/jobs` lets you add a Greenhouse or Lever board and run ingestion without touching the API.

Decision history (context, options, rationale): [rationale.md](rationale.md). Verify steps: [verify.md](verify.md).

## Requirements

**User stories**:
- As the founder, I want to see the best matching jobs first and narrow them by what matters, so I spend my time on real options.
- As the founder, I want to hide jobs I have ruled out, so the list gets shorter as I go.
- As the founder, I want one job's full picture (details and why it matches) on one page, so I can decide to apply.
- As the founder, I want to add a company's board and fetch its jobs from the app, so I never need a script.

**Acceptance criteria**:
- **AC-1**: `/jobs` lists non deleted jobs from every source, 25 per page with page numbers, sorted as spec 0019 AC-9 orders matches (scored before unscored, unblocked before blocked, then score descending), then posted date descending (`PostedAt`, falling back to first seen), then id. A sort switch offers `Best match` and `Newest`.
- **AC-2**: Filters: text (`q`, case insensitive contains on title or company), company (exact, from a list of companies present), location (contains), remote type (from the values present), source, minimum score (0 to 100; unscored jobs excluded when set), posted within (1, 7 or 30 days), minimum salary (jobs whose top of range is at least the value; jobs with no salary excluded when set), and "Hide blocked jobs". Every filter, the sort and the page live in the URL query; changing a filter goes back to page 1; "Clear filters" resets them.
- **AC-3**: Each row shows title (links to `/jobs/{id}`), company, location, remote type, posted (relative, your time zone), source badges (one per link type), and a score badge with confidence (`82 · High`), or `Not scored` with a reason (`Complete your profile` link to `/profile` when the profile is incomplete, `Scoring…` when pending, `Not enough information` when the match has a null score), plus a blocker flag when present.
- **AC-4**: You can dismiss a job from its row or its detail page; dismissed jobs are hidden by default, a "Show dismissed" toggle shows them marked as dismissed, and "Undo" restores one. Dismissing twice is harmless.
- **AC-5**: `/jobs/{id}` has two columns. Left: title, company, location, remote type, salary range (when present), posted and first seen dates, the description, and the source links (source type, link to the original posting, first and last seen) from spec 0017. Right: the match panel from spec 0019 AC-10 (score, confidence, why it matches, missing requirements, unknown information, blockers, unverified items, the extraction failure notice, Rescore). A soft deleted job shows a "No longer listed" notice above its last known details; an unknown id shows a not found state.
- **AC-6**: A "Job sources" drawer on `/jobs` lists each source (type, board, company name, jobs linked, last run time and its created/updated counts, or `Never run`), lets you add a board (type `greenhouse` or `lever`, board token, optional company name) which queues an ingestion, and has "Run now" per source. Validation failures (unknown type, rejected board token, company name over 200 chars) show next to the field, from ProblemDetails.
- **AC-7**: When your profile is incomplete (no skills or no experience, spec 0019 AC-9), a banner on `/jobs` says scores need a complete profile and links to `/profile`.
- **AC-8**: Loading, empty (no jobs yet, with a button that opens the Job sources drawer; no jobs match the filters, with "Clear filters") and error states render; a failed dismiss shows an error and leaves the row as it was.
- **AC-9**: The content of `/jobs`, `/jobs/{id}` and the Job sources drawer works on any device. It is checked at 320, 390, 768, 1024, 1440 and 1920px, in dark and light themes, inside the content area (`.wp-app-shell__main`). Nothing scrolls sideways, no element's right edge passes the content area's right edge, and titles, company names and descriptions are never cut off with an ellipsis. The shell itself (sidebar, TopBar and its actions) belongs to the responsive app shell decision (see Follow-up); together they make the whole app responsive. The narrow rules:
  - **Below 960px**: the filters fold into a "Filters (n active)" button, closed by default, that opens them above the list on tap; the sort switch sits on the line above the list; the detail page is one column, details first, then the match panel.
  - **Below 600px**: each row stacks: title, then the score badge and blocker flag, then company, location, remote type, posted and source badges wrapping freely, then Dismiss or Undo. Pagination shows Previous, Next, the current page and its neighbours instead of every page number. The match panel's breakdown is one column, and Rescore and Dismiss run full width. The drawer is full width and full height, source rows stack with Run now below them, and the add form fields stack.
  - **Long text**: titles, company names, locations, salary, URLs and badges wrap (`overflow-wrap: anywhere`); a preformatted block in the description scrolls inside its own box.
  - **Touch**: every button, link and form control is at least 24 by 24px at every width (WCAG 2.2 target size; links inside running text are exempt, as 2.5.8 allows), main buttons (filter toggle, Dismiss, Rescore, Run now, the drawer's close and add) are at least 44px tall below 960px, and each checkbox is tappable across its whole label. Nothing needs hover: the exact posted and seen dates show as text on the detail page, so the list's hover tooltip is a convenience only.
  - **Out of scope**: color contrast (the design tokens own it).

## Decision

**Chosen option**: Option 1: spec 0019's `GET /internal/matches` extended with filters, sort and dismissal over stored columns, a two column detail page, and a Job sources drawer on `/jobs`.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Feature design

**Data model sketch** (migration `AddJobsList`):
- `JobDismissal` (Jobs), table `job_dismissals`: `ProfileId` uuid (req, FK → profiles), `JobId` uuid (req, FK → jobs, cascade on job hard delete), `DismissedAt` timestamptz (req). PK (`ProfileId`, `JobId`). Undo deletes the row (a hard delete: it is a preference, not history).
- Indexes: `jobs` (`PostedAt` desc) where not deleted, for `Newest`; `job_matches` (`ProfileId`, `HasBlocker`, `Score` desc) comes from spec 0019.
- Merges (spec 0017): dismissals move with the job like matches do; `JobRepository`'s merge moves `job_dismissals` rows to the kept job, skipping ones already there.

**State transitions**: per (profile, job): visible ⇄ dismissed.

**API surface** (Jobs module, `MapJobsEndpoints`; DTOs in `WorkPilot.Contracts/Jobs/`):

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/matches` (spec 0019, extended) | GET | `profileId` (req), `page` ≥ 1, `pageSize` 1..100 (default 25), plus new optional `q`, `company`, `location`, `remoteType`, `sourceId`, `minScore` 0..100, `postedWithinDays` ∈ {1,7,30}, `salaryMin` ≥ 0, `hideBlocked`, `includeDismissed`, `sort` ∈ {`score`,`newest`} (default `score`) | spec 0019's page (`items`, `total`, `page`, `pageSize`, `profileIncomplete`) with items widened to `JobListItemDto` | internal | 400 ProblemDetails with `errors[param]`, 404 unknown profile |
| `/internal/jobs/facets` | GET | `profileId` (req) | `{ companies[], remoteTypes[], sources[{id, type, name}] }` (distinct values present, non deleted jobs) | internal | none |
| `/internal/jobs/{id}` | GET | existing (spec 0017), plus `SalaryRangeMin`/`Max` and `IsDeleted` added to `JobDetailView` | detail with links | internal | 404 |
| `/internal/jobs/{id}/match` | GET | spec 0019 | `JobMatchDto` | internal | 404 |
| `/internal/jobs/{id}/dismissal` | PUT | body `{ profileId }` | 204 | internal | 404 unknown job |
| `/internal/jobs/{id}/dismissal` | DELETE | `profileId` (req) | 204 (also when not dismissed) | internal | 404 unknown job |
| `/internal/jobs/sources` | GET | none | `JobSourceSummaryDto[] { id, type, name, companyName?, jobCount, lastRunAt?, lastRunCreated?, lastRunUpdated? }` | internal | none |
| `/internal/jobs/sources/{id}/ingestions` | POST | `id` | 202 | internal | 404 unknown source |
| `/internal/jobs/ingestions` | POST | existing (spec 0008) | 202 | internal | 400 → migrated to ProblemDetails with `errors[source|boardToken|companyName]` |

`JobListItemDto { jobId, title, company, location?, remoteType?, postedAt?, firstSeenAt, sourceTypes[], score?, confidence? (High|Medium|Low), hasBlocker, rankedAt?, matchStatus (Scored|NotEnoughInfo|ProfileIncomplete|Pending), dismissed }`. The fields spec 0019 already returns keep their names.

Search runs as one query: `jobs` left join the profile's `job_matches`, anti join (or join when `includeDismissed`) `job_dismissals`, filters as SQL predicates, `COUNT` in the same round trip or a second query. `q` and `location` use `ILIKE` with escaped wildcards. Implemented by widening spec 0019's list query (Application/Modules/Jobs), not a second query.

Web (`AddJobsWeb()`, `WorkPilot.Web/Features/Jobs/JobsApiClient.cs`): pages `WorkPilot.Web/Components/Pages/Jobs/Jobs.razor` (`/jobs`, replaces the placeholder) and `JobDetail.razor` (`/jobs/{id:guid}`, replaces spec 0019's minimal one and reuses its `MatchPanel` component); `JobSourcesDrawer.razor`. Filters bind with `[SupplyParameterFromQuery]` and `NavigationManager.GetUriWithQueryParameters`.

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Search | profile | Web claim (`ProfileWebExtensions.ProfileId`) |
| Search | score, confidence, blocker | `job_matches` for (job, profile) |
| Search | match status | `Scored` when the row has a score; `NotEnoughInfo` when the row's score is null; with no row, `ProfileIncomplete` when `profileIncomplete` (spec 0019), else `Pending` |
| Search | posted | `jobs.PostedAt`, else the earliest `job_source_links.FirstSeenAt` (spec 0017, mirrored in `Provenance`) |
| Search | source badges | `job_source_links` join `job_sources.Type` |
| Filters | companies, remote types, sources | `/internal/jobs/facets` |
| Posted within | "now" | server UTC now (`TimeProvider`) |
| Relative times | local time | `BrowserTimeZone` (spec 0011) |
| Sources drawer | job count | count of `job_source_links` per source (non deleted jobs) |
| Sources drawer | last run and counts | latest `audit_logs` row with `Action = JobsIngested`, `TargetId = source id`, payload `created`/`updated` (read only, spec 0018 allows reads) |
| Run now | board and company | stored `JobSource` (`Type`, `Name`, `CompanyName`); the endpoint enqueues `IngestJobsJob` for the source id |

**Key invariants**:
- A dismissal exists at most once per (profile, job); dismissing never changes the shared job.
- Search never returns soft deleted jobs; the detail page may show one, marked "No longer listed".
- Only Jobs writes `job_dismissals`.

**Security model**: single user, internal Api (spec 0004). Scores and dismissals are always read and written for the `profileId` from the authenticated claim. The jobs catalog and sources are shared (spec 0017), so the sources endpoints take no profile. Board tokens are public identifiers, not secrets.

**Configuration required**: none.

**Critical test scenarios**:
- Happy path: with preferences set and two boards ingested, `/jobs` shows 25 rows best match first, and page 2 continues without repeats; verifies **AC-1**, **AC-3**.
- Filters: `minScore=70&remoteType=remote&postedWithinDays=7` returns only matching jobs, survives reload, and a filter change resets to page 1; verifies **AC-2**.
- Dismiss: dismiss hides the job, "Show dismissed" shows it, undo restores it, a second dismiss is a no op; verifies **AC-4**.
- Detail: a job seen on Greenhouse and Lever shows both links and the match panel with evidence; a soft deleted job shows "No longer listed"; verifies **AC-5**.
- Sources: adding a Lever board with a bad token shows the field error; "Run now" queues a run and the drawer shows the new last run after it finishes; verifies **AC-6**.
- Incomplete profile: banner shows, links to `/profile`, and unscored rows say `Complete your profile`; verifies **AC-7**.
- Validation: `pageSize=500` gives 400 ProblemDetails; verifies **AC-8**.
- Responsive: at 320, 390, 768, 1024, 1440 and 1920px, in both themes, `.wp-app-shell__main` has `scrollWidth` equal to `clientWidth` and no descendant's right edge passes its right edge, on `/jobs`, `/jobs/{id}` and with the drawer open; verifies **AC-9**.
- Merge: dismissing a job that later merges keeps it dismissed on the kept job; verifies **Key invariants**.

## Build plan

Tracer Bullet: a thin list end to end first, then filters, detail, dismissal and sources.

1. Thin thread: widen spec 0019's list query and `GET /internal/matches` with both sorts and the wider items, `AddJobsWeb` + `JobsApiClient`, `/jobs` list rows with score badge and match status, incomplete profile banner (replaces spec 0019's minimal list). Satisfies **AC-1**, **AC-3**, **AC-7**.
2. Filters: facets endpoint, every filter predicate with validation, URL bound filter bar, clear filters. Satisfies **AC-2**.
3. Detail: extend `JobDetailView` with salary and deleted flag, two column `/jobs/{id}` reusing `MatchPanel`. Satisfies **AC-5**.
4. Dismissal: migration `AddJobsList` (`job_dismissals`, posted index), PUT/DELETE endpoints, merge moves dismissals, row and detail actions, show dismissed toggle. Satisfies **AC-4**.
5. Sources drawer: sources summary endpoint, run now endpoint, `POST /internal/jobs/ingestions` errors as ProblemDetails, drawer UI. Satisfies **AC-6**.
6. States and tests: loading, empty, error; Api integration tests (real Postgres) for search, filters, paging, dismissal, merge, sources; bUnit tests for the filter bar and states. Satisfies **AC-1** to **AC-8**.
7. Responsive: the collapsible filter toggle and sort placement below 960px, the row order, windowed pagination, one column match breakdown and full screen drawer below 600px, `overflow-wrap` on long text, and the 24px and 44px target sizes, all in `jobs.css` and the two pages; bUnit test for the filter toggle's "n active" count. Satisfies **AC-9**.

## Consequences

**Positive**:
- You can run the whole discover, score, browse loop from the app.
- The detail layout is final, so #16 only adds its Prepare button.

**Negative / tradeoffs**:
- Experience level, job type and visa filters from the scope are not built; the match breakdown covers them on the detail page.
- `ILIKE` contains searches scan; fine at thousands of jobs, needs a trigram index if the catalog grows large.
- The sources drawer on `/jobs` duplicates a job the Integrations hub (#31) may take over later.
- AC-9 covers only the content area, so this feature can finish before the shell; the app is fully responsive only once the responsive app shell ships too.

**Neutral**:
- `POST /internal/jobs/ingestions` moves its bare 400s onto ProblemDetails (spec 0018 convergence for a touched endpoint).
- `GET /internal/jobs?jobSourceId=` (spec 0008) stays as it is.

## Follow-up

- [ ] Responsive app shell (scope feature #35, enrolled from this spec; "Mobile layout" left Deferred): decide how navigation works on a phone and tablet (`/architect responsive app shell`). It owns the sidebar, the TopBar and its actions, and the full page check (`document.documentElement.scrollWidth` equals the viewport width) on every page.
- [ ] Experience level, job type and visa filters read from `job_requirements.Requirements` (spec 0019 extracts `MinYears`, `JobType`, `Sponsorship`).
- [ ] Save/shortlist and the Prepare application button with #16.
- [ ] Decisions made without the engineer (please review): dismissals move on merge; "Run now" enqueues by source id (new endpoint) instead of resending the board token; last run data read from the audit log.
