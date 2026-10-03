# 0021. Jobs list and job detail: rationale

The decision record behind [the build spec](index.md).

## Context

Spec 0008 and spec 0017 fill a shared job catalog, and spec 0019 scores each job per profile. The current `/jobs` page is an empty placeholder, and the only list endpoint (`GET /internal/jobs?jobSourceId=`) lists one source at a time with no score, no filter and no paging beyond `take`. Adding a board is possible only with a hand written `POST /internal/jobs/ingestions`, so in normal use the catalog would stay empty.

The scope lists eleven filters. Only some map to stored data: title, company, location, remote type, salary (often empty), source, match score and posted date exist; experience level, job type and visa sponsorship are not `jobs` columns; spec 0019 extracts them into the `job_requirements.Requirements` document per job, and filtering on them is a follow up. Filtering on raw description text would mean scanning descriptions per request.

Jobs are shared (spec 0017), scores and dismissals are per profile. The catalog can reach thousands of rows from a handful of boards, so the list must page and sort in the database. Every page is InteractiveServer (spec 0016) and reads the Api through a typed client, with failures as ProblemDetails read by `ApiResultReader` (spec 0018).

## Options considered

### Option 1: Filter on stored columns only (chosen)

Ship the filters the data supports; add the rest when a column exists.

**Pros**: every filter is a plain indexed or cheap predicate; no change to ingestion; nothing guesses.
**Cons**: experience level, job type and visa filters from the scope wait for later.

### Option 2: Derive more columns during ingestion

Extract job type, seniority and visa wording into `Job` columns at ingestion so every scoped filter works now.

**Pros**: all eleven scoped filters on day one.
**Cons**: touches ingestion and dedup (spec 0017 hashes and snapshots), duplicates spec 0019's phrase rules in a second place, and makes this feature much larger.

## Rationale

The data only supports some of the scoped filters, and a filter that silently matches nothing (job type on a catalog that never stores it) is worse than no filter. Keeping the list on stored columns keeps it fast and honest, and the match score already carries seniority and visa as evidence on the detail page. Extending spec 0019's `GET /internal/matches` (as its follow up asks) keeps one list query instead of two that could disagree on ordering, and leaves spec 0008's `GET /internal/jobs` contract intact. Offset paging with page numbers fits a single user browsing a few thousand rows and makes every view linkable; keyset paging would only pay off at far larger volumes. The sources drawer is small and reuses the existing ingestion use case, and without it the list would be empty in normal use.
