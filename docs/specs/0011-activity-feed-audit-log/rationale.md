# 0011. Activity feed over the audit log: rationale

## Context

Every meaningful action already writes an `AuditLog` row through `IAuditService.Record` in the same transaction as the change (spec 0002, spec 0018 exception list): ingestion runs, merges and splits, agent tool calls, approval requests, gate refusals, decisions, planning failures. Nothing shows them. You cannot see what the Agent did overnight, why a job merged, or which run failed, without querying Postgres.

The scope asks for a timeline filterable by domain (Agent, Jobs, Email, Calendar, System, Errors). The audit row holds `Actor`, `Action`, `TargetType`, `TargetId`, `Payload` (JSON) and `OccurredAt`, but no domain label, and actions are free strings chosen by each module (`JobsIngested`, `ApprovalApproved`, a tool name such as `list_my_profile`). New modules in later waves (outreach, calendar, tasks) add more. The table has no index on `OccurredAt`, and it grows with every ingestion run.

The product has one user; `AuditLog` has no profile column. The Api is internal only (spec 0004). The dashboard (#13) needs the latest few entries, so the read path must be reusable, not page code.

## Options considered

### Option 1: Stored category column (chosen)

Add `AuditLog.Category`, set at write time from a Domain rule, backfilled once, indexed with `OccurredAt`.

**Pros**: filtering is one indexed equality; a new action lands in a sensible category through the fallback rules, not nowhere; the rule lives in one Domain class that unit tests pin.
**Cons**: a migration with a backfill over a table that only grows; changing the rules later needs another backfill for old rows.

### Option 2: Map categories in the query

No schema change; the feed query turns a category into a list of target types and actions.

**Pros**: no migration, no backfill, rules change instantly for old rows too.
**Cons**: an action missing from the map silently drops out of every filter; the `IN` list grows with each module and cannot use one clean index.

## Rationale

The scope's done bar is that every meaningful action shows up. Option 2 fails that bar quietly: a module that adds an action and forgets the map makes it vanish from every filter. Option 1's fallback rules (below) always assign something, and the actor and target type are reliable signals that exist on every row. The backfill is a single `UPDATE` with the same `CASE` the Domain rule encodes, on a table that is still small, so its cost is paid once now rather than on every query. Cursor paging (keyset on `OccurredAt`, `Id`) keeps "Load more" correct while ingestion keeps inserting rows at the top, which offset paging would not.
