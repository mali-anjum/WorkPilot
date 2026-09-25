# Verify: approval engine & Approval center · spec 0007 · updated 2026-09-25
_Steps derived from spec 0007 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones. Run live against the Api (`dotnet run --project src/WorkPilot.Api`, Hangfire worker enabled) on a fresh database (`wp_f08_approvals`, migrations applied from zero on Api start), and the Web host (`dotnet run --project src/WorkPilot.Web`) signed in through the self hosted GoTrue (`docker compose up -d auth` in `supabase/`) with a throwaway user deleted afterwards._

## Commands
- [x] Live schema after start: `approvals` has `EvidenceJson jsonb null`, `ExplicitlyConfirmed bool default false`, `RequestedAt timestamptz default now()`, index `IX_approvals_Status_RequestedAt`; `20260924144636_AddApprovalEngine` in the migrations history → AC-3, AC-9
- [x] `POST /internal/agent/runs` with goals `approval required demo`, `explicit confirmation demo`, `list my profile` → the two gated runs reach `AwaitingApproval` with one `Pending` approval each; `list my profile` completes with no approval row → AC-1
- [x] `GET /internal/approvals?profileId=<owner>` → pending approvals oldest first with id, tier, requested at, run id, goal, ordinal, tool, description, arguments, evidence, and `confirmationPhrase` only on the explicit tier; another profile's call → `{"pending":[],"recentlyDecided":[]}`; empty `profileId` → `400` → AC-4
- [x] With a seeded resume (v1, v2) and cover letter (v1), a new gated run's evidence lists `Resume` v2 and `CoverLetter` v1; after adding resume v3 the stored evidence still shows v2; the `ApprovalRequested` audit row (actor `Agent`) carries the evidence as payload → AC-3, AC-10
- [x] Decide: non owner → `403`; explicit tier Approve with no phrase or a wrong phrase → `422` and still `Pending`; bad decision → `400`; unknown id → `404`; phrase with surrounding spaces → `200` `ExplicitlyConfirmed=true`; second decide → `409` `AlreadyDecided`; Reject → step `Skipped`, run `Failed` → AC-7, AC-8, AC-9
- [x] Approved runs resume and complete (`Succeeded`, `success:true`) → AC-1, AC-2
- [x] Exactly one `ApprovalApproved` / `ApprovalRejected` audit row per decision, actor the deciding profile, payload `{approvalId, agentRunId, toolName, riskTier, decision, explicitlyConfirmed}` → AC-10
- [x] Gate: an explicit tier approval whose recorded tier was tampered to `ApprovalRequired` and approved without a phrase → the tool never executes (0 tool calls), the step goes `Skipped`, the run `Failed`, and an `ApprovalGateRefused` audit row is written → AC-2
- [x] Race: holding an `UPDATE` on the run row in another transaction while deciding → `409` `ConcurrentChange`, approval still `Pending`, no audit row; a later decide succeeds and the run completes → AC-9

## UI / manual
- [x] Signed out `GET /approvals` → `302` to `/login?returnUrl=%2Fapprovals` → AC-5
- [x] Signed in with nothing pending → "Nothing pending" empty state and "No decisions yet" → AC-5
- [x] With three pending approvals → one card each showing tool, risk badge, summary, target, goal, tool description, step and run, requested at, inputs, and document versions (resume v4); the explicit card asks to type `explicit_confirmation_demo` → AC-5
- [x] Form posts: without antiforgery → `400`; signed out (anonymous antiforgery cookie only) → `302` to `/login`, approval unchanged; Approve → `outcome=approved` with `DecidedBy` = the session's profile even when the form carries a forged `decidedBy`; repeat → `already-decided`; explicit without phrase → `confirmation-required`, with phrase → `approved`; Reject → `rejected`; unknown id → `not-found` → AC-6
- [x] Every outcome banner renders (approved, rejected, already decided, confirmation required, not allowed, not found), and "Recent decisions" lists the three decisions with "Approved (confirmed)" on the explicit one → AC-5, AC-6

## Acceptance-criteria coverage
- AC-1 … live tier runs · AC-2 … live gate tamper step · AC-3 … schema + evidence snapshot steps · AC-4 … listing step · AC-5 … page steps · AC-6 … form post steps · AC-7 … non owner `403` (the Web host can't produce it, since it always sends the session's profile) · AC-8 … phrase steps · AC-9 … double decide and live race steps · AC-10 … audit steps

## Automated (`/test`, 2026-09-25)
- [x] `dotnet test WorkPilot.slnx` (with `WORKPILOTDB_CONNECTION` set) → Domain 85/85, Web 112/112, Api 106/106; `dotnet format --verify-no-changes` clean
- Domain `ApprovalPolicyTests` → AC-1, AC-2, AC-8 · Api `ApprovalEndpointsTests` → AC-4, AC-7, AC-8, AC-9 (a real held row lock on the run), AC-10 · Api `AdvanceRunJobTests` (spec 0007 cases) → AC-1, AC-2, AC-3, AC-10 · Web `ApprovalsTests`, `ApprovalCenterEndpointsTests`, `ApprovalCenterClientTests` → AC-5, AC-6
- Not automated: the signed out case goes through a test auth scheme (`401`), not the real cookie scheme's `/login` redirect, which only the live step above proves
