# 0016. Render the Blazor app in InteractiveServer mode

**Date**: 2026-09-26
**Status**: Accepted

## Summary

The whole Blazor app now renders in InteractiveServer mode (set once on `<Routes>` in `App.razor`), not InteractiveAuto. This replaces the render mode row of [0001](0001-stack-architecture.md) and the Auto notes in [0003](0003-design-system-ui-foundation/index.md) and [0004](0004-auth-app-shell/index.md). Auto never really worked here: every routed page lives in the server project, so the WebAssembly half could not load them. The change was already built and verified in commit `f81ae97` (feature 14, resume management). This spec records it.

## Context

Spec 0001 picked Auto (Server + WebAssembly) for a fast first paint over SignalR, then WebAssembly for snappier interaction. In practice the app was built as a server app. `Routes`, the layout and every page live in `WorkPilot.Web` (the server project), and pages inject server only services (for example `IResumesApiClient`, `IApprovalCenterClient`). Only a few shared components live in `WorkPilot.Web.Client`.

Under Auto, the first visit runs on a server circuit. Once the browser has the WebAssembly runtime cached, later visits try to start in WebAssembly, and it fails with `Root component type 'WorkPilot.Web.Components.Routes' could not be found in the assembly 'WorkPilot.Web'`. The page then never becomes interactive. Nobody noticed until the resume pages (spec 0009). Earlier pages were either static, or used plain form posts (the Approval center), so they still worked. The live verify of spec 0009 caught it: the create, edit and tailor forms fell back to plain GETs on repeat visits and wrote nothing.

## Requirements

**User stories**:
- As the founder, I want every interactive page to work on every visit, not only the first one in a fresh browser.

**Acceptance criteria**:
- **AC-1**: `<Routes>` in `App.razor` renders with `@rendermode="InteractiveServer"`; no routed page or layout declares InteractiveAuto or InteractiveWebAssembly.
- **AC-2**: On a repeat visit, with the WebAssembly runtime already cached in the browser, interactive forms (the resume create, edit, tailor and file upload) submit and write their rows, and the browser console shows no `ManagedError`.
- **AC-3**: Sign in and sign out, the theme cookie with no flash on first paint, the command palette and the Approval center keep working as specs 0003, 0004 and 0007 describe.

## Options considered

### Option 1: InteractiveServer app wide
**Pros**: one line; fixes every page at once; matches where the pages actually live; no dual hosting to reason about.
**Cons**: every page needs a live SignalR circuit, so there is no WebAssembly fallback after a disconnect (the reconnect modal handles it); more server memory per open tab (fine for a single user).

### Option 2: Keep Auto, mark only the resume pages InteractiveServer
**Pros**: smallest change to the stated stack.
**Cons**: leaves the broken WebAssembly half in place for every future page to trip over; mixed render modes per page are harder to reason about.

### Option 3: Rewrite the resume forms as plain form posts (like the Approval center)
**Pros**: no render mode change.
**Cons**: more code; file upload and live form state get clunkier; does not fix the root cause.

## Decision

**Chosen option**: Option 1: InteractiveServer app wide. The engineer chose it on 2026-09-26.
**Implementation skills**: none needed.

## Rationale

The pages were always server pages. Auto only added a failure mode, not a benefit. Making it actually work would mean moving pages into `WorkPilot.Web.Client` and putting an HTTP boundary behind every server only service. That is a lot of work for a single user internal tool that is always online. Server mode fits what was built.

## Feature design

**Key invariants**:
- The render mode is set in exactly one place: `<Routes @rendermode="InteractiveServer" />` in `App.razor`.
- Cookie writing flows (sign in, sign out, password reset) stay as plain HTTP endpoints, as spec 0004 requires. An interactive circuit still cannot set a cookie.
- A component that calls JS interop in `DisposeAsync` must tolerate `JSDisconnectedException`. With server mode, every full page load closes a circuit (`CommandPalette` does this).

**Configuration required**: none. `AddInteractiveWebAssemblyComponents` and the `WorkPilot.Web.Client` assembly stay registered, so shared components still resolve. Removing the WebAssembly half entirely is a follow-up, not part of this decision.

**Critical test scenarios**: the live repeat visit run in [0009 verify.md](0009-resume-management/verify.md) (AC-2, AC-3). The existing Web tests cover the shell (AC-3).

## Consequences

**Positive**: interactive pages work on every visit; pages may inject server services directly; one render mode to reason about.
**Negative / tradeoffs**: every page needs a live circuit, with no offline or WebAssembly fallback; server memory grows with open tabs.
**Neutral**: the persistent authentication state wiring from spec 0004 (for the WebAssembly half) is now unused but harmless.

## Follow-up

- Decide whether to remove the unused WebAssembly half (`WorkPilot.Web.Client` hosting, `AddInteractiveWebAssemblyComponents`, the auth state serialization) to shrink the download. Only worth it if nothing needs it.
- /sync: the root `AGENTS.md` stack line still says "Blazor Web App (Auto render mode)".

## References

**Project sources**: `src/WorkPilot.Web/Components/App.razor`, `src/WorkPilot.Web.Client/Shared/CommandPalette.razor`, [0001](0001-stack-architecture.md) (frontend framework row), [0003](0003-design-system-ui-foundation/index.md) (render mode notes), [0004](0004-auth-app-shell/index.md), [0009 verify.md](0009-resume-management/verify.md).
