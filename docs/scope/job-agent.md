# Epic: Job Agent (Slice 1 — core loop)

The walking skeleton of the whole product: discover a real job, know why it matches, prepare a real application, get it approved, and know for certain whether it was actually submitted. Every feature here is a vertical slice (UI + logic + data + verification), not a layer.

### 9. Job source ingestion & normalization · done
`IJobSource` interface; at least one real source wired end to end; raw job -> normalizer -> canonical Job + JobSnapshot with provenance (source URL, retrieved/verified timestamps).
**Done when:** a real search against one live source produces canonical Job rows with provenance fields populated.
- [x] Design it (spec): `/architect job source ingestion & normalization` → [0008](../specs/0008-job-source-ingestion/index.md)
- [x] Build it: `/develop job source ingestion & normalization`
  - [x] Domain normalizer + `Job.Create`/`Refresh` snapshot and provenance rules (AC-2, AC-3, AC-4)
  - [x] Migration `AddJobIngestion` + `IJobSource`, `JobIngestionService`, repository (AC-2, AC-4, AC-5, AC-6, AC-8)
  - [x] `GreenhouseJobSource` (public Greenhouse Job Board API) + `IngestJobsJob` Hangfire job (AC-2, AC-6)
  - [x] `POST /internal/jobs/ingestions` trigger + `GET /internal/jobs` read endpoint (AC-1, AC-7)
- [x] Verify it: `/check verify job source ingestion & normalization`
- [x] Test it: `/test job source ingestion & normalization`
- [x] Review it (fresh model): `/check review` → [review](../reviews/2026-09-25-feat-job-ingestion.md)
- [x] Document it: `/document pr` → PR #6

`/check verify` (2026-09-24): PASS; see [verify.md](../specs/0008-job-source-ingestion/verify.md). Driven live against the Api (Hangfire worker on) and the real Greenhouse board API: `gitlab` with keyword `engineer` fetched 206 postings and stored 103 canonical jobs, each with one snapshot and `SourceUrl`/`RetrievedAt`/`VerifiedAt`/`Confidence` populated; a re-run changed nothing but `VerifiedAt`; a changed hash produced exactly one update and a second snapshot; an unknown board failed after Hangfire's 2 retries with nothing stored; `stripe` stored 61 more. One fix during verify: the Greenhouse client now has its own resilience pipeline, since the default 10 s per attempt timeout was too tight for a 3 MB board.

`/test` (2026-09-24): 53 new tests, all passing (Domain 72/72, Api 59/59, Web 68/68). `tests/WorkPilot.Domain.Tests/JobIngestionTests.cs` (25, unit): normalizer whitespace, HTML and entity encoded HTML, remote detection, stable hash, keyword search, `Job.Create`/`Refresh` provenance and snapshot rules. `tests/WorkPilot.Api.Tests/JobIngestionTests.cs` (13, real Postgres, scripted `IJobSource`): stored jobs and audit row, keyword filter and skips, idempotent re-run and snapshot on change, source failure stores nothing, trigger 400s and find or create, read endpoint and 404. `tests/WorkPilot.Api.Tests/GreenhouseJobSourceTests.cs` (15): field mapping on a captured payload, fallbacks, malformed bodies, token validation, request URL, 404. Follow ups: recurring schedule, stale job closing, a second source, salary extraction (see the spec).

### 10. Job deduplication · done
Same job from multiple sources collapses to one canonical Job with multiple source links, never duplicate applications.
**Done when:** ingesting the same posting twice (from the same or a different source) produces one canonical Job, not two.
- [x] Design it (spec): `/architect job deduplication` → [0017](../specs/0017-job-deduplication/index.md)
- [x] Build it: `/develop job deduplication` (code in `src/*/Modules/Jobs/`, `Infrastructure/Modules/Jobs/Sources/LeverJobSource.cs`, migration `AddJobDeduplication`)
  - [x] Domain: `JobDedupKey`, `JobSourceLink`, reworked `Job` (attach, revive, primary link, stale, split, merge) (AC-2 to AC-5, AC-8, AC-11)
  - [x] `AddJobDeduplication` migration with link backfill, and Greenhouse ingestion end to end on links with locks and merges (AC-1, AC-2, AC-10 to AC-12)
  - [x] `companyName` on the trigger with rename rematch, and the Lever source (AC-6, AC-7)
  - [x] `ReconcileJobsJob`, the read endpoints and the split endpoint (AC-8, AC-9, AC-12, AC-13)
- [x] Verify it: `/check verify job deduplication`
- [x] Test it: `/test job deduplication`
- [x] Review it (fresh model): `/check review` → [review](../reviews/2026-09-26-feat-job-deduplication.md)
- [x] Document it: `/document pr`

