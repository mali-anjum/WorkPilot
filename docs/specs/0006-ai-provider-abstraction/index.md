# 0006. AI provider abstraction

**Date**: 2026-09-24
**Status**: Accepted

## Summary

This spec replaces the fake AI client with real providers, chosen entirely by configuration. One adapter that speaks the OpenAI chat format covers OpenAI, Gemini, DeepSeek, and Ollama (each of them offers an OpenAI compatible endpoint), so switching the model behind the agent's Planner is a config change and a restart, never a code change. Each "purpose" (Planner today; cover letters and matching later) maps to a provider and model, with a Default fallback. Bad config stops the app at startup, provider outages fail the run loudly with an audit record, and the fake client stays available on purpose for tests and keyless development.

## Requirements

**User stories**:
- As the founder, I want to pick which AI provider and model the agent uses by editing configuration, so I can compare cost and quality across OpenAI, Gemini, DeepSeek, and a local Ollama model without touching code.
- As the founder, I want different jobs (planning now, writing and matching later) to use different models, so cheap work runs on a cheap model and important writing runs on a strong one.
- As the founder, I want a broken key or a provider outage to fail clearly and leave a record, so I never wonder why a run silently hung or which model produced a result.

**Acceptance criteria** (the contract):
- **AC-1**: The Planner's model call goes to the provider and model mapped to the `Planner` purpose, or to `Default` when `Planner` is not mapped. Changing only configuration (appsettings, user secrets, or environment variables) and restarting the Api switches it between any two configured providers, with no code change.
- **AC-2**: One OpenAI compatible adapter serves OpenAI, Gemini, DeepSeek, and Ollama. Each provider is defined only by configuration (`Endpoint`, `ApiKey`, `RequiresApiKey`, `TimeoutSeconds`); adding another OpenAI compatible service is a config entry, not code.
- **AC-3**: The reserved provider name `Fake` selects the deterministic `FakeChatClient`. The committed `appsettings.json` maps `Default` to `Fake`, so a fresh clone and the whole test suite run with no key and no network, in any environment. At startup, every purpose that resolves to `Fake` is logged as a warning, so a deployment left on `Fake` is obvious.
- **AC-4**: Startup fails fast, before the Api serves a request, with one error listing every problem when AI config is invalid: no `Default` purpose; a purpose name that is not a known purpose; a purpose naming an unknown provider; a real provider purpose with no `Model`; a used provider with a missing or non absolute `http`/`https` `Endpoint`; a used provider with `RequiresApiKey` true (the default) and no `ApiKey`; `TimeoutSeconds` outside 1 to 600. The error message never contains a key value. Providers that no purpose uses are not checked.
- **AC-5**: Transient provider errors (HTTP 408, 429, 5xx, network failures, and per attempt timeouts) are retried up to 2 more times with exponential backoff that honors `Retry-After`. Each attempt is cut off after the provider's `TimeoutSeconds` (default 60), so the worst case before a call gives up is about 3 × `TimeoutSeconds` plus backoff (roughly 3 minutes at the default). A call never falls over to a different provider.
- **AC-6**: When the Planner's AI call still fails after retries, the run moves to `Failed` (never stuck at `Planning`) and one `AuditLog` row with action `PlanningFailed` records `{"reason": "provider_error", "purpose", "provider", "model", "error"}`, with no API key and no prompt or response text. An unparseable plan after the existing one retry (spec 0005, AC-7) also writes `PlanningFailed`, with `"reason": "unparseable_plan"`.
- **AC-7**: Every AI call emits OpenTelemetry traces and metrics carrying provider, model, input and output token counts, and duration, visible in the Aspire dashboard. Prompt and response text is recorded only when `Ai:LogSensitiveData` is `true`; the default is `false`.
- **AC-8**: `GET /health/ai` sends one tiny prompt through the `Default` purpose and returns `200` with `{status: "ok", purpose, provider, model, latencyMs}`, or `503` with `{status: "error", purpose, provider, model, error}`. `/health` and `/alive` never call a provider.
- **AC-9**: The Planner accepts a JSON reply wrapped in a single markdown code fence (```` ```json … ``` ````), which real models often return even in JSON mode, before counting the reply as unparseable.

