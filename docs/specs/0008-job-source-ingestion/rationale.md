# Rationale: job source ingestion and normalization

## Context

Everything in the Job Agent's core loop (dedup, matching, applications) starts from a job posting, and the product's rule is that nothing is invented: every externally sourced record carries provenance (spec 0002, AC-2). Spec 0002 already designed `JobSource`, `Job` and `JobSnapshot` with an owned `Provenance` value, but left the actual ingestion (who sets `RetrievedAt`, how a source is plugged in, what happens on a re-run) to this feature.

The forces: sources differ wildly in shape (ATS JSON APIs, aggregator APIs, later maybe HTML pages), so the source specific part has to be small and isolated; the result has to be stable enough for dedup (feature 10) and matching (feature 11) to build on; runs must be safe to repeat, since ingestion is naturally periodic and Hangfire delivers jobs at least once; and the source must allow programmatic access in its terms (no LinkedIn scraping, nothing needing a paid key).

Not deciding this blocks the whole Job Agent slice: features 10 to 18 all need real canonical jobs to exist.

## Options considered

### Option 1: `IJobSource` returns raw postings, one shared domain normalizer, upsert by external id (chosen)

Each source adapter only maps its wire format to a small `RawJobPosting` record (plus the verbatim JSON). A pure domain `JobNormalizer` turns that into canonical fields and a content hash, and the ingestion use case upserts by `(JobSourceId, ExternalId)`, adding a snapshot only when the hash changes.

**Pros**:
- Source adapters stay tiny and testable against a captured payload; cleanup rules live once.
- Idempotent by construction, which is exactly what Hangfire's at least once delivery needs.
- Snapshots keep the raw truth for audit and dedup without duplicating identical copies.

**Cons**:
- A source with genuinely richer fields (structured salary) needs `RawJobPosting` to grow a field.

### Option 2: each source produces canonical `Job` rows itself

Every adapter does its own normalization and persistence.

**Pros**:
- Maximum freedom per source.

**Cons**:
- Normalization rules drift between sources, which quietly breaks dedup's content hash comparison; persistence logic is copied per source.

### Option 3: store raw only, normalize lazily on read

Ingestion writes only snapshots; canonical fields are computed when something reads them.

**Pros**:
- Simplest write path.

**Cons**:
- Every reader (list filters, matching) pays the cost, and nothing can index or filter canonical columns; contradicts spec 0002's canonical `Job` table.

### Source choice

- **Greenhouse Job Board API (chosen)**: public GET endpoints documented by Greenhouse for embedding and reading a company's job board, no key, JSON with `id`, `title`, `absolute_url`, `location.name`, `company_name`, `first_published`, `updated_at` and (with `content=true`) the HTML description. Checked live on 2026-09-24 against the `gitlab` board (206 postings).
- **Lever postings API (runner up)**: equally public and keyless, similar shape; fewer of the likely target companies use it.
- **Remotive / Arbeitnow style aggregators**: cross company search, but Remotive asks for only a few calls a day and attribution, and aggregator data is second hand (lower confidence).
- **LinkedIn / Indeed**: excluded, their terms forbid scraping.

## Rationale

Option 1 keeps the one piece that must be consistent (normalization and the content hash) in exactly one pure, unit tested place in `Domain`, which is what makes feature 10's dedup trustworthy later. It also puts the only source specific code behind a single interface, so the second source is additive. Upserting by the source's own stable id, and snapshotting only on content change, makes a re-run (manual, scheduled, or a Hangfire retry) harmless.

Greenhouse wins as the first source because it is the employer's own ATS (so provenance confidence is genuinely high), it is explicitly public, and it will likely matter again for feature 18's ATS submission work.

## References

**Project sources**:
- spec 0002 (data model, `Provenance`, `Job`/`JobSnapshot`), spec 0005 (Hangfire job and `/internal/*` endpoint patterns), `AGENTS.md` (Clean Architecture, folder by feature)

**Practices & standards**:
- Idempotent consumers for at least once job delivery; ports and adapters for external sources.
