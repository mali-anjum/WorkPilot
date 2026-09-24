# 0009. Resume management: rationale

## Context

Scope feature 14 asks for a base resume plus tailored or company specific versions, with the rule that a version used by an application can never be silently edited, and a visible version history. Spec 0002 already created `Resume` and `ResumeVersion` tables, but only as a skeleton: a version held a `StorageUrl` and nothing else, every version was declared immutable from birth, and nothing read or wrote them.

Three forces shaped the design. First, the rule that matters to the founder is about what an employer received, so the lock must hold even against a race (a draft edit read just before an application locks the version and saved just after). Second, features 15 to 18 (cover letters, application preparation, tracking, execution) all need the resume: 15/16 want text to feed an AI, 18 wants a real file to upload to an ATS form, 17 needs a single call to lock a version. Third, spec 0001 put resume files in self hosted Supabase Storage, but on this machine only the Postgres container is running (the `supabase/storage-api` image is not even pulled), and the parallel build brief forbids starting the shared stack.

## Options considered

### Option A: every save is a new immutable version

Spec 0002 AC-5 as written. Any change inserts a new row; "locking" only records which application used a version.

**Pros**: simplest invariant, full history of every save.
**Cons**: a tailoring session of ten small saves makes ten versions, burying the few that were actually sent; the "used" mark has no effect on editing, so the scope's "immutable once used" rule is trivially true rather than meaningful.

### Option B: the newest version is a draft until used, then locked (chosen)

The newest version can be edited in place until an application locks it; after that, an edit appends a new draft version. A Postgres trigger refuses any change to a locked row.

**Pros**: history shows the versions that matter; the lock is the single meaningful event, and it is enforced by the database, not just the entity.
**Cons**: drafts are not versioned; a trigger lives in hand written migration SQL.

### Option C: explicit "publish version" button

The user edits a working copy and explicitly publishes numbered versions; applications may only use published versions.

**Pros**: user controls version granularity.
**Cons**: an extra concept and an extra step the scope does not ask for; applications would still need a lock on top.

### Files: Supabase Storage vs Postgres vs local disk

- **Supabase Storage (REST API)**: the planned home per spec 0001, but not running here, so it cannot be built against a real service or verified; building it blind would ship untested infrastructure.
- **Postgres `bytea` behind an interface (chosen)**: works today, is transactional with the version row, backed up with the database, testable against the real database; costs database size.
- **Local disk volume**: simple, but a second thing to back up, and not reachable from the integration test host the same way.

## Rationale

Option B is the only option where the "used" event actually changes behavior, which is what the scope's done when line tests. The trigger closes the one gap the entity cannot: two requests interleaving across the lock. Postgres file storage behind `IResumeFileStore` is the honest choice given what is actually running: it keeps the feature fully verifiable now and makes the move to Supabase Storage a one class change once that container is up, which the spec records as a follow up rather than silently diverging from spec 0001.

## References

**Project sources**:
- spec 0001 (stack; Supabase Storage for files) · spec 0002 (`Resume`/`ResumeVersion`, AC-5) · spec 0004 (internal Api, `profile_id` claim) · `supabase/docker-compose.yml` (storage service defined, not running)
- `.agents/skills/ef-core/`, `.agents/skills/supabase-postgres-best-practices/`

**Practices & standards**:
- Append only history with an explicit lock event; database enforced invariants for anything a race could break.
