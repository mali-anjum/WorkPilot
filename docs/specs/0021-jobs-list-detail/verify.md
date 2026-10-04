# Verify: Jobs list and job detail · spec 0021 · updated 2026-10-04 (AC-9 responsive steps)
_Steps derived from spec 0021 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

Setup you may use: the Api and Web outside Aspire (see AGENTS.md), a signed in user, and at least one real board added through the drawer (Greenhouse `gitlab` works and gives about 200 jobs). Fill the match profile on `/profile` so scores are meaningful.

## UI / manual
- [x] Visit `/jobs` with no sources → "No jobs yet" with an "Add a job source" button that opens the Job sources drawer → AC-8
- [x] In the drawer, choose Lever and enter `Not A Board!` → "That is not a valid Lever board." shows under Board token and the field is marked invalid; type a 201 character company name the same way → error under Company name → AC-6
- [x] Add Greenhouse `gitlab` with company `GitLab` → "Added gitlab…" message; after the run the source row shows its job count and "last run …: N new, M updated" → AC-6
- [x] A source that never ran shows "Never run"; "Run now" queues a run and Refresh shows the newer last run time → AC-6
- [x] Press Escape, or click outside the drawer → it closes; Tab stays inside while it is open → AC-6
- [x] `/jobs` lists 25 rows per page with page numbers: scored before unscored, unblocked before blocked, best score first; page 2 repeats no row of page 1 → AC-1
- [x] Switch Sort by to Newest → rows ordered by posted date (first seen when the source gives none), newest first; the URL has `sort=newest` → AC-1
- [x] Each row shows title (links to `/jobs/{id}`), company, location, a remote badge when known, "Posted …" relative time, one badge per source type, and a `82 · High` score badge; a blocked job shows a Blocker badge → AC-3
- [x] An unscored row says "Not scored" with `Scoring…`, `Not enough information`, or `Complete your profile` (a link to `/profile`) → AC-3
- [x] Set Minimum score 70 and Posted within 30 days → only matching rows, the URL holds `minScore=70&posted=30`, a reload keeps both inputs and the same rows; the page resets to 1 → AC-2
- [x] Try each filter once: search text (title or company, case insensitive; `%` and `_` match literally), company (exact), location (contains), remote, source, minimum salary (unscored or salary less jobs drop out), Hide blocked jobs → AC-2
- [x] "Clear filters" empties every filter but keeps the sort; with filters that match nothing → "No jobs match these filters" with a Clear filters button → AC-2, AC-8
- [x] Dismiss a row → it leaves the list with "Dismissed <title>. Undo"; Undo puts it back in its sorted place → AC-4
- [x] Tick "Show dismissed" → dismissed rows show faded with a Dismissed badge and an Undo button; dismissing twice changes nothing → AC-4
- [x] Stop the Api and dismiss a row → an error shows and the row stays → AC-8
- [x] With an empty profile, `/jobs` shows the banner "Scores need a complete profile…" linking to `/profile` → AC-7
- [x] Open a job → left: details (company, location, remote, salary when present, posted, first seen), description, and "Where it was seen" (source type, Primary, original posting link, first and last seen); right: score, confidence, why it matches, missing, unknown, score breakdown, Rescore → AC-5
- [x] On the detail page, Dismiss → "You dismissed this job" note and an "Undo dismiss" button → AC-4
- [x] Soft delete a job (`update app.jobs set "IsDeleted" = true where "Id" = '<id>'`) and open it → "No longer listed." notice above its last known details, no Rescore; it is gone from `/jobs` → AC-5
- [x] Open `/jobs/00000000-0000-0000-0000-000000000001` → "Job not found" → AC-5
- [x] At 320, 390, 768, 1024, 1440 and 1920px wide, in dark and light themes, on `/jobs`, `/jobs/{id}` and with the drawer open: `.wp-app-shell__main` has `scrollWidth` equal to `clientWidth`, no descendant's right edge passes its right edge, and no title, company or description ends in an ellipsis. Until the responsive app shell ships, hide the sidebar first (inject `.wp-sidebar{display:none}`) so the content gets the full width → AC-9
- [x] Below 960px: the filters sit behind a "Filters (n active)" button, closed on load, with the right count; the sort switch is above the list; the detail page is one column, details then match panel → AC-9
- [x] Below 600px: a row reads title, score badge and blocker flag, details, then Dismiss; pagination shows Previous, Next, the current page and its neighbours; the match breakdown is one column with full width Rescore and Dismiss; the drawer is full width and full height with stacked source rows and form → AC-9
- [x] At 390px: every control is at least 24 by 24px (inline text links exempt), the filter toggle, Dismiss, Rescore, Run now and the drawer's close and add are at least 44px tall, tapping a checkbox's label toggles it, and the detail page shows exact posted and seen dates as text → AC-9

