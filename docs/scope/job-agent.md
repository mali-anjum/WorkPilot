# Epic: Job Agent (Slice 1 — core loop)

The walking skeleton of the whole product: discover a real job, know why it matches, prepare a real application, get it approved, and know for certain whether it was actually submitted. Every feature here is a vertical slice (UI + logic + data + verification), not a layer.

### 9. Job source ingestion & normalization · needs a decision
`IJobSource` interface; at least one real source wired end to end; raw job -> normalizer -> canonical Job + JobSnapshot with provenance (source URL, retrieved/verified timestamps).
**Done when:** a real search against one live source produces canonical Job rows with provenance fields populated.
- [x] Design it (spec): `/architect job source ingestion & normalization` → [0008](../specs/0008-job-source-ingestion/index.md)

### 10. Job deduplication
Same job from multiple sources collapses to one canonical Job with multiple source links, never duplicate applications.
**Done when:** ingesting the same posting twice (from the same or a different source) produces one canonical Job, not two.
- [ ] Design it (spec): `/architect job deduplication`

### 11. Job matching engine & scoring · needs a decision
Scores a canonical Job against the user's Profile (skills, experience, education, location, remote preference, salary, technology, job type, work authorization). Output must be explainable: score, confidence, evidence, missing requirements, unknown information.
**Done when:** a job's match score is displayed with a "why it matches" list backed by real evidence pointers, not a black box number.
- [ ] Design it (spec): `/architect job matching engine & scoring`

### 12. Jobs list & job detail
`/jobs` list with filters (location, remote, salary, experience, technology, company, job type, source, match score, date posted, visa sponsorship) and the two column job detail (job info + agent analysis) per the product spec.
**Done when:** the list is filterable and sortable against real matched jobs, and detail shows the explainable match analysis from feature 11.
- [ ] Design it (spec): `/develop jobs list & job detail`

### 13. Dashboard overview
`/` answers "what needs attention right now": pipeline counts, today's priorities, upcoming interviews/follow-ups, recent agent activity.
**Done when:** the dashboard reflects real application pipeline counts and at least one live "today's priorities" item sourced from real state.
- [ ] Design it (spec): `/develop dashboard overview`

### 14. Resume management
Immutable resume versions once used in an application; base resume plus tailored/company-specific versions.
**Done when:** a resume version used by a submitted application can never be silently edited, and version history is visible.
- [ ] Design it (spec): `/architect resume management`

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

### 19. Activity feed & audit log
`/activity` timeline of everything the Agent and user did, filterable by domain (Agent, Jobs, Email, Calendar, System, Errors).
**Done when:** every meaningful action anywhere in the product (who, what, when, why, target, result, evidence) shows up here.
- [ ] Design it (spec): `/develop activity feed & audit log`

### 20. Notifications
Approval required, application submitted/failed, reply received, interview upcoming, deadline approaching, workflow failed, integration expired; priority levels (Info/Success/Warning/Action Required/Error).
**Done when:** at least the approval-required and application-outcome notifications fire in real time off real events.
- [ ] Design it (spec): `/develop notifications`
