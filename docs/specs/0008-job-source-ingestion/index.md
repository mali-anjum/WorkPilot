# 0008. Job source ingestion and normalization

**Date**: 2026-09-24
**Status**: In Progress

## Summary

This spec designs how WorkPilot pulls real job postings from the outside world and turns them into its own tidy `Job` records. Every source (a job board, a company careers API) plugs in behind one small interface, `IJobSource`, and hands back raw postings; one shared normalizer cleans them up (trims text, turns HTML into plain text, works out whether a role is remote) and the result is stored as a canonical `Job` plus a `JobSnapshot` (a frozen copy of exactly what the source said), both stamped with where the data came from and when. The first real source is the Greenhouse Job Board API (public, free, no key, meant for exactly this use). Ingestion runs as a Hangfire background job started from one internal endpoint, and running it again updates what is there instead of creating duplicates.

## Requirements

**User stories**:
- As the founder, I want WorkPilot to pull real, current postings from a company's job board, so that everything downstream (matching, applications) starts from real jobs, not samples.
- As the founder, I want to know for every job exactly where it came from and when it was last confirmed live, so that I never apply to something stale or invented.
- As the developer, I want a new job source to be one new class behind `IJobSource`, so that adding boards later never touches the normalizer, the storage logic, or the job runner.

**Acceptance criteria**:
- **AC-1**: `POST /internal/jobs/ingestions` with `{"source":"greenhouse","boardToken":"<token>","keywords":"<optional>"}` finds or creates the matching `JobSource` row, enqueues one Hangfire ingestion job, and returns `202` with `jobSourceId` and `backgroundJobId`. An unknown `source`, or a missing or malformed `boardToken`, returns `400` and enqueues nothing.
- **AC-2**: The ingestion job fetches the live board through the source's `IJobSource`, keeps only postings whose title matches the keywords (when given), and stores each as one canonical `Job` (title, company, location, remote type, plain text description, posted date, external id) plus one `JobSnapshot` (the posting's verbatim JSON and its content hash), with `Provenance.SourceUrl`, `RetrievedAt`, `VerifiedAt` and `Confidence` populated on both.
- **AC-3**: Normalization is deterministic and source independent: text is trimmed and inner whitespace collapsed, HTML descriptions (including entity encoded HTML) become plain text, remote type is derived from the location text (`Remote`, `Hybrid`, or unknown), and the same content always yields the same `ContentHash`.
- **AC-4**: Re-running ingestion is idempotent: the same `(JobSourceId, ExternalId)` never produces a second `Job`. Unchanged content adds no snapshot and only advances `VerifiedAt` on the job and its latest snapshot; changed content updates the job's fields and adds a new snapshot. `Provenance.RetrievedAt` on the `Job` keeps its first retrieval time.
- **AC-5**: Each completed ingestion writes exactly one `AuditLog` row (`Actor` `"Agent"`, `Action` `"JobsIngested"`, `TargetType` `"JobSource"`) whose payload holds the fetched, matched, created, updated, unchanged and skipped counts, in the same save as the jobs themselves.
- **AC-6**: A source failure (HTTP error, timeout, malformed response body) writes no jobs, no snapshots and no audit row for that attempt, and the Hangfire job fails so it is retried (2 automatic retries). A single posting missing a required field (id, title, url) is skipped and counted, without failing the whole run.
- **AC-7**: `GET /internal/jobs?jobSourceId=<id>&take=<n>` returns the canonical jobs for that source, newest first, each with its provenance and snapshot count, so a run's result can be inspected (the real `/jobs` list is scope feature 12). `take` defaults to 50 and is capped at 200.
- **AC-8**: `IJobSource` is the only source specific seam: the normalizer, the ingestion use case, the Hangfire job and both endpoints never branch on a source type; they resolve the source implementation by its declared `SourceType`.

## Decision

**Chosen option**: Option 1: a pluggable `IJobSource` returning raw postings, one shared domain normalizer, and an upsert keyed on `(JobSourceId, ExternalId)`, with Greenhouse's public Job Board API as the first real source, run as a Hangfire job.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

Reasoning and full option tradeoffs: see [rationale.md](rationale.md).

## Decisions made without the engineer (please review)

