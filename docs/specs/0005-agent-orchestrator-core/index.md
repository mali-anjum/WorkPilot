# 0005. Agent orchestrator core

**Date**: 2026-09-23
**Status**: Accepted

## Summary

This decision designs the shared machinery every domain agent (Job, University, Personal) will run its work through: a Planner that turns a plain goal into a plan, a Policy Engine that checks each planned action is allowed, a Tool Registry that declares what actions exist and how risky they are, an Execution Engine that runs them, a Verification Engine that checks the result, an Approval Engine that pauses for a human decision when needed, and an Audit trail that records what happened. For this milestone it proves the whole pipeline end to end with two small tools (one that needs no approval, one that does), reusing the database tables already designed in spec 0002 and the durable job runner (Hangfire) already running in this project. Nothing here picks which AI model answers the Planner's questions; that is decided separately in [0006](../0006-ai-provider-abstraction/index.md).

## Context

> Premise note (updated 2026-09-25): this feature takes an injected `IChatClient` (the provider agnostic AI abstraction from `Microsoft.Extensions.AI`) and does not pick or configure a model itself. That choice was scope item 7, now settled by [0006](../0006-ai-provider-abstraction/index.md): each purpose (the Planner uses `Planner`, falling back to `Default`) gets a keyed `IChatClient` whose provider and model come from configuration only. The committed default is still the deterministic `Fake` provider, so builds and tests need no key.

Every later domain agent (Job Agent's matching and application prep, University Agent's outreach, Personal Agent's task suggestions) needs the same underlying machinery: turn a goal into a plan, check the plan is allowed, run it, verify it worked, and leave a durable trail. Building this per domain agent would mean three copies of the same policy, retry, and audit logic drifting apart over time. The scope's own "done when" line fixes the pipeline shape (Planner → Policy Engine → Tool Registry → Execution → Verification → Audit) and two hard requirements that shape every choice below: workflows must persist step by step and survive a process restart mid run (an in memory only implementation is disqualified outright), and the LLM must never receive passwords, OAuth tokens, refresh tokens, client secrets, or session cookies (it only ever calls tools, never touches credentials directly).

The product is single user today (the founder), so this milestone does not need multi tenant isolation, high concurrency, or a polished human approval screen; the real Approval Center (scope item 8) is a separate, later decision that builds on whatever minimal decision path this spec puts in place. The data model this feature persists into (`AgentRun`, `AgentStep`, `ToolCall`, `WorkflowInstance`, `WorkflowStep`, `WorkflowEvent`, `Approval`, `AuditLog`) was already designed in spec 0002, which explicitly deferred the final word on whether that shape fits the orchestrator's real flow to this spec.

## Requirements

**User stories**:
- As the developer, I want a trivial goal ("list my profile") to run through the full pipeline and durably persist its trace, so that the orchestrator's machinery is proven before any domain agent builds on it.
- As the developer, I want an approval-required action to pause a run rather than execute silently, so that the three tier policy (auto allowed / approval required / explicit confirmation) is real, not just documented.
- As the founder, I want every tool call recorded in the audit trail, so that I can see exactly what the agent did and why, even for actions that didn't need my approval.

**Acceptance criteria** (the contract, each criterion is independently checkable):
- **AC-1**: Given a goal string submitted to the trigger endpoint, the Planner produces an ordered plan of one or more tool calls (tool name + arguments, capped at 10 steps) via a single upfront `IChatClient` call, and the plan is persisted as `AgentStep` rows (with their `Ordinal`, `ToolName`, `ArgumentsJson`) under a new `AgentRun`. Planning itself runs as its own background job, not inline in the request, so it is restart safe (see AC-9).
- **AC-2**: Before any step executes, the Policy Engine validates that every planned step's tool exists in the Tool Registry and its arguments validate against that tool's declared schema ("permitted"); if any planned step fails this check, the whole run fails before any step executes (no partial execution of an invalid plan). An empty plan counts as a parse failure under AC-7, not a valid zero-step run.
- **AC-3**: An auto-allowed tool call (e.g. "list my profile") executes without pausing, records a `ToolCall` row with its outcome, passes the Verification Engine's structural check, and the run reaches `Completed`. A `ToolCall.Success` is true only when both the tool itself reported success AND the Verification Engine's check passed; a verification failure fails the step and the run, same as a tool failure.
- **AC-4**: An approval-required tool call suspends the run (its `AgentStep.Status` becomes `AwaitingApproval`, with an `Approval` row created and audited) before executing and does not execute until a decision is recorded; approving resumes and executes it, rejecting fails that step and the run. No `ToolCall` row exists for that step until it actually executes.
- **AC-5**: Every `ToolCall` (success or failure) and every `Approval` created or decided writes an `AuditLog` entry (actor, action, target, outcome).
- **AC-6**: The Planner's `IChatClient` call never receives a password, OAuth token, refresh token, client secret, session cookie, or profile identifier as an argument it could echo back; the calling `ProfileId` is injected into tool execution context by the engine itself, never passed through the LLM. Any tool needing credentials resolves them itself, out of band from the Planner.
- **AC-7**: If the Planner's output fails to parse into a valid, non-empty plan, the planning call is retried once with the parse error fed back; a second failure marks the run `Failed`.
- **AC-8**: A tool call that exhausts its declared retry policy fails its `AgentStep` and the parent `AgentRun`; no further planned steps execute afterward.
- **AC-9**: A running `AgentRun` (one step `Succeeded`, resumption driven purely by step status stored in the database) survives an Api process restart at any point, including mid-planning and between steps: the run resumes and completes without re-executing an already `Succeeded` step, and without double-executing a side-effecting step that crashed mid-execution (idempotent tools may safely re-run a `Running` step; non-idempotent tools fail it instead of silently retrying).
- **AC-10**: Two independently triggered `AgentRun`s can execute at the same time without corrupting or blocking each other's state (an optimistic concurrency token prevents a lost update between a step's own job and a concurrent approval decision on the same run).

