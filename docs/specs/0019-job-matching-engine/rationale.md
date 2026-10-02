# 0019. Job matching engine: rationale

## Context

Job ingestion (spec 0008) and deduplication (spec 0017) now fill `jobs` with canonical roles, but nothing tells the founder which of them are worth time. The scope asks for a score against the profile across skills, experience, education, location, remote preference, salary, technology, job type and work authorization, and it sets a hard bar: the output must be explainable, with a score, a confidence, evidence, missing requirements and unknown information. A bare number is explicitly not acceptable.

Two gaps shape the problem. First, a job carries only free text: its requirements live in prose, not columns, so something must read the description. Second, the profile (spec 0002) only has skills, experience, education, location and target roles; remote preference, salary, job type and work authorization have no home, and there is no screen to edit any of it. A score over missing inputs is not a real score.

Forces: the product is single user today, so volumes are a few thousand jobs, not millions. AI calls cost money and vary between runs, and the job description is untrusted text from the internet (a prompt injection surface). The module contracts (spec 0018) require each module to write only its own tables and to react to other modules only through outbox events, and the AI layer (spec 0006) requires a config switchable purpose with a no key `Fake` default so builds and tests never need a key. Later features (the jobs list, dashboard, application preparation) all consume this score, so its shape and freshness rules are load bearing.

## Options considered

### Option 1: AI judges the whole match

Send the job description and the profile to the model and ask for a score, evidence, gaps and unknowns as structured output.

**Pros**:
- Fastest to build; handles synonyms and nuanced prose well.
- One prompt covers every dimension.

**Cons**:
- The same inputs give different scores between runs, so ranking jitters and nothing can be unit tested.
- Every profile change costs one AI call per job, and profile PII goes to the provider.
- The "evidence" is whatever the model says, close to the black box the scope forbids.

### Option 2: Hybrid, AI extracts requirements, C# scores (chosen)

The model turns each description into structured, quoted requirements once per content hash; a pure Domain scorer compares them to the profile with configured weights.

**Pros**:
- Deterministic, testable score; every point traces to a verifiable quote and a profile row.
- One AI call per job content, reused across profile edits and weight tuning.
- Only the job text goes to the provider.

**Cons**:
- Two moving parts (extraction and scoring) and a stored requirements document to version.
- Synonym handling is only as good as the alias list.

### Option 3: Rules only, no AI

Keyword and regex matching on the raw description.

**Pros**:
- No AI cost, fully deterministic, simplest to operate.

**Cons**:
- Misses requirements phrased in prose ("comfortable owning backend services in C#"), can't tell required from preferred, and produces many false Unknowns.

## Rationale

The explainability bar decides it. Option 1 cannot show evidence that is checkable, cannot be tested, and sends the founder's work authorization and salary expectations to a third party on every rescore. Option 3 is honest but blind to how postings are actually written. Option 2 uses the model only for what it is good at (reading prose into structure) and keeps the judgment in code, where the Domain layer can enforce invariants and unit tests can pin every rule, as `AGENTS.md` requires. Verifying every quote against the description turns a hallucinated or injected requirement into a visible "unverified" item that scores nothing, which is the key safety property.

The engineer's choices then close the rest. Storing requirements per job and content hash keeps AI cost at one call per posting change. Excluding Unknown dimensions and renormalizing, with confidence from the known weight fraction, keeps the score honest about what it does not know instead of assuming. Capping blocked jobs at 20 keeps them visible and explainable rather than hidden. Weights in config let tuning happen without a deploy while startup validation keeps them sane. Event driven triggers plus a sweep reuse the outbox and Hangfire exactly as spec 0018 lays out; handlers only enqueue, so no network call ever holds the outbox transaction open. Adding the missing profile fields plus a small `/profile` editor was chosen because, without them, most dimensions would be permanently Unknown and the "done when" could not be shown with real evidence.

Decisions made while writing (the engineer delegated these): the dimension rules and default weights (skills 40, experience 20, location 15, salary 10, education 5, job type 5, work authorization 5), chosen because skills and seniority dominate fit for the roles in scope, runner up equal weights, rejected because a salary mismatch would count as much as missing the core stack; required skills counting double over preferred, runner up equal counts; an unverified quote scoring zero rather than counting with a flag, because a fabricated requirement should not move the score; one migration for the whole target in slice 1, since the tables are empty and every slice reads the same columns, runner up a migration per slice; a SQL upsert guarded by the fingerprint for concurrency instead of an advisory lock, because the unique index already serializes writers; `profiles.UpdatedAt` plus `xmin` as the ETag so a child only edit still changes the token, runner up a manual version counter.

## Merged draft (2026-10-02)

An earlier draft of this spec (`0019-job-matching-scoring`, 2026-09-29, written without the engineer) proposed a rules only scorer edited from a settings page. It was compared with this spec and dropped: this spec meets the scope's dimensions (education, job type, work authorization), tells required from preferred skills, compares salary only in the same currency, and its decisions were made with the engineer. Two parts of the draft were kept because nothing else covered them: the strong match threshold with the `JobMatched` event (spec 0018 already lists it for #11, and notifications in spec 0020 depend on it), and a Title dimension against your target roles, since a job that lists your stack under the wrong role should not rank with the right ones.
