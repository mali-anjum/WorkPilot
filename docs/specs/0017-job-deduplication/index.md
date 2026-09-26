# 0017. Job deduplication across and within sources

**Date**: 2026-09-26
**Status**: In Progress

## Summary

The same role is often posted in several places: on two job boards, or taken down and reposted on the same board under a new id. Today each one becomes its own `Job`, because a job's identity is "this source plus this source's id". This spec makes that pair the identity of a **source link** instead. A `Job` becomes the canonical role, and it collects one link per place the role was seen. A newly seen posting joins an existing job when their normalized company, title and location match exactly. Everything else it needs comes with it:
- Lever becomes a second real source, so this can be proven live across boards.
- A split endpoint undoes a wrong merge.
- A startup job merges the duplicates already in the database.

## Requirements

**User stories**:
- As the founder, I want one job per real role, however many boards list it, so that I never review or apply to the same role twice.
- As the founder, I want to see every place a job was found, and undo a merge that was wrong.

**Acceptance criteria**:
- **AC-1**: Ingesting the same posting twice (same source, same external id) leaves one `Job` with one link. This keeps spec 0008's behavior.
- **AC-2**: A posting from a different source, or from the same source under a new external id, whose normalized company, title and location equal an existing job's, becomes a new **link on that job**. No second `Job` is created, and the ingestion summary counts it as `merged`.
- **AC-3**: The match rule normalizes as follows:
  - **Company:** the source's configured `CompanyName` when set, else the posting's company. Lowercase it, drop punctuation, and remove a trailing legal suffix (`inc`, `incorporated`, `llc`, `ltd`, `limited`, `gmbh`, `ag`, `sa`, `bv`, `plc`, `corp`, `corporation`, `co`).
  - **Title:** lowercase, drop punctuation, and expand `sr` to `senior`, `jr` to `junior` and `mgr` to `manager`.
  - **Location:** lowercase and drop punctuation.
  - **All three:** collapse whitespace. A missing location only matches a missing location.
  - Postings that differ in any normalized part stay separate jobs.
- **AC-4**: A job's displayed fields (title, company, location, remote type, description, posted at) come from its **primary link**: the link with the highest confidence, with ties going to the most recently seen. When a better link joins, the job's fields switch to that link's posting. Every link's own text is kept in its snapshots.
- **AC-5**: A snapshot is added only when *that link's* content hash changes. A second source's posting never counts as a change to the first source's snapshot history.
- **AC-6**: Lever is a second real source. `POST /internal/jobs/ingestions` accepts `source: "lever"` with a Lever site name. Postings come from Lever's public postings API, need no key, and go through the same normalizer and dedup as Greenhouse.
- **AC-7**: The ingestion trigger accepts an optional `companyName`. It is stored on the `JobSource` and used as the company for every posting from it, whatever the source: it is both the displayed company and the company in the match key. Triggering again with a different name updates it, and the same ingestion job (before it fetches) recomputes that source's jobs' fields and match keys and merges any jobs that now collide (audited). The trigger's one `backgroundJobId` covers the rename and the fetch.
- **AC-8**: `POST /internal/jobs/{id}/links/{linkId}/split` moves one link and its snapshots into a new job of its own, built from that link's latest snapshot, and marks the link as split. Both jobs recompute their primary link and fields.
  - Applications and matches stay on the original job. The new job starts with none.
  - Splitting a job's only link returns `400`. An unknown or soft deleted job, or a link not on that job, returns `404`.
  - A split link is never merged back automatically.
- **AC-9**: A Hangfire reconcile job processes every job whose `DedupRuleVersion` is older than the current rule (a stale job). It is enqueued on Api start when any job is stale, and after any ingestion run that marked a job stale (see the "field change" decision). It recomputes the keys and merges every group of jobs that now share a key, oldest job first, skipping jobs that hold a split link.
  - Each key group commits in its own transaction, which also marks that group's jobs current. A crash leaves the rest stale, so the next run picks them up.
  - After this deploy, the duplicates already in the database are merged.
  - Running it again changes nothing.
