# 0021. Jobs list and job detail

**Date**: 2026-09-29
**Status**: Proposed

## Summary

`/jobs` becomes the place you browse every discovered job: 25 per page, best match first, with filters for the data the catalog actually has (text, company, location, remote type, source, minimum score, posted within, salary, hide dealbreakers), all kept in the URL. You can dismiss a job you are not interested in (hidden by default, one click to undo). `/jobs/{id}` shows the job on the left (details, description, where it was seen) and the Agent's analysis on the right (the match breakdown from spec 0019). A Job sources drawer on `/jobs` lets you add a Greenhouse or Lever board and run ingestion without touching the API.

## Context

Spec 0008 and spec 0017 fill a shared job catalog, and spec 0019 scores each job per profile. The current `/jobs` page is an empty placeholder, and the only list endpoint (`GET /internal/jobs?jobSourceId=`) lists one source at a time with no score, no filter and no paging beyond `take`. Adding a board is possible only with a hand written `POST /internal/jobs/ingestions`, so in normal use the catalog would stay empty.

The scope lists eleven filters. Only some map to stored data: title, company, location, remote type, salary (often empty), source, match score and posted date exist; experience level, job type and visa sponsorship are not columns (seniority and visa appear only as scoring evidence in spec 0019). Filtering on text that is not stored would mean scanning descriptions per request.

Jobs are shared (spec 0017), scores and dismissals are per profile. The catalog can reach thousands of rows from a handful of boards, so the list must page and sort in the database. Every page is InteractiveServer (spec 0016) and reads the Api through a typed client, with failures as ProblemDetails read by `ApiResultReader` (spec 0018).

## Requirements

**User stories**:
- As the founder, I want to see the best matching jobs first and narrow them by what matters, so I spend my time on real options.
- As the founder, I want to hide jobs I have ruled out, so the list gets shorter as I go.
- As the founder, I want one job's full picture (details and why it matches) on one page, so I can decide to apply.
- As the founder, I want to add a company's board and fetch its jobs from the app, so I never need a script.

**Acceptance criteria**:
- **AC-1**: `/jobs` lists non deleted jobs from every source, 25 per page with page numbers, sorted by your match score descending (unscored jobs after scored ones), then posted date descending (`PostedAt`, falling back to first seen), then id. A sort switch offers `Best match` and `Newest`.
- **AC-2**: Filters: text (`q`, case insensitive contains on title or company), company (exact, from a list of companies present), location (contains), remote type (from the values present), source, minimum score (0 to 100; unscored jobs excluded when set), posted within (1, 7 or 30 days), minimum salary (jobs whose top of range is at least the value; jobs with no salary excluded when set), and "Hide dealbreakers". Every filter, the sort and the page live in the URL query; changing a filter goes back to page 1; "Clear filters" resets them.
- **AC-3**: Each row shows title (links to `/jobs/{id}`), company, location, remote type, posted (relative, your time zone), source badges (one per link type), and a score badge with confidence (`82 · high`), or `Not scored` with a reason (`Set preferences` link when preferences are missing, `Scoring…` when pending), plus a dealbreaker flag when present.
- **AC-4**: You can dismiss a job from its row or its detail page; dismissed jobs are hidden by default, a "Show dismissed" toggle shows them marked as dismissed, and "Undo" restores one. Dismissing twice is harmless.
- **AC-5**: `/jobs/{id}` has two columns. Left: title, company, location, remote type, salary range (when present), posted and first seen dates, the description, and the source links (source type, link to the original posting, first and last seen) from spec 0017. Right: the match panel from spec 0019 (score, confidence, dealbreakers first, dimensions with evidence quotes, missing requirements, unknown information). A soft deleted job shows a "No longer listed" notice above its last known details; an unknown id shows a not found state.
- **AC-6**: A "Job sources" drawer on `/jobs` lists each source (type, board, company name, jobs linked, last run time and its created/updated counts, or `Never run`), lets you add a board (type `greenhouse` or `lever`, board token, optional company name) which queues an ingestion, and has "Run now" per source. Validation failures (unknown type, rejected board token, company name over 200 chars) show next to the field, from ProblemDetails.
- **AC-7**: When your matching preferences are not set, a banner on `/jobs` says scores need preferences and links to `/settings/profile.matching`.
- **AC-8**: Loading, empty (no jobs yet, with a button that opens the Job sources drawer; no jobs match the filters, with "Clear filters") and error states render; a failed dismiss shows an error and leaves the row as it was.

