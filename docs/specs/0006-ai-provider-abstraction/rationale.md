# Rationale: AI provider abstraction

## Context

Spec 0005 built the Planner against `IChatClient` and shipped only a deterministic `FakeChatClient`, hardcoded in the Api's DI setup. Nothing can plan against a real model yet. The scope's done when is narrow: the same planning call must switch between two configured providers (an OpenAI compatible one and a DeepSeek compatible one) by configuration only.

Forces that shape the choice:
- `AGENTS.md` already names `Microsoft.Extensions.AI` as the provider abstraction and forbids framework code in `Domain`/`Application`, so vendor SDK types must stay in the AI adapter project.
- `AGENTS.md` requires configuration to be validated at startup (fail fast). A missing key found only when a Hangfire job runs would leave a run stranded in the background.
- Keys are paid credentials and must never reach git, logs, or a prompt.
- Development and the test suites (including other features built in parallel) must keep running with no key and no network.
- Single user, one VPS: no need for per tenant keys, quotas, or a gateway service.

Once a real network call exists, a new failure mode appears that the fake never had: the provider can time out or reject the call, and spec 0005's `PlanRunJob` only handles a parse failure.

## Options considered

### Option 1: `IChatClient` only, one config selected OpenAI compatible provider (chosen)

Keep `IChatClient` as the only abstraction. Build the concrete client from configuration with the official `Microsoft.Extensions.AI.OpenAI` adapter, pointing its endpoint at whichever OpenAI compatible provider is active. `Fake` stays as a second kind.

**Pros**:
- Zero new interfaces; the Planner and any later feature just inject `IChatClient`.
- One adapter covers OpenAI, DeepSeek, OpenRouter, and local OpenAI compatible servers.
- Standard options binding and `ValidateOnStart` give fail fast for free.

**Cons**:
- Vendors that are not OpenAI compatible need a new kind and package.
- One model per process until keyed clients are added.

### Option 2: A WorkPilot `IAiProvider` interface with a class per vendor

Define `IAiProvider` in `Application`, implement `OpenAiProvider` and `DeepSeekProvider` in the AI project, and have the Planner depend on it.

**Pros**:
- Matches the scope's wording literally; room for WorkPilot specific methods later.

**Cons**:
- Duplicates `IChatClient`, which already is a vendor neutral contract, and forces a rewrite of the Planner and its tests.
- Two classes doing the same HTTP call with different base URLs.

### Option 3: Keyed clients for all providers plus a routing or failover client

Register every declared provider as a keyed `IChatClient` and put MEAI's `RoutingChatClient` or `FailoverChatClient` in front.

**Pros**:
- Automatic fallback when one provider is down; per task model choice later.

**Cons**:
- Those types are experimental in MEAI 10.9+; failover stacked on the SDK's own retries needs careful bounding.
- Every declared provider would need a valid key at startup, or the validation gets complicated.
- Solves problems the product does not have yet.

### Option 4: An external AI gateway (LiteLLM or similar) as its own service

Run a proxy container that speaks OpenAI's API to the app and fans out to vendors.

**Pros**:
- Swapping and fallback live outside the app; supports many vendors.

**Cons**:
- A new container to run, secure, and upgrade on a one VPS deployment, for a single user app.
- Still needs Option 1 inside the app to talk to it.

## Rationale

The scope asks for a config only swap between two providers that both speak the same OpenAI style API. Option 1 is the smallest thing that meets it while honoring the project rules: the adapter types stay in `WorkPilot.AI`, `Domain`/`Application` see only `IChatClient`, and `ValidateOnStart` satisfies the fail fast rule. Option 2 adds an interface that already exists. Option 3's routing and failover are experimental and answer a need (automatic fallback) nobody has asked for. Option 4 adds an operated service to a one box deployment, and Option 1 remains a prerequisite for it anyway, so choosing Option 1 now keeps Option 4 open later (a gateway is just another `OpenAICompatible` entry).

Keeping `Fake` as a provider kind rather than a special case means tests and development run through the same composition code as production, which is what makes AC-5 honest. Validating only the active provider keeps the common case (both providers declared, one key present) valid.

## References

**Project sources**:
- `AGENTS.md` (Microsoft.Extensions.AI as the provider layer; fail fast config; no framework code in inner layers)
- spec 0005 (`ChatClientPlanner`, `FakeChatClient`, `PlanRunJob`)
- `.claude/skills/microsoft-extensions-ai/` (compose `IChatClient` explicitly in DI; routing and failover are experimental)

**Practices & standards**:
- Options pattern with startup validation (`ValidateOnStart`)
- Secrets out of source control (user secrets in development, environment variables in production)