- **AC-10**: Two ingestion runs that see the same new job at the same time create one `Job`, not two. The second run waits on a lock for that match key and then joins the first run's job.
- **AC-11**: A match on a soft deleted job revives it (clears `IsDeleted`) and attaches the link. A known link reappearing on a soft deleted job revives it too.
- **AC-12**: Every merge (at ingestion, on a rename, in reconcile) and every split writes an audit row: `JobsMerged` or `JobLinkSplit`, with the job ids, the link id and the reason. The ingestion run's `JobsIngested` payload gains a `merged` count.
- **AC-13**: `GET /internal/jobs?jobSourceId=` lists the jobs that have a link from that source, each with its `sources` list. New `GET /internal/jobs/{id}` returns the job with every link (source, external id, URL, first and last seen, confidence, primary, split at) and its snapshot count. An unknown job returns `404`.

## Decision

**Chosen option**: A separate `JobSourceLink` owns the source identity, plus an exact, normalized match key (company, title, location) stored on the job. Matching happens only when a link is first seen. Races are handled with a Postgres advisory lock (a named lock Postgres holds until the transaction ends) on the key. Existing data is merged by a startup reconcile job that is versioned by the rule. See [rationale.md](rationale.md).
**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

Decisions settled while writing (recommended, the engineer may override):
- **Normalization and the key live in the Domain**: `JobDedupKey.For(company, title, location)` sits beside `JobNormalizer` and returns a SHA-256 hex of `company|title|location`. `JobDedupKey.CurrentRuleVersion = 1`. Runner up: compute the key in SQL. Why: one tested C# rule, with no second copy.
- **Which job a new link joins when several share its key** (possible after a split): the oldest among jobs that are not soft deleted, otherwise the oldest soft deleted one, which is then revived. Runner up: the one with the most links. Why: this is stable and predictable.
- **"Oldest" means the lowest `Id`**: ids are UUIDv7 (spec 0002), which sort by creation time, so no `CreatedAt` column is added. Runner up: add `Job.CreatedAt`. Why: the ordering already exists and needs no backfill.
- **A job's own field change never merges inside the run**: when a primary link's posting changes its title, company or location, the job's key is recomputed and its `DedupRuleVersion` is set to `0` (stale). Only new links, a rename and the reconcile ever merge jobs. A run that marked any job stale enqueues the reconcile after it commits. Runner up: merge on every key change. Why: an update is not a new sighting, and merging mid update surprises you, but the collision is still caught right after the run.
- **Merging job B into job A** (A is the older job, the lower `Id`):
  - Move B's links and snapshots to A.
  - Re-point `job_applications.JobId` and `job_matches.JobId` from B to A. Drop B's match row if A already has one for that profile, since matches can be recomputed.
  - Hard delete B.
  - Recompute A's primary link.
  - All of it happens in one transaction.
  - Runner up: soft delete B with a pointer to A. Why: no table reads a "merged into" pointer yet, and a hard delete leaves one row per role. The audit row keeps B's id.
- **Lever adapter**: `GET {Jobs:Lever:BaseUrl}postings/{site}?mode=json`, where the default base URL is `https://api.lever.co/v0/`. Fields map as follows:
  - `id` → `ExternalId`
  - `hostedUrl` → `SourceUrl`
  - `text` → `Title`
  - `categories.location` → location, with `workplaceType` `remote` or `hybrid` added to the location text when it is missing there
  - `description` + `lists` + `additional` HTML → description
  - `createdAt` (epoch milliseconds) → `PostedAt`
  - the element's JSON → `RawContent`
  - Company comes from `JobSource.CompanyName`, else the site name (for Greenhouse too, `CompanyName` replaces the board's own company name, AC-7). Confidence is `1.0` (the employer's own ATS). The site name is validated against `^[a-z0-9_-]{1,100}$`, like a Greenhouse token.
  - The client gets its own resilience pipeline, as spec 0008 did for Greenhouse (AGENTS.md).
  - Runner up: Lever's authenticated API. Why: it needs a key per company.
