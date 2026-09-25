# 0007. Approval engine and Approval center

**Date**: 2026-09-24
**Status**: Accepted

## Summary

This spec turns the minimal approval pause that spec 0005 built into the real approval engine. Every tool already declares one of three risk tiers; this makes all three tiers real: auto allowed tools just run, approval required tools wait for your Approve, and explicit confirmation tools (delete data, security changes) also make you type the action's name before an Approve counts. It adds the Approval center screen at `/approvals`, which lists every pending action with the evidence you need to decide (what it targets, how risky it is, the exact inputs the tool will run with, and any resume or cover letter versions involved), plus Approve and Reject buttons that go through the same decide path spec 0005 built. Only the owner of a run can decide its approvals, a decision can be made once, and every decision lands in the audit log.

## Requirements

**User stories**:
- As the founder, I want every pending agent action listed in one place with its target, risk, inputs, and document versions, so that I can decide without digging through run logs.
- As the founder, I want a destructive action to need a deliberate typed confirmation, not just one click, so that I can't approve a delete by accident.
- As the founder, I want every approval decision recorded with who decided, when, and what was decided, so that I can audit what the agent was allowed to do.

**Acceptance criteria** (the contract, each criterion is independently checkable):
- **AC-1**: The three tier policy is enforced by one domain policy (`ApprovalPolicy`): an `AutoAllowed` tool executes without an `Approval` row; an `ApprovalRequired` or `ExplicitConfirmation` tool suspends its run (`AwaitingApproval`, a `Pending` `Approval` row) before it executes.
- **AC-2**: An approval gated tool never executes unless an `Approval` for its step is `Approved`, carries a `DecidedBy`, and (for the `ExplicitConfirmation` tier) is marked `ExplicitlyConfirmed`. `AdvanceRunJob` checks this through `ApprovalPolicy.PermitsExecution` immediately before it moves the step to `Running`, using the stricter of the tool's current tier and the tier recorded on the approval; an approved step that no longer satisfies the policy is skipped (it never ran), its run fails, and an `ApprovalGateRefused` audit row is written instead of executing. The same check runs when the job re-enters a gated step found already `Running` (crash recovery); that step fails instead (it may have partly run), with the same audit row.
- **AC-3**: When a run suspends, the `Approval` row stores an immutable evidence snapshot (`EvidenceJson`): a plain summary, the target (type, id, label), and the document versions the action involves (kind, version id, name, version number, created at), as described by the tool itself through the optional `IApprovalEvidenceProvider`. A tool that doesn't implement it, or whose description throws, still suspends; its snapshot is null (or carries the error as its summary).
- **AC-4**: `GET /internal/approvals?profileId=` returns the Approval center view for that profile only: every `Pending` approval (oldest first) with approval id, risk tier, requested at, run id, run goal, step ordinal, tool name, tool description, the step's arguments, the evidence snapshot, and the confirmation phrase when the tier needs one; plus up to 20 most recently decided approvals (status, decided at, decided by, explicitly confirmed). Another profile's approvals never appear.
- **AC-5**: `/approvals` in the Web app renders that view for the signed in founder: one card per pending approval showing target, risk badge, goal, tool, inputs, summary, and document versions (when present), with Approve and Reject; an empty state when nothing is pending; and a recent decisions list.
- **AC-6**: Approve and Reject on `/approvals` post an antiforgery protected form to the Web host, which takes the deciding `ProfileId` from the signed in session cookie (never from the form) and calls the existing `POST /internal/agent/approvals/{id}/decide`. The page then shows the outcome (approved, rejected, already decided, confirmation required, not allowed, not found).
- **AC-7**: The decide endpoint only accepts a decision from the profile that owns the run (`decidedBy` must equal `AgentRun.ProfileId`), otherwise `403` and nothing changes.
- **AC-8**: Approving an `ExplicitConfirmation` approval requires `confirmation` to equal the confirmation phrase (the tool's name, exact match after trimming); otherwise `422` and the approval stays `Pending`. Rejecting never needs a confirmation.
- **AC-9**: A decision is recorded exactly once: the approval's status change, the step and run transitions, and the decision's audit row commit in one `SaveChanges` transaction, guarded by optimistic concurrency on both `approvals.Status` (`UPDATE ... WHERE Status = 'Pending'`) and `agent_runs.xmin`. A second or concurrent decision gets `409` and leaves no partial state; a replayed request can never decide a different approval.
- **AC-10**: Every decision writes one `AuditLog` row with the deciding `ProfileId` as actor, action `ApprovalApproved` or `ApprovalRejected`, target the `AgentStep`, and a JSON payload naming the approval id, run id, tool, risk tier, decision, and whether it was explicitly confirmed. The approval request's audit row (actor `Agent`, action `ApprovalRequested`) carries the evidence snapshot as its payload.

## Decision

**Chosen option**: Option 1: Strengthen the spec 0005 decide path in place, add a domain policy, an evidence snapshot, and a server rendered Approval center that posts plain forms.

The approval engine stays the one built in spec 0005 (same tables, same `AwaitingApproval` suspend, same decide URL), with the tier rules moved into a domain `ApprovalPolicy`, the inline decide endpoint moved into an Application use case, and the Approval center built as a Blazor page whose buttons are ordinary antiforgery protected form posts.

**Implementation skills**: `ef-core` (`github/awesome-copilot`, `.agents/skills/ef-core/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

Reasoning and full option tradeoffs: see [rationale.md](rationale.md).

## Feature design

**Data model sketch** (one migration, `AddApprovalEngine`; everything else reuses spec 0002/0005 tables):

| Entity | Change | Notes |
|---|---|---|
| `Approval` | add `EvidenceJson` (jsonb, nullable) | immutable snapshot written once at suspension (AC-3) |
| `Approval` | add `RequestedAt` (timestamptz, not null, default `now()`) | ordering and display; existing rows get the migration time |
| `Approval` | add `ExplicitlyConfirmed` (bool, not null, default `false`) | set only by an Approve that passed the typed confirmation (AC-8) |
| `Approval` | `Status` becomes an EF concurrency token | turns the decision's UPDATE into `WHERE Id = @id AND Status = 'Pending'` (AC-9) |
| `Approval` | new index `(Status, RequestedAt)` | the pending list query |

Domain additions: `ApprovalPolicy` (static, pure), `ApprovalEvidence`/`ApprovalTarget`/`DocumentVersionEvidence` records, `IApprovalEvidenceProvider` (optional tool interface, next to `ITool`), `Approval.Tier`, and `Approval.Decide(..., explicitlyConfirmed)` which refuses an unconfirmed Approve on the explicit tier.

**State transitions**: unchanged from spec 0005. `Approval.Status`: `Pending` → `Approved` | `Rejected` (terminal, once). On Approve the run returns `AwaitingApproval` → `Executing` and the step stays `AwaitingApproval` until `AdvanceRunJob` runs the policy check and moves it to `Running`. On Reject the step goes to `Skipped` and the run to `Failed`.

**API surface**:

| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/internal/approvals` | GET | `profileId:guid` (query, req) | `{pending:[...], recentlyDecided:[...]}` (AC-4) | internal only (Api is never externally routed) | 400 empty `profileId` |
| `/internal/agent/approvals/{id}/decide` | POST | `decision:"Approve"\|"Reject"` (req), `decidedBy:guid` (req), `confirmation:string` (explicit tier Approve only) | `approvalId`, `status`, `resumed` | internal only | 400 bad decision, 403 not the run's owner, 404 unknown approval, 409 already decided or concurrent change, 422 confirmation missing or wrong |
| `/approvals` (Web) | GET | none (profile from the session cookie) | the Approval center page | session cookie (`RequireAuthorization`) | redirect to `/login` when signed out |
| `/approvals/{id}/decide` (Web) | POST (form) | `decision`, `confirmation` (optional), `__RequestVerificationToken` | `302` to `/approvals?outcome=<outcome>` | session cookie + antiforgery | 400 bad antiforgery token, redirect to `/login` when signed out |

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Suspension (`AdvanceRunJob`) | whether a step suspends | `ApprovalPolicy.RequiresDecision(tool.RiskTier)`, the tool's baked in declaration (spec 0005) |
| Suspension | `Approval.RiskTier` | `tool.RiskTier` at suspension time |
| Suspension | `Approval.EvidenceJson` | `IApprovalEvidenceProvider.DescribeForApprovalAsync(context)` on the tool, given the same `ToolExecutionContext` (profile + step arguments) it will execute with; null when the tool doesn't implement it |
| Suspension | `Approval.RequestedAt` | the time the row is created (UTC) |
| Demo tools' evidence | target label, document versions | the calling `Profile.Name`; the newest `ResumeVersion` of the profile's active resume (else newest resume) and the newest `CoverLetterVersion`, when they exist |
| Approval center | risk, tool, arguments, goal, ordinal | `approvals.RiskTier`, `agent_steps.ToolName`/`ArgumentsJson`/`Ordinal`, `agent_runs.Goal`, joined through `Approval.TargetId` → step → run |
| Approval center | tool description | the Tool Registry's current `ITool.Description` (falls back to empty when the tool is no longer registered) |
| Approval center | confirmation phrase | `ApprovalPolicy.ConfirmationPhraseFor(toolName)`, the tool name, only for the `ExplicitConfirmation` tier |
| Approval center | which approvals are listed | `agent_runs.ProfileId = profileId`; Web passes the `profile_id` claim of the session cookie |
| Web decide | `decidedBy` | the `profile_id` claim of the signed in session, never a form field |
| Decide | owner check | `AgentRun.ProfileId` of the approval's step's run |
| Decide | `Approval.DecidedAt` | `TimeProvider.GetUtcNow()` at decision time |
| Decide | `Approval.ExplicitlyConfirmed` | true only when the tier is `ExplicitConfirmation`, the decision is Approve, and `confirmation` matched |
| Decide | audit actor, action, payload | deciding `ProfileId`; `Approval{Status}`; `{approvalId, agentRunId, toolName, riskTier, decision, explicitlyConfirmed}` |
| Execution gate | permitted to execute | `ApprovalPolicy.PermitsExecution(max(tool tier, approval tier), approval)` |

**Key invariants**:
- An approval gated step reaches `Running` only after `ApprovalPolicy.PermitsExecution` returned true for its approval in the same job invocation.
- The evidence snapshot and the step's arguments are both written once and never updated, so what the Approval center showed is what executes. A real tool that uses a document must take the version id as an argument (pinned), never "the latest version" at execution time.
- A decision is all or nothing: the approval status, step/run transition, and audit row commit together or not at all.
- Only the run's owner can decide; the deciding profile is always the authenticated session's, never a request field the browser controls.

**Security model**: Single user today, but ownership is checked anyway (AC-7) so the design holds if more users arrive. The Api stays internal only (spec 0004/0005 boundary), so `decidedBy` in the internal request is trusted from the Web host, which derives it from the cookie. The Web decide endpoint requires an authenticated session and a valid antiforgery token (blocks cross site request forgery). Replay: a decision request names one approval id, and an already decided approval returns `409`. Race between decide and execute: the job only executes after reading `Approved` and passing the policy check; the decide transaction and the job's `SaveChanges` are both guarded by `agent_runs.xmin`.

**Configuration required**: none new.

**Critical test scenarios**:
- Happy path: an approval required demo run suspends with an evidence snapshot, appears in `GET /internal/approvals` and on `/approvals`, Approve from the page resumes and completes it, verifies **AC-1**, **AC-3**, **AC-4**, **AC-5**, **AC-6**
- Explicit tier: an explicit confirmation demo run suspends; Approve without the phrase gets `422` and stays `Pending`; with the phrase it approves and executes, verifies **AC-2**, **AC-8**
- Auth/permission: a decide from a profile that doesn't own the run gets `403`, and the listing for another profile never shows the approval, verifies **AC-4**, **AC-7**
- Failure case: a second decide gets `409`; a decision whose run was changed concurrently gets `409` with the approval still `Pending`, verifies **AC-9**
- Gate: a step whose approval is `Approved` but not explicitly confirmed on the explicit tier never executes, verifies **AC-2**
- Audit: approve and reject each write one audit row with the deciding profile and the JSON payload, verifies **AC-10**

## Build plan

1. [x] Domain: `ApprovalPolicy`, evidence records, `IApprovalEvidenceProvider`, `Approval` fields (`EvidenceJson`, `RequestedAt`, `ExplicitlyConfirmed`, `Tier`) and the confirmed `Decide`, satisfies **AC-1**, **AC-2**, **AC-8**
2. [x] Migration `AddApprovalEngine` (three columns, the index, the `Status` concurrency token), applied and confirmed live, satisfies **AC-3**, **AC-9**
3. [x] `AdvanceRunJob`: suspend via `ApprovalPolicy`, capture the evidence snapshot, audit it, and gate execution through `PermitsExecution`, satisfies **AC-1**, **AC-2**, **AC-3**, **AC-10**
4. [x] Demo tools: evidence on `approval_required_demo`, a new inert `explicit_confirmation_demo`, satisfies **AC-1**, **AC-3**, **AC-8**
5. [x] Application: `DecideApprovalHandler` (owner check, confirmation, domain transitions, audit) over an `IApprovalRepository` and `IAgentRunScheduler`, and `GetApprovalCenterHandler`, satisfies **AC-4**, **AC-7**, **AC-8**, **AC-9**, **AC-10**
6. [x] Api: move the decide endpoint out of `Program.cs` into `Endpoints/ApprovalEndpoints.cs` on the use case, add `GET /internal/approvals`, satisfies **AC-4**, **AC-7**, **AC-8**, **AC-9**
7. [x] Web: `/approvals` page, `ApprovalCenterClient`, and the antiforgery protected `POST /approvals/{id}/decide`, satisfies **AC-5**, **AC-6**
8. [x] Prove it live: suspend, list, approve and reject from the page, explicit confirmation, owner check, double decide, audit rows, satisfies every AC

## Consequences

**Positive**:
- The three tiers are real and enforced in one place, at both decision time and execution time (defence in depth).
- Evidence is frozen at request time, so an approval is always of exactly what will run.
- The Approval center works without an interactive circuit (plain form posts), which keeps it robust and easy to verify.

**Negative / tradeoffs**:
- Evidence richness depends on each tool implementing `IApprovalEvidenceProvider`; a tool that doesn't shows only its inputs.
- A crash after a decision commits but before its resume job is enqueued leaves an approved run waiting (the window spec 0005 already had). Recorded as a follow up.
- The typed confirmation is the tool name, which is precise but not friendly for long names.

**Neutral**:
- The decide endpoint's code moves out of `Api/Program.cs`; its URL and response shape stay the same, with one new optional request field (`confirmation`) and two new status codes (`403`, `422`).
- `Approval.Status` is now an optimistic concurrency token; any future code that updates an approval must expect `DbUpdateConcurrencyException`.

## Follow-up

- [ ] A periodic sweep that re-enqueues `AdvanceRunJob` for runs at `Executing` whose next step is `AwaitingApproval` with an `Approved` approval (closes the decide commit then crash window).
- [ ] Approval expiry (still none, carried from spec 0005).
- [ ] Notifications (scope item 20) should alert the founder when an approval is requested; the Approval center is pull only today.
- [ ] Real approval gated tools (send email, submit application) must implement `IApprovalEvidenceProvider` and pin document version ids in their arguments.
- [ ] The project wide API error handling decision should decide whether `DbUpdateConcurrencyException` in `AdvanceRunJob` needs handling too (decide now maps it to `409`).
- [ ] For `AGENTS.md` via `/sync`: the three tier rule lives in `Domain/Modules/Approvals/ApprovalPolicy`; tools add approval evidence by implementing `IApprovalEvidenceProvider`.

## Decisions made without the engineer (please review)

- Where the explicit confirmation lives: a typed phrase (the tool's name) required on Approve, checked in the domain and again at execution. Runner up: a second "are you sure" click. Why: a typed phrase can't be clicked through by accident and is checkable server side.
- Evidence source: an immutable snapshot built by the tool itself at suspension (`IApprovalEvidenceProvider`, optional). Runner up: build evidence live when the page loads. Why: a snapshot guarantees the approver saw exactly what executes and keeps the evidence auditable after documents change.
- Decision UI: plain antiforgery protected form posts from the Blazor page to a Web endpoint that reads the profile from the cookie. Runner up: interactive buttons calling the Api from the Blazor circuit. Why: no dependency on the interactive render mode, the deciding profile can't be spoofed by the browser, and it's verifiable over plain HTTP.
- Double decide / race guard: EF concurrency token on `approvals.Status` plus the existing `agent_runs.xmin`, in one `SaveChanges`. Runner up: keep spec 0005's separate `ExecuteUpdate` then `SaveChanges`. Why: the old path committed the approval separately from the run change, so a failure between them left an approved approval with a suspended run.
- Owner check on decide: `403` when `decidedBy` isn't the run's profile. Runner up: no check (single user). Why: cheap, closes "approve on behalf of someone else", and keeps the design honest for more users.
- Confirmation failure status: `422 Unprocessable Entity`. Runner up: `400`. Why: the request is well formed but fails a business rule, and the page needs to tell it apart from a malformed decision.
- Recent decisions list on the Approval center (20 newest). Runner up: pending only. Why: "the decision is auditable" is easier to see without opening the database; the full audit trail stays in `audit_logs`.
- A second demo tool (`explicit_confirmation_demo`) so the third tier is exercisable live. Runner up: prove it only in tests. Why: GA needs the live proof.