## Options considered

### Option 1: Filter on stored columns only (chosen)

Ship the filters the data supports; add the rest when a column exists.

**Pros**: every filter is a plain indexed or cheap predicate; no change to ingestion; nothing guesses.
**Cons**: experience level, job type and visa filters from the scope wait for later.

### Option 2: Derive more columns during ingestion

Extract job type, seniority and visa wording into `Job` columns at ingestion so every scoped filter works now.

**Pros**: all eleven scoped filters on day one.
**Cons**: touches ingestion and dedup (spec 0017 hashes and snapshots), duplicates spec 0019's phrase rules in a second place, and makes this feature much larger.

## Decision

**Chosen option**: Option 1: a paged, filtered search endpoint over stored columns joined to your match and dismissal, a two column detail page, and a Job sources drawer on `/jobs`.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

The data only supports some of the scoped filters, and a filter that silently matches nothing (job type on a catalog that never stores it) is worse than no filter. Keeping the list on stored columns keeps it fast and honest, and the match score already carries seniority and visa as evidence on the detail page. A new `GET /internal/jobs/search` keeps spec 0008's list contract intact while adding the per profile join. Offset paging with page numbers fits a single user browsing a few thousand rows and makes every view linkable; keyset paging would only pay off at far larger volumes. The sources drawer is small and reuses the existing ingestion use case, and without it the list would be empty in normal use.

## Feature design

**Data model sketch** (migration `AddJobsList`):
- `JobDismissal` (Jobs), table `job_dismissals`: `ProfileId` uuid (req, FK → profiles), `JobId` uuid (req, FK → jobs, cascade on job hard delete), `DismissedAt` timestamptz (req). PK (`ProfileId`, `JobId`). Undo deletes the row (a hard delete: it is a preference, not history).
- Indexes: `jobs` (`PostedAt` desc) where not deleted, for `Newest`; `job_matches` (`ProfileId`, `Score` desc) already exists (spec 0019).
- Merges (spec 0017): dismissals move with the job like matches do; `JobRepository`'s merge moves `job_dismissals` rows to the kept job, skipping ones already there.

**State transitions**: per (profile, job): visible ⇄ dismissed.