- **Lock order**: at the start of the run's transaction, a run computes the keys of all its first seen links and takes `pg_advisory_xact_lock` on each, in sorted order. Sorting stops two runs from deadlocking. Once the locks are held, the run looks each first seen link up again by `(JobSourceId, ExternalId)`: a competing run on the same source may have just committed it, and then it is treated as seen again instead of inserted twice.
- **Rebuilding a posting from a snapshot**: `IJobSource` gains `RawJobPosting ParseStored(JobSource source, string rawContent)`, which parses one stored element (the same JSON an adapter writes to `RawContent`). The split and a primary link that wasn't fetched this run use it, then `JobNormalizer`. Runner up: store normalized fields as columns on the link. Why: the raw content is already kept, and only the adapter knows its shape.

## Feature design

**Data model sketch** (one migration `AddJobDeduplication`):

| Entity | Change | Notes |
|---|---|---|
| `JobSourceLink` **new** `app.job_source_links` | `Id`, `JobId` (FK jobs, cascade), `JobSourceId` (FK job_sources, restrict), `ExternalId` varchar 200, `SourceUrl` text, `FirstSeenAt`, `LastSeenAt` timestamptz, `Confidence` numeric(3,2), `SplitAt` timestamptz null | **unique `(JobSourceId, ExternalId)`**, index `JobId` |
| `Job` | **drops** `JobSourceId`, `ExternalId` (and their unique index); **adds** `DedupKey` varchar 64, `DedupRuleVersion` int default 0, `PrimaryLinkId` uuid null (FK job_source_links, set null) | index `DedupKey`, not unique; `Provenance` now mirrors the links (below) |
| `JobSource` | **adds** `CompanyName` varchar 200 null | |
| `JobSnapshot` | unchanged | belongs to a link through `(JobSourceId, ExternalId)` |

`Job.Provenance` mirrors the links. Today it is `init` only, so it becomes replaceable, but only through `Job`'s own methods (attach, merge, split, refresh), never set from outside:
- `SourceUrl` is the primary link's URL.
- `RetrievedAt` is the earliest `FirstSeenAt`.
- `VerifiedAt` is the latest `LastSeenAt`.
- `Confidence` is the primary link's confidence.

