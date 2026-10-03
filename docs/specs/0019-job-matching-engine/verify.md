# Verify: job matching engine & scoring · spec 0019 · updated 2026-10-03
_Steps derived from spec 0019 acceptance criteria and its Value sourcing table. `/check verify` runs these; `/test` locks the durable ones._

**Result (2026-10-03): PASS after one fix.** Run live against the Api (`:5199`, Hangfire on, Fake provider, real Greenhouse boards `gitlab` and `stripe`) and the Web host (`:5292`, signed in through the self hosted GoTrue as a throwaway user, deleted afterwards), the UI driven in headless Chrome (screenshots `ui-01` to `ui-08` in the session scratchpad). Evidence highlights: 16 new Stripe jobs extracted (`fake`) and scored for both profiles with no manual step; an unchanged re-ingest added 0 content events and left `RankedAt` alone; all 108 stored scores and confidences recomputed exactly from their explanations; blockers (no sponsorship, onsite outside a Remote only preference) capped at 20; an invented COBOL quote listed as unverified; a stub provider returning 500 gave 3 attempts (9 HTTP requests), `Failed`, a Low title and location fallback, and the captured requests carried no tools, a `json_schema` format, the description only and no profile values; merge removed the loser's requirements by cascade; bad weights stopped startup. **One failure, fixed and re-verified:** a changed `Matching` config never rescored (the start sweep only queued profiles with an old scoring version or a missing match); the start sweep now rescores every profile, and three restarts showed 107 written, 0 written, 107 written. Left unticked below: steps not exercised live (version bumps, years and degree rules, fallback location parsing, truncation of a long description, the UI "already running" wording, which the button's busy state and the fast Fake make unreachable); `/test` covers them.

Setup: the self hosted Supabase stack up (`docker compose up -d` in `supabase/`), the Api running with the Hangfire server on and the committed `Fake` AI provider, `P` = the founder's profile id (`select "Id" from app.profiles where "Name"='founder'`).

## UI / manual
- [x] Sign in, open `/profile` → preferences, skills, experience and education load; the Profile nav item is under Work → AC-11
- [x] On `/profile` set currency `XXQ` and save → a field error names the currency; edits stay on screen → AC-11
- [x] Open `/profile` in two tabs, save in tab 1, then save in tab 2 → tab 2 says the profile changed elsewhere, keeps its edits, and offers reload → AC-11
- [x] With no skills or no experience, open `/jobs` → a banner links to `/profile` → AC-9
- [x] With a filled profile and ingested jobs, open `/jobs` → 25 per page, scored first, best first, blocked after unblocked, unscored last, each with score, confidence and a blocker badge when blocked → AC-9
- [x] Open a job → score, confidence, "Why it matches" lines each with a verbatim posting quote and a "your skill/target role/…" pointer, missing requirements, unknown information with a reason, a score breakdown of all eight dimensions → AC-2, AC-10
- [ ] Click Rescore → "Rescore queued"; click again at once → "already running"; reload after a few seconds → fresh `Scored` time → AC-10