## Options considered

### Option 1: Plan-then-execute pipeline, one idempotent `AdvanceRun` job per run (chosen)

A single upfront LLM call produces an ordered plan; the Policy Engine validates the whole plan before execution starts; a single Hangfire job type, `AdvanceRun(runId)`, drives the whole run: on each invocation it reads the run's steps from the database, finds the first non-terminal one, executes it if it's `Pending`, and either re-enqueues itself for the next step or exits at an `AwaitingApproval` gate. The database, not Hangfire's own job bookkeeping, is the resumption cursor, which is what makes restart survival correct rather than merely present.

**Pros**:
- Matches the scope's own linear pipeline description exactly; simplest to reason about, test, and verify.
- Reuses Hangfire, already provisioned and durable in this stack, for restart survival; no new persistence mechanism to build.
- Because the DB (not the job queue) is the source of truth for "what's already done," Hangfire's at-least-once job semantics (a killed job can be re-run) don't cause a completed step to re-execute, and a non-idempotent step that crashed mid-execution fails cleanly instead of silently re-running.

**Cons**:
- Cannot adapt mid-plan: if step 2's real-world result invalidates step 3's assumption, the plan doesn't notice until step 3 fails outright.
- A single job type re-enqueuing itself per step is a slightly less obvious mental model than "one job per step," and needs its own idempotency discipline (mark `Running` before executing, check that flag on re-entry) that a naive per-step job design would have skipped and gotten wrong.

### Option 2: ReAct-style iterative agent loop

The LLM re-plans after every tool result (think, act, observe, repeat) instead of committing to a plan upfront.

**Pros**:
- Handles open-ended goals and unexpected tool results far better; each step can genuinely react to the last.

**Cons**:
- Making an LLM call itself durable and restart-safe (not just the tool execution around it) is materially harder, and a bigger lift than a "trivial tool call" milestone needs.
- Every extra LLM call in the loop adds latency and cost with no bound on how many turns a goal takes.

### Option 3: Fixed workflow templates per goal type, no LLM planning

A goal is matched to a predefined, hardcoded step sequence (a state machine per goal type); the LLM is used only inside individual steps, never to produce the plan itself.