**State transitions** (a link): *first seen* → matched to a job (joined or created) → seen again (the job updates; it never rematches) → optionally *split* (`SplitAt` set, its own job, never automatically merged again).

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/jobs/ingestions` | POST | `source` (`greenhouse` \| `lever`), `boardToken` (site or token, `[a-z0-9_-]{1,100}`), `keywords?`, **`companyName?`** (1 to 200 chars) | 202 `jobSourceId`, `backgroundJobId` | internal only | 400 |
| `/internal/jobs` | GET | `jobSourceId` (req), `take` (default 50, max 200) | `[{id, title, company, location, remoteType, postedAt, sourceUrl, retrievedAt, verifiedAt, confidence, snapshotCount, sources:[{jobSourceId, externalId, sourceUrl, lastSeenAt}]}]` | internal only | 404 unknown source |
| `/internal/jobs/{id}` | GET | | job fields plus `links:[{id, jobSourceId, sourceType, externalId, sourceUrl, firstSeenAt, lastSeenAt, confidence, isPrimary, splitAt}]`, `snapshotCount` | internal only | 404 |
| `/internal/jobs/{id}/links/{linkId}/split` | POST | | 200 `{jobId, newJobId}` | internal only | 400 last link, 404 |

**Value sourcing**:

| Action | Value | Source |
|---|---|---|
| ingest | match key | `JobDedupKey.For(JobSource.CompanyName ?? posting.Company, posting.Title, posting.Location)` |
| ingest | which job a new link joins | `jobs` where `DedupKey` = key (plus jobs created earlier in this run), per the "which job" rule |
| ingest | primary link | the link with max `Confidence`, then max `LastSeenAt` |
| ingest | job display fields | the primary link's current posting (or its latest snapshot, through `IJobSource.ParseStored` and `JobNormalizer`, when it wasn't fetched this run) |
| ingest | displayed company | `JobSource.CompanyName ?? posting.Company` (every source) |
| ingest | a job going stale | its recomputed key differs from its stored `DedupKey` → `DedupRuleVersion = 0` |
| ingest | link `Confidence` | `IJobSource.ProvenanceConfidence` (Greenhouse and Lever 1.0) |
| ingest | `FirstSeenAt` / `LastSeenAt` | the run's `TimeProvider` time (as spec 0008) |
| ingest | `merged` count | new links that joined an existing job, counted by the use case |
| trigger | `CompanyName` | the request's `companyName`, trimmed; absent means unchanged |
| split | new job's fields | the split link's latest snapshot (`RawContent` through `IJobSource.ParseStored`, then `JobNormalizer`) |
| split | which job is "older" | the original job keeps its `Id`; the new job gets a new UUIDv7 |
| reconcile | which jobs to process | `DedupRuleVersion < JobDedupKey.CurrentRuleVersion` |
| rename | which jobs to rematch | the jobs with a link from that source, inside the ingestion job before the fetch |
| read | `sourceType` | `JobSource.Type` |
| read | `isPrimary` | `link.Id == job.PrimaryLinkId` |

**Key invariants**:
- At most one link per `(JobSourceId, ExternalId)` (unique index). Every job has at least one link.
- A link that is seen again never changes jobs. Only a split moves it.
- A job's `DedupKey` equals the key of its current canonical fields and `DedupRuleVersion` equals the rule version, except while it is stale (a reconcile is then enqueued or pending).
- Two current (not stale) jobs that are not soft deleted and hold no split link never share a `DedupKey`. The reconcile restores this for stale jobs.
- An ingestion run (including a rename rematch) commits its links, jobs, snapshots, merges and audit rows in one transaction, or nothing. The reconcile commits one transaction per key group.

**Security model**: single user and internal only, the same boundary as spec 0008. Jobs are shared, not scoped to a profile. The Lever site name is validated against a strict pattern before it goes into the URL path, and it is checked again when read back from `Config`. Only HTTPS GETs go to the configured base URL, with no credentials.

**Configuration required**:
- `Jobs:Lever:BaseUrl` (env `Jobs__Lever__BaseUrl`): optional, default `https://api.lever.co/v0/`. It is validated at startup as an absolute https URL ending in `/`, and startup fails fast if it isn't. For Lever's EU instance, set `https://api.eu.lever.co/v0/`.

**Critical test scenarios**:
- The same posting ingested twice → one job, one link (**AC-1**).
- A scripted second source with the same company, title and location → one job with two links and `merged = 1`. A different location → two jobs (**AC-2**, **AC-3**).
- Normalizer and key unit cases: legal suffixes, `Sr.`, punctuation, missing location (**AC-3**).
- A higher confidence link joins → the job takes its fields. The first source changes → only that link gets a snapshot (**AC-4**, **AC-5**).
- The Lever adapter against a captured payload: field mapping, epoch time, the workplace type rule and a bad site name (**AC-6**).
- A rename triggers a merge. A split moves the link and its snapshots and doesn't re-merge on the next ingest. Splitting the last link returns `400` (**AC-7**, **AC-8**).
- The reconcile merges seeded duplicates, skips a split job and is idempotent (**AC-9**).
- Two concurrent ingestions of the same new posting on real Postgres → one job; two concurrent runs of the same source → one link (**AC-10**).
- A primary link's title changes into another job's key → the job goes stale, the reconcile it enqueues merges them (**AC-9**).
- A split keeps the applications on the original job, and a soft deleted job returns `404` (**AC-8**).
- A soft deleted job is revived. Audit rows and the read endpoints (**AC-11**, **AC-12**, **AC-13**).

## Build plan

