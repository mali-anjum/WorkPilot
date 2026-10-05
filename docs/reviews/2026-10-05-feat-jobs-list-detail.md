# Review, feat/jobs-list-detail, 2026-10-05

**Reviewed by**: Claude Sonnet 5.5 (author on an earlier Claude session)
**Scope**: 41 files, branch vs main
**Verdict**: Approve with nits

## Summary
Spec 0021 is implemented end to end: the matches query is widened with filters, sorts and dismissals as SQL predicates, plus facets, dismissal and sources endpoints, a two column detail page and a sources drawer. Layering, Result/ProblemDetails, module ownership and the JSDisconnectedException rule are respected, and the merge path moves dismissals. No blockers or majors; the findings are small scale and layering points.

## Minor
### 🟡 Posted index does not match the Newest sort expression, `src/WorkPilot.Infrastructure/Persistence/Configurations/JobsConfigurations.cs:41`
**Problem**: `IX_jobs_PostedAt_listed` indexes `PostedAt`, but both the sort and the posted-within filter use `COALESCE(PostedAt, Provenance.RetrievedAt)`.
**Why it matters**: Postgres cannot use the index for that ordering, so the index costs writes and does nothing for reads.
**Suggested fix**: Index the coalesced expression (hand written SQL) or drop the index until scale needs it; note the choice in the spec.

### 🟡 Endpoint talks to the DbContext for Run now, `src/WorkPilot.Api/Endpoints/JobsEndpoints.cs:94`
**Problem**: `RunSourceAsync` checks `db.JobSources.AnyAsync` in the endpoint instead of going through a use case or port.
**Why it matters**: Conflicts with the thin endpoint / `Result<T>` + `ToHttp` pattern and returns a hand built 404 (same pattern exists in `ListJobsAsync`, so it spreads).
**Suggested fix**: Move the existence check and enqueue into an Application service returning `Result<T>`.

### 🟡 Unbounded facets lists, `src/WorkPilot.Infrastructure/Modules/Jobs/JobCatalogQueries.cs:13`
**Problem**: `GetFacetsAsync` returns every distinct company with no cap, and runs on each `/jobs` load.
**Why it matters**: A large catalog makes a big payload and a huge dropdown; the spec accepts thousands of jobs only.
**Suggested fix**: Cap or search-as-you-type the company list when the catalog grows; note it as a follow-up.

### 🟡 Dismiss can 500 if the job is hard deleted concurrently, `src/WorkPilot.Infrastructure/Modules/Jobs/JobCatalogQueries.cs:~148`
**Problem**: `JobExistsAsync` then the raw INSERT is not atomic; a merge removing the job between them violates the FK and surfaces as an unhandled 500 instead of a 404.
**Why it matters**: Rare, but the spec's 404 contract and the single error pattern are bypassed.
**Suggested fix**: Catch the FK violation (SQLSTATE 23503) in the repository and return not found.

### 🟡 Salary filter ignores currency, `src/WorkPilot.Infrastructure/Modules/Jobs/Matching/MatchQueries.cs` (Filter, SalaryMin)
**Problem**: `SalaryMin` compares raw numbers across jobs regardless of currency or pay period.
**Why it matters**: A 90,000 GBP and a 90,000 INR job both pass; the UI shows no currency either.
**Suggested fix**: Record the limitation in the spec's Consequences, or filter on currency when it is available.

## Nits
- ⚪ `src/WorkPilot.Api/Endpoints/JobsEndpoints.cs:100`, `GetFacetsAsync` requires `profileId` but never uses it.
- ⚪ `src/WorkPilot.Application/Modules/Jobs/Matching/JobSearchValidation.cs:46`, `q`, `company` and `location` have no length cap.
- ⚪ `src/WorkPilot.Web/Features/Jobs/JobsApiClient.cs:82`, a dismiss 404 for an unknown profile falls back to the "That job no longer exists" text.

## Strengths
- The search is one SQL query with predicates and escaped ILIKE wildcards, a deterministic sort ending in id, and validation that returns per field ProblemDetails; `ApiResultReader` now carries field errors cleanly.
- Dismissal is idempotent via `ON CONFLICT DO NOTHING`, merges move (and de-duplicate) dismissals, and Web actions cancel stale loads and leave the row unchanged on failure.

## Test coverage
Good: Api integration tests on real Postgres cover paging, both sorts, each filter, validation 400s, facets, dismiss/undo, merge, soft deleted detail and sources/run-now; Domain tests cover the dismissal invariant; bUnit tests cover the pages, drawer and display rules. Untested branch: the merge case where both jobs were already dismissed by the same profile (the delete-then-move path in `ReassignDismissalsAsync`).
