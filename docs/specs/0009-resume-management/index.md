# 0009. Resume management

**Date**: 2026-09-24
**Status**: In Progress

## Summary

This spec designs how your resumes live in WorkPilot: one or more base resumes, plus tailored resumes made for a specific company, each with its own numbered version history. The newest version of a resume stays an editable draft until an application uses it; from then on it is locked forever, and any later edit becomes a new version instead of quietly changing what an employer already received. The resume text is stored in the database and an optional attached file (the PDF you actually send) is stored behind a small file store interface, backed by Postgres for now because the self hosted Supabase Storage service is not running in this stack yet. A `/resumes` screen shows every resume, its full version history, and which versions are locked.

## Requirements

**User stories**:
- As the founder, I want to keep a base resume (text plus an optional PDF/DOCX file) in WorkPilot so that later features can pick it for an application.
- As the founder, I want to make a tailored copy of a resume for a specific company, so that I can adjust it without touching my base resume.
- As the founder, I want every version of a resume kept and visible, and a version an application used to never change, so that I always know exactly what an employer received.

**Acceptance criteria**:
- **AC-1**: Creating a resume (name, text content, optional note, optional file) creates a `Base` resume with version 1 as an unlocked draft; it appears in `GET /internal/resumes` and on `/resumes`.
- **AC-2**: Revising a resume whose newest version is an unlocked draft updates that draft in place (same version number, `UpdatedAt` moves); no new version row is created.
- **AC-3**: Locking a version (`ResumeVersion.Lock(applicationId, now)`, the domain operation feature 17 calls when an application uses it; exposed as `POST /internal/resumes/versions/{id}/lock`) sets `LockedAt` and `LockedByApplicationId`. Locking an already locked version is a no op that keeps the first application id.
- **AC-4**: Revising a resume whose newest version is locked creates version N+1 as a new draft (carrying over the file when no new file is given) and leaves the locked version's content, file, and timestamps byte for byte unchanged.
- **AC-5**: A locked version can never be edited: the domain entity throws when asked to, and a Postgres trigger rejects any `UPDATE` or `DELETE` of a `resume_versions` row whose `LockedAt` is set, so even a racing request or a raw SQL statement cannot change it.
- **AC-6**: Tailoring creates a separate `Tailored` resume (name, target company) whose version 1 is a draft copy of the chosen source version's content and file, and records `SourceVersionId`; the source resume and version are unchanged.
- **AC-7**: Version history is visible: `GET /internal/resumes/{id}` and `/resumes/{id}` list every version newest first with number, note, created and updated time, file name, and locked state (with the locking application id), and the file of any version can be downloaded.
- **AC-8**: Input is validated: name 1 to 200 chars, content up to 100,000 chars, note up to 500 chars, a version needs content or a file, files at most 5 MB and only `.pdf`, `.docx`, `.txt`, `.md`; a violation returns `400` and writes nothing.
- **AC-9**: Every read and write is scoped to the calling profile; a resume or version belonging to another profile (or not existing) returns `404`.
- **AC-10**: A revision identical to the newest version (same content, same note, no new file) is a no op: no new version, no draft update.

## Decision

**Chosen option**: Draft until used, then locked (Option B in [rationale.md](rationale.md)), with tailored resumes as separate `Resume` rows, resume text in Postgres, and resume files behind an `IResumeFileStore` interface implemented on Postgres for now.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Decisions made without the engineer (please review)