- **First real source**: pick Greenhouse Job Board API (`boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true`); runner up Lever's public postings API (`api.lever.co/v0/postings/{company}`). Why: official, documented for public programmatic reads, no key, stable JSON, employer's own data (high confidence), and Greenhouse is a common ATS target for feature 18 later. Remotive was also considered but asks callers to keep to a few requests a day and needs attribution.
- **What a "search" is**: pick board token plus an optional keyword filter on title (case insensitive, any term matches); runner up fetch the whole board with no filter. Why: Greenhouse has no server side search, and a keyword filter keeps a run focused without inventing a matching engine (feature 11).
- **JobSource granularity**: pick one `JobSource` row per Greenhouse board (`Type` `Greenhouse`, `Name` `greenhouse:<token>`, `Config` `{"boardToken":"<token>"}`), found or created by the trigger; runner up one row per source type with all boards in its config. Why: a board is the unit Greenhouse scopes ids to, so `(JobSourceId, ExternalId)` stays a true unique key.
- **Snapshot policy**: pick a new snapshot only when the content hash changes, otherwise advance `VerifiedAt`; runner up a snapshot on every retrieval. Why: every retrieval would bloat `job_snapshots` with identical copies and add nothing for dedup (feature 10).
- **Provenance meaning**: pick `SourceUrl` = the posting's public URL, `RetrievedAt` = first retrieval, `VerifiedAt` = last time the source still returned it, `Confidence` = `1.0` for an employer's own ATS; runner up `VerifiedAt` left null until a separate check. Why: the source listing the posting right now is the verification.
- **Schema additions (one migration, `AddJobIngestion`)**: pick `jobs.Description` (text) and `jobs.PostedAt`, `job_snapshots.JobSourceId` and `ExternalId`, and a unique index on `job_sources (Type, Name)`; runner up keeping the description only inside the raw snapshot. Why: matching (feature 11) needs a plain text description, and dedup (feature 10) needs each snapshot to carry its own source link once one `Job` gathers snapshots from several sources.
- **How a run is observed**: pick the `AuditLog` row plus the `GET /internal/jobs` read endpoint; runner up a new `IngestionRun` table. Why: no new table for a milestone that only needs a count and a list.
- **No recurring schedule, no UI, no stale job closing yet**: pick trigger only; runner up a Hangfire recurring job per source. Why: stays inside scope item 9; each is a follow up.
- **Tests against Greenhouse**: pick an in test fake `IJobSource` for the Postgres integration tests plus a unit test of the Greenhouse adapter against a captured payload, with the live source proven in `verify.md`; runner up integration tests hitting the live board. Why: a third party board changing its postings must not make the suite flaky.

## Feature design

