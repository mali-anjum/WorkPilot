# 0006. AI provider abstraction

**Date**: 2026-09-24
**Status**: In Progress

## Summary

This decision picks how WorkPilot talks to a real AI model without tying any code to one vendor. The Planner (spec 0005) already depends only on `IChatClient`, the vendor neutral chat interface from `Microsoft.Extensions.AI`; this spec decides which concrete client sits behind it, and makes that choice pure configuration. You name a few providers in config (for example OpenAI and DeepSeek, both of which speak the same OpenAI style HTTP API), pick one with `AI:ActiveProvider`, and restart. Bad or missing provider settings stop the Api at startup with a clear message instead of failing later inside a background job.

## Requirements

**User stories**:
- As the founder, I want to switch the model that plans agent runs between providers (for example OpenAI and DeepSeek) by changing configuration only, so that I can trade cost against quality without a code change or rebuild.
- As the developer, I want a wrong or missing provider setting to stop the Api at startup with a message naming the bad key, so that I never discover it as a run stuck in the background.
- As the founder, I want API keys kept out of the repo and out of logs, so that pushing the code never leaks a paid credential.

**Acceptance criteria** (the contract, each criterion is independently checkable):
- **AC-1**: With `AI:ActiveProvider` naming a configured `OpenAICompatible` provider, the Planner's `IChatClient` call (spec 0005's `ChatClientPlanner`, unchanged) is sent over HTTP to that provider's `Endpoint`, asks for that provider's `Model`, and authenticates with that provider's `ApiKey` as a bearer token; a run triggered through `POST /internal/agent/runs` completes through spec 0005's pipeline on that provider's answer.
- **AC-2**: Changing only `AI:ActiveProvider` (an appsettings value, user secret, or `AI__ActiveProvider` env var) between two configured providers, for example `OpenAI` and `DeepSeek`, and restarting the Api makes the same planning call go to the other provider's endpoint, model, and key. No code change, no rebuild.
- **AC-3**: Invalid provider configuration fails Api startup (the host does not start serving) with an error naming the offending configuration key, for each of: `AI:ActiveProvider` missing or blank; `AI:ActiveProvider` naming no entry under `AI:Providers`; the active provider's `Kind` missing or unknown; for an `OpenAICompatible` active provider, `Endpoint` missing or not an absolute `http`/`https` URL, `Model` missing, or `ApiKey` missing; `TimeoutSeconds` outside 1 to 600. Providers that are declared but not active are not required to have a key.
- **AC-4**: No API key is committed: no tracked config file carries an `ApiKey` value; keys come only from .NET user secrets (the Api project has a `UserSecretsId`) or environment variables (`AI__Providers__<Name>__ApiKey`). The key never appears in logs: the startup log line names the active provider, kind, endpoint, and model only. The key never reaches the Planner's prompt.
- **AC-5**: In the Development environment with no extra configuration, the deterministic `Fake` provider (spec 0005's `FakeChatClient`) is active, so the app and every existing test run with no key and no network; a startup warning says the Fake provider is active. Outside Development nothing defaults to Fake.
- **AC-6**: Provider calls are bounded and a provider failure never strands a run: each call uses the active provider's `TimeoutSeconds` (default 60), and when the provider still fails after the SDK's own transient retries (timeout, network error, 401, 429, 5xx), the run moves to `Failed` (not stuck at `Planning`) and the error is logged without the key.

## Decision

**Chosen option**: Option 1: `IChatClient` as the only abstraction, with one config selected provider built by the official OpenAI adapter (`Microsoft.Extensions.AI.OpenAI`) against any OpenAI compatible endpoint.

No new WorkPilot interface. A named provider list under `AI:Providers`, one active at a time, validated at startup; `Fake` stays available as a second provider kind for keyless development and tests.

**Implementation skills**: `microsoft-extensions-ai` (`.claude/skills/microsoft-extensions-ai/`) · `aspire` (`.claude/skills/aspire/`)

## Rationale

Reasoning and options: see [rationale.md](rationale.md).

## Feature design

**Data model sketch**: none. No table, no migration. Provider settings live in configuration, not the database (a runtime Settings screen for them is scope item 30's call, see Follow-up).

**Configuration shape** (the `AI` section, bound to an options class in `WorkPilot.AI`):

| Key | Required | Meaning |
|---|---|---|
| `AI:ActiveProvider` | yes | name of one entry under `AI:Providers` |
| `AI:Providers:<Name>:Kind` | yes (active) | `OpenAICompatible` or `Fake` |
| `AI:Providers:<Name>:Endpoint` | yes for `OpenAICompatible` | absolute base URL, e.g. `https://api.openai.com/v1`, `https://api.deepseek.com/v1` |
| `AI:Providers:<Name>:Model` | yes for `OpenAICompatible` | model id sent on every call, e.g. `gpt-4.1-mini`, `deepseek-chat` |
| `AI:Providers:<Name>:ApiKey` | yes for `OpenAICompatible` | secret; user secrets or env var only, never a tracked file |
| `AI:Providers:<Name>:TimeoutSeconds` | no (default 60, range 1 to 600) | per HTTP call network timeout |

Tracked defaults: `appsettings.json` declares `OpenAI` and `DeepSeek` (kind, endpoint, model; no key, no active provider). `appsettings.Development.json` declares `Fake` and sets `ActiveProvider` to `Fake`.

**Composition** (in DI, one place): `AddWorkPilotChatClient(configuration)` binds the options, registers the validator with `ValidateOnStart`, and registers `IChatClient` as a singleton through `AddChatClient(factory).UseLogging()`. The factory reads the active provider: `OpenAICompatible` builds `new OpenAIClient(key, { Endpoint, NetworkTimeout }).GetChatClient(Model).AsIChatClient()`; `Fake` builds `FakeChatClient`. A small hosted service resolves the client at startup and logs the provider line (AC-4, AC-5), so construction errors also surface at startup.

**API surface**: no new endpoint. The existing spec 0005 endpoints are the entry point (`POST /internal/agent/runs`, `GET /internal/agent/runs/{id}`).

**Value sourcing**:

| Action | Value produced / displayed | Source |
|---|---|---|
| Planner call | which client answers | `AI:ActiveProvider` → the named `AI:Providers:<Name>` entry's `Kind` |
| Planner call | HTTP base URL | active provider's `Endpoint` |
| Planner call | model id in the request body | active provider's `Model` (the Planner never sets `ChatOptions.ModelId`) |
| Planner call | `Authorization: Bearer` value | active provider's `ApiKey` (user secrets or env var) |
| Planner call | network timeout | active provider's `TimeoutSeconds`, else 60 |
| Planner call | transient retry count and backoff | the OpenAI SDK's built in pipeline defaults (3 retries, exponential backoff on 408/429/5xx); not configurable here |
| Planner call | JSON mode flag | spec 0005's `ChatClientPlanner` (`ChatResponseFormat.Json`), mapped by the adapter to `response_format: json_object` |
| Startup | the provider log line | active provider name, `Kind`, `Endpoint`, `Model`; never `ApiKey` |
| Startup failure | error message | the validator, naming the key path (e.g. `AI:Providers:DeepSeek:ApiKey`) |
| Provider failure | run status `Failed` | `PlanRunJob`, on `PlannerUnavailableException` thrown by `ChatClientPlanner` when the client call throws |

**Key invariants**:
- Exactly one provider is active per process; changing it takes a restart, never a code change.
- Domain, Application, Workers, and the Planner depend only on `IChatClient`; no type from the OpenAI SDK appears outside `WorkPilot.AI/Providers`.
- An API key is read only from configuration at client construction; it is never logged, persisted, or put into a prompt.
- Nothing outside Development defaults to the Fake provider.

**Security model**: single user, server side only. The key lives in the Api process's configuration (user secrets in development, env vars on the VPS). The LLM still receives only the goal plus tool descriptors (spec 0005, AC-6). Provider traffic goes over HTTPS for the real endpoints; a plain `http` endpoint is allowed only because local OpenAI compatible servers (tests, Ollama style gateways) use it.

**Configuration required**:
- `AI__ActiveProvider`: which provider plans runs (production must set it; Development defaults to `Fake`).
- `AI__Providers__OpenAI__ApiKey`, `AI__Providers__DeepSeek__ApiKey`: the keys, set only for the provider(s) you use, via `dotnet user-secrets` in `src/WorkPilot.Api` or env vars.
- Optional overrides: `AI__Providers__<Name>__Endpoint`, `__Model`, `__TimeoutSeconds`, or a whole new named provider.

**Critical test scenarios**:
- Happy path: two providers configured against two local OpenAI compatible HTTP servers; with each one active in turn, the resolved `IChatClient` drives `ChatClientPlanner` to a valid plan and only that provider's server sees the request, with its model and bearer key, verifies **AC-1**, **AC-2**.
- Failure case: each invalid configuration listed in AC-3 stops host startup with an `OptionsValidationException` naming the key; a declared but inactive provider with no key does not, verifies **AC-3**.
- Failure case: a provider that returns 500 (or times out) makes `ChatClientPlanner` throw `PlannerUnavailableException`, and `PlanRunJob` moves the run to `Failed`, verifies **AC-6**.
- Default: the Api under `WebApplicationFactory` (Development) resolves the Fake provider, verifies **AC-5**.
- Secrets: tracked `appsettings*.json` contain no `ApiKey` value; the startup log line contains no key, verifies **AC-4**.

## Build plan

1. Tracer thread: add `Microsoft.Extensions.AI.OpenAI` to `WorkPilot.AI`; add the options classes and `AddWorkPilotChatClient` (both `OpenAICompatible` and `Fake` kinds) under `WorkPilot.AI/Providers`; replace the hardcoded `FakeChatClient` registration in `Program.cs` with one call; add the tracked provider defaults (`appsettings.json`) and the Development `Fake` default, satisfies **AC-1**, **AC-2**, **AC-5**
2. Startup validation: an `IValidateOptions` validator with `ValidateOnStart` covering every AC-3 case, plus the startup hosted service that resolves the client and logs the provider line (Fake gets a warning), satisfies **AC-3**, **AC-4**, **AC-5**
3. Bounded calls and failure path: per provider `TimeoutSeconds`; `ChatClientPlanner` wraps a failed client call in `PlannerUnavailableException`; `PlanRunJob` fails the run and logs it, satisfies **AC-6**
4. Secrets: add a `UserSecretsId` to the Api project; document the key setup in the scope row and `verify.md`, satisfies **AC-4**
5. Prove the swap live: run the Api against two local OpenAI compatible servers, trigger a run with each provider active, and confirm which server answered, satisfies **AC-1**, **AC-2**

`/develop` (2026-09-24): all 5 tasks built. `WorkPilot.AI/Providers/` holds `AiOptions` (+ `AiProviderOptions`, `AiProviderKinds`), `AiOptionsValidator`, `AiChatClientFactory` (the only OpenAI SDK touch point), `AiProviderStartupLogger`, and `AddWorkPilotChatClient`; `Program.cs` swaps its hardcoded `FakeChatClient` line for one `AddWorkPilotChatClient(builder.Configuration)` call. `ChatClientPlanner` wraps a failed client call in the new `PlannerUnavailableException` (`Application/Modules/Agent/IPlanner.cs`), which `PlanRunJob` now treats like a parse failure. The Api project gained a `UserSecretsId`. Exercised live on port 5207 against `wp_f07_ai` and local OpenAI compatible servers: the same "list my profile" run completed on `OpenAI` then `DeepSeek` with only `AI__ActiveProvider` changed, each server saw its own model and bearer key; a 500 provider and a 3s timeout provider both left the run `Failed`, not `Planning`; five bad configs each stopped startup with the key named. No migration. Note for AC-6: `TimeoutSeconds` bounds each HTTP try, and the SDK makes up to 4 tries, so the worst case wait is about 4 times the timeout plus backoff.

## Consequences

**Positive**:
- Any OpenAI compatible provider (OpenAI, DeepSeek, OpenRouter, a local Ollama or vLLM server) is a config entry, not code.
- The Planner, and every later feature that injects `IChatClient`, stays vendor neutral and unit testable with a scripted client.
- Misconfiguration surfaces at startup, per `AGENTS.md`.

**Negative / tradeoffs**:
- Only one model per process: a later feature that wants a cheap model for classification and a strong one for writing needs keyed clients (a follow up, not built).
- A vendor that is not OpenAI compatible (Anthropic's native API, Gemini's native API) needs a new `Kind` and its own adapter package.
- Switching needs a restart; there is no hot reload of the active provider.
- Provider quirks hide behind the same adapter: for example DeepSeek's JSON mode requires the word "json" in the prompt (the Planner's prompt already has it), and another provider may ignore `response_format`.

**Neutral**:
- No migration and no UI in this feature.
- `FakeChatClient` stays, now as the `Fake` provider kind rather than a hardcoded registration.

## Decisions made without the engineer (please review)

- Abstraction: use `IChatClient` itself, no custom `IAiProvider` interface. Runner up: a WorkPilot `IAiProvider` wrapper. Why: `IChatClient` already is the vendor neutral contract the Planner depends on; a wrapper would duplicate it.
- Adapter: one `OpenAICompatible` kind built with `Microsoft.Extensions.AI.OpenAI` (official OpenAI .NET SDK) and a configurable endpoint, serving both OpenAI and DeepSeek. Runner up: a separate kind and package per vendor. Why: DeepSeek exposes an OpenAI compatible Chat Completions API, so one maintained adapter covers both.
- Selection: one active provider by `AI:ActiveProvider` over a named `AI:Providers` list. Runner up: keyed clients for every provider plus per call routing or the experimental failover client. Why: the done when needs only a config swap; routing is not needed yet and those MEAI types are experimental.
- Validation scope: validate only the active provider. Runner up: validate every declared provider. Why: lets you keep both declared while holding only one key.
- Development default: `Fake` active in `appsettings.Development.json` only; production has no default and fails fast. Runner up: `Fake` default everywhere with a warning. Why: keeps dev and tests keyless without letting production silently plan with a fake.
- Secrets: user secrets (new `UserSecretsId` on the Api) or env vars. Runner up: an Aspire secret parameter in the AppHost. Why: works with and without the AppHost and avoids editing the AppHost other branches touch.
- Provider failure: fail the run (new `PlannerUnavailableException`), no Hangfire retry. Runner up: let Hangfire retry the job. Why: the SDK already retries transient errors; a run stuck at `Planning` is worse than a clear `Failed`.
- Timeout: per provider `TimeoutSeconds`, default 60. Runner up: the SDK default (100s). Why: a planning call longer than a minute is almost certainly stuck.
- Default models: `gpt-4.1-mini` (OpenAI) and `deepseek-chat` (DeepSeek). Runner up: `gpt-4o-mini`. Why: cheap, JSON mode capable defaults; both are just config.
- Live verification: two local fake OpenAI compatible servers, since no real key exists on this machine; the real key test is left for Ali.

## Follow-up

- [ ] Ali: run the live key check in `verify.md` (set a real `OpenAI` and/or `DeepSeek` key via user secrets, switch `AI:ActiveProvider`, trigger "list my profile").
- [ ] Keyed clients per task (cheap vs strong model) when a second AI using feature needs a different model than the Planner.
- [ ] A non OpenAI compatible `Kind` (e.g. Anthropic native) if ever wanted.
- [ ] OpenTelemetry for AI calls (`UseOpenTelemetry()` plus adding its source in `ServiceDefaults`), with sensitive data off, once observability is wired for real.
- [ ] Settings (scope item 30) may want to show the active provider and model read only; switching stays config only unless a spec says otherwise.
- [ ] `AGENTS.md` could gain one line: AI provider is config only (`AI:ActiveProvider`, `AI:Providers:<Name>`), keys via user secrets or env vars.
