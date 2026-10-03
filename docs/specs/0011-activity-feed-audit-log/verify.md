# Verify: activity feed & audit log · spec 0011 · verified 2026-10-03
_Steps derived from spec 0011 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

Setup: the self hosted Supabase stack up (`docker compose up -d` in `supabase/`), the Api on `:5199` and the Web on `:5292`, signed in. `A` = `http://localhost:5199/internal/audit/activity`. `q` = `docker exec workpilot-supabase-db psql -U postgres -tA -c`.

_`/check verify` 2026-10-03: every ticked step was run against the live Api, Web and Postgres in headless Chrome with a throwaway user (deleted afterwards, with its seeded rows). Backfill parity was checked against `AuditCategories.For` itself: 11 groups, 0 mismatches. A bad `before` value (not a date) gives 500 in Development, like every minimal API query value here (`/internal/jobs?take=abc` too), because Development throws on a binding failure; that is outside this spec._

## UI / manual
- [x] Open `/activity` → Activity is the first item under Agent in the nav; entries newest first, 50 shown, a "Load more" button below (seen) → AC-1
- [x] Click "Load more" until the end → rows append with no repeats, then "You have reached the start of the feed" (seen: 98 rows, 0 duplicates) → AC-1
- [x] Hover an entry's time → the exact time shows in your browser's time zone with the zone name; switch the browser (or OS) zone and reload → the tooltip follows it (seen with America/Los_Angeles) → AC-1, Value sourcing "When"
- [x] An approval you decided shows `You`; an Agent row shows `Agent`; a row by another profile id shows `System` (seen) → AC-1, Value sourcing "Who"
- [x] Click the `Errors` chip → the URL becomes `/activity?category=errors`, only Errors rows remain, the chip is marked current; reload → still filtered (seen) → AC-2
- [x] Try `?category=JoBs` → same rows as `?category=jobs` → AC-2
- [x] Open an entry → its evidence shows key and value pairs, nested objects as indented JSON; open an entry with no payload → "No evidence was recorded" (seen) → AC-4
- [x] Open an entry whose payload is over 10 KB → a truncated, scrollable block with "Show all"; click it → the full key and value view (seen) → AC-4
- [x] Target links: a `Job` row → `/jobs/{id}`, a `JobSource` row → `/jobs?source={id}`, an `AgentStep` row → `/approvals`, an `AgentRun` row → `/agent/runs`; a `Profile` row is plain text (seen) → AC-5
- [x] Summaries: `JobsIngested` reads "Ingested jobs from a source: N new, M updated" with the counts from its payload; `JobsMerged`, `JobLinkSplit`, `ApprovalRequested`, `ApprovalApproved`, `ApprovalRejected`, `ApprovalGateRefused`, `PlanningFailed` read as in AC-6; a tool name such as `list_my_profile` shows raw (seen) → AC-6
- [x] `?category=email` with no Email rows → "Nothing in this category yet"; an empty database → "Nothing has happened yet" (first seen) → AC-7
- [x] `?category=nope` → the error state shows the ProblemDetails message and a Retry button (seen) → AC-7
- [x] Stop the Api, then click "Load more" → the 50 rows stay, an error and a Retry button show; start the Api and click Retry → the next page appends (seen up to the error) → AC-7

## Commands
- [x] `q 'select count(*) from app.audit_logs where "Category" is null or "Category" = '"''"`'` → `0` → AC-3
- [x] Backfill parity: for each distinct (`Actor` is `Agent` or not, `Action`, `TargetType`) in `audit_logs`, the stored `Category` equals `AuditCategories.For` → AC-3
- [x] A failed tool call (an Agent row whose payload is `{"error": ...}`) has `Category` = `Errors` after the backfill → AC-3
- [x] A failed tool call newly written by `AdvanceRunJob` gets `Errors` (no real tool fails on demand, so `AdvanceRunJobTests` runs the real job against Postgres with a failing tool to prove it) → AC-3
- [x] Trigger a new audited action (an ingestion via `POST /internal/jobs/ingestions`, or an approval decision) → its new row has the category the rule gives (`Jobs`, `Agent`) → AC-3
- [x] `curl "$A?take=3"`, then the same with `before`/`beforeId` from `nextBefore`/`nextBeforeId` → no overlap, strictly older; insert a row between the two calls → the second page is unchanged → AC-1, Key invariants
- [x] `curl "$A?category=nope"`, `"$A?category=3"`, `"$A?before=2026-10-01T00:00:00Z"`, `"$A?take=0"`, `"$A?take=101"` → each a 400 ProblemDetails naming the field → AC-2, AC-7
- [x] `EXPLAIN` the filtered cursor query → it uses `IX_audit_logs_feed_category` → Data model
- [x] `IActivityQuery` is the only reader of `audit_logs` for the feed (`grep -rn "AuditLogs" src --include=*.cs` shows only the query, the writer `AuditService` and the `DbSet`) → AC-8

## Acceptance-criteria coverage
- AC-1 … UI steps 1 to 4, Commands step 4 · AC-2 … UI steps 5, 6, Commands step 5 · AC-3 … Commands steps 1 to 3 · AC-4 … UI steps 7, 8 · AC-5 … UI step 9 · AC-6 … UI step 10 · AC-7 … UI steps 11 to 13, Commands step 5 · AC-8 … Commands step 7 (the dashboard reuse lands with #13)
