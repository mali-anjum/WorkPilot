# Review, feat/job-matching-engine, 2026-10-03

**Reviewed by**: Sonnet 5.5 (author model not stated)
**Scope**: 64 files, branch vs main (merge base a6d7310)
**Verdict**: Approve with nits

## Summary
Implements spec 0019: AI-extracted, quote-verified job requirements, a pure deterministic Domain scorer, a fingerprint-guarded raw SQL upsert, events and Hangfire jobs for extraction and rescoring, the match profile with an xmin ETag, and the `/jobs`, `/jobs/{id}` and `/profile` pages. The design follows the spec and AGENTS.md closely (layering, module ownership, outbox events, `Result<T>`/`ToHttp`, startup validation). I found no blockers or majors. The main finding is a hole in AC-1 event publishing for same-run duplicates, which the hourly sweep papers over, plus a handful of validation, UX and polish items.

## Minor
### 🟡 New job joined by a second posting in the same ingestion run never raises JobContentChanged, `src/WorkPilot.Application/Modules/Jobs/JobIngestionService.cs:230`
**Problem**: When two new postings in one run share a dedup key, the second takes the `createdThisRun` job as `existing` and calls `content.Stamp([existing])`. The job was never stamped before creation, so this stamps it as if it pre-existed, with its current (post-create) content. `AttachLink` normally leaves the primary unchanged (same source confidence), so `PublishChanges` sees before == now and publishes nothing for that job.
**Why it matters**: AC-1 says a created job is extracted and scored with no manual action. This job gets no event; it is only picked up by the hourly `MatchSweepJob` (no requirements row), so scoring and any `JobMatched` alert are delayed up to an hour. The sweep makes it self-healing, hence Minor, but the event path silently fails for this case and no test covers it.
**Suggested fix**: Do not stamp jobs created in this unit (skip `Stamp` when `existing` came from `createdThisRun`), or track new jobs in `JobContentEvents` explicitly. Add an ingestion test with two same-key postings in one run asserting one `JobContentChanged` for the new job.

### 🟡 Extraction failure handling is type-based and can mask the real error, `src/WorkPilot.Application/Modules/Jobs/Matching/JobMatchingService.cs:83`
**Problem**: The catch filters on `ex is not OperationCanceledException` rather than on the token, and the `try` also wraps the `SaveChangesAsync` that persists `MarkExtracted`. If that save throws (DB error), the status is already Extracted in memory, so `RecordFailure` throws `InvalidOperationException("... not Pending")`, hiding the original exception. A provider timeout surfacing as an `OperationCanceledException` would also bypass the attempt counter (today `ProviderErrorChatClient` translates those into `AiProviderException`, so it is only a defensive gap).
**Why it matters**: Misleading failure reason in Hangfire and a row that can stay Pending without counting an attempt if another provider path ever leaks a cancellation.
**Suggested fix**: Filter with `when (!cancellationToken.IsCancellationRequested)`, and keep only the extractor call inside the try so persistence errors propagate as themselves.

### 🟡 Match profile validation misses DB limits, `src/WorkPilot.Domain/Modules/Profile/MatchProfile.cs:147`
**Problem**: `MinSalary` is only checked for negatives; the column is `numeric(12,2)`, so a value of 10 billion or more overflows at save and returns a 500 instead of a field error. `Experience.Description` has no length bound, and a JSON `null` element inside `experiences`/`educations` lists would throw in `ToDraft`.
**Why it matters**: AC-11 promises ProblemDetails field errors for invalid input; these inputs produce unhandled 500s (internal Api, single user, so low blast radius).
**Suggested fix**: Add an upper bound for `MinSalary`, a max length for the experience description, and skip or reject null rows with a field error.

### 🟡 A failed Rescore replaces the whole match panel with an error, `src/WorkPilot.Web/Components/Pages/JobDetail.razor:205`
**Problem**: `RescoreAsync` writes failures into `_error`, and the page renders the error-only branch whenever `_error` is set, hiding the loaded match. `OnParametersSetAsync` also returns early without clearing `_match`/`_error` when the session has no profile or when `JobId` changes.
**Why it matters**: A transient rescore failure makes a good, already loaded panel disappear; stale match data can show under a new job id.
**Suggested fix**: Keep a separate `_actionError` shown beside the panel, and reset `_match`, `_error` and `_notice` at the top of `OnParametersSetAsync`.

### 🟡 Test gaps, `tests/WorkPilot.Api.Tests/JobMatchingTests.cs`
**Problem**: No ingestion-level test for the same-run duplicate case above, and `Bad_matching_config_fails_options_validation` validates the options object rather than proving the host fails at startup (AC-16).
**Why it matters**: The first gap hides the event bug; the second leaves the `ValidateOnStart` wiring unverified.
**Suggested fix**: Add the ingestion test; add a `WebApplicationFactory` case with weights summing to 99 that expects startup to throw.