### 11. Job matching engine & scoring · done
Scores a canonical Job against the user's Profile (skills, experience, education, location, remote preference, salary, technology, job type, work authorization). Output must be explainable: score, confidence, evidence, missing requirements, unknown information.
**Done when:** a job's match score is displayed with a "why it matches" list backed by real evidence pointers, not a black box number.
- [x] Design it (spec): `/architect job matching engine & scoring` → [0019](../specs/0019-job-matching-engine/index.md)
- [x] Build it: `/develop job matching engine & scoring` (code in `src/*/Modules/Jobs/Matching/`, `src/WorkPilot.AI/Matching/`, `Infrastructure/Modules/Profile/MatchProfileService.cs`, `Web/Components/Pages/{Jobs,JobDetail,Profile}.razor`, migration `AddJobMatching`)
  - [x] `AddJobMatching` migration, Domain scorer (all eight dimensions, blockers, confidence, quote checks, fingerprint) and `Matching` config validation (AC-2 to AC-6, AC-16)
  - [x] Extraction on the `JobExtraction` purpose with the Fake answer, `JobContentChanged` from ingestion, merge and split, extraction and scoring jobs with the raw SQL upsert and `JobMatched` (AC-1, AC-7, AC-8, AC-12 to AC-14, AC-17)
  - [x] Match profile `GET`/`PUT` with ETag, 412/428 in `Result<T>`, `MatchProfileChanged` and `RescoreProfileJob`, the hourly sweep plus the Api start enqueue (AC-7, AC-11)
  - [x] Match list, match panel and rescore endpoints, and the `/jobs`, `/jobs/{id}` and `/profile` pages with the Profile nav item (AC-9, AC-10, AC-11, AC-15)
- [x] Verify it: `/check verify job matching engine & scoring` → [verify](../specs/0019-job-matching-engine/verify.md)
- [x] Test it: `/test job matching engine & scoring`
- [x] Review it (fresh model): `/check review` → [review](../reviews/2026-10-03-feat-job-matching-engine.md)
- [x] Document it: `/document pr`

### 12. Jobs list & job detail
`/jobs` list with filters (location, remote, salary, experience, technology, company, job type, source, match score, date posted, visa sponsorship) and the two column job detail (job info + agent analysis) per the product spec.
**Done when:** the list is filterable and sortable against real matched jobs, and detail shows the explainable match analysis from feature 11.
- [ ] Design it (spec): `/architect jobs list & job detail` (Wave 1, spec 0018 section 9)

### 13. Dashboard overview
`/` answers "what needs attention right now": pipeline counts, today's priorities, upcoming interviews/follow-ups, recent agent activity.
**Done when:** the dashboard reflects real application pipeline counts and at least one live "today's priorities" item sourced from real state.
- [ ] Design it (spec): `/architect dashboard overview` (Wave 1, spec 0018 section 9)

### 14. Resume management · done
Immutable resume versions once used in an application; base resume plus tailored/company-specific versions.
**Done when:** a resume version used by a submitted application can never be silently edited, and version history is visible.
- [x] Design it (spec): `/architect resume management` → [0009](../specs/0009-resume-management/index.md)
- [x] Build it: `/develop resume management`
  - [x] Domain draft/lock/version rules, `AddResumeManagement` migration with the locked row trigger, Postgres file store (AC-2 to AC-6, AC-8, AC-10)
  - [x] `IResumeService` + `/internal/resumes` endpoints, profile scoped (AC-1, AC-3, AC-7, AC-9)
  - [x] `/resumes` and `/resumes/{id}` pages, file download proxy, nav item (AC-1, AC-6, AC-7)
- [x] Verify it: `/check verify resume management`
- [x] Test it: `/test resume management`
- [x] Review it (fresh model): `/check review` → [review](../reviews/2026-09-26-feat-resume-management.md)
- [x] Document it: `/document pr`

### 15. Cover letter generation
Templates, AI-generated drafts (via the provider abstraction), versioning, linkage to the application that used them.
**Done when:** a cover letter is generated from a job + profile + resume, is editable, and is versioned once used.
- [ ] Design it (spec): `/develop cover letter generation`

### 16. Application preparation flow
`/applications/new`: resume selection, cover letter draft/edit, generated question answers, a readiness checklist, ending in a request for approval (not direct submission).
**Done when:** a prepared application cannot reach "ready for approval" unless resume, cover letter, and required questions are all complete.
- [ ] Design it (spec): `/architect application preparation flow`

