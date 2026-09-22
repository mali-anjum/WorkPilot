# 0003. Design system and UI foundation

**Date**: 2026-09-21
**Status**: Accepted

## Summary

This decision sets the visual language (colors, type, spacing) and the base set of reusable Blazor components (buttons, cards, tables, and so on) every later screen builds from, plus the app shell (sidebar, top bar) that holds the navigation. It also stands up empty stub pages for every planned route so the app's structure is real and clickable end to end, even before each screen has real content. Colors and components are hand built with plain CSS rather than a third party component library or a build tool, keeping the project on pure .NET/Blazor with no extra tooling.

## Requirements

**User stories**:
- As the founder using the app, I want a consistent visual language and working navigation so every future screen feels like one product, not a patchwork.
- As the founder, I want to switch between light and dark mode and have my choice remembered on my browser.
- As a future contributor building a new screen, I want a set of ready made components (card, table, badge, and so on) so I don't reinvent basic UI each time.

**Route map** (literal URLs, so "its own stub route" in AC-2 has a named target; a section heading like "Work" is a group label only, not itself routable):
| Nav section | Sub item | Route |
|---|---|---|
| Overview | Dashboard | `/` |
| Work | Jobs | `/jobs` |
| Work | Applications | `/applications` |
| Work | Universities | `/universities` |
| Work | Outreach | `/outreach` |
| Personal | Calendar | `/calendar` |
| Personal | Tasks | `/tasks` |
| Agent | Agent Runs | `/agent/runs` |
| Agent | Approvals | `/approvals` |
| System | Settings | `/settings` |
| System | Integrations | `/integrations` |

**Component scope for this feature** (trimmed to what the shell and its stub pages actually render; a later screen's spec adds the rest against real requirements instead of a guessed shape now): **AppShell, Sidebar, TopBar, PageHeader, Card, Button, Badge/StatusBadge, EmptyState, Modal, CommandPalette.** Deferred to the first screen that needs them (tracked in Follow-up, not built here): Input, Select, Tabs, DataTable, Skeleton, Timeline, Stepper, Toast.

**Acceptance criteria**:
- **AC-1**: The app shell (sidebar with the 5 top level nav sections, top bar, main content area) renders on every route in the Route map above, and each one is reachable and returns a real page (a stub page counts as real: it renders the AppShell, a PageHeader with the section title, and an EmptyState, not a 404).
- **AC-2**: The 5 top level nav sections (Overview, Work, Personal, Agent, System) render with their confirmed sub items per the Route map, and each sub item link navigates to its own stub route at the literal URL listed there. The active route's nav item is highlighted using Blazor's `NavLink` component (`NavLinkMatch.All` for `/`, `NavLinkMatch.Prefix` for every other route).
- **AC-3**: Every component in this feature's scope (AppShell, Sidebar, TopBar, PageHeader, Card, Button, Badge/StatusBadge, EmptyState, Modal, CommandPalette) exists as a reusable Blazor component with the parameter signature named in `## Feature design`, documents its parameters with an XML doc comment, and is rendered at least once on a dedicated `/design` checklist page (built as part of this feature) that lists every component with a live example, giving `## Feature design`'s inventory a page to point at instead of relying on incidental use across stub pages.
- **AC-4**: Every interactive component in scope (Button, Modal, CommandPalette) is reachable and operable by keyboard alone. Concretely and testably: Tab reaches every interactive element in DOM order; each interactive element's CSS includes a `:focus-visible` outline rule sourced from a token (verifiable by reading the component's `.razor.css`); Enter or Space raises a Button's `OnClick`; Escape raises a Modal's or the CommandPalette's `OnClose`. The last two are asserted with bUnit; DOM tab order and the rendered focus ring are exercised manually in `/check verify` (bUnit cannot assert real browser focus rendering).
- **AC-5**: The color, type, spacing, radius, and z-index tokens are defined once as CSS custom properties per the literal values in `## Feature design`'s Data model sketch, and every base component consumes them via `var(--token-name)`, not a hardcoded value, so a token change propagates everywhere.
- **AC-6**: Dark mode is the default on first visit; a visible toggle in the TopBar switches to light mode; the chosen mode persists across a page reload via a `workpilot-theme` cookie read server side before the first paint (see `## Feature design`), so there is no dark-mode flash and no signed in user or database is involved.
- **AC-7**: Pressing Cmd/Ctrl+K from anywhere in the shell opens the CommandPalette component (an `aria-modal="true" role="dialog"` overlay with a search input, no real search results yet, focus moved into it on open and restored to the trigger on close); Escape closes it. Implemented as a document level JS `keydown` listener (not a Blazor `@onkeydown`, which only fires on a focused element and would not catch the shortcut from anywhere in the shell).
- **AC-8**: `docs/design.md` exists and documents the literal token values (colors for both modes, type scale, spacing scale, radii, z-index layers) and the base component inventory (this feature's 10 in scope, plus the 8 deferred ones and where they're tracked), so `/architect` and `/develop` for later screens have a single source of truth to build against.