## Decision

**Chosen option**: Option 1: One OpenAI compatible adapter, keyed per purpose

Every real provider goes through `Microsoft.Extensions.AI.OpenAI` pointed at that provider's OpenAI compatible base URL. Each known purpose gets its own keyed `IChatClient` built at startup from validated configuration, with retries, telemetry, logging, and error translation composed as a `Microsoft.Extensions.AI` middleware pipeline around it.

**Implementation skills**: `microsoft-extensions-ai` (`managedcode/dotnet-skills`, `.claude/skills/microsoft-extensions-ai/`) · `aspire` (`managedcode/dotnet-skills`, `.claude/skills/aspire/`) · `csharp-xunit` (`github/awesome-copilot`, `.agents/skills/csharp-xunit/`)

## Rationale

Reasoning and options: see [rationale.md](rationale.md).

## Feature design

**Where the code lives** (Clean Architecture):
- `WorkPilot.Application/Modules/Agent/AiProviderException.cs`: a plain exception (`Purpose`, `Provider`, `Model`, safe `Message`), so `WorkPilot.Workers` can catch provider failures without referencing any AI SDK.
- `WorkPilot.AI/Providers/`: `AiOptions` (bound config), `AiOptionsValidator` (`IValidateOptions<AiOptions>`), `AiPurposes` (the known purpose names: `Default`, `Planner`), `ResolvedAiPurpose` (purpose, provider name, model after the Default fallback), `AiChatClientFactory` (the only file that touches OpenAI SDK types), `ProviderErrorChatClient` (a `DelegatingChatClient`, meaning a wrapper that passes calls through, which turns provider failures into `AiProviderException`), and `AddWorkPilotAi(IConfiguration)`, which registers one keyed `IChatClient` per known purpose.
- `WorkPilot.AI/Agent/ChatClientPlanner.cs` takes `[FromKeyedServices(AiPurposes.Planner)] IChatClient`.
- `WorkPilot.AI/Agent/FakeChatClient.cs` stays, now selected only through provider `Fake`.
- `WorkPilot.Api/Program.cs`: `AddWorkPilotAi(builder.Configuration)` replaces the `FakeChatClient` singleton; maps `/health/ai`.
- `WorkPilot.ServiceDefaults`: tracing and metrics subscribe to the `Microsoft.Extensions.AI` telemetry source and meter.

**Data model sketch**: no schema change. Configuration is the only new model:

| Config path | Type | Required | Notes |
|---|---|---|---|
| `Ai:LogSensitiveData` | bool | no, default `false` | turns prompt and response capture on in telemetry and logs |
| `Ai:Providers:{name}:Endpoint` | absolute URI | yes, when used | OpenAI compatible base URL |
| `Ai:Providers:{name}:ApiKey` | string | when `RequiresApiKey` and used | never committed; user secrets in dev, env var in prod |
| `Ai:Providers:{name}:RequiresApiKey` | bool | no, default `true` | `false` for Ollama |
| `Ai:Providers:{name}:TimeoutSeconds` | int 1 to 600 | no, default `60` | per attempt cutoff |
| `Ai:Purposes:{purpose}:Provider` | string | yes | a key of `Ai:Providers`, or `Fake` |
| `Ai:Purposes:{purpose}:Model` | string | yes unless `Fake` | e.g. `gpt-4o-mini`, `gemini-2.5-flash`, `deepseek-chat`, `qwen2.5:7b` |

Provider and purpose names match case insensitively (standard .NET config binding). `Fake` is reserved: it may not be declared under `Ai:Providers`. The committed `appsettings.json` ships the four provider presets with endpoints and no keys (`openai` → `https://api.openai.com/v1`, `gemini` → `https://generativelanguage.googleapis.com/v1beta/openai/`, `deepseek` → `https://api.deepseek.com/v1`, `ollama` → `http://localhost:11434/v1` with `RequiresApiKey: false` and `TimeoutSeconds: 180`), and `Purposes:Default` → `{ "Provider": "Fake" }`.