**API surface** (Jobs module, `MapJobsEndpoints`; DTOs in `WorkPilot.Contracts/Jobs/`):

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/jobs/search` | GET | `profileId` (req), `q`, `company`, `location`, `remoteType`, `sourceId`, `minScore` 0..100, `postedWithinDays` ∈ {1,7,30}, `salaryMin` ≥ 0, `hideDealbreakers`, `includeDismissed`, `sort` ∈ {`score`,`newest`}, `page` ≥ 1, `pageSize` 1..50 (default 25) | `JobSearchPageDto { items: JobListItemDto[], total, page, pageSize, preferencesSet }` | internal | 400 ProblemDetails with `errors[param]` |
| `/internal/jobs/facets` | GET | `profileId` (req) | `{ companies[], remoteTypes[], sources[{id, type, name}] }` (distinct values present, non deleted jobs) | internal | none |
| `/internal/jobs/{id}` | GET | existing (spec 0017), plus `SalaryRangeMin`/`Max` and `IsDeleted` added to `JobDetailView` | detail with links | internal | 404 |
| `/internal/jobs/{id}/match` | GET | spec 0019 | `JobMatchDto` | internal | 404 |
| `/internal/jobs/{id}/dismissal` | PUT | body `{ profileId }` | 204 | internal | 404 unknown job |
| `/internal/jobs/{id}/dismissal` | DELETE | `profileId` (req) | 204 (also when not dismissed) | internal | 404 unknown job |
| `/internal/jobs/sources` | GET | none | `JobSourceSummaryDto[] { id, type, name, companyName?, jobCount, lastRunAt?, lastRunCreated?, lastRunUpdated? }` | internal | none |
| `/internal/jobs/sources/{id}/ingestions` | POST | `id` | 202 | internal | 404 unknown source |
| `/internal/jobs/ingestions` | POST | existing (spec 0008) | 202 | internal | 400 → migrated to ProblemDetails with `errors[source|boardToken|companyName]` |

`JobListItemDto { id, title, company, location?, remoteType?, postedAt?, firstSeenAt, sourceTypes[], score?, confidence?, hasDealbreaker, matchStatus (Scored|NeedsPreferences|Pending), dismissed }`.

Search runs as one query: `jobs` left join the profile's `job_matches`, anti join (or join when `includeDismissed`) `job_dismissals`, filters as SQL predicates, `COUNT` in the same round trip or a second query. `q` and `location` use `ILIKE` with escaped wildcards. Implemented as `IJobSearchQuery` (Application/Modules/Jobs), Infrastructure implementation.

Web (`AddJobsWeb()`, `WorkPilot.Web/Features/Jobs/JobsApiClient.cs`): pages `WorkPilot.Web/Components/Pages/Jobs/Jobs.razor` (`/jobs`, replaces the placeholder) and `JobDetail.razor` (`/jobs/{id:guid}`, replaces spec 0019's minimal one and reuses its `MatchPanel` component); `JobSourcesDrawer.razor`. Filters bind with `[SupplyParameterFromQuery]` and `NavigationManager.GetUriWithQueryParameters`.

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Search | profile | Web claim (`ProfileWebExtensions.ProfileId`) |
| Search | score, confidence, dealbreaker | `job_matches` for (job, profile) |
| Search | match status | `Scored` when a row exists; else `NeedsPreferences` when `IMatchPreferencesQuery` says unset; else `Pending` |
| Search | posted | `jobs.PostedAt`, else the earliest `job_source_links.FirstSeenAt` (spec 0017, mirrored in `Provenance`) |
| Search | source badges | `job_source_links` join `job_sources.Type` |
| Search | confidence label | Web: `high` ≥ 0.75, `medium` ≥ 0.4, else `low` |
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
- No preferences: banner shows and rows say `Set preferences`; verifies **AC-7**.
- Validation: `pageSize=500` gives 400 ProblemDetails; verifies **AC-8**.
- Merge: dismissing a job that later merges keeps it dismissed on the kept job; verifies **Key invariants**.

## Build plan

Tracer Bullet: a thin list end to end first, then filters, detail, dismissal and sources.

1. Thin thread: `IJobSearchQuery` with paging and both sorts, `GET /internal/jobs/search`, `AddJobsWeb` + `JobsApiClient`, `/jobs` list rows with score badge and match status, preferences banner. Satisfies **AC-1**, **AC-3**, **AC-7**.
2. Filters: facets endpoint, every filter predicate with validation, URL bound filter bar, clear filters. Satisfies **AC-2**.
3. Detail: extend `JobDetailView` with salary and deleted flag, two column `/jobs/{id}` reusing `MatchPanel`. Satisfies **AC-5**.
4. Dismissal: migration `AddJobsList` (`job_dismissals`, posted index), PUT/DELETE endpoints, merge moves dismissals, row and detail actions, show dismissed toggle. Satisfies **AC-4**.
5. Sources drawer: sources summary endpoint, run now endpoint, `POST /internal/jobs/ingestions` errors as ProblemDetails, drawer UI. Satisfies **AC-6**.
6. States and tests: loading, empty, error; Api integration tests (real Postgres) for search, filters, paging, dismissal, merge, sources; bUnit tests for the filter bar and states. Satisfies **AC-1** to **AC-8**.

## Consequences

**Positive**:
- You can run the whole discover, score, browse loop from the app.
- The detail layout is final, so #16 only adds its Prepare button.

**Negative / tradeoffs**:
- Experience level, job type and visa filters from the scope are not built; the match breakdown covers them on the detail page.
- `ILIKE` contains searches scan; fine at thousands of jobs, needs a trigram index if the catalog grows large.
- The sources drawer on `/jobs` duplicates a job the Integrations hub (#31) may take over later.

**Neutral**:
- `POST /internal/jobs/ingestions` moves its bare 400s onto ProblemDetails (spec 0018 convergence for a touched endpoint).
- `GET /internal/jobs?jobSourceId=` (spec 0008) stays as it is.

## Follow-up

- [ ] Experience level, job type and visa filters once a spec adds those columns (or an AI extraction pass, spec 0019 follow up).
- [ ] Save/shortlist and the Prepare application button with #16.
- [ ] Decisions made without the engineer (please review): confidence labels (0.75, 0.4); dismissals move on merge; "Run now" enqueues by source id (new endpoint) instead of resending the board token; last run data read from the audit log.
