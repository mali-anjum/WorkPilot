# 0019. Job matching engine and explainable scoring

**Date**: 2026-09-30
**Status**: Accepted
**Updated**: 2026-10-02 (merged in the 2026-09-29 draft `0019-job-matching-scoring`: title dimension, strong match threshold and the `JobMatched` event)

## Summary

Every job gets a match score from 0 to 100 against your profile, with a confidence level and a "why it matches" list where every line points at real evidence: a sentence quoted from the posting and the profile item that meets it. An AI model reads each job description once and pulls out its requirements as structured data; plain C# code then does the scoring, so the same inputs always give the same score and every point can be traced. When a job newly reaches your strong match threshold, matching raises `JobMatched`, which notifications (spec 0020) turn into an alert. Scoring runs by itself when a job arrives or changes and when you save your profile, and this feature also adds a small `/profile` page (so the inputs are real) plus a minimal `/jobs` list and `/jobs/{id}` match panel (feature 12 thickens both later).

## Requirements

**User stories**:
- As the founder, I want every discovered job scored against my profile so that I look at the best fits first.
- As the founder, I want to see why a job scored what it did, with the exact posting text and profile item behind each point, so that I can trust or challenge the number.
- As the founder, I want to see what a job asks for that I lack, and what it never said, so that I know the gaps and how sure the score is.
- As the founder, I want to edit my match preferences, skills, experience and education in one place so that the score reflects me.
- As the founder, I want to be told when a job newly becomes a strong match, so that I do not have to keep checking the list.

**Acceptance criteria**:
- **AC-1**: When a job is created, or its primary posting's content hash changes (including when a merge picks a new primary, and both jobs of a split), its requirements are extracted once for that content hash and its match is scored for every profile, with no manual action (domain event, then Hangfire jobs).
- **AC-2**: A stored match has an integer `Score` 0 to 100 (null when unscored, see AC-3), a `Confidence` of High, Medium or Low, a `HasBlocker` flag, and an explanation listing each of the eight dimensions (skills, title, experience, location and remote, salary, education, job type, work authorization) with its status (Met, Partial, Missed, Unknown, Blocker), points earned out of its weight, the job evidence (verified quote) and the profile evidence (a pointer to the skill, target role, experience, education row or preference that meets it).
- **AC-3**: The score is the points earned over the known dimensions divided by the total weight of the known dimensions, times 100, rounded half away from zero. An Unknown dimension is left out of both sides and listed under "unknown information" with a reason (the job did not say, or your profile does not say). Confidence is High when known weight is at least 80 percent of total weight, Medium when at least 50 percent, else Low. When the known weight is 0 (every dimension Unknown), `Score` is null, Confidence is Low, and the job shows "not enough information to score".
- **AC-4**: Every Required skill, the minimum years, and the degree level that your profile does not meet appear under "missing requirements", each with its quote.
- **AC-5**: A dealbreaker (work authorization explicitly refused, or an onsite or hybrid job outside every preferred location while you accept Remote only) caps the score at `BlockerCap` (default 20), sets `HasBlocker`, and is listed as a blocker with its evidence.
- **AC-6**: A requirement whose quote is shorter than 10 characters, or does not appear in the description text the extractor was given (the truncated text, after whitespace and case normalization), is shown as "unverified" and does not count toward the score.
- **AC-7**: Scoring is idempotent: when the inputs fingerprint (see Value sourcing: requirements, the job columns the fallback reads, profile fingerprint, the whole `Matching` scoring config, `ScoringVersion`) is unchanged, no row is written; when any of them changes, the match is rescored. Saving the match profile rescores every job for that profile, and bumping `ScoringVersion` rescores everything.
- **AC-8**: When extraction fails 3 times (provider down, output that fails the schema), the requirements row is `Failed` with a reason, and the match is still scored from the job's own columns (location and remote type only; salary columns are never scored) with Confidence Low and a "requirements could not be read" notice. The Rescore action retries extraction, and the sweep retries `Failed` rows once a day.
- **AC-9**: `/jobs` lists jobs that are not soft deleted, 25 per page, with title, company, score, confidence and a blocker badge, sorted by score descending, blocked jobs after unblocked ones, unscored jobs (no match row, or a null score) last, ties broken by `JobId`. When your profile has no skills or no experience, a banner links to `/profile`.
- **AC-10**: `/jobs/{id}` shows the match panel: score, confidence, "why it matches" (Met and Partial items with evidence), missing requirements, unknown information, blockers, unverified items, the failure notice when AC-8 applies, and a Rescore button that reports it was queued (it forces a fresh extraction and rescores this profile; a click while extraction is already Pending queues nothing new).
- **AC-11**: `/profile` edits match preferences (target roles, remote preference, preferred locations, job types, minimum salary and currency, authorized countries, needs sponsorship elsewhere, strong match threshold 0 to 100, default 70), skills, experience rows and education rows (with degree level). Invalid input returns ProblemDetails field errors (currency is 3 letters ISO 4217, countries are 2 letter ISO 3166, end date not before start date, salary not negative, threshold 0 to 100, at most 20 target roles of 1 to 100 characters). A save carrying a stale ETag gets 412 and the page says "changed elsewhere, reload" while keeping your edits on screen.
- **AC-12**: Extraction sends only the job description (truncated at `MaxDescriptionChars`, default 20000) to the `JobExtraction` AI purpose, with no tools, and never any profile data. Output that does not parse into the v1 schema or breaks its bounds (at most 60 skills, quotes at most 300 characters) counts as a failed attempt.
- **AC-13**: With the committed `Fake` provider, extraction is deterministic: known skill names found in the description (each quoting its sentence), plus "N+ years", remote and salary patterns, so a fresh clone shows real looking scores with evidence and needs no key.
- **AC-14**: When jobs merge, matches follow the surviving job (existing `ReassignMatchesAsync`), the removed job's requirements row goes with it, and the survivor is rescored if its primary content changed.
- **AC-15**: Match reads are scoped to the given profile: asking for a job's match with a profile that has none returns 404 ProblemDetails, never another profile's match.
- **AC-16**: The `Matching` config is validated at startup: weights are non negative integers summing to 100, `BlockerCap` is 0 to 100, confidence thresholds satisfy 0 < Medium < High <= 1, `MaxDescriptionChars` is positive. Bad config fails startup.
- **AC-17**: When a write gives a job a non null `Score` at or above your `StrongMatchThreshold`, without a blocker, and the previous stored score for that (job, profile) was absent, null, below the threshold or blocked, `JobMatched(JobId, ProfileId, Score)` (`jobs.job-matched.v1`) is published in the same transaction as that write. Rescoring a job that was already strong does not raise it again; dropping below and crossing again raises it again. A write skipped by the fingerprint (AC-7) raises nothing.