**Client pipeline per purpose** (built once, singleton, keyed by purpose name):
1. Resolve the purpose's mapping (its own entry, else `Default`) into a `ResolvedAiPurpose`.
2. `Fake` → `FakeChatClient`. Otherwise `new OpenAIClient(new ApiKeyCredential(apiKey ?? "unused"), new OpenAIClientOptions { Endpoint, NetworkTimeout = TimeoutSeconds, RetryPolicy = new ClientRetryPolicy(maxRetries: 2) }).GetChatClient(model).AsIChatClient()`. (`"unused"` because the SDK requires a credential; Ollama ignores it.)
3. Wrap with `ChatClientBuilder`: `ProviderErrorChatClient` (innermost, so it sees final failures after the SDK's own retries) → `UseOpenTelemetry(configure: c => c.EnableSensitiveData = LogSensitiveData)` → `UseLogging()`.

`ProviderErrorChatClient` catches exactly: `System.ClientModel.ClientResultException` (HTTP error status after retries), `HttpRequestException` (network), and `OperationCanceledException` **only when the caller's token was not cancelled** (the SDK's `NetworkTimeout`). It rethrows each as `AiProviderException` with the message truncated to 500 characters. A caller cancellation and any other exception type (a real bug) pass through untouched, so bugs are never mislabeled as provider outages.

**State transitions**: no new states. `AgentRun` already allows `Planning → Failed`; this feature makes that transition happen on provider failure (AC-6).

**API surface**:
| Endpoint | Method | Key inputs | Key outputs | Auth | Key errors |
|---|---|---|---|---|---|
| `/health/ai` | GET | none | `status`, `purpose`, `provider`, `model`, `latencyMs` | none; internal network only (the Api has no external endpoint, same as `/internal/*`) | `503` with `status: "error"` and `error` when the provider call fails or times out |

No other endpoint changes. `/internal/agent/runs` behaves as in spec 0005; only its planning outcome gains the `PlanningFailed` path. With `Default` on `Fake`, `/health/ai` returns `200` with `provider: "Fake"` (it proves wiring, not a real key).

**Value sourcing**:
| Action | Value produced / displayed | Source |
|---|---|---|
| Planner call | which provider and model answer | `Ai:Purposes:Planner`, else `Ai:Purposes:Default` (config) |
| Planner call | endpoint, key, timeout | `Ai:Providers:{provider}` (config; key from user secrets or env var) |
| Planner call | retry count and backoff | fixed: `ClientRetryPolicy(maxRetries: 2)`, the SDK's exponential backoff honoring `Retry-After` |
| `PlanningFailed` audit | `purpose`, `provider`, `model` | carried on `AiProviderException`, set by `ProviderErrorChatClient` from its `ResolvedAiPurpose`; for `unparseable_plan`, from the Planner client's `ResolvedAiPurpose` (registered keyed alongside the client) |
| `PlanningFailed` audit | `error` | the provider exception message, truncated to 500 characters; for `unparseable_plan`, the `PlanParseException` message |
| `PlanningFailed` audit | actor, target | actor `"Agent"`, target type `"AgentRun"`, target id the run's id (spec 0005's convention) |
| telemetry | provider, model, token counts, duration | emitted by `UseOpenTelemetry` from the response `Usage` and client metadata |
| `/health/ai` | `purpose`, `provider`, `model` | the `Default` purpose's `ResolvedAiPurpose` (provider is the config name, e.g. `deepseek`, not the SDK's `openai`) |
| `/health/ai` | `latencyMs` | a `Stopwatch` around the one call |
| `/health/ai` | the probe prompt | fixed text `Reply with the single word OK.`, `MaxOutputTokens = 5` |

**Key invariants**:
- No code outside `WorkPilot.AI` names a vendor, SDK type, endpoint, or model; consumers ask for a purpose.
- An API key never appears in any log line, telemetry attribute, audit payload, exception message, or HTTP response.
- A purpose resolves to exactly one provider and model for the lifetime of the process: no runtime failover, no hot reload.
- The committed `appsettings.json` never holds an API key.
- A purpose not in `AiPurposes` cannot appear in config (a typo guard, AC-4).

