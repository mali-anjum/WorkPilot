# WorkPilot design system

Source: hand authored, no reference given (spec [0003](specs/0003-design-system-ui-foundation/index.md), "No design yet, suggest a direction"). Token values live in code (`src/WorkPilot.Web/wwwroot/css/tokens.css`); this file is the art direction and the pointer to them, not a duplicate copy.

## Character

A data dense, dashboard style internal tool for a single user (the founder). Dark first (matches how a founder actually works: long sessions, low glare), calm and utilitarian rather than marketing bright, one accent color used sparingly for primary actions and links. No brand identity to match; the direction favors legibility and density over decoration.

## Build mandate

- System font stack, no web font load (`--font-sans`).
- Every color, spacing, type, radius, and z-index value comes from a token in `tokens.css`; never a hardcoded value in a component's `.razor.css`.
- Dark is the default theme (`data-theme="dark"` on `<html>`, unset falls back via the `:root` block); light is available and toggled from the TopBar. The active theme is read from a `workpilot-theme` cookie server side, before first paint, so there is no flash and no dependency on browser storage or a signed in session.
- Every interactive component is keyboard operable: visible `:focus-visible` outline (from `--color-accent`), Enter/Space activates, Escape closes an open Modal or the CommandPalette.

## Tokens (values live in `src/WorkPilot.Web/wwwroot/css/tokens.css`)

- **Color**: `--color-bg`, `--color-surface`, `--color-border`, `--color-text`, `--color-text-muted`, `--color-accent`, plus semantic status colors `--color-success` / `--color-warning` / `--color-danger` / `--color-info`. Dark values live under `:root`/`[data-theme="dark"]`; light overrides under `[data-theme="light"]`.
- **Spacing**: `--space-1` through `--space-8`, a 4px base scale (4/8/12/16/24/32/48/64).
- **Type**: `--text-xs` through `--text-3xl` (12 to 30px), `--line-height-body: 1.5`.
- **Radius**: `--radius-sm` / `--radius-md` / `--radius-lg` (4/8/12px).
- **Z-index**: `--z-dropdown` / `--z-modal` / `--z-toast` (100/200/300), so overlays never collide.

## Component inventory

**In scope, built (spec 0003):**
- `AppShell` — the top level layout (sidebar + top bar + content), `@rendermode InteractiveAuto`. `src/WorkPilot.Web.Client/Shared/AppShell.razor`
- `Sidebar` — the 5 top level nav sections and their sub items, from the hardcoded route map (`NavRoutes.cs`). `src/WorkPilot.Web.Client/Shared/Sidebar.razor`
- `TopBar` — the theme toggle and the Cmd/Ctrl+K hint. `src/WorkPilot.Web.Client/Shared/TopBar.razor`
- `PageHeader` — a page's title and description. `src/WorkPilot.Web.Client/Shared/PageHeader.razor`
- `Card` — a bordered content surface with an optional title. `src/WorkPilot.Web.Client/Shared/Card.razor`
- `Button` — `Variant` (Primary/Secondary/Danger/Ghost) × `Size` (Sm/Md/Lg). `src/WorkPilot.Web.Client/Shared/Button.razor`
- `Badge` / StatusBadge — `Status` (`StatusKind`: Neutral/Success/Warning/Danger/Info). `src/WorkPilot.Web.Client/Shared/Badge.razor`
- `EmptyState` — title, optional description and icon, used by every stub page. `src/WorkPilot.Web.Client/Shared/EmptyState.razor`
- `Modal` — dialog role, `aria-modal`, focus trap and restore, Escape to close. `src/WorkPilot.Web.Client/Shared/Modal.razor`
- `CommandPalette` — same modal a11y contract, opened globally by Cmd/Ctrl+K (a document level JS listener, not a Blazor `@onkeydown`), no real search yet. `src/WorkPilot.Web.Client/Shared/CommandPalette.razor`

Live examples of all ten: `/design`.

**Deferred** (built by the first real screen that needs each, against that screen's real requirements; tracked in spec 0003's Follow-up): `Input`, `Select`, `Tabs`, `DataTable`, `Skeleton`, `Timeline`, `Stepper`, `Toast`.

## Route map

The shell's nav and every stub route (`src/WorkPilot.Web.Client/Shared/NavRoutes.cs`):

| Section | Sub item | Route |
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
