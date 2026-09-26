# Verify: job deduplication · spec 0017 · updated 2026-09-26
_Steps derived from spec 0017 acceptance criteria and its Value sourcing table. `/check verify` runs these; `/test` locks the durable ones._

Setup: `docker compose up -d db` in `supabase/`, then run the Api against a fresh database (it migrates on start). Pick one company that uses **both** Greenhouse and Lever (or one board plus a Lever site with the same roles), and pass the same `companyName` to both triggers. Postgres password comes from `supabase/.env`; never print it.

## Live API (Greenhouse + Lever)
- [ ] `POST /internal/jobs/ingestions {"source":"greenhouse","boardToken":"<gh>","companyName":"<Co>"}` → 202; the job runs; `JobsIngested` audit row has `created > 0`, `merged` present → AC-1, AC-12
- [ ] Trigger the same Greenhouse board again → `created = 0`, `unchanged` = all; no new jobs, no new links → AC-1
- [ ] `POST /internal/jobs/ingestions {"source":"lever","boardToken":"<site>","companyName":"<Co>"}` → 202; roles also on the Greenhouse board join those jobs: `merged > 0`, one `JobsMerged` audit row per joined link with `reason: "ingest"` → AC-2, AC-6, AC-12
- [ ] `GET /internal/jobs?jobSourceId=<lever source>` → the merged jobs list both sources in `sources` → AC-13
- [ ] `GET /internal/jobs/{id}` on a merged job → two links (`sourceType` Greenhouse and Lever), exactly one `isPrimary`; `snapshotCount` = one per link → AC-4, AC-13
- [ ] Both sources are confidence 1.0, so the primary is the most recently seen link: trigger Greenhouse again, and the primary flips to the Greenhouse link; the job's `sourceUrl` follows it → AC-4
- [ ] A Lever posting whose `workplaceType` is `remote` or `hybrid` and whose location lacks it shows e.g. `"Berlin (Remote)"` and the matching `remoteType` → AC-6
- [ ] `POST /internal/jobs/ingestions {"source":"lever","boardToken":"../x"}` → 400; `companyName` of 201 chars or only spaces → 400 → AC-6, AC-7
- [ ] `GET /internal/jobs/{random guid}` → 404 → AC-13

## Split, rename, reconcile
- [ ] `POST /internal/jobs/{mergedId}/links/{leverLinkId}/split` → 200 `{jobId, newJobId}`; the new job has the Lever link (with `splitAt`) and its snapshots, fields from the Lever posting; the original keeps the Greenhouse link; a `JobLinkSplit` audit row → AC-8, AC-12
- [ ] Trigger Lever again → the split link stays on its own job (no re-merge) → AC-8
- [ ] Split a job's only link → 400; a link id not on the job, or a soft deleted job → 404 → AC-8
- [ ] Trigger Greenhouse with a new `companyName` → that source's jobs show the new company; `job_sources.CompanyName` updated; if the new name now collides with another source's jobs they merge with `reason: "rename"`; the response had one `backgroundJobId` → AC-7
- [ ] Old duplicates: restore a pre-feature dump (or run migrations to `AddResumeManagement`, seed two jobs with the same company/title/location under two external ids, then migrate) and start the Api → one link per job backfilled, `DedupRuleVersion` 0, the startup `ReconcileJobsJob` runs once and merges them (audit `reason: "reconcile"`); restart → nothing stale, nothing enqueued → AC-9
- [ ] A soft deleted job (`IsDeleted = true` by SQL) matched by a new posting, or whose link is seen again, comes back with `IsDeleted = false` → AC-11

## Commands
- [ ] `dotnet test WorkPilot.slnx` (with `WORKPILOTDB_CONNECTION` set) → all pass, including `JobDeduplicationTests` → AC-1 to AC-5, AC-7 to AC-13
- [ ] `dotnet-ef migrations has-pending-model-changes` → none; `select condeferrable, condeferred from pg_constraint where conname = 'FK_jobs_job_source_links_PrimaryLinkId'` → `t, t` → data model
- [ ] Two concurrent triggers of two sources that share a brand new role → one job with two links (`JobDeduplicationTests.Two_concurrent_runs...` locks this) → AC-10

## Value sourcing (one check per row)
- [ ] match key: vary only punctuation, case, `Sr.`/`Senior`, `Inc.` → same job; vary location → separate jobs; missing location only matches missing → AC-3
- [ ] which job a new link joins: with two jobs on one key (after a split), a new posting joins the lower id, not soft deleted one
- [ ] primary link: highest `Confidence`, then latest `LastSeenAt`
- [ ] display fields from a link not fetched this run: after a merge where the other source's link becomes primary, the fields come from its stored snapshot (`ParseStored`), not the current run
- [ ] displayed company: with `companyName` set, both Greenhouse and Lever jobs show it (not the board's own name)
- [ ] a job going stale: a retitled primary posting sets `DedupRuleVersion = 0` and enqueues `ReconcileJobsJob` after the run
- [ ] link `Confidence` = the source's `ProvenanceConfidence` (1.00 for both); `FirstSeenAt`/`LastSeenAt` = the run's time
- [ ] `merged` count = new links that joined an existing job
- [ ] trigger `CompanyName`: trimmed; omitted leaves the stored one unchanged
- [ ] split: new job fields from the split link's latest snapshot; applications and matches stay on the original job
- [ ] reconcile processes only `DedupRuleVersion < 1` jobs; rename rematches only jobs linked to that source
- [ ] read `sourceType` = `JobSource.Type`; `isPrimary` = `link.Id == job.PrimaryLinkId`

## Acceptance-criteria coverage
- AC-1 live triggers, tests · AC-2 Lever merge · AC-3 value sourcing key · AC-4 detail/primary flip · AC-5 per link snapshots · AC-6 Lever steps · AC-7 rename · AC-8 split steps · AC-9 old duplicates · AC-10 concurrency · AC-11 revive · AC-12 audit rows · AC-13 read endpoints
