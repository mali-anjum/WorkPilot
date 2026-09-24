# 0007. Approval engine and Approval center: rationale

## Context

Spec 0005 built the agent orchestrator with a minimal approval pause: a tool whose `RiskTier` isn't `AutoAllowed` suspends its run, an `Approval` row is created, and `POST /internal/agent/approvals/{id}/decide` resumes or fails the run. It left three things for scope item 8. First, the third tier (`ExplicitConfirmation`, for deletes and security changes) was treated exactly like `ApprovalRequired`, so nothing distinguished "click Approve" from "confirm a destructive action". Second, there was no screen: a decision needed a hand written HTTP call, and the `Approval` row held only the target step id and a risk tier, so there was nothing to show a human deciding. Third, the decide path had gaps a GA feature can't carry: `decidedBy` came straight from the request body with no ownership check, and the atomic `ExecuteUpdate` on the approval committed separately from the run/step change and the audit row, so a failure between the two left an approved approval attached to a run that never resumed.

The forces: this is the gate in front of every action that is external and hard to reverse (sending email, submitting applications), so the safety properties matter more than polish. The product is single user, the Api is internal only behind the Web host, and the Web app uses Blazor in Auto render mode with pages living in the server project and a plain cookie session (spec 0004). The orchestrator's database, not Hangfire, is the source of truth for resumption (spec 0005), and whatever this feature adds must keep restart safety intact.

Not deciding means every later approval gated tool (features 16, 18, 24) either reinvents evidence and confirmation or ships without them.

## Options considered

### Option 1: Strengthen the existing decide path in place, snapshot evidence, server rendered Approval center with plain form posts (chosen)

Keep spec 0005's tables, suspend point, and decide URL. Add a pure domain `ApprovalPolicy` that owns the tier rules, and call it both when deciding and immediately before execution. Store an immutable evidence snapshot on the approval when the run suspends, built by the tool through an optional interface. Move decide into an Application use case that does the owner check, the confirmation check, and the domain transitions, persisted in one `SaveChanges` with optimistic concurrency on the approval's status and the run's `xmin`. Build `/approvals` as a Blazor page whose Approve/Reject are ordinary antiforgery protected forms posting to a small Web endpoint that reads the profile from the cookie.

**Pros**:
- Smallest change that closes every gap; the resume path spec 0005 proved (including restart survival) is untouched.
- Evidence frozen at request time means the approver sees exactly what will run, and it stays auditable after documents change.
- The deciding profile is never a browser controlled value.
- Works with or without an interactive circuit; verifiable over plain HTTP.

**Cons**:
- Each tool must implement the evidence interface to show more than its inputs.
- The Web host gains one more minimal endpoint alongside its auth endpoints.

### Option 2: A separate approval workflow service (approval as its own state machine and table set)

Model approvals as their own aggregate with requests, reviewers, expiry, and escalation, decoupled from `AgentStep`, with the orchestrator subscribing to decision events.

**Pros**:
- Scales to multi user review, delegation, expiry, and non agent approvals.

**Cons**:
- Rebuilds what spec 0005 already persists and proved restart safe; a second source of truth for "may this step run".
- Far more than a single user product needs now; expiry and delegation are already deferred.

### Option 3: Interactive Approval center (buttons call the Api from the Blazor circuit), evidence computed live on page load

Render cards interactively, look up the current resume/cover letter versions when the page loads, and call the decide endpoint from the component with the profile from `AuthenticationState`.

**Pros**:
- Richer interactivity (inline updates without a page reload).
- No evidence column needed.

**Cons**:
- Live evidence can drift from what the tool actually executes with (a new resume version uploaded between viewing and approving).
- Depends on the interactive render mode working for server project pages under Auto, which spec 0004 didn't prove for component event handlers.
- Harder to verify live without a browser automation tool.

## Rationale

The feature's job is safety at the moment of decision, so the deciding factors were: can the approver be shown something different from what runs (Option 3 fails this, Option 1 freezes it), can a request decide on someone else's behalf (Option 1 takes the profile from the cookie and checks ownership), and can a decision half commit (Option 1 puts everything in one guarded transaction). Option 2 would answer the same questions but by replacing machinery that already works and is restart safe; its extra capabilities (multi reviewer, expiry) are explicitly out of scope for a single user product.

Checking the policy twice (at decide and again right before execution) looks redundant, but the two checks guard different failures: decide time stops a bad request, execution time stops any path that reaches `Running` without a proper approval (a future bug, a manual database edit, or a tool whose tier was raised by a deploy while its approval was pending). Using the stricter of the tool's current tier and the recorded tier means a tier change can only make approval harder, never easier.

The typed confirmation uses the tool's own name because it is already unique, visible on the card, and requires no new data; the cost is friendliness for long names, which a later feature can replace with a per tool phrase without changing the policy's shape.

## References

**Project sources**:
- spec 0005 (agent orchestrator core): suspend/resume, decide endpoint, `xmin` token, restart survival
- spec 0004 (auth and app shell): cookie session, `profile_id` claim, internal only Api, antiforgery on form endpoints
- spec 0002 (data model): `Approval` polymorphic target, immutable `ResumeVersion`/`CoverLetterVersion`
- `docs/scope/foundation.md` section 8 (the three tier policy and the Done when line)

**Practices and standards**:
- Optimistic concurrency (compare and set on a status column) for single decision guarantees
- Synchronizer token antiforgery pattern (OWASP CSRF prevention)
- What you see is what you sign: freeze the evidence shown at approval time
