## Context

Nothing later can be built without this. Every planned screen (jobs list, dashboard, approvals, and so on) needs a shell to render inside and components (a card, a table, a button) to render with; without this feature first, each later feature would invent its own one off styling and structure, which fragments the product and multiplies rework.

The project is a single user internal tool (a personal work agent for the founder), not a public facing product, so there is no brand identity to match and no external design handed down; the visual direction has to be decided here, from first principles, for a data dense, dashboard style tool (tables, cards, timelines, status badges). The stack is .NET Aspire with a Blazor Web App in Auto render mode; Blazor already ships CSS isolation (a `.razor.css` file scoped to its component) with no extra build step, which shapes whether a CSS framework or utility library is worth adding.

Auth does not exist yet (feature #5, not yet designed), so this feature cannot assume a signed in user or a place to persist a per user preference (like a theme choice) in the database.

## Options considered

### Option 1: Hand built components with plain CSS custom properties (chosen)

Every base component is authored as a Blazor `.razor` file with a co-located `.razor.css` (Blazor's built in CSS isolation), consuming tokens defined once in a root stylesheet as CSS custom properties. Dark/light mode switches by toggling a `data-theme` attribute on `<html>`, which the CSS keys off.

**Pros**:
- Zero new dependency or build step; the project stays pure .NET/Blazor, matching a small single user app with no frontend build pipeline today.
- Full control over markup and behavior, so keyboard/focus handling (AC-4) is implemented exactly to spec, not fought against a third party library's defaults.
- CSS custom properties give light/dark theming for free (swap the property values under a `[data-theme="dark"]` selector), with no JS theming library.

**Cons**:
- More upfront component work than adopting a ready made library; every component (Modal, Tabs, DataTable) is built from scratch, including its accessibility behavior.

### Option 2: MudBlazor (established Blazor component library)

Adopt MudBlazor's component set (buttons, cards, tables, dialogs) and reskin its Material based theme to match the token direction.

**Pros**:
- Much faster initial build; most components already exist, tested and accessible out of the box.
- Large community and active maintenance reduce the risk of abandoned components.

**Cons**:
- Its own strong Material based design language fights a custom look; substantial reskinning effort just to feel native to this app, arguably more work than building narrower, purpose fit components directly.
- Adds a third party dependency whose release cadence and breaking changes this project doesn't control, for a single user app that doesn't need its broad feature surface (rich pickers, complex grids) yet.

### Option 3: Tailwind CSS utility classes

Add Tailwind via a Node/PostCSS build step wired into the .NET build, and author components with utility classes instead of custom properties and isolated CSS.

**Pros**:
- Fast, consistent utility based authoring once set up; large ecosystem of patterns to copy.

**Cons**:
- Introduces a Node/PostCSS build pipeline into a repo that is otherwise pure .NET, adding a second toolchain to install, run, and keep in sync with `dotnet build`/`aspire run`, for a project that doesn't need it yet.

## Rationale

The project is single user, has no existing brand or component library to reconcile, and the stack (`AGENTS.md`) is Aspire plus Blazor with no frontend build tooling anywhere else in the repo; adding Tailwind's Node/PostCSS pipeline (Option 3) or a component library with its own design language to reskin (Option 2) both cost more than they save at this scale; a small, hand built component set that exactly matches the token driven look and the accessibility requirements in AC-4 is more control for less total effort here. Blazor's CSS isolation already solves the "how do styles stay scoped per component" problem that a utility framework or component library would otherwise justify, so neither is needed to keep styling maintainable.