**Security model**: single user, internal only. The Api has no external endpoint (only `web` is exposed in `AppHost.cs`), so `/health/ai` is reachable only inside the Aspire or Compose network, like `/internal/*`. Keys live in `dotnet user-secrets` (Api project) in development and in the Compose `.env` as `Ai__Providers__<name>__ApiKey` in production, never in the repo or the database. Prompts will soon carry the founder's resume and profile (personal data), so prompt and response capture is off by default and switched on only on purpose. The LLM never receives keys or tokens (spec 0005 rule, unchanged).

**Configuration required**:
- `Ai__Providers__openai__ApiKey`: OpenAI key (only when a purpose uses `openai`)
- `Ai__Providers__gemini__ApiKey`: Google AI Studio key (only when used)
- `Ai__Providers__deepseek__ApiKey`: DeepSeek key (only when used)
- `Ai__Purposes__Default__Provider` / `Ai__Purposes__Default__Model`, and optionally `Ai__Purposes__Planner__*`: the active mapping
- `Ai__LogSensitiveData`: optional, `false` by default
- Development equivalent: `dotnet user-secrets set "Ai:Providers:openai:ApiKey" "<key>" --project src/WorkPilot.Api`
- Prerequisite for the live verify only: the four keys (the founder has them) and Ollama running locally with a small model pulled.