**Pros**:
- Fully deterministic and cheap to run; no plan-parsing failure mode at all (AC-7 wouldn't exist).

**Cons**:
- Defeats the point of an agent: every new goal needs a new hand-written template, which doesn't generalize across three different domain agents the way a real Planner does.

## Decision

**Chosen option**: Option 1: Plan-then-execute pipeline, one idempotent `AdvanceRun` job per run.

**Implementation skills**: `microsoft-extensions-ai` (`.claude/skills/microsoft-extensions-ai/`) · `aspire` (`.claude/skills/aspire/`) · `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`)

## Rationale

Reasoning and full option tradeoffs: see [rationale.md](rationale.md).

## Feature design

**Data model sketch**:

Reuses the entities spec 0002 already designed, with one small migration (Build plan task 1) to close gaps the cross check on this spec found: `AgentStep` as drafted in 0002 has no `Status`/`Ordinal`/`ToolName`/`ArgumentsJson`, and `AgentRun` has no `ProfileId`, both of which this feature's own acceptance criteria need a source for:

| Entity | Key fields | Relationships |
|---|---|---|
| `WorkflowInstance` | `DefinitionName`, `Status` | 1:1 with `AgentRun` |
| `WorkflowStep` | FK `WorkflowInstanceId`, `StepName`, `Status`, `PayloadUrl` (nullable) | N:1 `WorkflowInstance` |
| `WorkflowEvent` | FK `WorkflowInstanceId`, `EventType`, `PayloadUrl` (nullable), `OccurredAt` | N:1 `WorkflowInstance` |
| `AgentRun` | FK `WorkflowInstanceId` (1:1), **`ProfileId` (new FK)**, `Goal`, `Status`, **`RowVersion` (new, `xmin`-backed concurrency token)** | 1:N `AgentStep` |
| `AgentStep` | FK `AgentRunId`, **`Ordinal` (new, int)**, **`ToolName` (new, string)**, **`ArgumentsJson` (new, jsonb, inline not Storage, arguments are small)**, `ReasoningUrl` (nullable), `Timestamp`, **`Status` (new: `Pending`, `AwaitingApproval`, `Running`, `Succeeded`, `Failed`, `Skipped`)** | N:1 `AgentRun`, 0:1 `ToolCall` |
| `ToolCall` | FK `AgentStepId`, `ToolName`, `InputPayloadUrl` (nullable), `OutputPayloadUrl` (nullable), `Success` | N:1 `AgentStep` |
| `Approval` | `TargetType` (**now `"AgentStep"`**, not `ToolCall`, since a pending approval has no `ToolCall` row yet), `TargetId` (points at the `AgentStep`), `RiskTier`, `Status` (`Pending`/`Approved`/`Rejected`), `DecidedAt` (nullable), `DecidedBy` (nullable) | referenced by `TargetId`, no FK |
| `AuditLog` | `Actor`, `Action`, `TargetType`, `TargetId`, `Payload` (jsonb), `OccurredAt` | append only, one row per `ToolCall` AND per `Approval` create/decide |

**State transitions**:

`AgentRun.Status` (mirrored onto its 1:1 `WorkflowInstance.Status`): `Planning` → `PolicyCheck` → `Executing` → `Completed`; `Executing` also reaches `AwaitingApproval` when its current step needs a decision, returning to `Executing` on approval; any non terminal state → `Failed`.