- **When a version becomes immutable**: pick: the newest version is an editable draft until an application locks it; runner up: every save is an immutable new version (spec 0002 AC-5 as written); why: the scope and brief say "immutable once used", and saving every keystroke session as a version would bury the versions that matter. This narrows spec 0002 AC-5 for `ResumeVersion` only (`CoverLetterVersion` is untouched).
- **Where resume files live**: pick: `IResumeFileStore` with a Postgres `bytea` implementation (`app.resume_files`); runner up: Supabase Storage over its REST API (spec 0001's plan); why: the Storage container is not running on this machine (only `workpilot-supabase-db` is up and the `supabase/storage-api` image is not even pulled), so it could not be built or verified against a real service; the interface keeps a later switch to Supabase Storage a one class change. This deviates from spec 0001's "never stored in the database" line; see Follow-up.
- **Tailored resume shape**: pick: a separate `Resume` row (`Kind = Tailored`, `TargetCompany`, `SourceVersionId`) with its own history; runner up: tailored versions inside the base resume's history; why: interleaving base edits and per company edits in one linear history makes "which is my current base" ambiguous.
- **Lock link to the application**: pick: `LockedByApplicationId` stored without a foreign key; runner up: an FK to `job_applications`; why: `job_applications.ResumeVersionId` is already the authoritative FK, and feature 17 may lock in the same unit of work that inserts the application; the column is an audit breadcrumb.
- **Database enforcement**: pick: a `BEFORE UPDATE OR DELETE` trigger on `resume_versions` that raises when `OLD."LockedAt"` is set; runner up: entity rules only; why: the entity alone cannot stop a lost update race (a draft edit read before a lock commits, saved after it).
- **Where the use cases live**: pick: `IResumeService` interface plus DTOs in `Application/Modules/Profile/Resumes`, implemented in `Infrastructure/Modules/Profile` over `WorkPilotDbContext`, with every rule in the domain entities; runner up: use case classes in Application behind repository interfaces; why: matches the existing `IProfileProvisioningService` precedent (spec 0004).
- **File types and size**: pick: `.pdf`, `.docx`, `.txt`, `.md`, at most 5 MB; runner up: PDF only; why: covers what ATS forms accept while keeping `bytea` rows small.
- **No text extraction from files**: pick: the editable text is typed or pasted, the file is stored as is (`ParsedContent` stays unused); runner up: extract PDF text with a library; why: parsing is not in the scope row and would add a dependency; features 15/16 can read `Content`.
- **Web API access**: pick: Blazor pages call the Api through a typed `ResumesApiClient` over the existing `"api"` HttpClient, and a Web minimal API endpoint proxies file downloads; runner up: pages calling Api from WASM; why: the Api is internal only (spec 0004), so only the Web server can reach it.
- **Nav placement**: pick: "Resumes" under Work, after Applications; runner up: under System/Settings; why: resumes are day to day job work, not configuration.

## Feature design

**Data model sketch** (changes to spec 0002's existing `Resume`/`ResumeVersion`, all in migration `AddResumeManagement`):

- `Resume` (`app.resumes`, soft delete, existing): FK `ProfileId`, `Name` (≤200). New: `Kind` (`Base` | `Tailored`, stored as text, default `Base`), `TargetCompany` (≤200, null for `Base`, required for `Tailored`), `SourceVersionId` (uuid, nullable, FK `resume_versions.Id`, `Restrict`), `CreatedAt`. Existing `IsActive` and `ParsedContent` are left untouched and unused.
- `ResumeVersion` (`app.resume_versions`, existing): FK `ResumeId`, `VersionNumber` (unique per resume, existing index). New: `Content` (text, not null, default `''`), `Note` (≤500, null), `UpdatedAt`, `LockedAt` (null), `LockedByApplicationId` (uuid, null, no FK), `FileName` (≤255, null), `FileContentType` (≤100, null), `FileSizeBytes` (bigint, null). Changed: `StorageUrl` becomes nullable; it holds the file store key (`db:<resume_files.Id>`) or null when the version has no file.
- `StoredResumeFile` (`app.resume_files`, new, Infrastructure only, not a domain entity): `Id`, `Data` (bytea), `ContentType`, `FileName`, `SizeBytes`, `Sha256` (hex), `CreatedAt`. Rows are write once and never deleted (a version that drops or replaces a draft file just stops pointing at it; two versions may share one file).
- Trigger `app.resume_versions_block_locked_changes()` on `resume_versions`, `BEFORE UPDATE OR DELETE FOR EACH ROW`: `IF OLD."LockedAt" IS NOT NULL THEN RAISE EXCEPTION ... USING ERRCODE = 'WP409'`.

**State transitions** (per version): `Draft` (LockedAt null) → `Locked` (LockedAt set). No way back. Per resume, only the newest version can be a `Draft`; every older version is `Locked` (versions are only appended when the newest is locked, or on creation).

**Domain operations** (`Domain/Modules/Profile`, no framework code):
- `Resume.CreateBase(profileId, name, content, note, file?, now)` → resume with v1 draft.
- `Resume.CreateTailored(profileId, name, targetCompany, source: ResumeVersion, note?, now)` → resume with v1 draft copying source content and file; `SourceVersionId = source.Id`. With no note, the service writes `Tailored for <company> from <source name> v<N>` (cut to 500 chars).
- `Resume.Revise(content, note, newFile?, removeFile, now)` → `ReviseOutcome` (`UpdatedDraft` | `CreatedVersion` | `Unchanged`) plus the affected version.
- `ResumeVersion.Lock(applicationId, now)`; `ResumeVersion.IsLocked`.
- `ResumeFileRef` value object (key, file name, content type, size); `ResumeRules` holds the limits for AC-8 and a `ValidationException`-style `ResumeValidationException`; editing a locked version throws `ResumeVersionLockedException`.

**API surface** (internal Api, no auth beyond the network boundary per spec 0004; every call carries `profileId`):

| Endpoint | Method | Key inputs | Key outputs | Key errors |
|---|---|---|---|---|
| `/internal/resumes?profileId=` | GET | profileId | `ResumeSummaryDto[]` (id, name, kind, targetCompany, latestVersionNumber, latestIsLocked, versionCount, updatedAt) | 400 missing profileId |
| `/internal/resumes/{id}?profileId=` | GET | id, profileId | `ResumeDetailDto` (resume fields, sourceVersionId, versions newest first) | 404 |
| `/internal/resumes` | POST multipart | profileId, name, content, note?, file? | 201 `ResumeDetailDto` | 400, 404 profile |
| `/internal/resumes/tailored` | POST JSON | profileId, sourceVersionId, name, targetCompany, note? | 201 `ResumeDetailDto` | 400, 404 |
| `/internal/resumes/{id}/revisions` | POST multipart | profileId, content, note?, file?, removeFile? | 200 `ReviseResumeResultDto` (outcome, versionNumber, detail) | 400, 404 |
| `/internal/resumes/versions/{versionId}/lock` | POST JSON | profileId, applicationId | 200 `ResumeVersionDto` | 400 empty applicationId, 404 |
| `/internal/resumes/versions/{versionId}/file?profileId=` | GET | versionId, profileId | file bytes, content type, file name | 404 no version or no file |

Endpoints live in `src/WorkPilot.Api/Endpoints/ResumeEndpoints.cs` (`MapResumeEndpoints()`), DI in `AddResumeManagement()`; form endpoints call `.DisableAntiforgery()` (the Api has no antiforgery middleware, same as its JSON endpoints). A `DbUpdateException` whose inner `PostgresException.SqlState` is `WP409` maps to `409` (the lost race of AC-5).

**Web**: `/resumes` (list, create form with `InputFile`, empty state) and `/resumes/{id}` (header with kind and target company, edit form for the newest version, "Tailor for a company" form, version history list with Draft/Locked badges and download links). `GET /resumes/versions/{id}/file` on the Web host (requires the session cookie) proxies to the Api with the signed in profile id. Nav: one `NavItem("Resumes", "/resumes")` line.

**Value sourcing**:

| Action | Value | Source |
|---|---|---|
| any | `profileId` | Api: request input; Web: the `profile_id` claim of the session cookie (spec 0004) |
| create | `VersionNumber` 1, `Kind` Base, `CreatedAt`/`UpdatedAt` | fixed; `TimeProvider.System` UTC now |
| revise | new `VersionNumber` | newest version number + 1 (unique index guards a race) |
| revise | carried file when no new file | newest version's `StorageUrl`/file fields; cleared when `removeFile` |
| tailor | v1 content and file | the source version (must belong to the same profile) |
| lock | `LockedAt`, `LockedByApplicationId` | now; the `applicationId` input (feature 17 passes its `JobApplication.Id`) |
| file upload | `StorageUrl`, file metadata | `IResumeFileStore.SaveAsync` key; the uploaded file's name, content type (by extension), length |
| list | `latestVersionNumber`, `latestIsLocked`, `updatedAt` | newest version row |

**Key invariants**: only the newest version may be unlocked; a locked version's row never changes (entity + trigger); version numbers are contiguous from 1 and unique per resume; a version always has content or a file; stored file rows are never modified or deleted.

**Security model**: single user; the Api is internal only (spec 0004), the Web host is gated by the session cookie. Ownership is checked in the service on every call (AC-9). Files are served with `Content-Disposition: attachment` and the stored content type, never rendered inline.

**Configuration required**: none.

**Critical test scenarios**:
- Happy path: create → revise draft in place → lock → revise creates v2 → history shows v2 draft and v1 locked, verifies AC-1, AC-2, AC-3, AC-4, AC-7.
- Failure: editing a locked version via the entity throws; a raw `UPDATE` on a locked row fails with `WP409`, verifies AC-5.
- Failure: oversized or `.exe` file and empty content with no file return 400 and write nothing, verifies AC-8.
- Auth: another profile's resume id returns 404 on read, revise, lock, and file download, verifies AC-9.

## Build plan

Tracer Bullet: one thin thread (create, list, detail) through every layer first, then thicken with revise, lock, tailor, files, and the UI.

1. [x] Domain: extend `Resume`/`ResumeVersion` with the new fields, `CreateBase`, `Revise`, `Lock`, `CreateTailored`, the exceptions and rules, satisfies **AC-2**, **AC-3**, **AC-4**, **AC-5**, **AC-6**, **AC-8**, **AC-10**
2. [x] Infrastructure: entity configurations, `StoredResumeFile` + `PostgresResumeFileStore`, the single `AddResumeManagement` migration (columns, `resume_files`, trigger), applied and confirmed live, satisfies **AC-5**, **AC-7**
3. [x] Application + Infrastructure: `IResumeService`, DTOs, `IResumeFileStore`, `ResumeService` with profile scoping and the `WP409` mapping, satisfies **AC-1** to **AC-10**
4. [x] Api: `ResumeEndpoints` + `AddResumeManagement()` wired in `Program.cs`, satisfies **AC-1**, **AC-3**, **AC-4**, **AC-6**, **AC-7**, **AC-8**, **AC-9**
5. [x] Web: `ResumesApiClient`, `/resumes` and `/resumes/{id}` pages, file download proxy endpoint, nav line, satisfies **AC-1**, **AC-6**, **AC-7**

## Consequences

**Positive**:
- Feature 17 gets one call (`version.Lock(applicationId, now)`) and the database guarantees the rest.
- Features 15/16 can read resume text straight from `resume_versions.Content`.

**Negative / tradeoffs**:
- Files in Postgres grow the database and its backups; acceptable at single user scale (a few dozen files under 5 MB), but it is a deviation from spec 0001 until Supabase Storage is wired.
- The trigger is hand written SQL inside the migration; regenerating the migration on a rebase must keep it (see Follow-up).
- A draft edit is not versioned, so unsaved history between drafts is lost by design.

**Neutral**:
- `Resume.IsActive` and `ResumeVersion.ParsedContent` from spec 0002 stay unused.

## Follow-up

- [ ] Wire Supabase Storage: once the `storage` container runs, add a `SupabaseStorageResumeFileStore` (REST, service role key) behind `IResumeFileStore`, move existing `resume_files` rows, and decide whether to drop the Postgres store (spec 0001 says files never live in the database).
- [ ] Feature 17: call `ResumeVersion.Lock(application.Id, now)` in the same `SaveChanges` that creates/submits the `JobApplication`, and treat `409` from a concurrent draft edit as expected.
- [ ] If the migration is regenerated on rebase, re add the `migrationBuilder.Sql` trigger block by hand.
- [ ] API error handling pattern (AGENTS.md, undecided): these endpoints use `Results.ValidationProblem`/`NotFound`/`Conflict` inline; fold them into the project pattern when it is chosen.
- [ ] Deleting/archiving a resume and renaming it are not built; add when needed (soft delete already exists on `Resume`).
- [ ] Spec 0002 AC-5 should note that `ResumeVersion` follows this spec's draft until locked rule (for `/sync`).

## Rationale

See [rationale.md](rationale.md).
