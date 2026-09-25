# 0006. AI provider abstraction: rationale

The decision record behind [index.md](index.md). `/develop` builds from `index.md`; this file explains why.

## Context

> ⚠️ Premise note: the scope row asks for an "`IAiProvider`-style interface". Writing a custom interface would duplicate `IChatClient`, which spec 0001 already adopted as exactly that seam, and it would cut the project off from the `Microsoft.Extensions.AI` middleware (telemetry, logging, caching) built around it. The right framing is: keep `IChatClient` as the interface, and decide how concrete providers are built, configured, selected per purpose, and made to fail safely behind it.

Spec 0005 shipped the agent orchestrator with a `FakeChatClient` standing in for the model, so no run has ever planned against a real LLM. Everything the Job, University, and Personal agents will do (plan runs, write cover letters, score matches) depends on a real model call, and the founder wants to compare several vendors on cost and quality: OpenAI, Gemini, DeepSeek, and a local Ollama model, with all four keys or installs available now.

The forces: one user, self hosted on a VPS, cost sensitive, and a strict Clean Architecture rule that no vendor or SDK type may leak into `Domain` or `Application`. Prompts will soon carry the founder's resume and personal profile, so what gets logged matters. Runs are durable Hangfire jobs, and a failure that throws out of `PlanRunJob` today leaves the run at `Planning` forever, because that job deliberately has no Hangfire retry.

Not deciding keeps the whole product on a stub. Deciding badly means either vendor code spread through the use cases, or a fragile setup where a missing key only shows up as a stuck run.

An earlier draft of this spec (commit `21c8cc4`, written in a parallel session) chose a single global `AI:ActiveProvider` with OpenAI and DeepSeek only. The founder chose this per purpose design instead in the design conversation; that draft is superseded in place by this version.

## Options considered

### Option 1: One OpenAI compatible adapter, keyed per purpose (chosen)

Use `Microsoft.Extensions.AI.OpenAI` for every real provider, pointing the OpenAI SDK's `Endpoint` at each vendor's OpenAI compatible base URL. Named providers and a purpose map live in configuration, each known purpose becomes a keyed `IChatClient` built at startup, and cross cutting behavior is composed as middleware.

**Pros**:
- One code path covers all four requested providers, plus any future OpenAI compatible service, with no new code.
- Stays entirely on the abstraction spec 0001 chose, with the official Microsoft adapter.
- Per purpose keyed clients let later features use different models without new plumbing.

**Cons**:
- Limited to what each vendor's compatibility layer supports; vendor only features are out of reach.
- Compatibility layers differ in small ways (JSON mode, error shapes), which only a live test across providers catches.

### Option 2: A native SDK adapter per vendor

OpenAI through `Microsoft.Extensions.AI.OpenAI`, Gemini through Google's own .NET SDK, Ollama through OllamaSharp, DeepSeek through the OpenAI adapter, each exposed as an `IChatClient`.

**Pros**:
- Full access to each vendor's native features and exact error semantics.
- A stronger proof that the abstraction really isolates vendors.

**Cons**:
- Three packages and three code paths to keep current, for the same chat call.
- More surface to test, and each new vendor is code rather than config.

### Option 3: A separate AI gateway container (for example a LiteLLM style proxy)

Run a proxy on the VPS that speaks the OpenAI format to the app and routes to any vendor, with its own retries, keys, and cost tracking.

**Pros**:
- Rich routing, failover, budgets, and cost dashboards out of the box.
- The app only ever knows one endpoint.

**Cons**:
- A new always on service to deploy, patch, secure, and back up on a single VPS, for one user.
- Keys and policy move into another system's config, splitting where behavior is defined.

### Option 4: One global provider, no purposes

Same adapter as Option 1, but a single provider and model for every AI call (the superseded draft's shape).

**Pros**:
- The smallest possible config and code.

**Cons**:
- Cover letters and matching would share the Planner's model until a rework, which is exactly the change this feature exists to avoid.

## Rationale

Option 1 is the only one that meets the founder's four providers with one code path. OpenAI, Gemini, DeepSeek, and Ollama all publish OpenAI compatible chat endpoints, so a configurable base URL turns "support a vendor" into a config entry. That fits a single user VPS far better than running a gateway (Option 3), whose value (budgets, failover, team dashboards) this product doesn't need yet, and it avoids three native SDKs (Option 2) whose extra features nothing in the scope asks for. Per purpose mapping (versus Option 4) costs one dictionary in config and saves a rework as soon as cover letters arrive.

Failure handling follows from the durable job design. `PlanRunJob` runs once, so a provider exception must become a clean `Failed` state with an audited reason rather than a thrown job. Retries sit in the OpenAI SDK's own pipeline (2 extra attempts, exponential backoff, honoring `Retry-After`) rather than in an `IHttpClientFactory` resilience handler. The Aspire service defaults apply a standard resilience handler to every factory client, and its 10 second per attempt timeout would cut off slow model calls; stacking both would also double the retries. The runner up was a dedicated named `HttpClient` with a tuned resilience handler, rejected because it adds configuration to fight a default without adding behavior. Automatic failover was declined on purpose: a silent switch to another model changes output quality without telling you. The error translating wrapper catches only provider and network failures, so a programming bug is never relabeled as a provider outage.

Startup validation makes a wrong key or a typo in a purpose name fail before any run starts, matching the project rule to validate configuration at startup. `Default → Fake` is committed so tests and a fresh clone never depend on the host environment name (the cross check found that `WebApplicationFactory` hosts do not run as Development, so an environment based default would break the whole test suite). Telemetry through `UseOpenTelemetry` gives token counts and latency in the Aspire dashboard with no schema. Prompt capture is off by default because prompts will carry personal data.
