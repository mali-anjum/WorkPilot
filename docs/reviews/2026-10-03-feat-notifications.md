# Review, feat/notifications, 2026-10-03

**Reviewed by**: Sonnet 5.5 (author on Sonnet 5.5)
**Scope**: 34 files, branch vs main
**Verdict**: Changes requested

## Summary
Adds spec 0020: three outbox handlers (approval requested, run failed, strong match digest), a profile scoped `/internal/notifications` API on `Result<T>`, a daily cleanup job, a polling bell in the top bar, and a `/notifications` page. The design follows the spec and AGENTS.md closely: the module is the only writer, handlers never open a transaction, the digest uses `FOR UPDATE` plus the partial unique index and relies on the Hangfire retry, every endpoint filters by `profileId`, and links are validated in the Domain. One major issue: the page lets a cancellation exception escape event handlers, which can kill the circuit. The rest is minor.

## Major
### 🟠 In flight page actions throw an uncaught OperationCanceledException, `src/WorkPilot.Web/Components/Pages/Notifications/Notifications.razor:248`
**Problem**: `SetReadAsync` (248), `DismissAsync` (279) and `MarkAllReadAsync` (310) pass `_cts.Token` but catch only `HttpRequestException`. `Restart()` (330, called by `LoadAsync` on every query string change) and `Dispose()` cancel that same token. Clicking Dismiss or Mark read and then a page link or the Unread toggle before the POST returns makes the request throw `TaskCanceledException`. The same happens when the user navigates away mid request. `LoadAsync` handles this case, the three actions do not. The bell has the same gap in `OpenAsync` (`NotificationBell.razor:253`, `:283`) after Dispose.
**Why it matters**: An unhandled exception in a Blazor Server event handler terminates the circuit (the "unhandled error" banner and a reload). The server side change (dismiss or read) may already have applied while the UI shows nothing.
**Suggested fix**: Catch `OperationCanceledException` in the three action methods (and the bell actions) and return quietly, as `LoadAsync` does. Alternatively use a separate token for actions that only `Dispose` cancels, so a reload does not cancel a pending mutation.

## Minor
### 🟡 Marking a read digest unread can hit the unique index and return 500, `src/WorkPilot.Application/Modules/Notifications/NotificationsService.cs:114`
**Problem**: `MarkUnread` on a read digest whose day already has a newer open digest (AC-3 starts a new one after the first is read) violates `UX_notifications_open_digest`. `SaveChangesAsync` throws `DbUpdateException`, which is not mapped to a `Result`.
**Why it matters**: The UI offers "Mark unread" on every row, so a plausible click produces a 500 rather than ProblemDetails. This breaks the one error pattern.
**Suggested fix**: Handle it in the use case or repository (catch unique violation 23505 and return a conflict `Result`, or merge the digests), and add a test.

### 🟡 Page number can overflow the OFFSET, `src/WorkPilot.Infrastructure/Modules/Notifications/NotificationRepository.cs:242`
`(page - 1) * pageSize` is int math. `page=2000000000&pageSize=50` overflows to a negative OFFSET and Postgres fails with a 500. Cap `page` in `NotificationsService.GetPageAsync` or use long math and return an empty page.

### 🟡 Digest day comes from handling time, not event time, `src/WorkPilot.Application/Modules/Notifications/Handlers/NotificationHandlers.cs:153`
A retry or sweep delayed across 00:00 UTC puts the job in the next day's digest, and a retried delivery cannot dedupe against the earlier day's. This is spec-consistent ("event handling time"), but worth recording as accepted. Also, `ReadDigest` returns an empty digest on unreadable JSON (line 205), which would silently reset the count and job ids.

### 🟡 Bell poll loop can die silently, `src/WorkPilot.Web/Features/Notifications/NotificationBell.razor:125`
`PollAsync` is fire and forget and catches only OCE and `ObjectDisposedException`. Any other exception from `InvokeAsync(RefreshCountAsync)` (for example a `JsonException` from a malformed body, which `RefreshCountAsync` does not catch) ends the loop with an unobserved task, and the bell stops refreshing for the circuit's life, contrary to AC-9 (try again on the next tick). Catch `Exception` per tick inside the loop and log. `LoadLatestAsync` (225) likewise catches only `HttpRequestException`.

### 🟡 Page does not recover after dismissing or reading the last row on a page, `src/WorkPilot.Web/Components/Pages/Notifications/Notifications.razor:271`
Removing items locally leaves the page short and never refills. Dismissing the only row on page 2 shows "You're all caught up" with no pager while page 1 still has rows. Reload after a mutation when the page empties (or step back one page).

### 🟡 Page actions do not refresh the bell count, `src/WorkPilot.Web/Components/Pages/Notifications/Notifications.razor:229`
Mark read, dismiss and mark all read leave the bell stale for up to 15 s, since only navigation and the timer refresh it. Cheap fix: a small scoped notifier the bell listens to.

