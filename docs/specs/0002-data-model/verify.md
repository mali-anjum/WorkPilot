# Verify: Data model · spec 0002 · updated 2026-09-21

_Steps derived from spec 0002 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

## Commands

- [x] `dotnet ef database update --project src/WorkPilot.Infrastructure --startup-project src/WorkPilot.Api` against a fresh Postgres → applies cleanly, no errors → AC-1
- [x] `docker exec workpilot-supabase-db psql -U postgres -d postgres -c "\dt app.*"` → all 39 tables present in the `app` schema → AC-1, AC-7
- [x] `docker exec workpilot-supabase-db psql -U postgres -d postgres -c "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('auth','storage')"` → unchanged from before the migration (EF never touched these schemas) → AC-7
- [x] `docker exec workpilot-supabase-db psql -U postgres -d postgres -c "\d app.jobs"` (and `app.universities`, `app.professors`, `app.scholarships`, `app.job_snapshots`, `app.programs`) → each shows `Provenance_SourceUrl` and `Provenance_RetrievedAt` as `not null` → AC-2
- [x] `dotnet test tests/WorkPilot.Api.Tests` with `WORKPILOTDB_CONNECTION` set → passes, including the updated `/health/db` test → AC-1

## Unit / manual

- [x] Construct a `JobApplication` (default status `Discovered`) and call `TransitionTo(ApplicationStatus.Submitted)` directly → throws `InvalidOperationException`, no database call made → AC-4
- [x] Call `TransitionTo(ApplicationStatus.Matched)` then `TransitionTo(ApplicationStatus.Withdrawn)` → both succeed (any non-terminal state can withdraw) → AC-4
- [x] Soft delete a `Job` that has a `JobApplication` and `ApplicationEvent` history referencing it (`job.SoftDelete(...)`, save) → the `Job` is excluded from a default `dbContext.Jobs` query, but `JobApplication`/`ApplicationEvent` rows referencing it remain queryable and intact → AC-3
- [x] Create two `ResumeVersion` rows against the same `Resume` → second insert gets `VersionNumber = 2`, first row's content is unchanged (no update path exists on `ResumeVersion`) → AC-5
- [x] Insert an `OAuthConnection` with a real access token, then run `docker exec workpilot-supabase-db psql -U postgres -d postgres -c "SELECT \"AccessToken\" FROM app.oauth_connections"` directly → returns ciphertext, not the raw token → AC-6
- [x] Insert a `WorkflowEvent`/`AgentStep`/`ToolCall` with `OccurredAt`/`Timestamp` older than `AGENT_TRACE_RETENTION_DAYS` (default 90) → confirm it is queryable by that cutoff (the schema supports a retention sweep; the sweep job itself is a later feature) → AC-8

## Acceptance-criteria coverage

- AC-1 (migration applies, all entities created) · covered by the `dotnet ef database update` + `\dt app.*` steps · met
- AC-2 (provenance required on externally sourced entities) · covered by the `\d app.jobs` (etc.) column check · met
- AC-3 (soft delete excludes from default queries, preserves referencing history) · covered by the Job soft-delete manual step · met
- AC-4 (JobApplication state machine enforced in the entity) · covered by the two TransitionTo steps · met
- AC-5 (immutable resume/cover letter versions) · covered by the ResumeVersion insert step · met
- AC-6 (OAuth tokens encrypted at rest) · covered by the direct SQL SELECT step · met
- AC-7 (migrations never touch auth.*/storage.*) · covered by the information_schema count step (33 tables, unchanged) · met
- AC-8 (Agent/workflow trace supports a retention sweep) · covered by the WorkflowEvent insert + interval query step · met

**Verified 2026-09-21**: all 8 acceptance criteria met, evidence captured live against the self hosted Supabase Postgres stack (port 5433), test data cleaned up after.
