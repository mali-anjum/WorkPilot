# Review, feat/resume-management, 2026-09-26

**Reviewed by**: Claude Sonnet 5 (author on Claude Opus 5.5)
**Scope**: 33 files, branch vs main
**Verdict**: Approve with nits

## Summary

Spec 0009 (resume management) is implemented end to end: domain rules for draft-until-locked versioning, a Postgres trigger that backstops the immutability invariant against raw SQL and lost races, profile-scoped Api endpoints, a Web proxy for file downloads, and pages for create/revise/tailor. The domain model, the trigger, and profile scoping are all correct and well tested (Domain 139, Api 160, Web 133 passing, including a live raw-SQL trigger test and a genuine concurrent lost-race test). The accompanying render-mode fix (spec 0016, `InteractiveServer` app-wide) is minimal, correctly scoped, and matches its own spec. The main gaps are non-functional: multipart file-size validation happens only after ASP.NET's own form buffering (no explicit cap enforced earlier), and the Web download proxy — the one place that stitches a cookie-derived profile id into an Api call — has no automated test at all, only the live verify run.

## Major

### 🟠 Multipart file size is validated only after the framework buffers it, `src/WorkPilot.Api/Endpoints/ResumeEndpoints.cs:31` and `src/WorkPilot.Infrastructure/Modules/Profile/PostgresResumeFileStore.cs:33`
**Problem**: `ResumeRules.ValidateFile` (5 MB cap) runs only after `request.ReadFormAsync(ct)` has already parsed and buffered the whole multipart body (ASP.NET's default `FormOptions.MultipartBodyLengthLimit` is 128 MB, and Kestrel's default `MaxRequestBodySize` is ~28.6 MB — neither is overridden anywhere in `Program.cs` or on this endpoint). A request can push tens of MB into memory before the 5 MB check ever runs and rejects it with 400.
**Why it matters**: This is the one place in the feature where "validate before buffering" (called out explicitly in spec's AC-8 intent) isn't actually true at the transport layer — only at the app layer, after the framework has already done the expensive part. On an internal-only, single-user Api the blast radius is small, but it's a real gap between the stated design ("enforced before buffering") and what the code does, and it's the kind of gap that gets worse silently if this Api is ever reachable from more than the Web host.
**Suggested fix**: Cap the endpoint explicitly — e.g. `RequestSizeLimitAttribute`/`IHttpMaxRequestBodySizeFeature` or `FormOptions.MultipartBodyLengthLimit` set to something close to `ResumeRules.FileMaxBytes` plus headroom for the other form fields — so oversized uploads are rejected before the body is read, not after.

### 🟠 The Web file-download proxy has zero automated test coverage, `src/WorkPilot.Web/Features/Resumes/ResumeWebExtensions.cs:17`
**Problem**: `MapResumeFileEndpoints` (the endpoint that reads the `profile_id` claim off the session cookie and forwards it to the internal Api) has no unit or integration test anywhere in `tests/`. It's covered only by the manual live verify run in `docs/specs/0009-resume-management/verify.md`. `ResumeWebExtensions.ProfileId(ClaimsPrincipal)`, the piece that turns a claim into the profile id that scopes the whole download, is likewise untested in isolation.
**Why it matters**: This is exactly the code the review's security focus calls out — "profile scoping on every path including the Web file download proxy" — and it's the one path with no regression safety net. A future change to claim handling, routing, or the `Forbid`/`NotFound` branches here would not be caught by `dotnet test`; test signal is `configured`, so per the review guide untested security-relevant branching logic is at least a Major.
**Suggested fix**: Add a `WebApplicationFactory`-style or minimal-API test hitting `/resumes/versions/{id}/file` with (a) no auth → redirect/401, (b) a mismatched profile id → 404, (c) a matching profile id → 200 with the proxied bytes/headers.

## Minor

### 🟡 `ResumeRules` (a Domain static class) is referenced directly from Razor pages, `src/WorkPilot.Web/Components/Pages/Resumes.razor:2-3` and `ResumeDetail.razor:2-3`
**Problem**: The pages `@using WorkPilot.Domain.Modules.Profile` and read `ResumeRules.NameMaxLength`, `ContentMaxLength`, `NoteMaxLength`, `TargetCompanyMaxLength`, `AllowedFileExtensions`, `FileMaxBytes` directly for `maxlength`/`accept` attributes, and `ResumeFileReader` does the same for `FileMaxBytes`. AGENTS.md says "no domain entities leak into the presentation layer; cross boundary communication uses DTOs." No `Resume`/`ResumeVersion` entity itself leaks — all data still flows as `ResumeDetailDto`/`ResumeVersionDto`/`ResumeSummaryDto` — but this is a Web→Domain type reference that bypasses Application, which is new for this feature (Web already transitively references Domain via its existing Infrastructure/Application references, so it compiles, but no earlier page reaches this far in).
**Why it matters**: Defensible as DRY (one source of truth for the limits shown in the UI and enforced server-side), but it's a small crack in the layering that the next feature might widen without noticing, since there's no DTO carrying these limits across the boundary.
**Suggested fix**: Either accept this as a deliberate, narrow exception (worth a one-line note in the spec's Decisions) or expose the limits through a small Application-level constants/DTO type instead of importing Domain directly into Razor.

### 🟡 `LockVersionAsync`'s lost-race branch is not exercised by any test, `src/WorkPilot.Infrastructure/Modules/Profile/ResumeService.cs:182-193`
**Problem**: The `catch (DbUpdateException ex) when (IsConflict(ex))` branch inside `LockVersionAsync` only fires when two lock calls genuinely race at the database (both read unlocked, both try to set `LockedAt`). The existing `Lock_SetsTheApplication_AndLockingAgainKeepsTheFirstOne` test calls lock twice *sequentially*, so by the second call `version.Lock(...)` already returns `false` from the in-memory check and `SaveChangesAsync` is never reached — the WP409 catch path is dead code from the test suite's perspective. The equivalent mechanism is proven for revise-vs-lock (`Revise_ThatLosesARaceToALock_Returns409...`), just not for lock-vs-lock.
**Why it matters**: Minor because the trigger mechanism itself is already proven correct by the sibling test; this is a narrow gap in coverage of one specific catch clause, not a correctness question.
**Suggested fix**: A test that opens a second connection/transaction, locks the version there without committing, then calls `LockVersionAsync` through the service and asserts it still returns the first application's id after the other transaction commits — mirroring the existing lost-race revise test's structure.

### 🟡 Create/revise timestamps differ from a later read by less than a microsecond, per spec's own note
**Problem**: The in-memory `DateTimeOffset` returned by create/revise carries .NET's 100 ns tick precision; Postgres `timestamp with time zone` stores microseconds, so a subsequent `GET` returns a value truncated to microseconds. Flagged by the task as known.
**Why it matters**: No functional impact — display uses `"g"` formatting, ordering and equality checks in the app never need sub-microsecond precision, and the tests already compare at microsecond precision. Recorded here only because it was explicitly called out for judgment; it does not need a fix.
**Suggested fix**: None needed; optionally round in `TimeProvider` usage if a future feature ever needs exact byte-for-byte round-tripping of timestamps.

## Nits

- ⚪ `src/WorkPilot.Infrastructure/Modules/Profile/ResumeService.cs:239-240`, `IsConflict` also matches `PostgresErrorCodes.UniqueViolation`, which `LockVersionAsync` can't actually trigger (no inserts happen there) — harmless, but the shared helper is slightly wider than either call site needs.
- ⚪ `src/WorkPilot.Infrastructure/Modules/Profile/ResumeService.cs:98-106`, the auto-generated tailor note (`$"Tailored for {...} from {...} v{...}"`) isn't in spec 0009's API surface table for `/internal/resumes/tailored` (table lists only `profileId, sourceVersionId, name, targetCompany`) — a nice touch, but worth a one-line mention in the spec so it doesn't look like drift later.
- ⚪ `docs/specs/0009-resume-management/index.md:62`, `Resume.CreateTailored`'s signature in the spec's "Domain operations" section omits the `note` parameter the shipped code has.

## Strengths

- The trigger design (`resume_versions_block_locked_changes`) is genuinely solid: it blocks `UPDATE`, `DELETE`, and cascade deletes from a parent `Resume` alike, and the test suite proves this with real raw SQL and a real concurrent transaction (`RawSql_CannotUpdateOrDeleteALockedVersion`, `Revise_ThatLosesARaceToALock_Returns409...`) rather than just asserting the entity throws.
- File handling gets the security basics right end to end: content type is derived from the extension never the client's claim (`ResumeRules.ValidateFile`), file names are normalized with `Path.GetFileName` before storage and before validation (no path traversal), and downloads are always served as `Content-Disposition: attachment` — so even a file that lies about its extension can't execute inline in a browser.
- Profile scoping is applied consistently and is exhaustively tested in one place (`EveryOperationOnAnotherProfilesResume_Returns404` hits get/revise/lock/download/tailor/list in a single test against a real Postgres).
- The render-mode fix (spec 0016) is exactly as small as it should be — one attribute change plus a defensive `JSDisconnectedException` catch — and was re-verified live against the actual regression it fixed, not just asserted in isolation.

## Test coverage

Domain: thorough (create/revise/lock/tailor, every AC-8 boundary, file validation, path traversal in file names). Api: thorough, including the trigger and a real concurrency race, and a single test that walks every endpoint's 404 scoping. Web: covers the pages' rendering and API-client error mapping well. Gaps: the Web file-download proxy endpoint (`MapResumeFileEndpoints`/`ResumeWebExtensions.ProfileId`) has no automated test (Major, above); `LockVersionAsync`'s own lost-race catch branch is untested even though the underlying mechanism is proven elsewhere (Minor, above).

## Resolution (2026-09-26)
- Major, fixed: `POST /internal/resumes` and `POST /internal/resumes/{id}/revisions` now cap the request at `ResumeEndpoints.MaxUploadRequestBytes` (the 5 MB file plus 1 MB for the text fields) with a request size limit and a multipart body length limit, so an oversized upload is refused with `413` before it is buffered. Test: `Create_AndRevise_WithABodyOverTheUploadLimit_Return413AndWriteNothing`.
- Major, fixed: `ResumeFileEndpointTests` covers the Web download proxy: owner gets the file as an attachment, another profile `404`, signed out `401`, and a missing, malformed, or empty `profile_id` claim `403` without calling the Api. The empty claim case found a small gap: `ResumeWebExtensions.ProfileId` accepted `Guid.Empty`; it now rejects it, like the Approval center.
- Minor, fixed: `Lock_ThatLosesARaceToAnotherLock_KeepsTheFirstApplication` exercises `LockVersionAsync`'s WP409 catch.
- Minor, accepted: the pages read `ResumeRules` limits from Domain for `maxlength`/`accept`. It is constants only (no entity crosses), and it keeps the UI limits identical to the enforced ones. Revisit if a second feature needs the same, by moving the limits to an Application level type.
- Minor, no change: the sub microsecond timestamp difference between a write response and a later read.
- Nits: spec 0009 now lists the `note` parameter of `CreateTailored` and the tailor endpoint, and the default tailor note. `IsConflict` matching unique violations is kept (it also guards the version number unique index on revise).