**Critical test scenarios** (tests start an in process stub server that speaks the OpenAI chat format, and configure providers against it through in memory configuration; `SharedApiFactory` also sets `Ai__Purposes__Default__Provider=Fake` explicitly, never relying on the host environment name):
- Happy path: `Planner` → stub provider `a`, model `m1`, key `sk-test-a`; the plan comes from the stub, and the stub saw path `/chat/completions`, model `m1`, and bearer `sk-test-a`. Switching config to provider `b`, model `m2` routes there, verifies **AC-1**, **AC-2**
- Fallback: with no `Planner` mapping, the Planner uses `Default`, verifies **AC-1**
- Keyless: the committed config runs a full agent run on `Fake`, verifies **AC-3**
- Validation: each invalid case in AC-4 yields a failure naming it; a planted key value never appears in the message, verifies **AC-4**
- Retry: a stub returning `429` with `Retry-After: 0` twice then success yields a plan; `500` three times fails; a stub slower than `TimeoutSeconds` fails, verifies **AC-5**
- Failure case: provider keeps failing → run `Failed`, one `PlanningFailed` row with `reason: provider_error`, provider, and model, and neither the key nor the goal text in the payload, verifies **AC-6**
- Telemetry privacy: with `LogSensitiveData` false, captured logs contain no prompt text; with true, they do, verifies **AC-7**
- Health: `/health/ai` returns `200` against a working stub and `503` against a failing one; `/health` makes no stub call, verifies **AC-8**
- Fenced JSON: a reply wrapped in a ```` ```json ```` fence parses into a plan, verifies **AC-9**
- Auth/permission: not applicable beyond the network boundary (single user, internal Api); covered by the key never leaving config, verifies **AC-4**, **AC-6**

## Build plan

Tracer Bullet: first a thin working thread from config to a real HTTP call, then harden it. (Commits `21c8cc4` and `ca32dfc` on this branch came from an earlier, superseded draft with a single `AI:ActiveProvider`; the build reworks that code toward this spec rather than starting over.)

1. **Thin thread.** Add `Microsoft.Extensions.AI.OpenAI` (same release line as `Microsoft.Extensions.AI` 10.10.0) to `WorkPilot.AI`. Add `AiOptions`, `AiPurposes`, `ResolvedAiPurpose`, and `AddWorkPilotAi` building keyed clients (Fake or OpenAI compatible). Point `ChatClientPlanner` at the `Planner` key. Commit the provider presets and `Default → Fake` in `appsettings.json`. Add the in process stub server test helper and prove config routing and switching, satisfies **AC-1**, **AC-2**, **AC-3**
2. **Fail fast config.** `AiOptionsValidator` with `ValidateOnStart()`, collecting every problem into one message with no key values; the startup warning for purposes on `Fake`, satisfies **AC-3**, **AC-4**
3. **Failures and resilience.** Per provider `NetworkTimeout` and `ClientRetryPolicy(maxRetries: 2)`. `AiProviderException` and `ProviderErrorChatClient`. `PlanRunJob` catches `AiProviderException` and `PlanParseException`, fails the run, and audits `PlanningFailed`. The Planner strips one surrounding code fence before parsing, satisfies **AC-5**, **AC-6**, **AC-9**
4. **Telemetry.** `UseOpenTelemetry` and `UseLogging` in the pipeline, with `EnableSensitiveData` from `Ai:LogSensitiveData`; subscribe the tracing source and meter in `ServiceDefaults`, satisfies **AC-7**
5. **Health probe.** Map `GET /health/ai` against the `Default` purpose, outside the `/health` health check registry, satisfies **AC-8**
6. **Developer setup.** Document the user secrets commands and the `Ai__…` env vars (Api `appsettings.json` comments and the Compose env example), satisfies **AC-2**, **AC-3**
7. **Live proof** (during `/check verify`): run the same goal through OpenAI, Gemini, DeepSeek, and Ollama by changing only config; confirm the Aspire dashboard shows tokens per call; break a key and confirm the startup or `PlanningFailed` behavior, satisfies **AC-1**, **AC-2**, **AC-5**, **AC-6**, **AC-7**, **AC-8**

## Consequences

**Positive**:
- Switching or comparing models across four vendors is a config edit, and any future OpenAI compatible service (OpenRouter, Groq, a self hosted vLLM) needs no code.
- Later features (cover letters, matching) get their own model by adding one purpose constant and one config line.
- A provider outage now ends a run cleanly with an audited reason, instead of leaving it stuck at `Planning`.
- Token use and latency per call are visible in the Aspire dashboard from day one, with no new table.

**Negative / tradeoffs**:
- Only features every vendor exposes through its OpenAI compatible endpoint are reachable. Vendor only features (Gemini's native grounding, Anthropic, provider specific caching controls) would need a native adapter later.
- Compatibility layers differ in small ways: JSON mode, `Retry-After`, and error bodies are not identical across Gemini, DeepSeek, and Ollama. The live verify is the real proof, and a provider may need a small per provider switch (e.g. turning off JSON mode) if one rejects it.
- A failing provider can hold a planning job for about 3 × `TimeoutSeconds` before the run fails (AC-5); acceptable for background jobs, not for anything interactive.
- Committing `Default → Fake` means a deployment with no AI env vars runs on the fake rather than refusing to start; the startup warning (AC-3) is the guard.
- No persisted cost history: telemetry lasts only as long as the dashboard or exporter keeps it.
- Switching a model means restarting the Api (no hot reload); acceptable for one user.
- No failover: when the chosen provider is down, planning fails until you switch config yourself.

**Neutral**:
- `FakeChatClient` moves from "the default registration" to "a provider you choose on purpose".
- A new middleware pattern (`DelegatingChatClient`, `ChatClientBuilder`) enters the codebase; future AI cross cutting concerns (caching, rate limiting) plug in the same way.

## Follow-up

- [ ] If a provider rejects `response_format: json_object` during the live verify, add a `SupportsJsonMode` provider flag rather than dropping JSON mode for everyone.
- [ ] Persist per call usage and cost (an `ai_calls` table) when the Dashboard (scope 13) or Settings (scope 30) needs a cost history.
- [ ] Settings (scope 30) may later edit purpose mappings at runtime; that would need a config source other than appsettings and a client rebuild on change.
- [ ] AC-9 (code fence tolerance) was added by the architect from known model behavior, not asked; confirm it in spec review.
- [ ] The API wide error handling pattern is still undecided (`AGENTS.md`); `/health/ai` uses plain result objects until that decision lands.