- `Planning`: the run is created in this state by the trigger endpoint (which returns immediately, see API surface); a background job runs the upfront Planner call, including the one allowed retry on a parse failure (AC-7).
- `PolicyCheck`: the whole plan is validated against the Tool Registry (tool exists, arguments match its schema) before any step runs (AC-2).
- `Executing`: the `AdvanceRun` job is working through `AgentStep`s in `Ordinal` order.
- `AwaitingApproval`: the current step's `AgentStep.Status` is `AwaitingApproval` and an `Approval` row is `Pending`; the run has no job in flight until `/internal/agent/approvals/{id}/decide` resolves it.
- `Completed` / `Failed`: terminal. `Failed` is reached from `Planning` (AC-7, empty or unparseable plan after one retry), `PolicyCheck` (AC-2), or `Executing` (AC-8, a step's retries exhausted, or AC-3, its Verification check failed); a `Reject` decision at `AwaitingApproval` also routes to `Failed`.

`AgentStep.Status` (per step, drives `AdvanceRun`'s resumption logic): `Pending` → `Running` → `Succeeded` | `Failed`; `Pending` → `AwaitingApproval` → `Running` (on approve) or `Skipped` (on reject, which also fails the run). `AdvanceRun` treats a step it finds already `Succeeded` as done and moves to the next; a step it finds `Running` (meaning the process died mid-execution) is retried only if the tool declares itself idempotent, otherwise that step and the run fail rather than risk a double side effect.

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/agent/runs` | POST | `goal:string` (req), `profileId:guid` (req) | `agentRunId`, `status:"Planning"` (202 Accepted; planning and execution both happen in background jobs, never inline in the request) | internal only, never externally routed (mirrors `/internal/identity/profile`) | 400 empty goal, 404 unknown profile |
| `/internal/agent/runs/{id}` | GET | `id:guid` (route) | `agentRunId`, `status`, `steps: [{ordinal, toolName, status, success}]` | internal only | 404 unknown run |
| `/internal/agent/approvals/{id}/decide` | POST | `decision:"Approve"\|"Reject"` (req), `decidedBy:guid` (req, the deciding profile) | `approvalId`, `status`, `resumed:bool` (true only when `decision` was `Approve`) | internal only | 404 unknown approval, 409 already decided (an atomic `UPDATE ... WHERE Status = 'Pending'` is the guard, not a read-then-write check) |

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| POST `/internal/agent/runs` | `agentRunId` | generated (GUID v7) at creation, per spec 0002's key convention |
| POST `/internal/agent/runs` | `AgentRun.ProfileId` | request body `profileId`; stored on the run and injected into every tool's execution context by the engine itself, never passed to the Planner as an LLM argument (AC-6) |
| `PlanRun` background job | the plan (`AgentStep.Ordinal`/`ToolName`/`ArgumentsJson` rows) | produced by the Planner's `IChatClient` call, given the goal and the Tool Registry's declared tool names/schemas as prompt context; structured output shaped `{steps: [{tool, arguments}]}`, capped at 10 steps |
| Policy check | "permitted" (AC-2) | the tool named in each step exists in the Tool Registry AND its `arguments` validate against that tool's own declared JSON schema |
| Step execution | `ToolCall.OutputPayloadUrl`/inline result | the tool's own `ITool` implementation, executing against the domain's existing repositories, with `AgentRun.ProfileId` supplied by the engine |
| Step execution | `ToolCall.Success` | true only when the tool itself reported success AND the Verification Engine's structural check (output matches the tool's declared schema) passed |
| Step execution | `Approval.RiskTier` | the tool's own `ITool` declaration (baked in, not runtime configurable, per the engineer's confirmed choice) |
| Every `ToolCall`, and every `Approval` create/decide | `AuditLog.Actor` | constant `"Agent"` for tool calls; `"Agent"` for an approval's creation and the deciding `ProfileId` for its decision (there is no other actor in a single-user system) |
| Every `ToolCall` | `AuditLog.TargetType`/`TargetId` | the tool's own declared target reference (nullable when a tool has no single target, e.g. "list my profile" targets the calling `ProfileId`) |
| POST `/internal/agent/approvals/{id}/decide` | `Approval.DecidedBy` | request body, the deciding profile's id (the founder; no role check beyond existing) |
| Step execution | timeout / retry cutoff | the tool's own `ITool` declaration; defaults below when a tool doesn't override them |
| `AdvanceRun` re-entry after a crash | whether a `Running` step is retried or failed | the tool's own `ITool.IsIdempotent` flag (new, baked into the declaration alongside risk tier); idempotent tools are safely re-run, non-idempotent ones fail the step instead |

**Key invariants**:
- A plan is validated in full before any step executes; a run never partially executes an invalid plan.
- An approval-required step never executes before its `Approval.Status` is `Approved`.
- The Planner never receives a secret (password, OAuth/refresh token, client secret, session cookie) or the raw `ProfileId` in its prompt, context, or the plan it reads back; only the engine (never the LLM) injects `ProfileId` into tool execution, and only a tool's own execution code resolves credentials.
- A step's retry exhaustion, or a Verification failure, fails its `AgentRun`; later planned steps never execute after a fail.
- Every `ToolCall`, and every `Approval` create/decide, has exactly one corresponding `AuditLog` entry.
- Resumption after a restart is driven entirely by `AgentStep.Status` in the database, never by Hangfire's own job bookkeeping: a `Succeeded` step is never re-executed; a `Running` step found on re-entry (the process died mid-execution) is re-run only if its tool declares itself idempotent, otherwise it fails rather than risk a double side effect.
- `AgentRun.RowVersion` guards against a lost update between a step's own `AdvanceRun` job and a concurrent approval decision touching the same run.

**Security model**:
Single user product; all three endpoints above are internal only, following the same non-public-route pattern already used by `/internal/identity/profile` (spec 0004): never registered on the public-facing router, reachable only from within the trusted Api process boundary. No roles or per-tenant scoping needed at this stage (noted as a gap to revisit if the product ever serves more than one user, same caveat spec 0002 already recorded). The Planner/LLM boundary is the actual security surface: tools that need OAuth tokens or other credentials resolve them via the existing Data Protection-backed `OAuthConnection` service (spec 0002), never passing the raw credential through the Planner's prompt, the persisted plan, or any `ToolCall` payload.

**Configuration required**:
- `AGENT_TOOL_DEFAULT_TIMEOUT_SECONDS`: default per-tool call timeout when a tool doesn't declare its own (default `30`).
- `AGENT_TOOL_DEFAULT_MAX_RETRIES`: default retry count for an auto-allowed tool that doesn't declare its own (default `2`, exponential backoff); an approval-required tool always defaults to `0` retries regardless of this setting, since retrying a side-effecting action after a human already approved it once risks a double-execution.
- Reuses `AGENT_TRACE_RETENTION_DAYS` (spec 0002, default `90`) for the retention sweep over `AgentStep`/`ToolCall`/`WorkflowEvent`; no new retention config needed here.

**Critical test scenarios**:
- Happy path: trigger a run with goal "list my profile" → Planner plans one auto-allowed step → executes → `Completed`, with `AgentRun`/`AgentStep`/`ToolCall`/`AuditLog` rows all present and correctly linked, verifies **AC-1**, **AC-3**, **AC-5**, **AC-6**.
- Failure case (clean restart): kill and restart the Api process after step 1 of a 2 step run reaches `Succeeded` but before step 2's `AdvanceRun` job runs → on restart, Hangfire resumes the pending job, which reads `AgentStep.Status` from the database and skips the already-`Succeeded` step 1 → the run reaches `Completed` without re-executing it, verifies **AC-9**.
- Failure case (mid-step crash): kill the process while a non-idempotent step's `AgentStep.Status` is `Running` (before it reaches `Succeeded`) → on restart, `AdvanceRun` finds the `Running` step, does not blindly re-run it, and fails the step and the run instead of risking a double side effect, verifies **AC-9**.
- Approval path: trigger a run whose plan includes the approval-required dummy tool → run reaches `AwaitingApproval` and no `ToolCall` row exists yet for that step → `POST .../decide` with `Approve` → the step executes and the run completes; a second run rejected at the same point fails the run and never executes the tool; both the approval's creation and its decision write `AuditLog` rows, verifies **AC-4**, **AC-5**.

## Build plan

1. Migration: add `AgentRun.ProfileId` (FK) and `AgentRun.RowVersion` (`xmin` concurrency token); add `AgentStep.Ordinal`, `ToolName`, `ArgumentsJson` (jsonb), `Status`; change `Approval.TargetType` usage to `"AgentStep"`. Closes the schema gaps spec 0002 left for this feature, satisfies **AC-1**, **AC-2**, **AC-4**, **AC-6**, **AC-10**
2. Define the `ITool` contract (name, JSON schema, required permissions, risk tier, approval requirement, timeout, retry policy, `IsIdempotent` flag, audit/target metadata) and a DI-registered Tool Registry in `Domain`/`Application`, satisfies **AC-2**, **AC-6**, **AC-9**
3. Implement the "list my profile" auto-allowed tool against the existing Profile repository, satisfies **AC-3**
4. Implement the Planner: a `PlanRun` background job (not inline in the request) that makes a single `IChatClient` call turning a goal + the Tool Registry's declared schemas into a structured `{steps: [{tool, arguments}]}` plan (capped at 10 steps), persisted as `AgentStep` rows; retries once on a parse or empty-plan failure before failing the run (AC-7), satisfies **AC-1**, **AC-7**, **AC-9**
5. Implement the Policy Engine: validates every planned step's tool exists and its arguments match that tool's schema, before any step executes, satisfies **AC-2**
6. Implement the Execution Engine as a single idempotent `AdvanceRun(runId)` Hangfire job (`[AutomaticRetry(Attempts = 0)]`) that reads `AgentStep.Status` from the database to find and execute the next step, marks it `Running` before executing, applies the tool's declared timeout/retry policy, persists its `ToolCall` row, and re-enqueues itself for the next step (or exits at an `AwaitingApproval` gate); retry exhaustion fails the step and the run; on re-entry a `Succeeded` step is skipped and a `Running` step is retried only if its tool is idempotent, satisfies **AC-3**, **AC-8**, **AC-9**
7. Implement the Verification Engine's structural check (tool reported success and output matches its declared schema), wired to run immediately after each step executes; a failure fails the step and the run, satisfies **AC-3**
8. Wire the Audit step so every `ToolCall` (success or failure) and every `Approval` create/decide writes an `AuditLog` entry, satisfies **AC-5**
9. Build `POST /internal/agent/runs` (creates the `AgentRun` at `Planning`, enqueues `PlanRun`, returns 202) and `GET /internal/agent/runs/{id}` (internal-only, mirrors the `/internal/identity/profile` registration pattern), satisfies **AC-1**, **AC-9**, **AC-10**
10. Prove the thin end-to-end thread live: trigger "list my profile" through the full pipeline and confirm every row lands correctly, satisfies **AC-1**, **AC-3**, **AC-5**, **AC-6**
11. Implement the approval-required dummy tool and the Approval Engine (creates an `Approval` row targeting the `AgentStep`, suspends the run at `AwaitingApproval`, 0 retries), satisfies **AC-4**
12. Build `POST /internal/agent/approvals/{id}/decide` with an atomic `UPDATE ... WHERE Status = 'Pending'` guard against a double decision, re-enqueuing `AdvanceRun` on `Approve` and failing the run on `Reject`, satisfies **AC-4**, **AC-10**
13. Prove restart resilience: a clean restart between two steps resumes without re-executing the completed one, and a restart mid non-idempotent step execution fails that step rather than double-executing it, satisfies **AC-9**
14. Prove two concurrently triggered runs complete independently without interfering, and that a concurrent step-advance and approval-decide on the same run don't lose an update (`RowVersion`), satisfies **AC-10**

`/develop` (2026-09-23): all 14 tasks built. Migration applied and confirmed live against the self hosted Supabase Postgres stack (`agent_steps.Ordinal`/`ToolName`/`ArgumentsJson`/`Status`, `agent_runs.ProfileId`, `approvals.DecidedBy` now `uuid`, `agent_runs`'s `xmin` mapped as a shadow concurrency token). `ITool`/`ToolRegistry`/`PolicyEngine`/`VerificationEngine` in `Domain`/`Application`; `AuditService` and the two milestone tools in `Infrastructure`; `PlanRunJob`/`AdvanceRunJob` (the single self re-enqueuing `AdvanceRun` job) in `Workers`; `ChatClientPlanner` plus a deterministic `FakeChatClient` stand-in (scope item 7 not yet decided) in `AI`; the three endpoints in `Api/Program.cs`. Exercised live end to end against the real stack, Hangfire's worker enabled: the auto-allowed path (`list_my_profile`) reached `Completed` with every `AgentRun`/`AgentStep`/`ToolCall`/`AuditLog` row present and correctly linked (AC-1, AC-3, AC-5, AC-6); the approval-required path suspended at `AwaitingApproval` with no `ToolCall` row yet, `POST .../decide` with `Approve` resumed and completed it, and a repeat decide on the same approval returned 409 (AC-4, AC-10's decision race guard). AC-9's two specific crash scenarios (a clean restart between two `Succeeded`/`Pending` steps, and a restart mid non-idempotent step's `Running` window) and AC-10's true concurrent-trigger case were reasoned through and code-level guarded (the DB-status-driven `AdvanceRun` resumption logic, and `AgentRun`'s `xmin` concurrency token) but not exercised by an actual process kill or a real concurrent request pair this session; left for `/check verify` to drive directly. Code in `src/WorkPilot.Domain/Modules/Agent/`, `src/WorkPilot.Domain/Modules/Approvals/`, `src/WorkPilot.Application/Modules/Agent/`, `src/WorkPilot.Infrastructure/Modules/Agent/`, `src/WorkPilot.Infrastructure/Persistence/Configurations/AgentConfigurations.cs` + `ApprovalConfiguration.cs`, `src/WorkPilot.AI/Agent/`, `src/WorkPilot.Workers/Agent/`, `src/WorkPilot.Api/Program.cs`.

`/develop` (2026-09-23): 19 new tests, all passing (Domain 47/47, Api 12/12 total including these, Web unaffected at 68/68). `tests/WorkPilot.Domain.Tests/AgentOrchestratorTests.cs`: the `AgentRun`/`AgentStep` state machines (every valid transition plus rejected/terminal ones) and `Approval.Decide`'s single-decision invariant, no database. `tests/WorkPilot.Api.Tests/AgentOrchestratorEndpointsTests.cs`: trigger validation (empty goal 400, unknown profile 404, a known profile creates a `Planning` run and mirrors `WorkflowInstance.Status`), `GET` for an unknown run (404), and the decide endpoint's synchronous effects (Approve resumes the step/run, Reject fails them, a repeat decide 409s, an unknown approval 404s) seeded directly via EF Core since Hangfire's worker is disabled in the test host. `dotnet format --verify-no-changes` clean.

## Consequences

**Positive**:
- One generic, pluggable engine every domain agent (Job, University, Personal) builds its real tools on top of, instead of three drifting copies of the same policy/retry/audit logic.
- Restart survival and durable step persistence come from Hangfire, already provisioned in this stack; no new infrastructure component to operate.
- The audit trail is complete by construction (every `ToolCall` writes an `AuditLog` row), not something a later feature has to remember to add.

**Negative / tradeoffs**:
- The plan-then-execute shape cannot adapt mid-plan: if an early step's real result invalidates a later step's assumption, the run only discovers this when that later step fails, not before. A future ReAct-style loop is a real rework, not an incremental change.
- Whole-run failure on one step's retry exhaustion (the engineer's confirmed choice) is blunt: a later step unrelated to the failure still never runs, even if it had no real dependency on the failed one.
- No cross-run memory means every run starts cold; a domain agent that would benefit from remembering a prior run's outcome gets nothing from this milestone.
- The single `AdvanceRun` job re-enqueuing itself per step, plus the `Running`-status idempotency check, is more custom recovery logic than a naive one-job-per-step design would have needed to write, in exchange for actually being correct under Hangfire's at-least-once job semantics.

**Neutral**:
- This milestone ships with exactly two tools (one auto-allowed, one approval-required), proving the pattern, not real domain value; the Job/University/Personal agents' real tools are separate, later work.
- A tool's risk tier and approval requirement are fixed at code review time (baked into its `ITool` declaration), not adjustable at runtime without a code change.

## Follow-up

- [x] Design "AI provider abstraction" (scope item 7): done in [0006](../0006-ai-provider-abstraction/index.md), shipped 2026-09-25.
- [ ] Design "Approval engine & Approval center" (scope item 8): the real `/approvals` screen and richer evidence display build on top of this milestone's minimal `decide` endpoint; expect the same endpoint to keep being the resume path.
- [ ] Revisit a ReAct-style iterative Planner and cross-run memory once a real domain agent's goals show the plan-then-execute, cold-start model is genuinely limiting; not designed here, deliberately deferred.
- [ ] Revisit approval expiry (currently none) once real usage shows pending approvals actually accumulating.