## Decision

**Chosen option**: Option 2: Hybrid, AI extracts requirements once per job, deterministic C# scores.

The AI only turns a description into structured, quoted requirements (stored per job and content hash); a pure Domain scorer compares them to the profile with configured weights and produces the score, confidence and a fully evidenced explanation.

**Implementation skills**: `microsoft-extensions-ai` (`.claude/skills/microsoft-extensions-ai/`) · `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `supabase-postgres-best-practices` (`supabase/agent-skills`, `.agents/skills/supabase-postgres-best-practices/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

Reasoning and options: see [rationale.md](rationale.md).

## Feature design

**Module ownership** (spec 0018): Jobs owns and alone writes `job_requirements` and `job_matches`, and owns extraction and scoring. Profile owns and alone writes `profiles`, `skills`, `profile_skills`, `experiences`, `educations`. Jobs reads the profile tables through its own read query (any module may read). Cross module reactions go through the two events below.

**Data model sketch**:

`profiles` (Profile), new columns:
| Column | Type | Null | Notes |
|---|---|---|---|
| `RemotePreference` | text enum `Remote`, `Hybrid`, `Onsite`, `Any` | no | default `Any` |
| `PreferredLocations` | jsonb list of `{ City?, Country }` | no | default `[]`; Country is ISO 3166 alpha 2; City normalized for comparison |
| `JobTypes` | text[] of `FullTime`, `PartTime`, `Contract`, `Internship`, `Temporary` | no | default `{}` (empty means "not set", so Unknown) |
| `MinSalary` | numeric(12,2) | yes | yearly |
| `SalaryCurrency` | char(3) | yes | required when `MinSalary` is set |
| `AuthorizedCountries` | text[] | no | ISO 3166 alpha 2, default `{}` |
| `NeedsSponsorshipElsewhere` | boolean | no | default `true` |
| `StrongMatchThreshold` | int | no | default 70, check 0 to 100; read by AC-17 and by notifications (spec 0020) |
| `TargetRoles` | existing text list | no | reused, not new; first edited by the `/profile` page |
| `UpdatedAt` | timestamptz | no | set on every match profile save, so the profile row (and its `xmin`) changes even when only a child row changed |
| `xmin` | concurrency token | | the match profile ETag |

`educations` (Profile), new column `DegreeLevel` text enum `None`, `Associate`, `Bachelor`, `Master`, `Doctorate`, not null, default `None`.

