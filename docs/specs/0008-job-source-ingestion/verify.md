# Verify: job source ingestion and normalization · spec 0008 · 2026-09-24
_Steps derived from spec 0008's acceptance criteria. Run live against the Api (`dotnet run --no-launch-profile --project src/WorkPilot.Api`, `ASPNETCORE_URLS=http://localhost:5209`, Hangfire worker enabled), the real Greenhouse Job Board API, and an empty Postgres database (`wp_f09_jobs` on the self hosted Supabase stack; all three migrations, including `AddJobIngestion`, applied from zero on Api start)._

**Result: PASS.** Nothing blocked: the source needs no key.

## Commands
- [x] `POST /internal/jobs/ingestions {"source":"greenhouse","boardToken":"gitlab","keywords":"engineer"}` → `202 {"jobSourceId":"01a0d3d6-…","backgroundJobId":"1"}` → AC-1
- [x] Same call with `"source":"Greenhouse","boardToken":"GITLAB"` → `202`, the **same** `jobSourceId`, new `backgroundJobId` `2` (one `job_sources` row: `Greenhouse | greenhouse:gitlab | {"boardToken": "gitlab"}`) → AC-1
- [x] `source:"linkedin"` → `400`; `boardToken:"../evil.com/x"` → `400`; no `boardToken` → `400` → AC-1
- [x] Hangfire job 1 log: `Ingested job source …: fetched 206, matched 103, created 103, updated 0, unchanged 0, skipped 0.` → AC-2
- [x] DB after run 1: 103 `jobs`, 103 distinct `ExternalId`s, 0 rows missing `Provenance_SourceUrl`/`RetrievedAt`/`VerifiedAt`, 103 with a plain text `Description`, 83 `RemoteType = Remote` (the rest unknown/null, e.g. `Bangalore, India`), 103 `job_snapshots` all carrying `JobSourceId` + `ExternalId` → AC-2, AC-3
- [x] `GET /internal/jobs?jobSourceId=…&take=2` → e.g. `Staff Software Engineer - NLP`, `GitLab`, `Bangalore, India`, `postedAt 2026-09-24T06:04:48Z`, `sourceUrl https://job-boards.greenhouse.io/gitlab/jobs/8781299002`, `retrievedAt`/`verifiedAt` set, `confidence 1.0`, `snapshotCount 1` → AC-7
- [x] `GET /internal/jobs?jobSourceId=<unknown>` → `404` → AC-7
- [x] Run 2 (same board, unchanged): `created 0, updated 0, unchanged 103`; still 103 jobs and 103 snapshots; every job's `VerifiedAt` now later than its `RetrievedAt` → AC-4
- [x] Changed content: set one snapshot's `ContentHash` to `'stale'` in SQL, run 3 → `updated 1, unchanged 102`; that job now has 2 snapshots → AC-4
- [x] `audit_logs`: one `Agent | JobsIngested | JobSource` row per completed run, payload e.g. `{"created":103,"fetched":206,"matched":103,"skipped":0,"updated":0,"unchanged":0,"jobSourceId":"…"}` → AC-5
- [x] Dead source: `boardToken:"nonexistent-board-xyz"` → `202`, then Greenhouse `404`, `HttpRequestException`, `Retry attempt 1 of 2`, … Hangfire state `Failed`; 0 jobs and no audit row for that source → AC-6
- [x] Second real board: `boardToken:"stripe","keywords":"backend, platform"` → `fetched 693, matched 61, created 61` → AC-2, AC-8 (no code path branches on the board)
- [x] Fail fast config: `Jobs__Greenhouse__BaseUrl=http://insecure.test/v1` → Api refuses to start with `InvalidOperationException: Jobs:Greenhouse:BaseUrl must be an absolute https URL ending in '/'`
- [x] `dotnet test WorkPilot.slnx` → Domain 72/72, Api 59/59, Web 68/68; `dotnet format --verify-no-changes` clean

## Found and fixed during verify
- The first live fetch of the gitlab board (3.2 MB with `content=true`) took 6 to 10 s, right at the ServiceDefaults standard resilience handler's 10 s per attempt timeout, and the log showed the default pipeline (`-standard`) ignores options named per client. Fixed in `JobIngestionServiceCollectionExtensions` by replacing that pipeline for the Greenhouse client only (60 s attempt, 180 s total); re-run log shows `Source: 'Greenhouse-standard//Standard-Retry'`.

## Acceptance-criteria coverage
- AC-1 (trigger, find or create, 400s) … live trigger steps, `JobIngestionTests` trigger cases, `GreenhouseJobSourceTests.Describe_*`
- AC-2 (canonical job + snapshot with provenance) … live gitlab and stripe runs, `JobIngestionTests.Ingest_Stores…`
- AC-3 (normalization) … live descriptions and remote types, `Domain.Tests/JobIngestionTests`
- AC-4 (idempotent re-run, snapshot on change) … live runs 2 and 3, `JobIngestionTests.Ingest_TwiceIsIdempotent…`, domain `Refresh_*`
- AC-5 (one audit row per run) … live `audit_logs` query, integration test
- AC-6 (source failure stores nothing and retries; bad postings skipped) … live dead board, `Ingest_WhenTheSourceFails_StoresNothing`, `Ingest_AppliesTheKeywordFilterAndSkips…`, Greenhouse `Parse`/`FetchAsync` failure tests
- AC-7 (read endpoint) … live `GET`s, `ListJobs_*`
- AC-8 (single source seam) … structural: only `GreenhouseJobSource` knows Greenhouse; the integration tests run the whole use case on a different `IJobSource`
