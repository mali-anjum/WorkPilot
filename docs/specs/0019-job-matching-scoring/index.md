# 0019. Job matching engine and scoring

**Date**: 2026-09-29
**Status**: Proposed

## Summary

Every job gets a match score from 0 to 100 against your matching preferences, computed by plain rules in the Domain layer, never by an AI call. Each point is backed by evidence (the quoted sentence or field that earned it), so the score is a list of reasons, not a black box number. You set the inputs (skills, target roles, locations, remote preference, salary floor, years of experience, visa need, alert threshold) in a new Matching preferences section under Settings. Jobs are rescored after every ingestion, after you change your preferences, and after the scoring rules change, and a job that newly crosses your threshold raises the `JobMatched` event that notifications (#20) listen to.

## Requirements

**User stories**:
- As the founder, I want every discovered job scored against what I actually want, so I read the best ones first.
- As the founder, I want to see exactly why a job scored what it did (what matched, what is missing, what the posting does not say), so I can trust or overrule it.
- As the founder, I want jobs that break a hard rule of mine (no sponsorship when I need it, onsite when I want remote, pay below my floor) flagged at once, without them disappearing.

**Acceptance criteria**:
- **AC-1**: At `/settings/profile.matching` you can set skills (up to 100, each 1 to 60 chars), target roles (up to 20, each 1 to 100 chars), locations (up to 20, each 1 to 100 chars), remote preference (`Any`, `RemoteOnly`, `HybridOrRemote`, `Onsite`), salary floor (optional, 0 to 10,000,000), years of experience (optional, 0 to 60), needs visa sponsorship (yes/no) and strong match threshold (0 to 100, default 70). Saving validates every field (a failure is ProblemDetails with the field name, shown next to that field), persists, and reopening the page shows the saved values.
- **AC-2**: While your preferences have no skill and no target role, no `JobMatch` row is written for your profile, and the match endpoint answers with status `NeedsPreferences`.
- **AC-3**: When an ingestion run completes, every non deleted job linked to that source is scored for every profile with usable preferences by one background `ScoreJobsJob`. A job whose scoring inputs are unchanged since its last score (same `InputsHash`) is skipped with no write.
- **AC-4**: Saving preferences raises `MatchPreferencesChanged`; its Jobs handler enqueues a rescore of every non deleted job for that profile. The new scores are visible within about a minute.
- **AC-5**: On Api start, when any `JobMatch` has `ScoringVersion` lower than `JobScoring.CurrentVersion`, a `ScoreJobsJob` for all jobs and profiles is enqueued, and afterwards every row carries the current version.
- **AC-6**: The score is the weighted sum over applicable, known dimensions, rescaled to 0 to 100 and rounded half away from zero: Skills 40, Title 20, Location and remote 15, Salary 10, Seniority 10, Visa 5. A dimension your preferences do not set is `NotApplicable`; one the posting does not state is `Unknown` and listed under unknown information. Both are left out of the rescaling. `Confidence` is known weight divided by applicable weight (0 to 1, two decimals). With no known dimension at all the score is 0 with confidence 0.
- **AC-7**: A dealbreaker (you need sponsorship and the posting refuses it; you want `RemoteOnly` and the job is onsite; you want `Onsite` or `HybridOrRemote` and the job is neither remote nor in one of your locations; the job's top salary is below your floor) caps the score at 30, is stored in `Dealbreakers`, and is listed first in the breakdown. The job stays visible.
- **AC-8**: Every `Matched`, `Partial` or `Missing` dimension carries evidence: the field it came from (`title`, `description`, `location`, `remoteType`, `salary`) and a quote of at most 200 chars (the sentence around the hit, or the field value). Missing requirements (skills the posting asks for that you do not list) are listed by name.
- **AC-9**: When a score is written that is at or above your threshold and the job's previous score for you was absent or below it, `JobMatched(JobId, ProfileId, Score)` is published in the same save. Rescoring a job that was already strong does not raise it again.
- **AC-10**: `GET /internal/jobs/{id}/match?profileId=` returns status (`Scored`, `NeedsPreferences`, `Pending`), score, confidence, dimensions (key, weight, status, summary, evidence, missing), dealbreakers, unknowns and `scoredAt`; 404 ProblemDetails for an unknown job.
- **AC-11**: Scoring is deterministic: the same job fields, preferences and version always give the same score, breakdown and hash.
- **AC-12**: `/jobs/{id}` exists (minimal until #12 fills it) and shows the job's title, company and the match panel: score and confidence, dealbreakers first, then each dimension with its evidence quotes, missing requirements, and unknown information.

## Decision

**Chosen option**: Option 1: a deterministic rules scorer in the Domain, fed by structured preferences you edit.

Scoring is a pure Domain function `JobScorer.Score(JobScoringInput job, MatchPreferences prefs) → JobScore`, run by a serialized Hangfire job and stored per (job, profile) in `JobMatch`. No AI purpose is added.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`)

## Feature design

**Module placement** (spec 0018): preferences belong to **Profile** (it owns `Profile` fields, `Skill`, `ProfileSkill`); scores belong to **Jobs** (it owns `JobMatch`). Profile never writes `JobMatch`; Jobs never writes preferences. Profile tells Jobs through the `MatchPreferencesChanged` event; Jobs reads preferences through `IMatchPreferencesQuery` (Application, Profile module).

**Data model sketch**:

| Entity (owner) | Field | Type | Notes |
|---|---|---|---|
| `MatchPreferences` (Profile), table `match_preferences` | `ProfileId` | uuid, PK, FK → profiles | 1:1 with Profile, created on first save |
| | `Locations` | text[], required, default `{}` | |
| | `RemotePreference` | varchar(20), required, default `Any` | enum stored as string |
| | `SalaryFloor` | numeric(12,2), null | same unit as `Job.SalaryRangeMax` (the posting's own currency, see Consequences) |
| | `YearsOfExperience` | int, null | |
| | `NeedsVisaSponsorship` | bool, required, default false | |
| | `StrongMatchThreshold` | int, required, default 70 | check 0..100 |
| | `UpdatedAt` | timestamptz, required | |
| `Profile` (Profile) | `TargetRoles` | existing | reused |
| `Skill` / `ProfileSkill` (Profile) | existing | | reused: skills are find or create by case insensitive name; the form replaces the profile's `ProfileSkill` set |
| `JobMatch` (Jobs), table `job_matches` | `Score` | int, required | change from decimal; check 0..100 |
| | `Confidence` | numeric(3,2), required | 0..1 |
| | `Breakdown` | jsonb, required | shape below |
| | `Dealbreakers` | text[], required, default `{}` | dimension keys |
| | `ScoringVersion` | int, required | |
| | `InputsHash` | varchar(64), required | SHA 256 hex of job fields + preferences + version |
| | `ScoredAt` | timestamptz, required | replaces `RankedAt` |
| | `MatchedSkills` | dropped | replaced by `Breakdown` |
| | unique (`JobId`, `ProfileId`); index (`ProfileId`, `Score` desc) | | |

`Breakdown` jsonb shape (also the DTO shape, `WorkPilot.Contracts/Jobs/JobMatchDto.cs`):
```json
{ "dimensions": [ { "key": "skills", "weight": 40, "points": 0.75, "status": "Partial",
    "summary": "3 of 4 required skills", "evidence": [ { "field": "description", "quote": "…experience with C# and PostgreSQL…" } ],
    "missing": ["Kubernetes"] } ],
  "unknowns": ["salary"] }
```
`status` is one of `Matched`, `Partial`, `Missing`, `Unknown`, `NotApplicable`, `Dealbreaker`.

**Dimension rules** (all in `WorkPilot.Domain/Modules/Jobs/Matching/`; text compared after lowercasing and collapsing whitespace):
- **Skills (40)**: a built in `SkillVocabulary` (about 150 common technology and role skills with aliases, e.g. `c#`/`csharp`, `.net`/`dotnet`, `postgres`/`postgresql`, `js`/`javascript`) is matched as whole words in title and description to find the job's skills. Your skills are resolved through the same aliases (a skill not in the vocabulary matches as its own whole word). Points = matched job skills ÷ job skills found. `Missing` lists job skills you do not have. No vocabulary skill found in the posting → `Unknown`. No skills in your preferences → `NotApplicable`.
- **Title (20)**: tokens of the job title vs each target role, ignoring seniority words (`senior`, `junior`, `lead`, `staff`, `principal`, `sr`, `jr`, `i`, `ii`, `iii`). Points = best share of a role's tokens found in the title. No target roles → `NotApplicable`.
- **Location and remote (15)**: `Any` → `NotApplicable`. `RemoteOnly`: job remote → 1, hybrid → 0.5, onsite → dealbreaker, unstated → `Unknown`. `HybridOrRemote`: remote or hybrid → 1, onsite in one of your locations → 0.5, onsite elsewhere → dealbreaker. `Onsite`: job location contains one of your locations → 1, remote → 0.5, elsewhere → dealbreaker. Location match is a case insensitive substring either way.
- **Salary (10)**: no floor → `NotApplicable`. Job max (or min when max is missing) ≥ floor → 1; below → dealbreaker; no salary → `Unknown`.
- **Seniority (10)**: no years set → `NotApplicable`. The smallest `N` in phrases like `N+ years`, `N years of experience`, `N yrs` in the description. Yours ≥ N → 1, within 2 years → 0.5, otherwise 0 (`Missing`). No phrase → `Unknown`.
- **Visa (5)**: you do not need sponsorship → `NotApplicable`. A refusal phrase (`will not sponsor`, `unable to sponsor`, `no visa sponsorship`, `without sponsorship`, `not able to sponsor`, `does not sponsor`) → dealbreaker; an offer phrase (`visa sponsorship available`, `will sponsor`, `sponsorship is available`, `we sponsor`) → 1; neither → `Unknown`.

The vocabulary and phrase lists are Domain constants. Changing any rule, weight, vocabulary or phrase list bumps `JobScoring.CurrentVersion`.

**Flow**:
- `IngestJobsJob` (Workers/Jobs), after a run that returned a summary, enqueues `ScoreJobsJob.RunAsync(ScoreScope.ForSource(jobSourceId))` (same module, direct enqueue).
- `ReconcileJobsJob` and the split endpoint enqueue `ScoreScope.ForJobs(ids)` for the jobs they changed, so a merged or split job is rescored.
- `SaveMatchPreferences` (Profile use case) saves and publishes `MatchPreferencesChanged(ProfileId)` in the same save. Handler `jobs.rescore-on-preferences-changed` (Application/Modules/Jobs/Handlers) enqueues `ScoreScope.ForProfile(profileId)`. Enqueueing twice on a retried handler is harmless.
- Api start: `JobScoringStartup` enqueues `ScoreScope.All` when a stale version row exists (AC-5), next to the existing reconcile enqueue.
- `ScoreJobsJob` is `[DisableConcurrentExecution]` (one scoring run at a time), loads usable preferences through `IMatchPreferencesQuery`, pages jobs in batches of 200, computes `InputsHash`, skips unchanged rows, upserts changed ones (`INSERT … ON CONFLICT ("JobId","ProfileId") DO UPDATE`), publishes `JobMatched` on a threshold crossing (AC-9), and saves per batch. It never opens `IJobRepository.InTransactionAsync` (it writes no `jobs` row).

**State transitions**: none on `JobMatch` (a score is replaced, not moved through states). Threshold crossing (AC-9): `absent or < threshold` → `≥ threshold` raises `JobMatched`; `≥` → `≥` does not; dropping below and crossing again raises again.

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/profile/match-preferences` | GET | `profileId`: guid (req) | `MatchPreferencesDto` (defaults when never saved, `isSet` false) | internal (spec 0004) | 404 unknown profile |
| `/internal/profile/match-preferences` | PUT | body `MatchPreferencesDto` with `profileId` | saved `MatchPreferencesDto` | internal | 400 ProblemDetails with `errors[field]`, 404 unknown profile |
| `/internal/jobs/{id}/match` | GET | `id` (route), `profileId` (req) | `JobMatchDto`: status, score, confidence, dimensions, dealbreakers, unknowns, scoredAt | internal | 404 unknown job |

Web: `/settings` lists `ISettingsSection`s; `/settings/{section}` renders the section's component. This spec adds the contract (`WorkPilot.Web/Features/Common/ISettingsSection.cs`: `Key`, `Title`, `Order`, `Type ComponentType`), the two minimal pages, and the first section `profile.matching`; startup throws on a duplicate key. #30 builds on these pages.

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Score a job | job skills, title, location, remote, salary, description | `jobs` columns (displayed fields of the primary link, spec 0017) |
| Score a job | your skills, roles, locations, remote pref, floor, years, visa need, threshold | `match_preferences` + `Profile.TargetRoles` + `ProfileSkill`/`Skill`, via `IMatchPreferencesQuery` |
| Score a job | weights, vocabulary, phrase lists, version | Domain constants in `JobScoring` |
| Crossing check (AC-9) | previous score | existing `JobMatch.Score` for (job, profile), read before the upsert |
| Rescore after preference save | which profile | `MatchPreferencesChanged.ProfileId` |
| Match panel | profile | Web `ProfileWebExtensions.ProfileId(user)` claim, passed as `profileId` |
| Match panel `Pending` | job exists, prefs usable, no row yet | derived at read time |
| Evidence quote | sentence around the hit | derived from the description: text between the nearest sentence breaks (`.`, `!`, `?`, newline), trimmed to 200 chars centered on the hit |

**Key invariants**:
- One `JobMatch` per (job, profile) (unique index).
- `0 ≤ Score ≤ 100`, `Score ≤ 30` whenever `Dealbreakers` is not empty, `0 ≤ Confidence ≤ 1`.
- A row's `InputsHash` always matches the inputs its score was computed from.
- Only the Jobs module writes `job_matches`; only Profile writes `match_preferences`, `skills`, `profile_skills`.

**Security model**: single user, internal Api only (spec 0004). Every endpoint takes `profileId` and reads or writes only that profile's preferences and matches; the Web passes the signed in user's profile id from the claim, never from the query string. Jobs are a shared catalog (spec 0017); matches are per profile. No regulated data; salary floor and visa need are personal but stay in the product database like the rest of Profile.

**Configuration required**: none. No new env vars or keys.

**Critical test scenarios**:
- Happy path: save preferences with C#, PostgreSQL and a "Backend Engineer" role, ingest a Greenhouse fixture, and the job's match shows Skills `Partial` with quotes and a Title `Matched`; verifies **AC-1**, **AC-3**, **AC-8**, **AC-10**.
- Dealbreaker: needs sponsorship + "we are unable to sponsor visas" gives a score ≤ 30 with Visa listed first; verifies **AC-7**.
- Unknowns: a posting with no salary, no years and no remote info gives those dimensions `Unknown`, a lower confidence, and a score rescaled over known dimensions only; verifies **AC-6**.
- Idempotency: running `ScoreJobsJob` twice writes no rows the second time and raises `JobMatched` once; verifies **AC-3**, **AC-9**, **AC-11**.
- No preferences: an ingestion with an empty preferences profile writes no match and the endpoint says `NeedsPreferences`; verifies **AC-2**.
- Version bump: rows at an older version are rescored on start; verifies **AC-5**.
- Validation: threshold 150 gives 400 with `errors.strongMatchThreshold`; verifies **AC-1**.
- Scoping: preferences of another profile id are never read for a match; verifies **Security model**.

## Build plan

Tracer Bullet: the first task is a thin thread from preferences to a visible score, then it thickens.

1. Thin thread: `MatchPreferences` entity + `IMatchPreferencesQuery`; `JobScorer` with the Skills and Title dimensions only; migration `AddJobMatching` with the whole target schema above; `ScoreJobsJob` enqueued after ingestion with hash skip and upsert; `GET /internal/jobs/{id}/match`; minimal `/jobs/{id}` showing score and dimensions. Satisfies **AC-2**, **AC-3**, **AC-10**, **AC-11**, **AC-12**.
2. Preferences end to end: `GET`/`PUT /internal/profile/match-preferences` with validation on `Result<T>`; `ISettingsSection` contract, `/settings` and `/settings/{section}` pages, the Matching preferences form; `MatchPreferencesChanged` event + `jobs.rescore-on-preferences-changed` handler. Satisfies **AC-1**, **AC-4**.
3. The full scorer: `SkillVocabulary` with aliases, Location and remote, Salary, Seniority, Visa, rescaling, confidence, dealbreaker cap, evidence quotes and missing skills; match panel shows all of it. Satisfies **AC-6**, **AC-7**, **AC-8**, **AC-12**.
4. `JobMatched` on threshold crossing; `JobScoring.CurrentVersion` stale check on start; rescore after reconcile and split. Satisfies **AC-5**, **AC-9**.
5. Tests: Domain unit tests for every dimension rule, rounding, cap and determinism; Api integration tests (real Postgres) for the endpoints, the job, idempotency and the event. Satisfies **AC-1** to **AC-11**.

## Consequences

**Positive**:
- Every point in a score is explainable and testable, and scoring costs nothing to run.
- The `ISettingsSection` contract and settings pages exist before #30, so #30 only adds sections and polish.
- #12 and #13 read one stable `JobMatchDto`.

**Negative / tradeoffs**:
- Rules miss nuance: a posting that says "strong backend background" without naming a vocabulary skill scores `Unknown` on Skills. Synonyms outside the alias list are missed until the list grows (each change bumps the version and rescored everything).
- Salary has no currency: postings and your floor are compared as plain numbers. A floor in PKR against a USD posting is meaningless; the form says "same currency as the postings you target".
- Rescaling over known dimensions means a sparse posting can score high with low confidence; the UI must always show confidence next to the score.
- A preferences save rescored every job; fine at thousands of jobs, needs batching by recency if the catalog reaches hundreds of thousands.

**Neutral**:
- `JobMatch.RankedAt` becomes `ScoredAt`, `Score` becomes an int and `MatchedSkills` is dropped; nothing reads them today.
- The existing `ProfileSkill`/`Skill` tables get their first writer.
- A hybrid with AI extraction can be added later as another input to the same scorer without changing `JobMatch`.

## Follow-up

- [ ] Consider an AI assisted extraction pass (a `Matching` AI purpose) feeding extra evidence into the same scorer once real postings show where rules miss.
- [ ] Salary currency: store a currency on `Job` and on the floor when a source provides it.
- [ ] Decisions made without the engineer (please review), made while designing:
  - Weights (40, 20, 15, 10, 10, 5), the dealbreaker cap of 30, and rescaling over known dimensions.
  - `MatchPreferencesChanged` is a new catalog event (`profile.match-preferences-changed.v1`), because Profile to Jobs crosses a module boundary.
  - The settings pages and `ISettingsSection` land in this spec (not #30) so the form has a home.

## Rationale

Reasoning and options: see [rationale.md](rationale.md).