`job_requirements` (Jobs), new:
| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | uuid v7 | no | PK |
| `JobId` | uuid | no | FK `jobs`, unique, cascade delete |
| `ContentHash` | text | no | the primary link's latest snapshot hash this row was extracted from |
| `ExtractorVersion` | int | no | bump when the prompt or schema changes; lower than current means stale |
| `Status` | text enum `Pending`, `Extracted`, `Failed` | no | |
| `Requirements` | jsonb | yes | v1 document below, null unless `Extracted` |
| `Model` | text | yes | provider model id that answered |
| `Attempts` | int | no | reset to 0 whenever `Status` goes to `Pending` |
| `PendingSince` | timestamptz | yes | set when `Status` goes to `Pending`; lets the sweep find lost jobs |
| `FailureReason` | text | yes | last error, set when `Failed` |
| `ExtractedAt` | timestamptz | yes | |

Requirements v1 document (every item carries `Quote`, a verbatim sentence from the description, 10 to 300 characters; `Degree.Level` is `Associate`, `Bachelor`, `Master` or `Doctorate`, never `None`, plus `OrEquivalentExperience: bool`):
`{ "v": 1, "Skills": [{ "Name", "Importance": "Required"|"Preferred", "Quote" }] (max 60), "MinYears": { "Value": int, "Quote" }?, "Degree": { "Level", "Field"?, "Quote" }?, "Remote": { "Type": "Remote"|"Hybrid"|"Onsite", "Quote" }?, "Locations": [{ "City"?, "Country", "Quote" }] (max 20), "Salary": { "Min"?, "Max"?, "Currency", "Period": "Year"|"Month"|"Hour", "Quote" }?, "JobType": { "Value", "Quote" }?, "Sponsorship": { "Offered": bool, "Quote" }? }`

`job_matches` (Jobs), altered:
| Column | Change |
|---|---|
| `JobId`, `ProfileId` | kept, unique together; add FK `ProfileId` to `profiles` |
| `Score` | numeric to nullable int, check 0 to 100; null means unscored (AC-3) |
| `Confidence` | new text enum `High`, `Medium`, `Low` |
| `HasBlocker` | new boolean |
| `Explanation` | new jsonb (versioned, below), replaces `MatchedSkills` (dropped) |
| `InputsFingerprint` | new text, SHA-256 hex |
| `ScoringVersion` | new int |
| `RankedAt` | now updated on every rescore |
| index | new (`ProfileId`, `HasBlocker`, `Score` desc) for the list |

Explanation v1: `{ "v": 1, "Dimensions": [{ "Name", "Status", "Earned": decimal, "Weight", "Items": [Item] }], "Missing": [Item], "Unknown": [{ "Dimension", "Reason": "JobSilent"|"ProfileNotSet"|"CurrencyDiffers"|"ExtractionFailed"|"Unparseable" }], "Blockers": [Item & { "Dimension" }], "Unverified": [{ "Dimension", "Label", "JobQuote" }], "ExtractionFailed": bool }` where `Item = { "Label", "Status", "JobQuote"?, "Verified": bool, "ProfileRef"?: { "Kind": "Skill"|"TargetRole"|"Experience"|"Education"|"Preference", "Id"?, "Label" } }`. `Dimensions` always lists all eight in the fixed order skills, title, experience, location, salary, education, job type, work authorization. `JobQuote` is required for items taken from extraction; items built from job columns in the fallback have `JobQuote` null and `Verified` true. A dimension with status Blocker has `Earned` 0 and its full weight counted as known. Only the final score is rounded.

