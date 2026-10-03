# Verify: Notifications · spec 0020 · updated 2026-10-03
_Steps derived from spec 0020 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

## UI / manual
- [ ] Sign in, start an agent run whose plan has an approval gated step (e.g. the explicit confirmation demo tool) → within one 15 second poll the bell shows `1`; open the drawer → an Action required row "Approval needed: {tool}" with the run's goal as body → AC-1, AC-4
- [ ] Click that row → it is marked read, the bell count drops, and you land on `/approvals` → AC-4
- [ ] Make a run fail (planner provider error, or reject an approval) → an Error row "Agent run failed" whose body starts with the readable reason (e.g. "You rejected an approval. Goal: …"), link `/agent/runs` → AC-2
- [ ] Push 5 jobs over the strong match threshold in one UTC day → one Info row "5 new strong matches today", payload top 3 by score, link `/jobs?sort=score&minScore={threshold}` (threshold from your match profile, default 70) → AC-3
- [ ] Read today's digest, then push one more strong match → a new unread "1 new strong match today" row starts → AC-3
- [ ] With more than 99 unread, the bell shows `99+`; with 0 it shows no count → AC-4
- [ ] Navigate between pages → the count refreshes on each navigation; leave the page open → it refreshes every 15 seconds → AC-4
- [ ] Drawer "Mark all read" → count goes to 0, rows lose the unread highlight; "See all" opens `/notifications` → AC-4
- [ ] `/notifications` with more than 25 rows → page numbers, `?page=2` in the URL; "Unread only" → `?unread=true`, only unread rows → AC-5
- [ ] On `/notifications`: Mark read / Mark unread toggles one row; Dismiss removes it for good; Mark all read marks every unread one → AC-5
- [ ] Relative times show in the browser's time zone (hover shows the exact local time) → AC-5
- [ ] Stop the Api while a page is open → the bell keeps its last count and recovers on the next tick after the Api is back; the drawer and page show an error with Retry → AC-9
- [ ] With no notifications, the drawer and the page both say "You're all caught up" → AC-9

## Commands
- [ ] `curl "$API/internal/notifications/unread-count?profileId=$P"` → `{"count":N}` → AC-4
- [ ] `curl "$API/internal/notifications?profileId=$P&page=0"` → 400 ProblemDetails naming `page`; `pageSize=51` → 400 naming `pageSize` → AC-5
- [ ] `curl -X POST "$API/internal/notifications/$ID/read" -H 'content-type: application/json' -d '{"profileId":"<other profile>","read":true}'` → 404 ProblemDetails, row unchanged → AC-8
- [ ] `curl -X DELETE "$API/internal/notifications/$ID?profileId=<other profile>"` → 404 ProblemDetails, row still there → AC-8
- [ ] Replay one `JobMatched` outbox delivery (rerun `HandleEventJob` for the same message and handler) → digest count unchanged; a second `JobMatched` for a job already in the digest → count unchanged → AC-6
- [ ] Set a read notification's `ReadAt` to 91 days ago and an unread one's `CreatedAt` to 200 days ago, trigger `notifications.cleanup` from `/hangfire` → the read one is soft deleted, the unread one stays → AC-7
- [ ] Start the Api with `Notifications__ReadRetentionDays=0` → startup fails naming the setting → Value sourcing (cleanup cutoff)
- [ ] Hangfire `/hangfire` → Recurring jobs lists `notifications.cleanup` at `0 3 * * *` → AC-7

## Value sourcing
- [ ] Approval notification: profile, tool and goal come from `approvals` → `agent_steps` → `agent_runs` (an approval for another profile's run notifies that profile, not yours)
- [ ] Run failed: reason text from `AgentRunFailureReasons.Describe` for each of the six reasons; goal from `agent_runs.Goal`
- [ ] Digest day: the UTC date of handling time; a match just after 00:00 UTC starts a new digest even if your local date has not changed
- [ ] Digest jobs: title and company from `jobs`, score from the event
- [ ] Digest link threshold: change your strong match threshold to 80, then a new digest links `minScore=80`
- [ ] Bell and list profile: from the session claim; no Web request takes a profile id from the browser
- [ ] Relative time: switch the browser time zone and reload; the exact time on hover follows it
- [ ] Cleanup cutoff: `Notifications:ReadRetentionDays` (default 90), now in UTC minus that many days

## Acceptance criteria coverage
- AC-1: UI 1 · AC-2: UI 3 · AC-3: UI 4, UI 5 · AC-4: UI 1, 2, 6, 7, 8, Commands 1 · AC-5: UI 9, 10, 11, Commands 2 · AC-6: Commands 5 · AC-7: Commands 6, 8 · AC-8: Commands 3, 4 · AC-9: UI 12, 13
