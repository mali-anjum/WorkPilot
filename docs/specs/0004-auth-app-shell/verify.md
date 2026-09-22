# Verify: auth & app shell · spec 0004 · updated 2026-09-22
_Steps derived from spec 0004 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

## UI / manual
- [x] Visit `/jobs` while signed out → redirected to `/login?returnUrl=%2Fjobs`, no shell content rendered, even partially → AC-1, AC-7
- [x] Submit correct email + password on `/login` with `returnUrl=/jobs` → lands on `/jobs` inside the shell → AC-1, AC-2
- [x] Submit wrong password on `/login` → generic "Incorrect email or password" shown, stays on `/login` → AC-2
- [x] Sign in, then delete/rotate the Data Protection key ring (or corrupt the cookie) and revisit a shell route → redirected to `/login` with returnUrl preserved → AC-4
- [x] From inside the shell, use the TopBar "Sign out" link → `/logout-confirm` → confirm → session cookie cleared, redirected to `/login`; a subsequent shell request redirects again → AC-6
- [x] Request a password reset from `/forgot-password` with the founder's real email → same generic confirmation message shown regardless of whether the email exists → AC-5
- [x] Follow the real recovery link GoTrue emails (token_hash + type=recovery) to `/reset-password` → set a new password → redirected to `/login` → sign in with the new password succeeds → AC-5
- [x] Attempt the GoTrue `/signup` endpoint directly (or look for any signup UI) → rejected (`signup_disabled`) / not present anywhere in the app → AC-8
- [x] Inspect the session cookie's contents (dev tools) → only identity claims (`sub`, `email`, `profile_id`), `HttpOnly`, no GoTrue access/refresh token → AC-7

## Commands
- [x] `curl -s -o /dev/null -w "%{http_code} %{redirect_url}" http://localhost:5128/jobs` → `302` to `/login?returnUrl=%2Fjobs` → AC-1
- [x] `curl -s -X POST http://localhost:5128/login <correct credentials + antiforgery cookie/token>` → `302` to the return route, `Set-Cookie: workpilot-session=...` present → AC-2, AC-3
- [x] `curl -s -X POST http://localhost:5128/login <wrong password>` → `401`, body contains the generic error message → AC-2
- [ ] `docker exec workpilot-supabase-db psql -U postgres -c "select count(*) from app.profiles"` before and after a first-ever sign in → increases by exactly one → Value sourcing (`profileId` on first sign in)
- [x] `curl -s -X POST http://<gotrue>/signup -d '{"email":"x@example.com","password":"whatever123"}'` → `422 signup_disabled` → AC-8
- [x] `grep GOTRUE_PASSWORD_MIN_LENGTH,GOTRUE_MAILER_URLPATHS_RECOVERY,GOTRUE_DISABLE_SIGNUP` in the running `auth` container's env → matches `supabase/.env` → Configuration required

## Acceptance-criteria coverage
- AC-1 (unauthenticated redirect + returnUrl) … covered by the `/jobs` redirect step and its curl equivalent
- AC-2 (correct/wrong password) … covered by the sign in and wrong-password steps
- AC-3 (30 day sliding session, no GoTrue refresh) … covered by the successful sign in cookie check; long-duration survival is structural (cookie `ExpireTimeSpan`/`SlidingExpiration`), not independently re-verified here
- AC-4 (invalid/expired cookie → redirect) … covered by the key-rotation step
- AC-5 (password reset end to end) … covered by the forgot-password and real recovery link steps
- AC-6 (sign out) … covered by the TopBar sign out step
- AC-7 (no token reaches the browser / isn't persisted) … covered by the shell-gating step and the cookie contents inspection
- AC-8 (no public signup) … covered by the GoTrue `/signup` rejection step