**Scoring rules** (pure Domain, `Domain/Modules/Jobs/Matching`):
- **Skill name normalization** (`SkillName.Normalize`, table tested): trim, lower case (invariant), turn hyphens, underscores and runs of whitespace into one space, keep `#`, `+` and `.` (so `C#`, `C++`, `C` and `.NET` stay distinct), strip a trailing version token only when it follows a space and is digits and dots (`.NET 8` → `.net`, `Java 17` → `java`, while `python3` and `es6` stay as is), then map through `SkillAliases` (keys normalized the same way). The shared `skills.Name` is unique and at most 100 characters; the PUT upserts by normalized name and retries once on a unique violation from a concurrent insert.
- **Skills** (weight 30): each verified extracted skill is matched to a profile skill by normalized name. Required skills count 2, Preferred count 1. Earned is the weighted ratio (sum of matched skill counts over sum of all skill counts) times the weight. Status is Met when all match, Missed when none match, else Partial. Unverified skills are left out of both sums and out of Missing. Unknown when no verified skill was extracted, or your profile has no skills.
- **Title** (15): read from `jobs.Title` (a job column, so its item has `JobQuote` = the title and `Verified` true, and it is scored in the fallback too). Tokens are the normalized title split on spaces, `/`, `,` and `-`, with seniority words removed (`senior`, `junior`, `lead`, `staff`, `principal`, `sr`, `jr`, `i`, `ii`, `iii`, `iv`). For each target role, the share of its tokens found in the title; earned is the best share times the weight. Met at 1, Missed at 0, else Partial; the profile pointer is the best role (`Kind` `TargetRole`). Unknown (reason `ProfileNotSet`) when you have no target roles.
- **Experience** (15): your years are the union of your experience date ranges (overlaps merged, open ended rows run to today's UTC date), in years to one decimal. Met when at least `MinYears`, else Partial with earned `years / MinYears`. Unknown when the job states no minimum or you have no experience rows.
- **Location and remote** (15): a Remote job is Met when you accept `Remote` or `Any`, Partial (half) otherwise. An Onsite or Hybrid job is Met when one of its locations matches a preferred location (same normalized city and country, or same country when your preference lists only a country); with no match it is a Blocker when your preference is `Remote`, else Missed. Unknown when the job states neither remote type nor location, or you set `Any` with no preferred locations. In the fallback (AC-8) the job's `RemoteType` column is used as is, and `jobs.Location` counts only when it parses as `City, Country` with a recognizable country name or ISO code (a small static country table in the Domain); otherwise the location part is Unknown (reason `Unparseable`).
- **Salary** (10): only the extracted salary is ever scored; `jobs.SalaryRangeMin/Max` are not. Compared only when the extracted currency equals yours and its period is Year; Met when the job's max (or min when no max) is at least your `MinSalary`, else Missed. Unknown otherwise (reason `CurrencyDiffers`, `JobSilent`, `ProfileNotSet`, or `ExtractionFailed`).
- **Education** (5): Met when your highest `DegreeLevel` is at least the required level, or when the posting says "or equivalent experience" and your years meet its `MinYears`; else Missed. Unknown when the job states no degree, or you have no education rows or every row is `None` (reason `ProfileNotSet`).
- **Job type** (5): Met when the job's type is in your `JobTypes`, else Missed. Unknown when unstated or your list is empty.
- **Work authorization** (5): a job country is taken from its locations (a Remote job with no country is Unknown). Evaluated in this order: Met when every job country is in `AuthorizedCountries`; else Met when `NeedsSponsorshipElsewhere` is false; else Met when the posting offers sponsorship; else a Blocker when the posting says sponsorship is not offered; else Unknown. So an authorized country is never blocked by a "no sponsorship" line.
- Any Blocker caps the final score at `BlockerCap`. Quotes that fail verification (AC-6) drop their item from scoring and list it under Unverified.
- `ScoringVersion` is a Domain constant (like `JobDedupKey.CurrentRuleVersion`), starting at 1.

**State transitions** (`job_requirements.Status`): `Pending` → `Extracted` (valid output) · `Pending` → `Failed` (third failed attempt) · `Extracted` or `Failed` → `Pending` (content hash changed, extractor version bumped, or a manual Rescore).

**Events and jobs**:
- `JobContentChanged` (`jobs.job-content-changed.v1`, `{ JobId, ContentHash }`): the `Job` entity cannot publish, so the Application callers do it. `JobIngestionService`, `JobMerger` and `JobDedupService` read the job's primary link id and its latest snapshot hash before and after the change and, when either differs (or the job is new), call `IEventPublisher.Publish` inside the same `InTransactionAsync` unit, before its save. Because `InTransactionAsync` clears the change tracker and reruns the whole unit on a conflict, the compare and publish live inside the unit and run again with it. Cases: `Job.Create`; `AttachLink` or `SeeAgain` that changes the primary or its hash; the survivor of `MergeFrom` when its primary changed; both jobs after `SplitLink`. Handler `EnqueueRequirementExtraction` (HandlerKey `jobs.enqueue-requirement-extraction`) only enqueues `ExtractJobRequirementsJob(jobId, force: false)`. The payload `ContentHash` is informational; the job re-reads the current state.
- `MatchProfileChanged` (`profile.match-profile-changed.v1`, `{ ProfileId }`): raised by Profile on every successful match profile save and when a profile is first provisioned. Handler `EnqueueProfileRescore` (HandlerKey `jobs.enqueue-profile-rescore`) enqueues `RescoreProfileJob(profileId)`.
- Handlers run inside `HandleEventJob`'s transaction but a Hangfire enqueue uses its own connection, so an enqueue can outlive a rolled back handler transaction; harmless because every job below is idempotent.
- `ExtractJobRequirementsJob(jobId, force)`, marked `[DisableConcurrentExecution]` keyed by job id: loads the job (a missing or soft deleted job is a quiet no-op, no throw), reads its current description and the primary link's latest snapshot hash in one query, and skips the AI call when the row already has this hash and extractor version with `Extracted` and `force` is false. Otherwise it sets `Pending` (resetting `Attempts` and setting `PendingSince`) when not already Pending, and calls `IJobRequirementExtractor`. On failure it increments `Attempts`, stores `FailureReason`, commits that, then rethrows while `Attempts < 3` (the job uses `[AutomaticRetry(Attempts = 2)]`, so three runs in total, matching the counter); at the third failure it sets `Failed` and does not throw. Either way it then runs `ScoreJobMatches(jobId)` for every profile in the same job.
- `RescoreProfileJob(profileId)`, `[DisableConcurrentExecution]` keyed by profile id: scores every non deleted job for the profile in batches of 200, each batch in its own transaction.
- `MatchSweepJob` (recurring, hourly, `AddRecurringJob`): enqueues extraction for jobs with no requirements row, a stale content hash, a lower extractor version, a `Pending` row whose `PendingSince` is over 30 minutes old, or a `Failed` row last attempted over 24 hours ago; enqueues `RescoreProfileJob` for each profile that has a match with a lower `ScoringVersion` or lacks a match for any job with a requirements row. Also enqueued once on Api start (like `ReconcileJobsJob`), which covers a changed `Matching` config because the fingerprint check then rescores what changed. Bumping `ExtractorVersion` therefore re-extracts every job through this sweep (an AI cost, unthrottled, acceptable for one user).
- `JobMatched` (`jobs.job-matched.v1`, `{ JobId, ProfileId, Score }`, AC-17): `ScoreJobMatches` reads the stored `Score` and `HasBlocker` for the (job, profile) row inside its batch transaction before the upsert (Postgres 17 cannot return the old row from `ON CONFLICT`), and when the upsert wrote a row (it returns the id) and the crossing rule holds, calls `IEventPublisher.Publish` and saves the outbox row in that same transaction. Two concurrent runs can both see the old score and both publish; that is acceptable because the notifications handler counts a job once per day (spec 0020 AC-3). This is the event spec 0018 lists for Jobs (#11).
- Upsert: `ScoreJobMatches` writes with raw SQL `INSERT ... ON CONFLICT (JobId, ProfileId) DO UPDATE SET Score, Confidence, HasBlocker, Explanation, InputsFingerprint, ScoringVersion, RankedAt ... WHERE job_matches."InputsFingerprint" <> excluded."InputsFingerprint"`, so two concurrent runs end with one correct row, an unchanged fingerprint writes nothing (AC-7), and `Id` never changes. No advisory lock is needed; the unique index serializes writers. It does not use `InTransactionAsync`, because it never changes `jobs`.
- Merge (spec 0017): `ReassignMatchesAsync` keeps the survivor's match when both jobs had one; the survivor's `JobContentChanged` is published in the merge unit, so any match whose content changed is rescored. The removed job's requirements row goes by cascade when `RemoveJob` hard deletes it, and its in flight extraction job finds no job and exits.

**Fake extraction** (AC-13): `FakeChatClient` recognizes the extraction prompt by a marker line (`Task: extract-job-requirements v1`) and returns v1 JSON built from the description: skills from a static known skills list in `WorkPilot.AI` (about 80 common technologies, matched on word boundaries), each quoting its sentence (sentences split on `. ` and newlines); a skill whose sentence contains "nice to have", "preferred", "bonus" or "plus" is Preferred, else Required; `(\d+)\+? years` gives `MinYears`; "remote", "hybrid" or "on-site/onsite" give `Remote`; a `$` or `USD` amount range gives a USD yearly salary; "visa sponsorship" with or without "no"/"not" gives `Sponsorship`. `JobExtraction` is added to `AiPurposes.All`. The real extractor calls `IChatClient.GetResponseAsync<JobRequirementsV1>` (Microsoft.Extensions.AI structured output) with no tools, then checks the bounds itself. `Model` comes from `ChatResponse.ModelId`, else the resolved purpose's configured model, else the provider name (the Fake returns `fake`).

**API surface** (all `/internal/*`, same network boundary as the rest; the Web passes the signed in founder's `ProfileId` resolved from the session, spec 0004; every failure is ProblemDetails via `Result<T>` and `ToHttp`):
| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/matches` | GET | `profileId`: guid (req), `page`: int (opt, 1), `pageSize`: int (opt, 25, max 100) | `items[]` { `jobId`, `title`, `company`, `location`, `score`?, `confidence`?, `hasBlocker`, `rankedAt`? }, `total`, `page`, `pageSize`, `profileIncomplete` | internal | 400 bad paging, 404 unknown profile |
| `/internal/jobs/{jobId}/match` | GET | `profileId`: guid (req) | `score`, `confidence`, `hasBlocker`, `explanation`, `requirementsStatus`, `failureReason`?, `rankedAt`, job title/company/url | internal | 404 no job or no match for this profile |
| `/internal/jobs/{jobId}/match/rescore` | POST | `profileId`: guid (req, body) | 202 `{ queued: bool }` (false when extraction is already Pending) | internal | 404 unknown job or profile |
| `/internal/profile/{profileId}/match-profile` | GET | route `profileId` | preferences, `skills[]`, `experiences[]`, `educations[]`; header `ETag` | internal | 404 unknown profile |
| `/internal/profile/{profileId}/match-profile` | PUT | whole document (same shape, child rows with `id` when existing), header `If-Match` (req) | 200 document with new `ETag` | internal | 400 field errors, 412 stale ETag, 428 missing If-Match, 404 |

The list query is `jobs` (not soft deleted) LEFT JOIN `job_matches` for the profile, ordered by (has a non null score) desc, `HasBlocker` asc, `Score` desc, `JobId` asc; `total` counts all non deleted jobs. A missing or soft deleted profile is 404 on every route.

ETag handling: `ResultStatus` gains `PreconditionFailed` (mapped to 412) and `PreconditionRequired` (mapped to 428) in `Result<T>` and `ToHttp`. The ETag is the profile's `xmin` as a quoted decimal string (`"123456"`), mapped as a concurrency token the way `JobsConfigurations` maps `jobs.xmin`. Any other write to the profile row (a name edit) also changes it, so an unrelated save can cause an extra 412; accepted for a single user.

The PUT replaces the whole match profile in one transaction: preferences set, skills upserted by normalized name into the shared `skills` table and the `profile_skills` set replaced, experience and education rows added, updated or removed by id.

**Value sourcing**:
| Action | Value produced / displayed | Source |
|---|---|---|
| Score a match | job requirements | `job_requirements.Requirements` (AI extraction) |
| Score a match | job location, remote type, salary when extraction failed | `jobs.Location`, `jobs.RemoteType`, `jobs.SalaryRangeMin/Max` (currency and period Unknown, so salary is Unknown) |
| Score a match | your years of experience | derived from `experiences.StartDate/EndDate`; "today" is `TimeProvider.GetUtcNow()` date |
| Score a match | your degree level | `max(educations.DegreeLevel)` |
| Score a match | your preferences | `profiles` new columns |
| Score a match | job title, your target roles | `jobs.Title`, `profiles.TargetRoles` |
| `JobMatched` | previous score and blocker | the stored `job_matches` row for (job, profile), read in the batch transaction before the upsert |
| `JobMatched` | threshold | `profiles.StrongMatchThreshold` (part of the profile fingerprint, so changing it rescores) |
| Score a match | weights, `BlockerCap`, confidence thresholds, `SkillAliases` | `Matching` config section |
| Score a match | `ScoringVersion`, `ExtractorVersion` | Domain / Infrastructure constants |
| Score a match | profile fingerprint | SHA-256 of the canonical JSON of the match profile read model (sorted skills, rows by id) |
| Score a match | inputs fingerprint | SHA-256 of: requirements `ContentHash` + `ExtractorVersion` + `Status`; `jobs.Location` and `jobs.RemoteType` (read by the fallback); the profile fingerprint; the canonical JSON of the whole `Matching` scoring config (weights, `BlockerCap`, thresholds, `SkillAliases`); `ScoringVersion`; and today's UTC date only when the profile has an open ended experience row (so years stay fresh at day granularity) |
| Verify quotes | description text | `jobs.Description` truncated at `MaxDescriptionChars` (the exact text extraction saw) |
| Extraction | description sent | `jobs.Description` truncated at `MaxDescriptionChars` |
| Extraction | content hash | latest `job_snapshots.ContentHash` of the primary link |
| Extraction | model id | `ChatResponse.ModelId`, else the configured model for `JobExtraction` |
| List | `profileIncomplete` | derived: profile has no skills or no experience rows |
| Detail | job URL | `jobs.Provenance.SourceUrl` |
| Profile page | ETag | `profiles.xmin` |
| Web | `profileId` | session claims resolved as in spec 0004 |

**Key invariants**:
- A match's `Score` is null or 0 to 100; when `HasBlocker` it is at most `BlockerCap` (Domain enforced, DB check on range).
- One match per (job, profile); one requirements row per job.
- No AI call ever receives profile data; the scorer never calls AI.
- A stored explanation item with `Verified = false` contributed zero points.
- `SalaryCurrency` is set if and only if `MinSalary` is set.
- Weights sum to 100 (startup validation).
- Only Jobs writes `job_requirements`/`job_matches`; only Profile writes profile tables.

**Security model**: single user, internal routes behind the existing network boundary; the Web supplies the session's `ProfileId` and never takes one from the browser. Match reads filter by `ProfileId` (AC-15). The job description is untrusted input to the model: no tools, output schema validated and bounded, quotes verified against the description, so an injected instruction can at most produce wrong requirements, which show as unverified or wrong evidence the founder can see. Profile data (PII such as work authorization) never leaves the server. `Ai:LogSensitiveData` stays false, so prompts are not logged. No compliance scope beyond keeping PII local.

**Observability**: log each extraction with job id, attempt, model, duration and outcome (never the description text); log a warning on `Failed`; log each rescore batch with counts written versus skipped by fingerprint. Hangfire's dashboard shows retries.

**Configuration required** (no new secrets):
- `Matching:Weights:{Skills,Title,Experience,Location,Salary,Education,JobType,WorkAuthorization}`: defaults 30, 15, 15, 15, 10, 5, 5, 5.
- `Matching:BlockerCap`: default 20.
- `Matching:Confidence:High` / `Matching:Confidence:Medium`: defaults 0.8 / 0.5.
- `Matching:MaxDescriptionChars`: default 20000.
- `Matching:SkillAliases`: map of alias to canonical name, e.g. `"js": "javascript"`, `"c-sharp": "c#"`, `"postgres": "postgresql"`.
- `Ai:Purposes:JobExtraction`: optional; falls back to `Default` (committed default `Fake`).

**Critical test scenarios**:
- Happy path: ingest a Greenhouse fixture posting with the Fake provider, profile with matching skills and experience; the event dispatches, extraction and scoring run, `/jobs/{id}` shows a score with quoted evidence and profile pointers, verifies **AC-1**, **AC-2**, **AC-10**, **AC-13**.
- Scorer unit table: all Unknown except skills; mixed Met/Partial/Missed; renormalization; confidence bands at 0.5 and 0.8 exactly; rounding, verifies **AC-3**, **AC-4**.
- Blocker: sponsorship refused plus a 95 point fit ends at 20 with a blocker item; Remote only preference with an onsite job elsewhere, verifies **AC-5**.
- Hallucinated quote: extractor returns a quote absent from the description; item is unverified and scores zero, verifies **AC-6**.
- Idempotency and concurrency: run scoring twice unchanged (no write, `RankedAt` unchanged); two parallel runs after a change end with one row; profile save rescores all; `ScoringVersion` bump rescores, verifies **AC-7**.
- Provider failure: a throwing extractor three times gives `Failed`, a Low confidence rules only match and the notice; Rescore with a working extractor recovers, verifies **AC-8**.
- Schema bounds: 61 skills or a 301 character quote fails the attempt, and the prompt contains no profile field, verifies **AC-12**.
- Merge: merging two jobs keeps one match per profile on the survivor and removes the loser's requirements, verifies **AC-14**.
- Profile page: stale ETag gets 412 and edits stay; bad currency gets a field error, verifies **AC-11**.
- Auth/permission: detail with a second profile's id returns 404, never the first profile's match, verifies **AC-15**.
- Startup: weights summing to 99 fail startup, verifies **AC-16**.
- Title: target role "Backend Engineer" against "Senior Backend Engineer" is Met, against "Data Analyst" is Missed, with no roles set it is Unknown, verifies **AC-2**.
- Threshold crossing: a job going from 60 to 75 with threshold 70 publishes `JobMatched` once; rescoring it at 80 publishes nothing; a blocked job at any score publishes nothing, verifies **AC-17**.
- List ordering and paging: blocked after unblocked, unscored last, banner when no skills, verifies **AC-9**.

## Build plan

Tracer Bullet: the first slice runs one thin thread through every layer (one dimension, real event, real page), then later slices thicken it.

**Slice 1: thin thread, skills only, one job's match visible**
1. [x] Migration `AddJobMatching` for the whole confirmed model (profile columns, `DegreeLevel`, `job_requirements`, `job_matches` changes and index, drop `MatchedSkills`); EF configurations; `xmin` token on `profiles`. One migration because every later slice reads the same columns and the table is still empty, satisfies **AC-2**
2. [x] Domain: requirements v1 document, explanation v1, `MatchScorer` with the skills dimension, renormalization, confidence, quote verification, `ScoringVersion`, inputs fingerprint; `MatchingOptions` with startup validation, satisfies **AC-2**, **AC-3**, **AC-6**, **AC-16**
3. [x] Application: `IJobRequirementExtractor`, `IMatchRepository`, `MatchProfileReadModel` query; Infrastructure: AI extractor on the `JobExtraction` purpose (added to `AiPurposes.All`; no tools, truncation, schema bounds) and the Fake provider's extraction answer, satisfies **AC-12**, **AC-13**
4. [x] `JobContentChanged` event published by the Application callers at job create and primary change; handler; `ExtractJobRequirementsJob` with the raw SQL upsert, satisfies **AC-1**, **AC-7**
5. [x] `GET /internal/jobs/{jobId}/match` and a `/jobs/{id}` page with the match panel, satisfies **AC-10**, **AC-15**

**Slice 2: every dimension and real profile inputs**
6. [x] Scorer: title, experience, location and remote, salary, education, job type, work authorization, blockers and `BlockerCap`, missing and unknown lists, satisfies **AC-3**, **AC-4**, **AC-5**
7. [x] Profile: `PreconditionFailed`/`PreconditionRequired` in `Result<T>` and `ToHttp`; `GET`/`PUT /internal/profile/{profileId}/match-profile` with ETag, validation, skill name normalization, `MatchProfileChanged` event, handler and `RescoreProfileJob`; `JobMatched` on a threshold crossing, satisfies **AC-7**, **AC-11**, **AC-17**
8. [x] `/profile` page (preferences, skills, experience, education, 412 handling) and nav item, satisfies **AC-11**

**Slice 3: list, recovery and upkeep**
9. [x] `GET /internal/matches` and the `/jobs` list with paging, ordering, badges and the incomplete profile banner, satisfies **AC-9**
10. [x] Rescore endpoint and button; extraction failure path (attempt counter, `Failed`, rules only fallback with the location parser, notice), satisfies **AC-8**, **AC-10**
11. [x] Merge and split handling (survivor and split events, requirements cascade) and `MatchSweepJob` plus the Api start enqueue, satisfies **AC-1**, **AC-7**, **AC-14**
12. [x] Tests per the critical scenarios: scorer and fingerprint unit tests in the Domain tests; integration tests with `WebApplicationFactory` on the live Postgres for events, jobs, endpoints and ETag, satisfies **AC-1** to **AC-17**

## Consequences

**Positive**:
- The score is deterministic and unit testable; every point traces to a quote and a profile item, meeting the "not a black box" bar.
- One AI call per job content, reused across rescoring; tuning weights or aliases never costs a model call.
- Profile data never leaves the server.
- `/profile` now exists, which cover letters (15) and application prep (16) will also read.

**Negative / tradeoffs**:
- Extraction quality bounds match quality; a model that misses a requirement makes the dimension Unknown or wrong, and only a changed `ExtractorVersion` re-extracts old jobs (a real AI cost when bumped).
- Normalized name plus aliases misses genuine synonyms not in the alias list; the list needs care as you see misses.
- Same currency salary comparison leaves many salaries Unknown for cross border jobs.
- Stored scores are derived data and can go stale; the fingerprint, the events and the hourly sweep exist only to keep them fresh, which is more moving parts than computing at read time.
- Replacing `job_matches.Score` and dropping `MatchedSkills` is a breaking column change (safe today, the table has no writer).
- Bumping `ExtractorVersion` re-extracts every job with no throttle, a one off AI cost each time.
- Using the profile's `xmin` as the ETag means an unrelated profile write can cause a harmless extra 412.

**Neutral**:
- Three new events (`JobContentChanged`, `MatchProfileChanged`, `JobMatched`) and three new Hangfire jobs join the outbox and recurring job registries.
- Feature 12 builds filters and the two column layout on top of these endpoints; feature 13 can read the same list endpoint.

## Follow-up

- [ ] Feature 12 should reuse `GET /internal/matches` and add filters (match score, visa sponsorship read from `Requirements.Sponsorship`) rather than a second query.
- [ ] Revisit semantic skill matching (embeddings) if alias misses become common in real use.
- [ ] Revisit currency conversion once real cross currency postings show up often.
- [ ] Onboarding (33) and Settings (30) should link to or reuse the `/profile` page rather than a second editor.
- [ ] Decisions made without the engineer when merging the 2026-09-29 draft (please review):
  - New default weights to make room for Title: skills 30, title 15, experience 15, location 15, salary 10, education 5, job type 5, work authorization 5 (was 40, 0, 20, 15, 10, 5, 5, 5). Runner up: keep title out of the score and show it only as a label.
  - A blocked job never raises `JobMatched`, even when the threshold is set at or below `BlockerCap`.
  - The threshold lives on `profiles` and is edited on `/profile`, not on a separate settings page.
