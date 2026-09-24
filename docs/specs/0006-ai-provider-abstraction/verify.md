# Verify: AI provider abstraction · spec 0006 · updated 2026-09-24
_Steps derived from spec 0006 acceptance criteria. `/check verify` runs these; `/test` locks the durable ones._

Setup (once): keys go in user secrets, never a tracked file. Run each yourself in `src/WorkPilot.Api`:
`dotnet user-secrets set "Ai:Providers:openai:ApiKey" <key>` (same for `gemini`, `deepseek`). Ollama: `ollama pull qwen2.5:7b` (or any small model) and have `ollama serve` running.
Start the Api against a test database, e.g. `WORKPILOTDB_CONNECTION` pointed at `wp_f07_ai`. Switch providers only with env vars (`Ai__Purposes__Default__Provider`, `Ai__Purposes__Default__Model`) and a restart.

## Commands
- [ ] Default config (no AI env vars): startup logs a warning that `Default` and `Planner` use the Fake provider; `GET /health/ai` → `200`, `provider: "Fake"` → AC-3, AC-8
- [ ] `Default` → `openai` / `gpt-4o-mini`: `GET /health/ai` → `200`, `provider: "openai"`, `model: "gpt-4o-mini"`, a real `latencyMs` → AC-2, AC-8
- [ ] Same for `gemini` / `gemini-2.5-flash`, `deepseek` / `deepseek-chat`, `ollama` / `qwen2.5:7b` (a code change between them: none) → AC-1, AC-2
- [ ] For each of the four: `POST /internal/agent/runs` with goal "list my profile" → the run reaches `Completed`; the Aspire dashboard (or OTel exporter) shows a `WorkPilot.AI` span with that provider's model and input/output token counts → AC-1, AC-7
- [ ] If any provider rejects JSON mode (`response_format`), record which one (spec Follow-up: `SupportsJsonMode` flag) → AC-2
- [ ] `Planner` → `deepseek`, `Default` → `openai`: a run plans through DeepSeek (its span shows `deepseek-chat`) while `/health/ai` reports `openai` → AC-1
- [ ] Remove the `deepseek` key while `Default` uses it → the Api refuses to start, and the error names `Ai:Providers:deepseek:ApiKey` without printing any key → AC-4
- [ ] Set `Ai__Purposes__Planer__Provider=openai` (typo) → startup fails naming `Ai:Purposes:Planer` → AC-4
- [ ] Set the `openai` key to a wrong value → `/health/ai` → `503` with a 401 error and no key text; trigger a run → `Failed`, one `PlanningFailed` audit row with `reason: provider_error`, provider `openai`, no key or goal text → AC-5, AC-6
- [ ] Point `ollama` at a stopped server (or a bad port) with `TimeoutSeconds: 1` → the run fails within about 3 × 1s plus backoff, audited as `provider_error` → AC-5, AC-6
- [ ] `/health` and `/alive` → `200` with no call reaching any provider (check provider dashboards or the Ollama log) → AC-8
- [ ] `Ai__LogSensitiveData=true` with the log level at Trace for `Microsoft.Extensions.AI` → prompt text appears in the logs; `false` → it doesn't → AC-7

## Value sourcing
- [ ] Planner provider and model come from `Ai:Purposes:Planner`, else `Default` (vary each, confirm the span) → AC-1
- [ ] `PlanningFailed.provider` is the config name (`deepseek`), not the SDK's `openai` → AC-6
- [ ] `/health/ai` `purpose` is always `Default` → AC-8

## Acceptance criteria coverage
- AC-1: steps 3, 4, 6 · AC-2: steps 2, 3, 5 · AC-3: step 1 · AC-4: steps 7, 8 · AC-5: steps 9, 10 · AC-6: steps 9, 10 · AC-7: steps 4, 12 · AC-8: steps 1, 2, 11 · AC-9: covered by `ChatClientPlannerTests` (fenced replies); watch for fenced output from Gemini or Ollama in step 4