## Decision

**Chosen option**: Option 1: Hand built components with plain CSS custom properties.

Build the design system as hand authored Blazor components using Blazor's own CSS isolation, with all tokens defined once as CSS custom properties and theming driven by a `data-theme` attribute, keeping the project's toolchain exactly what `AGENTS.md` already records (dotnet, no Node/build step).

## Rationale

Reasoning and options: see [rationale.md](rationale.md).

## Feature design

**Blazor render mode note (the reason theme uses a cookie, not `localStorage`):** Auto render mode's first paint is server-prerendered, where `IJSRuntime` interop is unavailable (`OnAfterRenderAsync` hasn't run yet), so reading `localStorage` cannot influence the initial HTML; a `localStorage` based toggle would flash dark before flipping to the stored choice, and would lose in-memory state again on the server-to-WebAssembly handoff Auto performs after load. A cookie avoids both: it is sent with the initial HTTP request and read server side in `App.razor` before any markup is emitted, so `data-theme` is correct on the very first byte and survives the handoff unchanged. Every component in this feature's scope that isn't purely static (TopBar's toggle, Modal, CommandPalette) declares `@rendermode InteractiveAuto` explicitly, stated here so `/develop` does not have to guess it per component.

**Build note (found during `/develop`, not anticipated here):** the render mode cannot be set directly on `AppShell` when it receives `ChildContent` from a statically rendered parent (`RenderFragment` can't cross that boundary). Built instead with `@rendermode InteractiveAuto` set once on `<Routes>` in `App.razor`, and the theme cookie's value forwarded down as a named `string` cascading value (`InitialTheme`) rather than re-reading `HttpContext` inside the (now interactive) `MainLayout`. See the scope's `/develop` note for the full account.

**Design tokens** (literal values, root stylesheet `wwwroot/css/tokens.css`; both `data-theme="dark"` (default) and `data-theme="light"` define every color token):
- Color (dark default): `--color-bg: #0b0d10`, `--color-surface: #14171c`, `--color-border: #262b33`, `--color-text: #e6e9ee`, `--color-text-muted: #9aa4b2`, `--color-accent: #4f8cff`; light overrides under `[data-theme="light"]`: `--color-bg: #ffffff`, `--color-surface: #f6f7f9`, `--color-border: #e2e5ea`, `--color-text: #14171c`, `--color-text-muted: #5b6472`, `--color-accent: #2f6fe0`. Status colors (both modes): `--color-success: #2ea043`, `--color-warning: #d29922`, `--color-danger: #da3633`, `--color-info: #4f8cff`.
- Spacing (4px base): `--space-1: 4px`, `--space-2: 8px`, `--space-3: 12px`, `--space-4: 16px`, `--space-5: 24px`, `--space-6: 32px`, `--space-7: 48px`, `--space-8: 64px`.
- Type (system font stack, per the confirmed direction): `--font-sans: -apple-system, "Segoe UI", Roboto, sans-serif`; scale `--text-xs: 12px`, `--text-sm: 14px`, `--text-base: 16px`, `--text-lg: 18px`, `--text-xl: 20px`, `--text-2xl: 24px`, `--text-3xl: 30px`; `--line-height-body: 1.5`.
- Radius: `--radius-sm: 4px`, `--radius-md: 8px`, `--radius-lg: 12px`.
- Z-index layers (so Modal, CommandPalette, and Toast never collide once Toast ships): `--z-dropdown: 100`, `--z-modal: 200`, `--z-toast: 300`.

**Data model sketch**:
No new database entities. The theme preference (AC-6) is stored in a `workpilot-theme` cookie (values `dark` | `light`), not in Postgres; there is no signed in user session yet for it to attach to. If a later feature (#5 Auth & app shell, or #30 Settings) wants to persist theme per user, that is a new decision made there, not here.

**State transitions**: Not applicable (no entity lifecycle in this feature).

**API surface**:
No new backend endpoints. This feature is Blazor components and static/stub routes; theme read happens server side from the request cookie (`App.razor`, before first paint), theme write happens via a small JS helper (`wwwroot/js/interop.js`) that sets the cookie and toggles `data-theme` on click, called through `IJSRuntime` after `OnAfterRenderAsync`. No server round trip for the toggle itself.

**Component signatures** (pinned now so AC-3's XML docs and later screens build against a stable shape):
- `Button`: `Variant` (`Primary | Secondary | Danger | Ghost`), `Size` (`Sm | Md | Lg`), `Disabled` (`bool`), `OnClick` (`EventCallback`), `ChildContent` (`RenderFragment`)
- `Card`: `Title` (`string?`), `ChildContent` (`RenderFragment`)
- `Badge`: `Status` (`StatusKind` enum: `Neutral | Success | Warning | Danger | Info`), `ChildContent` (`RenderFragment`)
- `EmptyState`: `Title` (`string`), `Description` (`string?`), `Icon` (`string?`)
- `PageHeader`: `Title` (`string`), `Description` (`string?`)
- `Modal`: `IsOpen` (`bool`), `IsOpenChanged` (`EventCallback<bool>`), `Title` (`string`), `OnClose` (`EventCallback`), `ChildContent` (`RenderFragment`); renders with `role="dialog" aria-modal="true"`, traps focus while open, restores focus to the triggering element on close
- `CommandPalette`: `IsOpen` (`bool`), `IsOpenChanged` (`EventCallback<bool>`); same modal a11y contract as `Modal` (dialog role, focus trap and restore), opened by the global Cmd/Ctrl+K listener
- `Sidebar` / `TopBar` / `AppShell`: no public parameters beyond `ChildContent` on `AppShell`; the nav structure is the hardcoded Route map above, not a parameter

**Value sourcing**:
| Action | Value produced / displayed | Source |
|---|---|---|
| Server renders `App.razor` (first paint) | Initial theme (`dark`/`light`) applied to `<html data-theme>` | The `workpilot-theme` request cookie; `dark` when the cookie is absent (AC-6) |
| Click TopBar theme toggle | New theme value applied to `<html data-theme>` and persisted | User's click, written via `interop.js` to the `workpilot-theme` cookie (1 year expiry, `SameSite=Lax`) |
| Render Sidebar | The 5 nav sections + their sub items + literal routes | The Route map above, hardcoded in the Sidebar component; later features add real content behind each route without changing this table |
| Render a stub page | PageHeader title, EmptyState message | Static string per route, defined alongside that route in the Route map |
| Open CommandPalette | Overlay open/closed state, focus moved in | Local component state, toggled by the document level Cmd/Ctrl+K JS listener and the Escape key; focus captured/restored per the Modal a11y contract above |

**Key invariants**:
- Every color, spacing, type, radius, and z-index value used by a base component resolves to a token (`var(--token-name)`) from the Design tokens list; no component hardcodes a raw hex/px value (AC-5).
- The AppShell renders identically regardless of route; only the content area and the `NavLink`-driven active nav highlight change.
- Toggling theme never causes a full page reload; it flips the `data-theme` attribute client side and persists the cookie for the next server render.
- An open Modal or CommandPalette traps Tab focus within itself and restores focus to the element that opened it on close.

**Security model**:
No authorization in this feature; there is no signed in user yet. All routes render for anyone who can reach the app (acceptable, this stub shell carries no real data). Feature #5 (Auth & app shell) is responsible for gating access before real data is wired behind these routes.

**Configuration required**: None. No new environment variables or credentials.

**Critical test scenarios**:
- Happy path: navigating to each of the 11 routes in the Route map renders the AppShell with the correct PageHeader title and an EmptyState, with no 404, verifies **AC-1**, **AC-2**.
- Failure case: requesting the page with a `workpilot-theme=light` cookie already set renders `data-theme="light"` in the very first server-rendered HTML (no dark flash, no dependency on JS interop having run), verifies **AC-6**.
- Keyboard/accessibility: a bUnit test confirms Escape raises `Modal.OnClose` and `CommandPalette`'s close, and click raises `Button.OnClick`; a manual `/check verify` pass confirms Tab reaches every interactive element in DOM order with a visible focus ring, and that Cmd/Ctrl+K opens the palette from anywhere in the shell, verifies **AC-4**, **AC-7**.

## Build plan

Ordered as a Tracer Bullet slice (the project's recorded build approach): stand up one thin thread end to end first (tokens, shell, one real route with a couple of components), then thicken with the remaining components and routes, matching how the "Done when" bar in the scope note itself is phrased (shell renders, base components handle focus/keyboard).

1. [x] Add the token stylesheet (`wwwroot/css/tokens.css`) with the literal values from `## Feature design`'s Design tokens list, for both `data-theme` values, satisfies **AC-5**
2. [x] Build the cookie based theme mechanism: read `workpilot-theme` server side in `App.razor` before first paint (default `dark`), and `interop.js` to write the cookie and flip `data-theme` on toggle, satisfies **AC-6**
3. [x] Build AppShell, Sidebar (with the literal Route map), TopBar (including the theme toggle, InteractiveAuto), and PageHeader; wire the `/` route through them end to end as the tracer thread, satisfies **AC-1** (partial), **AC-2** (partial)
4. [x] Build Card, Badge/StatusBadge, and EmptyState (static display components), satisfies **AC-3**
5. [x] Build Button with full keyboard support, satisfies **AC-3**, **AC-4**
6. [x] Build Modal (dialog role, focus trap and restore) and the CommandPalette component (same a11y contract), wire the document level Cmd/Ctrl+K JS listener and Escape to close, satisfies **AC-3**, **AC-4**, **AC-7**
7. [x] Wire every remaining stub route from the Route map (all sub items under Work, Personal, Agent, System) through the shell, satisfies **AC-1**, **AC-2**
8. [x] Build the `/design` checklist page rendering a live example of every in-scope component, satisfies **AC-3**
9. [x] Write `docs/design.md` documenting the token values, the 10 in-scope components, and the 8 deferred ones with where they're tracked, satisfies **AC-8**
10. [x] Add bUnit component tests for Modal/CommandPalette Escape and Button click (new `tests/WorkPilot.Web.Tests` project, wired into `WorkPilot.slnx`); DOM tab order and the rendered focus ring are exercised manually in `/check verify`, covering **AC-4**

## Consequences

**Positive**:
- Every later screen (Jobs list, Dashboard, Approvals, and so on) can be built directly on top of a working shell and component set, with no styling or navigation decisions left open.
- No new dependency or build tool added to the project; `dotnet build`/`aspire run` remain the only commands needed.
- Light/dark mode and keyboard accessibility are solved once, centrally, instead of per screen.

**Negative / tradeoffs**:
- Every component is hand maintained; bugs or missing behavior (e.g. a DataTable feature a later screen needs, like sorting) must be added here rather than inherited free from a library.
- The theme preference lives only in the browser until auth exists; it will not follow the founder across devices until a later feature persists it server side.
- Component test coverage depends on bUnit being wired correctly into a new test project; if that scaffolding step is skipped or misconfigured, AC-4's automated coverage silently doesn't run (mitigated by `/check verify` exercising it manually too).

**Neutral**:
- Introduces the project's first Blazor CSS isolation usage pattern and its first `docs/design.md`, both becoming the reference for every later UI feature.
- Introduces bUnit as a new test library (declined an Agent Skill search for it; the existing `csharp-xunit` and `scaffold-dotnet-test-project` skills already cover its xUnit style wiring).

## Follow-up

- [ ] Once feature #5 (Auth & app shell) exists, decide whether to persist the theme preference to the user's profile instead of (or in addition to) the cookie.
- [ ] Once feature #32 (Command palette & global search) is designed, the CommandPalette component built here gets real search wired in; this feature only ships the empty shell and shortcut.
- [ ] Input, Select, Tabs, DataTable, Skeleton, Timeline, Stepper, and Toast are deferred out of this feature's scope; each is built by the first real screen that needs it (e.g. DataTable by the Jobs list, feature #12), against that screen's real requirements instead of a guessed shape now, and gets its component signature pinned in that screen's own spec.
- [ ] `bunit` pulls a transitive `AngleSharp` 1.2.0 with a known moderate severity advisory (NU1902); test-only dependency, low risk for a single user app, but worth revisiting when bUnit ships a version pinning a patched AngleSharp.
