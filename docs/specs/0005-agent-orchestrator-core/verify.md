# Verify: agent orchestrator core · spec 0005 · updated 2026-09-24
_Steps derived from spec 0005 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones. Run live against the Api (`dotnet run --project src/WorkPilot.Api`, Hangfire worker enabled) and a fresh self hosted Postgres (`docker compose up -d db` in `supabase/`, migrations applied from zero on Api start)._

## Commands
- [x] `POST /internal/identity/profile {"authUserId":..., "email":...}` → `{"profileId":...}` (the profile every run below uses)
- [x] `POST /internal/agent/runs {"goal":"list my profile","profileId":...}` → `202`, `status:"Planning"`; polling `GET /internal/agent/runs/{id}` → `Completed`, one step `list_my_profile` `Succeeded`, `success:true` → AC-1, AC-3
- [x] `POST /internal/agent/runs {"goal":"approval required demo",...}` → run and its step reach `AwaitingApproval`, `success:null`, no `ToolCall` row yet → AC-4
- [x] `POST /internal/agent/runs` with an empty goal → `400`; with an unknown `profileId` → `404` → API surface errors
- [x] `POST /internal/agent/approvals/{id}/decide {"decision":"Approve","decidedBy":<profileId>}` → `200`, `resumed:true`; the run → `Completed`, step `Succeeded`; the same call again → `409` → AC-4, AC-10's decision guard
- [x] `select "Actor","Action" from app.audit_logs` → `"Agent"` for every tool call, the deciding `ProfileId` for `ApprovalApproved` → AC-5
- [x] Restart survival, awaiting approval: the approval-required run above stayed `AwaitingApproval` across four Api restarts (including two `kill -9`s) and completed on approval afterward → AC-9
- [x] Restart survival, mid-planning: `kill -9` the Api right after triggering a run (its `PlanRunJob` already fetched by the dying worker, run left at `Planning` with no steps) → on restart the run resumes and reaches `Completed` → AC-9. **First run of this step failed**: the run stayed at `Planning` for 2+ minutes after restart, because Hangfire.PostgreSql only re-fetches a dead worker's job after its `InvisibilityTimeout` (default 30 min). Fixed in `src/WorkPilot.Api/Program.cs` with a sliding 1 minute invisibility timeout; re-run passed (the stuck run completed within 3s of restart; a freshly simulated dead lease, `update hangfire.jobqueue set fetchedat = now()`, recovered in ~75s)
- [x] `dotnet test WorkPilot.slnx` (with `WORKPILOTDB_CONNECTION` set) → Domain 47/47, Web 68/68, Api 31/31; `dotnet format --verify-no-changes` clean

## Acceptance-criteria coverage
- AC-1 (plan persisted as steps, planning in a background job) … live trigger step
- AC-2 (policy rejects an invalid plan before any step runs) … `PlanRunJobTests` only; the live `FakeChatClient` never emits an invalid plan
- AC-3 (auto-allowed tool executes and verifies) … live `list my profile` step
- AC-4 (approval suspends, approve resumes) … live approve step; reject path by `AgentOrchestratorEndpointsTests`
- AC-5 (audit per tool call and approval create/decide) … live audit query, plus `AdvanceRunJobTests` payload cases
- AC-6 (no secret or `ProfileId` reaches the Planner) … structural (`PlanRunJob` passes only goal + tool descriptors), plus the endpoint tests
- AC-7 (one planner retry, then `Failed`) … `ChatClientPlannerTests` only; not reachable live with `FakeChatClient`
- AC-8 (retry exhaustion fails step and run) … `AdvanceRunJobTests` only
- AC-9 (restart survival) … both live restart steps; the idempotent vs non-idempotent `Running` re-entry branches by `AdvanceRunJobTests`
- AC-10 (independent concurrent runs, no lost update) … `409` on a second decision live; a true simultaneous race on one run's `xmin` token is not exercised, and a `DbUpdateConcurrencyException` is still uncaught (known gap, deferred to the API error handling decision)