### 🟡 Digest link targets filters the Jobs page ignores, `src/WorkPilot.Domain/Modules/Notifications/StrongMatchDigest.cs:26`
`/jobs?sort=score&minScore={n}` matches the spec, but `Jobs.razor` reads no `sort` or `minScore` query parameters, so "See the matches" lands on the plain list. Add the parameters to the Jobs page or record a follow-up.

### 🟡 Link rule is duplicated and the Web copy is weaker, `src/WorkPilot.Web/Features/Notifications/NotificationDisplay.cs:468`
`SafeLink` omits the control character check in `Notification.IsAppRelative` (a stored `/\t/host` would be accepted). The Api never stores such a row, so this is defense in depth only. Reuse the Domain method, or share the rule through Contracts.

## Nits
- ⚪ `src/WorkPilot.Web/Components/Pages/Notifications/Notifications.razor:67`, `@onclick:preventDefault` on the title link breaks ctrl/cmd click to open in a new tab.
- ⚪ `src/WorkPilot.Web/Features/Notifications/NotificationBell.razor:320`, `Dispose` disposes `_cts` right after `Cancel`; any later `_cts.Token` access in an in flight handler throws `ObjectDisposedException`. Skip the dispose, or fold into the AC fix above.
- ⚪ `src/WorkPilot.Infrastructure/Modules/Notifications/NotificationRepository.cs:259`, dismissed rows are never purged, so the table only grows (spec accepts soft delete).
- ⚪ Drawer has `role="dialog"` but no focus trap or focus return to the bell.

## Strengths
- Digest concurrency is sound: `FOR UPDATE` serialises updaters under READ COMMITTED, the partial unique index catches the insert race, and the delivery insert rolls back with the failed transaction so the Hangfire retry (10 attempts) reruns it cleanly. The live 74 event burst result confirms it.
- Handler rerun safety is real: delivery rows make replay a no-op, the digest also dedupes on job id, missing approval, run, job or profile is skipped without failing, and no handler opens a transaction.
- Profile scoping is uniform: every query, `ExecuteUpdate` and `FindAsync` filters on `ProfileId`, another profile's id is a 404, and the Web takes the id from the claim. Output is Razor encoded and the payload is never rendered.
- The migration backfill is careful (defaults, orphan rows removed before the FK, hand written SQL called out), and the indexes match the queries.
- Bell lifecycle is mostly right: timer and `LocationChanged` start only after the interactive first render, `InvokeAsync` marshals ticks, and teardown cancels and unsubscribes.

## Test coverage
Good: 965 tests pass. Domain tests cover the digest, link validation and reasons; Api tests run the handlers through `HandleEventJob` on real Postgres (replay, race, deleted targets, read digest starts a new one), scoping, paging and cleanup; bUnit covers the bell and page. Gaps: no test for cancelling an in flight action (the Major), for mark unread colliding with an open digest, for an oversized page number, or for the poll loop surviving a non-HTTP exception.

## Resolution (2026-10-03, on feat/notifications)
- **Major, fixed.** Page actions (mark read, dismiss, mark all) now run on a token only leaving the page cancels, and both the page and the bell catch `OperationCanceledException` in their actions. `Dispose` cancels without disposing the token sources. Locked by `Leaving_the_page_mid_action_does_not_throw`.
- **Mark unread on a digest, fixed.** It answers 409 ProblemDetails when a newer digest for the same day is open (`NotificationsService.SetReadAsync`, `INotificationRepository.HasOtherOpenDigestAsync`). Locked by `Marking_a_read_digest_unread_while_a_newer_one_is_open_is_a_409`.
- **Page overflow, fixed.** `page` is limited to 1 to 100,000 (400 above). Locked by the `page=100001` case.
- **Poll loop, fixed.** The refresh keeps the last count on any failure (not only HTTP ones), and the loop survives a bad tick. The test for this caught the same gap on the first refresh, in `OnAfterRenderAsync`, which is fixed too. Locked by `An_unexpected_error_on_one_tick_does_not_stop_polling`.
- **Empty page after the last dismiss, fixed.** The page reloads its rows, or moves to the last page that still has rows. Locked by `Dismissing_the_last_row_of_a_page_loads_the_rows_left`.
- **Kept as is.** The digest day from handling time is what spec 0020 decides (Value sourcing). `ReadDigest` resetting on bad JSON only happens for a corrupted row the module itself never writes. The bell's 15 second lag after page actions is the polling tradeoff the spec accepts. The digest link's `sort` and `minScore` take effect when the jobs list (#12, spec 0021) reads them; the link already matches that spec's parameters.
- **Nits left for later.** Ctrl or Cmd click on a title, a focus trap for the drawer, and purging old dismissed rows.
