# Rationale: agent orchestrator core

## Context

> ⚠️ Premise note: this feature assumes a working `IChatClient` (the provider agnostic AI abstraction from `Microsoft.Extensions.AI`, already the chosen library per `AGENTS.md`) is available to inject into the Planner, but which AI provider actually answers those calls is scope item 7 ("AI provider abstraction"), not yet designed. This spec treats `IChatClient` as an injected dependency and does not pick or configure a real model. Follow-up in `index.md` tracks designing item 7 before this feature can run against a real provider; until then it can only be built and tested against a fake/stub `IChatClient`.

Every later domain agent (Job Agent's matching and application prep, University Agent's outreach, Personal Agent's task suggestions) needs the same underlying machinery: turn a goal into a plan, check the plan is allowed, run it, verify it worked, and leave a durable trail. Building this per domain agent would mean three copies of the same policy, retry, and audit logic drifting apart over time. The scope's own "done when" line fixes the pipeline shape (Planner → Policy Engine → Tool Registry → Execution → Verification → Audit) and two hard requirements that shape every choice below: workflows must persist step by step and survive a process restart mid run (an in memory only implementation is disqualified outright), and the LLM must never receive passwords, OAuth tokens, refresh tokens, client secrets, or session cookies (it only ever calls tools, never touches credentials directly).

The product is single user today (the founder), so this milestone does not need multi tenant isolation, high concurrency, or a polished human approval screen; the real Approval Center (scope item 8) is a separate, later decision that builds on whatever minimal decision path this spec puts in place. The data model this feature persists into (`AgentRun`, `AgentStep`, `ToolCall`, `WorkflowInstance`, `WorkflowStep`, `WorkflowEvent`, `Approval`, `AuditLog`) was already designed in spec 0002, which explicitly deferred the final word on whether that shape fits the orchestrator's real flow to this spec.

## Options considered

### Option 1: Plan-then-execute pipeline on Hangfire-backed steps (chosen)

A single upfront LLM call produces an ordered plan; the Policy Engine validates the whole plan before execution starts; each planned step runs as its own Hangfire background job, which persists its `AgentStep`/`ToolCall` row and lets Hangfire's own job queue (already durable in the shared Postgres) carry the run across a process restart.

**Pros**:
- Matches the scope's own linear pipeline description exactly; simplest to reason about, test, and verify.
- Reuses Hangfire, already provisioned and durable in this stack, for restart survival; no new persistence mechanism to build.
- A step is a natural approval pause point: the next Hangfire job simply isn't enqueued until a decision arrives.

**Cons**:
- Cannot adapt mid-plan: if step 2's real-world result invalidates step 3's assumption, the plan doesn't notice until step 3 fails outright.

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
- Fully deterministic and cheap to run; no plan-parsing failure mode at all (there would be no AC-7).

**Cons**:
- Defeats the point of an agent: every new goal needs a new hand-written template, which doesn't generalize across three different domain agents the way a real Planner does.

## Rationale

The scope's own "done when" line names the pipeline in a fixed order (Planner → Policy Engine → Tool Registry → Execution → Verification → Audit) and demands restart survival; Option 1 is the only one of the three that satisfies both directly without inventing new infrastructure. Option 2 (ReAct) is a better fit for genuinely open-ended goals, but making an LLM's own turn-by-turn loop durable and restart-safe (not just the tool calls it makes) is a materially larger engineering problem than this milestone's "trivial tool call" bar calls for; it is noted as a real follow-up once a domain agent's actual goals show plan-then-execute is limiting, not ruled out forever. Option 3 (fixed templates) is rejected because it isn't really an agent: it would work for the one example goal in the scope but would need a new hand-written template for every future domain agent goal, which is exactly the per-domain duplication this spec exists to avoid.

The engineer's own answers settled every remaining load bearing call: an internal-only trigger endpoint (mirroring `/internal/identity/profile`'s existing pattern, spec 0004) rather than pulling UI work into this feature; one auto-allowed plus one approval-required tool, to prove both branches of the 3-tier policy rather than only the easy path; building suspend/resume now with a minimal decision hook, since the Workflow Engine's own "never rely on an in-memory process surviving" requirement forces a real pause/resume mechanism to exist regardless of whether the Approval Center's UI exists yet; a generic, pluggable Tool Registry and Policy Engine from day one rather than concretely built for one domain agent, since two tools already prove the pattern without needing all three domains built; explicit C# `ITool` classes with DI registration over reflection-based discovery or an external config file, matching this codebase's existing explicit-module conventions and avoiding a second source of truth for tool metadata; a tool's risk tier baked into its own declaration rather than a separately configurable policy table, since there is no product need yet to change a tool's risk tier without a code review; one Hangfire job per step for restart recovery, reusing what the stack already guarantees rather than writing custom recovery scanning; and whole-run failure on retry exhaustion, since a plan is a single coherent sequence for this milestone, not a set of independently schedulable steps (a real dependency model between steps is more than two tools need).

A cross check (a fresh model reading only the drafted spec plus the actual `Entities.cs` files) found that the first draft's claim of "no schema changes" was wrong, and that a naive one-Hangfire-job-per-step execution model doesn't actually deliver the restart-safety AC-9 demands, because Hangfire is an at-least-once job runner, not exactly-once: a killed job can be re-run by Hangfire itself, which would re-execute an already-completed or half-completed step unless something makes that safe. The fix folds the database itself into the resumption path, `AgentStep.Status` (not Hangfire's own queue state) decides what runs next, so a re-run of the driving job is a no-op for already-`Succeeded` steps and a controlled failure (not a silent re-execution) for a non-idempotent step caught mid-run. This also exposed that `AgentRun` had nowhere to store which profile the run belongs to, that `AgentStep` had nowhere to store the plan itself (its tool name and arguments), and that `Approval` was pointed at a `ToolCall` row that, by AC-4's own definition, doesn't exist yet at the moment an approval is needed. All three are folded into one small migration (Build plan task 1) rather than left as gaps for `/develop` to discover mid-build.

## References

None (the engineer opted out of a References section; this decision builds entirely on the stack already chosen in spec 0001 and the data model already chosen in spec 0002, with no new external tool landscape to check).
