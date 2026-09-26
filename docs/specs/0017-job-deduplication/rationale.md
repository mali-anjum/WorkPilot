# 0017. Job deduplication: rationale

## Context

Spec 0008 made ingestion idempotent (running it again changes nothing new) per source. A job's identity is `(JobSourceId, ExternalId)`, enforced by a unique index, and a job's snapshots already carry their own source pair so that one job could one day gather several. There is one real source (Greenhouse). The forces:
- **The scope's promise for feature 10:** the same posting from the same or a different source is one canonical job, and it is never applied to twice. Applications (feature 17) and matches (feature 11) key on `JobId`, so a duplicate job means a duplicate application.
- **Reposts:** boards take postings down and repost them under new ids, so duplicates happen even within one source.
- **Mismatched company names:** sources disagree on company naming. Greenhouse gives a display name, while Lever gives only a site name.
- **A single user, small volume:** hundreds to low thousands of jobs, with no UI yet to review merges. A wrong merge is worse than a missed one, because it hides a real opening.
- **Concurrency:** ingestion runs as Hangfire jobs, which can run at the same time.
- **Existing data:** the database may already hold duplicates from reposts.

## Options considered

### Option A: Exact normalized match, a links table, matching only on first sighting (chosen)
**Pros**:
- Deterministic and explainable: the key is visible, and a merge can be traced and undone.
- Indexed lookups, cheap.
- A link's job never changes behind your back.

**Cons**:
- Misses reworded titles.
- Needs a schema reshape and a backfill.

### Option B: Fuzzy similarity (title and description similarity above a threshold)
**Pros**:
- Catches reworded titles.

**Cons**:
- The threshold needs tuning against data you don't have yet.
- A wrong merge is silent.
- Harder to index.

### Option C: AI judges the candidates (exact company match, then the provider from spec 0006 decides)
**Pros**:
- The best recall.

**Cons**:
- A model call per candidate.
- Nondeterministic, so tests and verify become flaky.
- Cost grows with every run.

### Option D: Keep `JobSourceId`/`ExternalId` on `Job` as the "first source", and add links for the rest
**Pros**:
- Less migration churn.

**Cons**:
- Two places hold source identity, and they drift.
- Every query has to check both.

## Rationale

- **Exact matching:** it suits a single user with small volume and no review UI. The cost of a wrong merge (a hidden opening) outweighs a missed one (seeing a role twice). The split endpoint and the audit rows make even exact merges reversible.
- **A links table, not columns on the job:** the one question "where did this come from" then has exactly one home. The unique index moves to where the identity actually lives.
- **Matching only on first sighting:** a job's membership then never shifts because a posting was edited. The reconcile job exists for the cases where the rule itself changes (a new rule version, or a renamed company).
- **Advisory locks:** they solve the only real race (two runs creating the same new job) inside the existing one transaction per run, with no new table.
- **Taking the company name at the trigger:** the engineer knows the company when adding a board, so this beats both guessing from slugs and an alias table with no UI.
- **Lever as the second source:** its public postings API needs no key, and it is common among the companies a Greenhouse user also targets. That gives the live verify a real cross source merge. Spec 0008 listed it as a follow up for exactly this feature.

Engineer choices recorded on 2026-09-26:
- match on company, title and location, exactly;
- reposts merge too;
- add Lever;
- the highest confidence, then newest, link fills the job;
- the company name is given at the trigger;
- a missing location only matches a missing location;
- an internal split endpoint;
- existing duplicates merged at rollout, by a startup reconcile job;
- an advisory lock for races;
- deleted jobs are revived;
- a rename updates and rematches;
- list plus detail read endpoints.

After a cross check by another model (2026-09-26), the engineer accepted every recommended fix:
- "oldest" is the lowest UUIDv7 `Id`;
- a key change marks the job stale, and the reconcile runs after that run;
- `IJobSource.ParseStored` rebuilds a posting from a snapshot;
- `Job.Provenance` becomes replaceable inside `Job`;
- a split leaves applications and matches on the original job;
- the rename rematch runs inside the ingestion job;
- the reconcile commits per key group;
- a run looks its links up again after taking the lock;
- a split on a soft deleted job returns `404`;
- `companyName` sets both the displayed and the match company, for every source.