## Commands
- [x] `POST /internal/jobs/ingestions` for a Greenhouse board → every new job gets a `job_requirements` row (`Extracted`, model `fake`) and one `job_matches` row per profile with no manual step → AC-1, AC-13
- [x] Ingest the same board again with nothing changed → no new `JobContentChanged` rows in `app.outbox_messages`, `job_matches."RankedAt"` unchanged → AC-1, AC-7
- [x] `GET /internal/jobs/{jobId}/match?profileId=P` → `explanation.dimensions` lists all eight in order skills, title, experience, location, salary, education, jobType, workAuthorization; each Met/Partial item has `jobQuote` and `profileRef` → AC-2
- [x] Pick a match with Unknown dimensions → its score equals earned over known weight times 100, rounded half away from zero; confidence matches the known weight share (High ≥ 0.8, Medium ≥ 0.5) → AC-3
- [x] A job whose every dimension is Unknown (empty profile) → `score` null, confidence Low, page shows "not enough information" → AC-3
- [x] Required skills you lack appear under `missing` with their quotes → AC-4
- [x] Profile with `remotePreference` Remote and a posting stating on site work elsewhere, or "no visa sponsorship" while you need it outside an unauthorized country → `hasBlocker` true, score at most 20, a blocker item with evidence → AC-5
- [x] Hand edit a `job_requirements."Requirements"` quote to text absent from the description, rescore that profile → the item is under `unverified` and earns nothing → AC-6
- [x] `PUT` the match profile unchanged → every match is rescored by fingerprint and `RankedAt` stays the same for unchanged matches; change one skill → affected matches rewrite → AC-7
- [x] Point `Ai__Purposes__JobExtraction__Provider` at a provider with a bad key, ingest one job → after 3 attempts its row is `Failed` with a reason, the match is scored from location and remote only with confidence Low and `extractionFailed` true; Rescore with the Fake back → `Extracted` → AC-8
- [x] `GET /internal/matches?profileId=P&pageSize=101` → 400; `profileId` unknown → 404 → AC-9
- [x] `POST /internal/jobs/{jobId}/match/rescore` twice quickly → `{"queued":true}` then `{"queued":false}` → AC-10
- [x] `PUT /internal/profile/P/match-profile` without `If-Match` → 428; with `If-Match: "1"` → 412; bad currency or country → 400 field errors; good → 200 with a new `ETag` → AC-11
- [x] Inspect the extraction prompt (Fake client breakpoint or a logging provider) → only the marker, instructions and the description cut at 20000 characters, no profile field, no tools → AC-12
- [x] Run with no AI key at all → scores with quoted skill evidence appear → AC-13
- [x] Merge two scored jobs (retitle one into the other's key, run the reconcile) → one match per profile on the survivor, the removed job's `job_requirements` row gone, survivor rescored when its primary changed → AC-14
- [x] `GET /internal/jobs/{jobId}/match?profileId=<another profile>` → 404, never the founder's match → AC-15
- [x] Start the Api with `Matching__Weights__Skills=29` → startup fails naming the weight sum → AC-16
- [x] Set the threshold to 70 on a job scored 60, raise its fit to 75 → one `jobs.job-matched.v1` outbox row; rescore at 80 → none; a blocked job → none → AC-17

## Value sourcing
- [ ] Requirements come from `job_requirements."Requirements"`: delete a row and rescore → the job is unscored until extraction runs again
- [ ] Fallback reads `jobs."Location"`/`"RemoteType"` only: on a Failed job, `Location` `Berlin, Germany` scores location; `San Francisco, CA` is Unknown `Unparseable`; salary columns are never scored
- [ ] Years of experience: an open ended row counts to today's UTC date; overlapping rows are not double counted
- [ ] Degree level is the highest `education."DegreeLevel"`; all `None` → education Unknown `ProfileNotSet`
- [x] Preferences come from the new `profiles` columns; title compares `jobs."Title"` to `profiles."TargetRoles"` with seniority words removed
- [x] `JobMatched` reads the stored score inside the batch transaction; threshold is `profiles."StrongMatchThreshold"` and changing it rescores
- [x] Weights, `BlockerCap`, thresholds and `SkillAliases` come from `Matching` config; changing one rescores via the sweep on restart
- [ ] `ScoringVersion` bump rescores every profile via the sweep; `ExtractorVersion` bump re-extracts every job
- [x] Inputs fingerprint changes when any listed input changes (requirements stamp, location, remote type, profile, config, version, today only with an open ended row)
- [ ] Quotes verify against `jobs."Description"` cut at `MaxDescriptionChars`
- [x] Content hash is the primary link's latest `job_snapshots."ContentHash"`; model id is `fake` with the Fake provider
- [x] `profileIncomplete` is true with no skills or no experience; job URL is `jobs."Provenance_SourceUrl"`; ETag is `profiles.xmin`; the Web passes the session's profile id only

## Acceptance-criteria coverage
- AC-1 commands 1, 2 · AC-2 UI 6, command 3 · AC-3 commands 4, 5 · AC-4 command 6 · AC-5 command 7 · AC-6 command 8 · AC-7 commands 2, 9 · AC-8 command 10 · AC-9 UI 4, 5, command 11 · AC-10 UI 6, 7, command 12 · AC-11 UI 1 to 3, command 13 · AC-12 command 14 · AC-13 commands 1, 15 · AC-14 command 16 · AC-15 command 17 · AC-16 command 18 · AC-17 command 19