## Value sourcing
- [x] Profile: every Api call from the pages carries the signed in user's `profileId` (Web claim), never one from the browser; another user's dismissals and scores never show → Value sourcing: profile
- [x] Score, confidence, blocker: change one `job_matches` row's `Score` and `HasBlocker` in the database → that row's badge and order change after reload → Value sourcing: score
- [x] Match status: a match row with a null score → "Not enough information"; no row and an incomplete profile → "Complete your profile"; no row and a complete profile → "Scoring…" → Value sourcing: match status
- [x] Posted: set a job's `PostedAt` to null → its row says "First seen …" with the earliest link's first seen time, and Newest sorts it by that time → Value sourcing: posted
- [x] Source badges: a job linked from two source types shows both badges → Value sourcing: source badges
- [x] Facets: company, remote and source lists hold only values on non deleted jobs; a source with no listed jobs is not offered → Value sourcing: filters
- [x] Posted within uses server UTC now: a job posted 23 hours ago is in "Last 24 hours", one posted 25 hours ago is not, whatever your browser time zone → Value sourcing: posted within
- [x] Relative times use your time zone: change the browser time zone and hover a "Posted" time → the exact time shows in that zone → Value sourcing: relative times
- [x] Drawer job count counts links from that source to non deleted jobs (it can exceed the list total when a merge put two postings on one job) → Value sourcing: job count
- [x] Drawer last run reads the latest `JobsIngested` audit row for that source, with its `created` and `updated` counts → Value sourcing: last run
- [x] Run now reuses the stored source (no rename): the run's audit row targets the same source id and its company name stays → Value sourcing: run now

## Commands
- [x] `curl "$API/internal/matches?profileId=$P&pageSize=500"` → 400 ProblemDetails with `errors.pageSize` → AC-8
- [x] `curl "$API/internal/matches?profileId=$P&minScore=101&postedWithinDays=3&sort=oldest"` → 400 naming `minScore`, `postedWithinDays` and `sort` → AC-2
- [x] `curl -X PUT "$API/internal/jobs/$JOB/dismissal" -d '{"profileId":"'$P'"}' -H 'Content-Type: application/json'` twice → 204 both times, one `job_dismissals` row → AC-4
- [x] `curl -X DELETE "$API/internal/jobs/$JOB/dismissal?profileId=$P"` twice → 204 both times → AC-4
- [x] `curl -X POST "$API/internal/jobs/ingestions" -d '{"source":"workday","boardToken":"x"}' -H 'Content-Type: application/json'` → 400 with `errors.source` → AC-6
- [x] `curl -X POST "$API/internal/jobs/sources/<unknown id>/ingestions"` → 404 ProblemDetails → AC-6
- [x] `dotnet test WorkPilot.slnx` (with `WORKPILOTDB_CONNECTION`) → all pass, including `JobsListTests` (merge keeps a dismissal on the kept job) → Key invariants

## Acceptance-criteria coverage
- AC-1 … list order, paging, Newest sort, layout steps · AC-2 … filter, URL, clear steps and the 400 commands · AC-3 … row content and unscored reason steps · AC-4 … dismiss, show dismissed, detail dismiss, PUT/DELETE commands · AC-5 … detail, soft deleted, unknown id steps · AC-6 … drawer steps and ingestion commands · AC-7 … incomplete profile banner step · AC-8 … empty, error, failed dismiss steps and `pageSize=500`