## Nits
- ⚪ `src/WorkPilot.Infrastructure/Modules/Jobs/Matching/MatchRepository.cs:176`, a job with no snapshot has `c.hash` NULL while the row stores `""`, so `IS DISTINCT FROM` re-enqueues it every hour; use `COALESCE(c.hash, '')`.
- ⚪ `src/WorkPilot.Domain/Modules/Jobs/Matching/MatchInputs.cs:108`, `settings.Canonical()` (and alias normalization in the scorer `Context`) is recomputed per job; compute once per batch.
- ⚪ `src/WorkPilot.Domain/Modules/Jobs/Matching/MatchScorer.cs:436`, a job place whose country is unrecognized is dropped from `countries`, so work authorization can read Met on the recognized places alone; consider Unknown when any place has no resolvable country.
- ⚪ `src/WorkPilot.AI/Matching/ChatClientJobRequirementExtractor.cs:55`, a description containing `DESCRIPTION>>>` can close the data block early; quote verification bounds the impact, but escaping or a random delimiter is cheap.
- ⚪ `src/WorkPilot.Web/Components/Pages/JobDetail.razor:32`, `JobUrl` goes into `href` without a scheme check (http/https only); the data comes from an external feed.
- ⚪ `src/WorkPilot.Web/Components/Pages/Jobs.razor:117`, the success band hardcodes 70 instead of the profile's `StrongMatchThreshold`.
- ⚪ `src/WorkPilot.Infrastructure/Modules/Profile/MatchProfileService.cs:110`, new `skills` rows use `gen_random_uuid()` (v4) while the rest of the schema uses v7; also skills display lower cased and version stripped after a save (by spec, but surprising: "Python" comes back as "python").
- ⚪ `src/WorkPilot.Application/Modules/Jobs/Matching/JobMatchingService.cs:314`, `EnqueueRequirementExtraction.HandleAsync`, `EnqueueProfileRescore.HandleAsync` and `MatchingJson.Serialize/Deserialize` lack the XML docs AGENTS.md asks for.

## Strengths
- The scorer is pure and fully traceable: every point maps to a verified quote and a profile pointer, rounding, renormalization, confidence bands and the blocker cap match AC-3/AC-5 exactly.
- Idempotency is done right: canonical, length-prefixed fingerprint plus an `ON CONFLICT ... WHERE fingerprint IS DISTINCT FROM` upsert, and the previous score is read before the write so `JobMatched` fires only on a real crossing, inside the same transaction.
- `JobRequirement` state machine keeps the attempt counter across Hangfire retries (`MarkPending` is a no-op for same-content Pending), so the three-attempt rule and the Failed fallback actually work.
- Conventions respected: module ownership, outbox events with versioned names, `Result<T>`/`ToHttp` with new 412/428 statuses, startup validation of `Matching`, no profile data in the prompt (tested), AI default stays Fake.
- Migration hand edits (defaults, clearing the writerless table) are documented and safe; profile reads are scoped by `ProfileId` (AC-15).

## Test coverage
Strong: Domain tests (56 cases) cover the scorer table, fingerprint, normalization, rounding and confidence boundaries; Api integration tests against real Postgres cover events, extraction retries and fallback, idempotent and parallel upserts, threshold crossing (theory plus end to end), merge and split, the sweep, ETag/validation, list ordering/paging and cross-profile 404; Web tests cover the pages and clients. Untested: the same-run duplicate ingestion case, host-level startup failure for bad `Matching` config, and the JobDetail failure/navigation paths noted above.

## Resolution (2026-10-03, same branch)
- Fixed: a job joined in the run that created it now raises `JobContentChanged` (`JobIngestionService` no longer stamps jobs created in the unit); test `Two_same_key_postings_in_one_run_raise_one_content_changed_for_their_new_job`.
- Fixed: only the model call counts as a failed extraction attempt, filtered on the token; a failed save propagates as itself (`JobMatchingService.ExtractAsync`).
- Fixed: `MinSalary` above `numeric(12,2)`, an experience description over 4000 characters and null list entries are field errors (400), not 500s; tests in Domain and Api.
- Fixed: a failed Rescore shows beside the match panel; changing job resets the page state; only http and https posting URLs become links; tests in Web.
- Fixed nits: sweep compares a missing snapshot hash as empty (no hourly re-enqueue), new skills get v7 ids (generated in C#, Postgres 17 has no `uuidv7()`), XML docs on the handlers and `MatchingJson`.
- Left as is, by spec or low value now: per batch reuse of `settings.Canonical()`, unknown country places in work authorization, the prompt delimiter (quote verification bounds the impact), the hardcoded 70 colour band on `/jobs`, lower cased skill display (spec: stored by normalized name), and a host level startup test for AC-16 (a second WebApplicationFactory risks the Hangfire static logger issue documented in `SharedApiFactory`; startup failure was verified live).