### 17. Application pipeline & tracking
The full state machine (DISCOVERED -> MATCHED -> SHORTLISTED -> PREPARING -> READY_FOR_APPROVAL -> APPROVED -> SUBMITTING -> CONFIRMATION_PENDING -> APPLIED -> INTERVIEW, terminal REJECTED/WITHDRAWN/FAILED) plus `/applications` list and detail (timeline, documents used, submission evidence, activity).
**Done when:** every state transition is recorded as an auditable event, and an application's detail page can always show exactly which resume/cover letter version and what evidence was used.
- [ ] Design it (spec): `/architect application pipeline & tracking`

### 18. Application execution engine (browser worker / ATS) · needs a decision · GA
The highest risk feature in the product. Browser worker (Playwright class tooling) for navigation, form discovery/mapping, uploads, submission, screenshots, result extraction, run through supported ATS integrations or provider adapters where possible. Explicit failure handling for timeout, login required, CAPTCHA, unsupported form, upload failure, network failure, rate limit, site error, and "submission uncertain." CAPTCHA/anti-bot always routes to human intervention, never bypass. LinkedIn: discovery/matching/prep automated, final submission user-assisted or manual, never assumed auto-authorized. Every submission carries an idempotency key so a crashed worker cannot double submit.
**Done when:** for at least one real, permitted target, an approved application is submitted, evidence of success or a clear "uncertain, needs review" state is captured, and no scenario silently reports success without evidence.
- [ ] Design it (spec): `/architect application execution engine`

### 19. Activity feed & audit log · done
`/activity` timeline of everything the Agent and user did, filterable by domain (Agent, Jobs, Email, Calendar, System, Errors).
**Done when:** every meaningful action anywhere in the product (who, what, when, why, target, result, evidence) shows up here.
- [x] Design it (spec): `/architect activity feed & audit log` → [0011](../specs/0011-activity-feed-audit-log/index.md)
- [x] Build it: `/develop activity feed & audit log` (code in `src/*/Modules/Audit/`, `Api/Endpoints/AuditEndpoints.cs`, `Contracts/Audit/`, `Web/Features/Audit/`, `Web/Components/{Pages/Audit,Audit}/`, `Web/Features/Common/BrowserTimeZone.cs`, migration `AddActivityFeed`)
  - [x] Category rule, `AddActivityFeed` migration with backfill, `IActivityQuery` and `GET /internal/audit/activity`, `/activity` page with Load more and nav item (AC-1, AC-3, AC-8)
  - [x] Category chips bound to the URL, 400 for bad input (AC-2)
  - [x] Summaries, who, browser time zone, evidence expander, target links (AC-4, AC-5, AC-6)
  - [x] Loading, empty and error states (AC-7)
- [x] Verify it: `/check verify activity feed & audit log` → [verify.md](../specs/0011-activity-feed-audit-log/verify.md)
- [x] Test it: `/test activity feed & audit log` (`AuditCategoriesTests`, `ActivitySummariesTests`, `ActivityTests`, `ActivityFeedTests`, `AdvanceRunJobTests`)

### 20. Notifications · in-progress
Approval required, application submitted/failed, reply received, interview upcoming, deadline approaching, workflow failed, integration expired; priority levels (Info/Success/Warning/Action Required/Error).
**Done when:** at least the approval-required and application-outcome notifications fire in real time off real events.
- [x] Design it (spec): `/architect notifications` → [0020](../specs/0020-notifications/index.md)
- [x] Build it: `/develop notifications` (code in `src/*/Modules/Notifications/`, `Workers/Notifications/`, `Api/Endpoints/NotificationsEndpoints.cs`, `Contracts/Notifications/`, `Web/Features/Notifications/`, `Web/Components/Pages/Notifications/`, migration `AddNotifications`)
  - [x] `AddNotifications` migration, `Notification` domain, approval handler, unread count, `TopBarActions` slot and polling bell (AC-1, AC-4, AC-6)
  - [x] List, read, read all and dismiss endpoints; drawer; `/notifications` page with paging and the unread toggle (AC-4, AC-5, AC-8)
  - [x] Run failed handler with readable reasons; strong match digest with row lock and dedupe (AC-2, AC-3, AC-6)
  - [x] `notifications.cleanup` recurring job and `Notifications:ReadRetentionDays` (AC-7); loading, empty and error states (AC-9)
- [x] Verify it: `/check verify notifications` → [verify.md](../specs/0020-notifications/verify.md)
- [x] Test it: `/test notifications` (`NotificationTests`, `StrongMatchDigestTests`, `AgentRunFailureReasonTextTests`, Api `NotificationsTests`, `NotificationBellTests`, `NotificationsPageTests`, `NotificationDisplayTests`, `NotificationsApiClientTests`)
- [x] Review it (fresh model): `/check review` → [review](../reviews/2026-10-03-feat-notifications.md)
- [x] Document it: `/document pr`
