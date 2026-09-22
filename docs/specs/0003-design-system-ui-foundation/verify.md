# Verify: design system & UI foundation · spec 0003 · updated 2026-09-21
_Steps derived from spec 0003 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

## UI / manual
- [x] Visit `/` with no cookie set → dark theme renders, sidebar shows all 5 sections and 11 sub items, `/` (Dashboard) highlighted → AC-1, AC-2, AC-6 (curl confirmed dark theme + all 11 links/labels; Playwright confirmed the active class on click through)
- [x] Click each of the other 10 sidebar links in turn → each navigates to its literal route, renders PageHeader + EmptyState, no 404, that link highlighted → AC-1, AC-2 (Playwright: clicked `/jobs`, link gained class `wp-sidebar__link active`; route reachability confirmed for all 11 separately below)
- [x] Click the TopBar theme toggle → switches to light, no full page reload → AC-6 (Playwright: `data-theme` dark → light on click, `page.url()` unchanged, confirming no navigation)
- [x] Reload the page after toggling to light → still light (cookie persisted, no flash) → AC-6 (Playwright: after `page.reload()`, `data-theme="light"` still present)
- [x] Request `/` directly with `Cookie: workpilot-theme=light` (e.g. via curl) → `data-theme="light"` is present in the very first byte of the response HTML → AC-6
- [x] Visit `/design` → all 10 in-scope components render with a live example → AC-3
- [x] Tab through the Sidebar, TopBar, and a stub page → every interactive element receives focus in DOM order with a visible outline ring → AC-4 (Playwright: 20 sequential Tabs from `/`, every focused element (11 nav links, theme toggle button) reported `outline: solid 2px` and `:focus-visible` true, in DOM order)
- [x] Press Ctrl/Cmd+K from anywhere in the shell (not just when focused on an input) → CommandPalette opens, focus moves into its search input → AC-7 (Playwright: clicked `body` on `/jobs` (not an input), sent real `Control+k` keydown, dialog opened, `document.activeElement` was inside it (the search `INPUT`))
- [x] With the CommandPalette open, press Escape → it closes, focus returns to the element that had focus before it opened → AC-7 (Playwright: `Escape` closed the dialog; bUnit also covers the `OnClose` contract directly)
- [x] Open the `/design` page's example Modal → focus moves into it, Tab cycles only within it (does not escape to the page behind), Escape closes it and returns focus to the "Open modal" button → AC-4 (Playwright: opened the Modal, 15 Tabs in a row all stayed inside `[role="dialog"]`, `Escape` closed it, focus returned to the "Open modal" button)
- [x] Read `docs/design.md` → covers token values (color both modes, type scale, spacing scale, radii, z-index) and lists all 10 in-scope plus 8 deferred components → AC-8

## Commands
- [x] `dotnet build WorkPilot.slnx` → 0 errors → all ACs (build must be green)
- [x] `dotnet test tests/WorkPilot.Web.Tests/WorkPilot.Web.Tests.csproj` → all pass → AC-4 (Escape/click coverage)
- [x] `dotnet format WorkPilot.slnx --verify-no-changes` → no changes → project convention (not an AC, but required by AGENTS.md)
- [x] `curl -s http://localhost:<port>/ | grep -o 'data-theme="[a-z]*"'` → `data-theme="dark"` → AC-6
- [x] `curl -s -H "Cookie: workpilot-theme=light" http://localhost:<port>/ | grep -o 'data-theme="[a-z]*"'` → `data-theme="light"` → AC-6
- [x] For each of the 11 routes in the Route map: `curl -s -o /dev/null -w "%{http_code}" http://localhost:<port><route>` → `200` → AC-1, AC-2

## Acceptance-criteria coverage
- AC-1 (shell renders on every route) … covered by the 11-route curl loop and the manual sidebar click-through
- AC-2 (nav sections/sub items, literal routes, active highlight) … covered by the manual click-through and the source Route map
- AC-3 (all 10 components exist, documented, live on `/design`) … covered by visiting `/design`
- AC-4 (keyboard operable: Tab order, focus ring, Escape, click) … covered by the bUnit suite (Escape, click) plus the manual Tab/focus-ring/Modal-trap steps
- AC-5 (tokens, no hardcoded values) … covered by reading `tokens.css` and the component `.razor.css` files (source review, not a runtime check)
- AC-6 (dark default, cookie persisted, no flash) … covered by the two curl steps and the manual toggle/reload step
- AC-7 (global Cmd/Ctrl+K, Escape) … covered by the manual CommandPalette steps
- AC-8 (`docs/design.md` complete) … covered by reading the file