**Data model sketch** (reuses spec 0002's entities; one migration `AddJobIngestion`):

| Entity | Change | Notes |
|---|---|---|
| `JobSource` | unique index `(Type, Name)`; `Type` max 50 | `Config` jsonb `{"boardToken":"gitlab"}` |
| `Job` | **new** `Description` (text, nullable), **new** `PostedAt` (timestamptz, nullable) | unique `(JobSourceId, ExternalId)` already exists; `Provenance` owned |
| `JobSnapshot` | **new** `JobSourceId` (uuid, required, FK `job_sources`), **new** `ExternalId` (varchar 200, required) | `RawContent` verbatim posting JSON, `ContentHash` SHA-256 hex, `Provenance` owned |

Domain value types (no table): `RawJobPosting` (what a source returns: `ExternalId`, `SourceUrl`, `Title`, `Company`, `LocationText`, `DescriptionHtml`, `PostedAt`, `RawContent`), `NormalizedJob` (the normalizer's output plus `ContentHash`), `JobSearchQuery` (`Keywords`).

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/jobs/ingestions` | POST | `source:string` (req, `greenhouse`), `boardToken:string` (req, `[a-z0-9_-]{1,100}` case insensitive), `keywords:string` (opt) | 202 `jobSourceId`, `backgroundJobId` | internal only (same boundary as `/internal/agent/runs`) | 400 |
| `/internal/jobs` | GET | `jobSourceId:guid` (req), `take:int` (opt, default 50, max 200) | `[{id, externalId, title, company, location, remoteType, postedAt, sourceUrl, retrievedAt, verifiedAt, confidence, snapshotCount}]` | internal only | 404 unknown source |

**Value sourcing**:

| Action | Value | Source |
|---|---|---|
| trigger | `JobSource.Name` / `Type` / `Config` | `greenhouse:<lowercased token>` / the `IJobSource.SourceType` (`Greenhouse`) / `{"boardToken":...}` from the request |
| trigger | `backgroundJobId` | returned by Hangfire's `IBackgroundJobClient.Enqueue` |
| ingestion | raw postings | Greenhouse `GET {BaseUrl}boards/{token}/jobs?content=true`: `id` → `ExternalId`, `absolute_url` → `SourceUrl`, `title`, `company_name` → `Company` (falls back to the board token when absent), `location.name`, `content` (entity encoded HTML), `first_published` → `PostedAt` (falls back to `updated_at`), the posting's JSON element → `RawContent` |
| ingestion | `RetrievedAt`, `VerifiedAt` | `TimeProvider` UTC now, taken once per run, when the fetch returns |
| ingestion | `Confidence` | `1.0` for Greenhouse (declared by the `IJobSource` as `ProvenanceConfidence`) |
| ingestion | `RemoteType` | derived from normalized location text: contains `remote` → `Remote`, `hybrid` → `Hybrid`, else null |
| ingestion | `ContentHash` | SHA-256 hex of `title \n company \n location \n description` after normalization |
| ingestion | audit counts | computed by the use case while upserting |
| read | `snapshotCount` | count of `job_snapshots` for the job |

**Key invariants**:
- At most one `Job` per `(JobSourceId, ExternalId)` (DB unique index plus the upsert).
- Every `Job` and `JobSnapshot` row has `SourceUrl` and `RetrievedAt`; ingestion always sets `VerifiedAt` too.
- A `Job`'s latest snapshot's `ContentHash` equals the hash of its current normalized fields.
- A run commits all its changes and its audit row in one `SaveChanges`, or nothing.

**Security model**: single user, internal endpoints only, reachable inside the Api's network boundary like the existing `/internal/*` routes. Only HTTPS GETs to a configured base URL; the board token is validated against a strict pattern before it is placed in the URL path, so a request can't steer the call to another host or path. No credentials are involved.

**Configuration required**:
- `Jobs:Greenhouse:BaseUrl` (env `Jobs__Greenhouse__BaseUrl`): optional, default `https://boards-api.greenhouse.io/v1/`; validated at startup as an absolute `https` URL ending in `/` (fail fast).

**Critical test scenarios**:
- Happy path: ingest a fake source's two postings → two `Job`s and two `JobSnapshot`s with provenance populated and one `JobsIngested` audit row, verifies **AC-2**, **AC-5**.
- Re-run: ingest the same postings again → still two `Job`s, no new snapshot, `VerifiedAt` advanced; change one posting's title → that job updated and a second snapshot added, verifies **AC-4**.
- Failure: the source throws → no rows written, the exception propagates for Hangfire's retry, verifies **AC-6**.
- Endpoint validation: unknown source and bad token → `400`; unknown `jobSourceId` on read → `404`, verifies **AC-1**, **AC-7**.
- Normalizer unit cases: whitespace, entity encoded HTML, remote detection, stable hash, verifies **AC-3**.
- Greenhouse adapter against a captured payload: field mapping and skipping a posting missing its title, verifies **AC-2**, **AC-6**.

## Build plan

Tracer Bullet: one thin thread (trigger → Hangfire job → Greenhouse → normalizer → Postgres → read endpoint) first, then thicken.

1. Domain: `RawJobPosting`, `NormalizedJob`, `JobNormalizer`, and `Job.Create` / `Job.Refresh` / `JobSnapshot` creation rules, satisfies **AC-2**, **AC-3**, **AC-4**
2. Migration `AddJobIngestion` and configuration changes, satisfies **AC-2**, **AC-4**
3. Application: `IJobSource`, `IJobIngestionRepository`, `JobIngestionService` (fetch, filter, normalize, upsert, audit, one save), satisfies **AC-2**, **AC-4**, **AC-5**, **AC-6**, **AC-8**
4. Infrastructure: `GreenhouseJobSource` (typed `HttpClient`, validated base URL), `JobIngestionRepository`, `AddJobIngestion()` DI extension, satisfies **AC-2**, **AC-6**, **AC-8**
5. Workers: `IngestJobsJob` Hangfire job (2 automatic retries), satisfies **AC-6**
6. Api: `Endpoints/JobsEndpoints.cs` with the trigger and read endpoints, one `MapJobEndpoints()` line in `Program.cs`, satisfies **AC-1**, **AC-7**
7. Prove it live against the real Greenhouse board and the real Postgres, satisfies **AC-1** to **AC-7**

## Consequences

**Positive**:
- Every later source is one class; dedup (feature 10) gets `ContentHash`, `ExternalId` and `JobSourceId` on every snapshot; matching (feature 11) gets a plain text description.
- Re-runs are cheap and safe; provenance says exactly when a job was last seen live.

**Negative / tradeoffs**:
- Greenhouse is per company, so discovery breadth depends on which boards are ingested; a cross company source is a second `IJobSource`.
- Title only keyword filtering is crude by design; real relevance is feature 11's job.
- Jobs that vanish from a board stay as they are until a stale job rule exists.

**Neutral**:
- `job_snapshots` gains two required columns; existing environments have no snapshot rows yet, so defaults never matter in practice.

## Follow-up

- [ ] Recurring ingestion: a Hangfire recurring job per active `JobSource` (and a place to configure which boards), once the founder picks target companies.
- [ ] Stale jobs: mark or soft delete jobs a source stops returning (needs a rule, e.g. missing for N runs).
- [ ] A second source (Lever, or a cross company API) to prove the seam, ideally together with feature 10.
- [ ] Salary extraction: Greenhouse's list endpoint has no structured pay; parse ranges from text or use the `pay_input_ranges` field of the per job endpoint.
- [ ] Error handling: `/internal/jobs/ingestions` follows the existing `Results.BadRequest` style; revisit when the project wide error pattern is decided.
- [ ] `AGENTS.md` could gain a line: new job sources implement `IJobSource` in `Infrastructure/Modules/Jobs/Sources/` and register in `AddJobIngestion()`.
