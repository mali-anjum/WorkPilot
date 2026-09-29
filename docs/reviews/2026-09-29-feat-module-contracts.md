# Review, feat/module-contracts, 2026-09-29

**Reviewed by**: Claude Sonnet 5 (author on unspecified model)
**Scope**: 64 files, branch vs main (merge base b48b8c7)
**Verdict**: Approve with nits

## Summary

This is the Wave 0 groundwork from spec 0018: `Result<T>` + ProblemDetails, the transactional outbox (tables, interceptor, dispatcher, `HandleEventJob`, sweep), `EventRegistry`, `IEventPublisher`/`IEventHandler<T>`, `IAuditService` moved to Audit, `ApplyRecurringJobs`, both `Program.cs` module blocks, and `ResumeResult<T>`/`ResumeApiResult<T>` migrated to the shared types. The outbox is the highest-risk piece and it is correct: publish only stages a row in the caller's unit of work (atomic with the state change), the dispatcher claims with `FOR UPDATE SKIP LOCKED` inside its own transaction, `HandleEventJob` records the delivery and runs the handler in one transaction with `ON CONFLICT DO NOTHING` making re-delivery a no-op, and the sweep both catches missed dispatches and prunes retention. Every place an `AgentRun` becomes `Failed` publishes `AgentRunFailed` exactly once, and the concurrency-retry paths (`AdvanceRunJob`'s `DbUpdateConcurrencyException` catch, `ApprovalRepository.SaveDecisionAsync`, `JobRepository.InTransactionAsync`) all clear the change tracker before retrying, so a lost race never leaves a partially-published event behind — this is directly tested (`OutboxTests`, `AdvanceRunJobTests`, `ApprovalEndpointsTests.Decide_ThatLosesARace_PublishesNoEvent`). Test coverage is thorough and exercises the tricky concurrency/idempotency paths, not just happy paths. The two things below are worth a look but neither blocks merge.

## Minor

### 🟡 Approvals endpoints weren't converged onto `Result<T>`/ProblemDetails, `src/WorkPilot.Api/Endpoints/ApprovalsEndpoints.cs:49-54`
**Problem**: `DecideApprovalHandler` returns its own `DecideApprovalOutcome` enum, and the endpoint maps it with a raw switch: `Results.BadRequest()`, `Results.NotFound()` (bodyless), `Results.UnprocessableEntity(new { error = "ConfirmationRequired" })`, `Results.Conflict(new { error = "AlreadyDecided" })`. The last two return an ad hoc `{ error: "..." }` shape, not RFC 7807 ProblemDetails, unlike every endpoint that goes through `Result<T>.ToHttp`.
**Why it matters**: Section 4's stated goal is one error shape for every endpoint so the Web reads every failure the same way; AGENTS.md still frames the error pattern as something this groundwork is meant to settle. Right now there are two shapes in the API depending on which endpoint you hit. It happens to be harmless today only because `ApprovalCenterClient.DecideAsync` reads the status code and ignores the body, so no caller is broken by it.
**Suggested fix**: Either leave a note in spec 0018 explicitly scoping the "Replaces" list to `ResumeResult<T>` only (it currently reads that way but doesn't call out that Approvals is deliberately deferred), or fold `DecideApprovalOutcome` into `Result<T>` in a follow-up so the whole API is on one pattern before more endpoints copy the old style.

### 🟡 Agent module still writes the Approvals module's table, `src/WorkPilot.Workers/Agent/AdvanceRunJob.cs:215`
**Problem**: `SuspendForApprovalAsync` does `db.Approvals.Add(approval)` directly from the Agent module. Section 1 makes Approvals the sole writer of `Approval`, and section 9's rollout note says existing code converges once, in this groundwork branch.
**Why it matters**: It's exactly the kind of direct cross-module write section 1's "Consequences" says the architecture test can't see and `/check review` has to catch. Left as is, later features may copy the same shortcut for other tables now that there's a merged example of it.
**Suggested fix**: Either route this through an Approvals interface (e.g. `IApprovalRequester.RequestAsync(...)`) in a near-term follow-up, or record it explicitly as an accepted, scoped-out deviation in spec 0018 (the way `IAuditService`'s same-transaction exception already is) so it isn't mistaken for an oversight by the next feature branch. This appears to be a pre-existing shape from spec 0007 that this branch didn't touch beyond adding the `ApprovalRequested` publish, so it's not new risk from this diff — just not converged as the rollout note implies it should be.

## Strengths

- The outbox's hardest correctness properties — at-least-once delivery, per-handler idempotency via `outbox_deliveries` with `ON CONFLICT DO NOTHING`, and "never lose or duplicate an event across a concurrency retry" — are each directly tested against real Postgres, including a real race (`Decide_ThatLosesARace_PublishesNoEvent` holds a row lock in one transaction while a concurrent decide runs and asserts zero events).
- `EventRegistry` fails fast (at host startup, via `OutboxStartupCheck`) on a duplicate event name, a duplicate handler key, and an undispatched outbox row naming an unknown event type — matches section 2/3 exactly and is unit tested in isolation (`EventRegistryTests`).
- The mechanical renames (`ResumeResult<T>` → `Result<T>`, `ResumeApiResult<T>` → `ApiResult<T>`, `ResumeEndpoints` → `ProfileEndpoints`, `Resumes` → `Profile` feature folder, `ApprovalEndpoints` → `ApprovalsEndpoints`) were carried through consistently into every call site and every test double, with no stale references left behind.

## Test coverage

Strong. New/changed logic (outbox publish/dispatch/handle/sweep, the interceptor's fire-on-save-with-an-outbox-row behavior, `EventRegistry` duplicate detection, `Result<T>.ToHttp` per status, the bodyless-404-gets-ProblemDetails fix, every `AgentRunFailed`/`ApprovalRequested`/`ApprovalDecided` publish site) all have dedicated tests against the real Postgres instance, including the negative/concurrency cases that are easy to get wrong. Nothing material in this diff looks untested.

## Resolution (2026-09-29)

- **Approvals endpoints not on ProblemDetails**: fixed. `POST /internal/agent/approvals/{id}/decide` keeps every status code (400, 403, 404, 409, 422) but each refusal is now ProblemDetails with a `detail`, so the Api has one error shape. `DecideApprovalOutcome` stays as the handler's return type for now (its outcomes map 1:1 to statuses the Web relies on); moving it onto `Result<T>` is left for the next feature that touches Approvals. Locked by `ApprovalEndpointsTests.Decide_ASecondDecision_IsAConflictProblemWithADetail`.
- **Agent writes the `Approval` table**: accepted as a recorded deviation in spec 0018 ("Decisions made without the engineer"), with a follow up to route it through an Approvals interface. It predates this branch (spec 0007) and moving it now would change the approval engine's transaction, which Wave 0 promises not to do.
