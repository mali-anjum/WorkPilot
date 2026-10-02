# 0019. Job matching engine and scoring: rationale

## Context

Ingestion (spec 0008) and deduplication (spec 0017) now fill a shared catalog of canonical jobs from Greenhouse and Lever boards, and a single board can hold hundreds of postings. Without a ranking you would read every one to find the few worth applying to. The scope asks for a score that is explainable: evidence, missing requirements and unknown information, not a black box number.

The inputs are thin. `Profile` has a name, a headline, target roles and a location; salary, remote preference, years of experience and visa need are not stored anywhere, and the `Skill`/`ProfileSkill` tables have never been written. Postings are just as uneven: Greenhouse rarely fills salary, remote type is often missing, and the description is free text. Any design has to cope with "we do not know" on both sides without pretending.

The product runs for one user on one VPS, the AI layer is config driven with a `Fake` default so tests need no key (spec 0006), and the project rule is that Domain and Application are unit tested without infrastructure mocks. Scores feed three later features in this wave (notifications, the jobs list, the dashboard), so the shape of a score must be stable before they are built.

## Options considered

### Option 1: Deterministic rules scorer

A pure Domain function compares structured preferences with job fields and keyword hits in the description, each dimension weighted, each hit quoted as evidence.

**Pros**:
- Every point traces to a quote, which is exactly the explainability the scope asks for.
- Free and instant; rescoring thousands of jobs after a preference change is cheap.
- Deterministic, so unit tests pin every rule.

**Cons**:
- Misses meaning that is not spelled out in known words (synonyms outside the alias list, implied seniority).
- The vocabulary and phrase lists need upkeep, and each change rescored everything.

### Option 2: Hybrid, AI extracts requirements, rules score them

One AI call per new or changed job extracts structured requirements with quotes; the same rules scorer then scores them.

**Pros**:
- Better recall on free text: implied requirements and unusual wording are captured.
- Still explainable, since the model must return quotes.

**Cons**:
- One provider call per job; a large board costs real money and time, and a provider outage stalls matching.
- The `Fake` provider cannot produce useful extractions, so tests need a hand written fake extractor.
- A model can quote text that is not there; quotes must be verified against the description.

### Option 3: AI judges the whole match

The model reads your profile and the posting and returns a score and reasons.

**Pros**:
- Most nuanced reading of both sides.

**Cons**:
- Not repeatable: the same job can score differently on two runs, which breaks the threshold crossing event.
- Hardest to test and highest cost; reasons are prose, not structured evidence.

## Rationale

Option 1 fits the forces in Context best. The scope's bar is "real evidence pointers, not a black box number", and a quote found by a rule is the strongest evidence there is. The threshold event (`JobMatched`) only makes sense if a score is stable between runs, which rules out Option 3. Option 2 adds a provider call per job to a pipeline that currently has none, and its main gain (recall) cannot be judged until real postings show where rules miss. Starting with rules keeps the `JobMatch` shape stable, and an extraction pass can later feed the same scorer (Follow-up) without touching #12, #13 or #20.

Structured preferences you edit beat reading the résumé because the hard rules (visa, remote, salary floor) are not in a résumé at all, and a skills list you control is more precise than keyword scraping. Dealbreakers cap rather than hide because a wrong phrase match hiding a real opportunity is worse than a flagged job you skip. Unknown dimensions are excluded from the score and reported through confidence, so sparse postings are not punished for what they leave out, and the UI shows confidence next to every score to keep that honest.