1. [x] Domain: `JobDedupKey` (normalize, key, rule version), and `JobSourceLink` with its first seen and seen again rules. Then rework `Job`: `Create`/`AttachLink` (with revive), primary link selection, a replaceable `Provenance` set only by `Job`, marking stale on a key change, snapshots per link, `SplitLink`, and `MergeFrom`. Unit tests. Satisfies **AC-2** to **AC-5**, **AC-8**, **AC-11**.
2. [x] Migration `AddJobDeduplication`:
   - create `job_source_links`;
   - backfill one link per existing job from its `JobSourceId`, `ExternalId` and `Provenance`;
   - set `PrimaryLinkId`;
   - add `CompanyName`, `DedupKey` (null until the reconcile) and `DedupRuleVersion` (0);
   - drop the job's `JobSourceId` and `ExternalId`.
   Satisfies **AC-1**, **AC-9**.
3. [x] Rework ingestion (`JobIngestionService`, repository):
   - look up by link;
   - lock the sorted keys of first seen links, then look those links up again;
   - match or create;
   - add `IJobSource.ParseStored` (Greenhouse now) for primary links not fetched this run;
   - record the `merged` count and `JobsMerged` audit rows;
   - keep one transaction per run, and enqueue the reconcile after it when a job went stale.
   Tracer thread: Greenhouse ingestion works end to end on the new model. Satisfies **AC-1**, **AC-2**, **AC-10**, **AC-11**, **AC-12**.
4. [x] Trigger gains `companyName` (display and match company for every source; the rename rematch runs inside the ingestion job before the fetch), and the Lever `IJobSource` (with `ParseStored`) with its config and resilience pipeline. Satisfies **AC-6**, **AC-7**.
5. [x] `ReconcileJobsJob` (Hangfire), one transaction per key group, enqueued on Api start when any job is stale and after a run that made one stale. It shares the group merge logic with the rename rematch. Satisfies **AC-9**, **AC-12**.
6. [x] The read endpoints (the list's `sources`, `GET /internal/jobs/{id}`) and the split endpoint. Satisfies **AC-8**, **AC-13**.
7. [ ] Integration tests on real Postgres (concurrency with two contexts, merge, split, reconcile, rename) (done: `JobDeduplicationTests`), and live verify with a real Greenhouse board plus a Lever site of the same company. Satisfies all.

## Migration plan

**Strategy**: one deployment. The migration reshapes the schema and backfills the links in SQL (which needs no C#). The startup reconcile then computes the keys and merges existing duplicates.
**Phases**:
1. The migration runs on Api start: links are backfilled 1:1, so every job stays reachable and nothing merges yet.
2. The reconcile job runs once: keys are computed, duplicates merge, and each merge is audited.
**Rollback**: `Down` restores `JobSourceId` and `ExternalId` from each job's primary link and drops the links table. Jobs merged by the reconcile stay merged, and the other links' ids live on only in snapshots. Take a database dump before deploying.
**Risks**: a wrong merge during the reconcile. Mitigation: exact match only, every merge is audited, and the split endpoint undoes one.

## Consequences

**Positive**:
- One row per real role, so applications and matches can't be duplicated.
- The provenance of every sighting is kept.
- A second source proves the `IJobSource` seam.
- The #9 review minor about reviving deleted jobs is fixed.

**Negative / tradeoffs**:
- An exact match misses reworded titles ("Software Engineer, Backend" vs "Backend Software Engineer"), so some duplicates remain.
- A title change on the primary link can briefly leave two jobs for one role, until the reconcile it enqueues runs.
- A `companyName` hides the board's own company name for that source.
- A merge hard deletes the newer job row, and only the audit log remembers its id.
- The `Down` migration can't un-merge.

**Neutral**: `JobSnapshot` keeps identifying its link by `(JobSourceId, ExternalId)` rather than a foreign key to the link.

## Follow-up

- [ ] Fuzzy or AI assisted matching for reworded titles, if exact matching leaves too many duplicates in practice.
- [ ] A split or merge button on the job detail page (feature 12).
- [ ] Remove a split mark (let a split link merge again) if that is ever needed.
- [ ] Recurring ingestion per source, still owed from spec 0008.

## Rationale

See [rationale.md](rationale.md).
