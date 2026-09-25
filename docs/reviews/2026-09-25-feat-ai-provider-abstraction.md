# Review, feat/ai-provider-abstraction, 2026-09-25

**Reviewed by**: Claude Sonnet 5 (author on Claude Sonnet 5)
**Scope**: 32 files, branch vs `main` (merge base `fca372f`)
**Verdict**: Changes requested

## Summary

This change replaces `FakeChatClient` as the hardwired AI client with a config-driven, per-purpose `IChatClient` pipeline (`AiOptions` → `AiOptionsValidator` → `AiChatClientFactory` → `ProviderErrorChatClient`/`UseOpenTelemetry`/`UseLogging`), routes `PlanRunJob` provider/parse failures to an audited `Failed` state, adds fenced-JSON tolerance, and exposes `GET /health/ai`. The implementation is careful and unusually well tested: I traced the `ChatClientBuilder` wrapping order with a small standalone repro to confirm `ProviderErrorChatClient` really is innermost (as the spec requires) and it checks out; the same is true for key redaction, startup validation completeness, and the `PlanRunJob`/`AuditService` atomicity (confirmed both stage onto the same scoped `DbContext`, committed by one `SaveChangesAsync`). The one real gap is that the streaming half of `ProviderErrorChatClient`'s error translation — the same redaction-and-classification logic called out as a special-attention area — has no test coverage anywhere in the suite.

## Major

### 🟠 `ProviderErrorChatClient.GetStreamingResponseAsync` error translation is completely untested, `src/WorkPilot.AI/Providers/ProviderErrorChatClient.cs:37`
**Problem**: The streaming override duplicates `GetResponseAsync`'s provider-failure classification and key redaction (`IsProviderFailure`, `Translate`, `Redact`), but no test in `AiProviderTests` or elsewhere ever calls `GetStreamingResponseAsync` on a real or stub client. `grep` across `src/` and `tests/` confirms it: nothing in the app invokes streaming today (`ChatClientPlanner` and `AiHealthProbe` both use `GetResponseAsync` only), so the path is live production code with zero exercised coverage.
**Why it matters**: This is exactly the code the review brief flags for special attention (secret redaction, exception translation). A subtle bug here — e.g. the `await using`/`try` boundary not covering `GetAsyncEnumerator()` itself, or a raw `ClientResultException` slipping past untranslated on a future streaming caller — would surface first in production, potentially leaking an unredacted provider error (or a key) into a log or audit row the day streaming is wired to something.
**Suggested fix**: Add a stub-server test (reuse `StubOpenAiServer`, which already supports scripted replies) that drives `GetStreamingResponseAsync` through a 500-then-give-up or timeout scenario and asserts it throws a redacted `AiProviderException`, mirroring the existing `GetResponseAsync` cases. If streaming genuinely isn't needed yet, an alternative is to not override it at all until it has a caller, and let `DelegatingChatClient`'s default pass-through behavior stand — smaller surface, nothing untested in production.

## Minor

### 🟡 Compose env example doesn't document the `Ai__Providers__*__ApiKey` vars, `supabase/.env.example`
**Problem**: Build plan task 6 (spec 0006, index.md:133) promises documenting the `Ai__…` env vars in both `appsettings.json` comments and "the Compose env example." The `appsettings.json` comments are thorough, but `supabase/.env.example` has no mention of any `Ai__Providers__*` variable.
**Why it matters**: Minor completeness gap against the stated build plan; low real impact today since `supabase/docker-compose.yml` doesn't run the Api itself yet (only Postgres/GoTrue/Storage), so there's no compose-managed Api process to configure. Worth closing before a real Compose-based Api deployment exists, so the pattern isn't skipped by omission later.
**Suggested fix**: Either add a commented `Ai__Providers__<name>__ApiKey=` block to `supabase/.env.example` now (matching the pattern already used for other secrets there), or update the build-plan/scope line to note this is deferred until the Api joins the Compose stack.

### 🟡 `StripCodeFence` silently no-ops on a fence with no line break, `src/WorkPilot.AI/Agent/ChatClientPlanner.cs:398`
**Problem**: `StripCodeFence` only strips the fence when it can find a `\n` right after the opening ```` ``` ````/```` ```json ````. A reply fenced on a single line (e.g. `` ```json{"steps":[...]}``` `` with no newline) falls through to `return trimmed`, leaving the backticks in place, so `JsonSerializer.Deserialize` will throw and the Planner burns its one retry instead of tolerating the fence.
**Why it matters**: Real models occasionally return single-line fenced output, especially for short replies; this would count as a parse failure needing a retry (and, on the second failure, a `PlanningFailed` audit) instead of parsing cleanly, which is exactly the class of behavior AC-9 exists to tolerate.
**Suggested fix**: Fall back to stripping a leading fence token by regex/`IndexOf` rather than requiring a newline, or at minimum strip the opening fence up to the first `{`/`[` when no newline is present.

## Nits
- ⚪ `src/WorkPilot.AI/Providers/ProviderErrorChatClient.cs:107`, the key-shaped regex only recognizes `sk-` (OpenAI/DeepSeek) and `AIza` (older Google keys) prefixes; harmless today because the exact configured key is also scrubbed verbatim, but worth a comment noting it's a secondary net, not the primary defense, so a future provider with a different key shape doesn't get a false sense of coverage from the regex alone.

## Strengths
- The `ChatClientBuilder` wrapping order (`ProviderErrorChatClient` innermost, so it only ever sees the final failure and only ever hands `UseLogging`/`UseOpenTelemetry` an already-redacted `AiProviderException`) is exactly right — verified experimentally, not just by reading — and is a genuinely easy thing to get backwards.
- `AiOptionsValidator` collects every config problem into one message, names the exact key path to fix, and is proven to never echo a planted key value across a wide combinatorial test matrix (`AiOptionsValidatorTests`), including the "several problems at once" case.
- `PlanRunJob.FailPlanningAsync` stages the `AgentRun` transition and the `PlanningFailed` audit row on the same scoped `DbContext`, committed by a single `SaveChangesAsync` — genuinely atomic, not just apparently so.
- Test coverage is unusually strong for this kind of infrastructure: retries, `Retry-After` honoring, per-attempt timeout cutoff, Gemini's array-shaped error body, caller-cancellation-vs-provider-timeout disambiguation, and telemetry privacy are all exercised against a real Kestrel stub server and the real OpenAI SDK pipeline, not mocks.
- `SharedApiFactory` pins both `Default` and `Planner` to `Fake` via environment variables explicitly, rather than relying on `IsDevelopment()`, closing a real "tests accidentally call a paid provider" risk the rationale doc calls out.

## Test coverage
Strong overall: AC-1 through AC-9 each have direct, purpose-built tests (config-only provider switching, Default fallback, Fake keyless path, every AC-4 validation branch, retry/timeout/non-retry behavior, key redaction including a Gemini-shaped key the regex doesn't recognize, telemetry with/without sensitive data, fenced-JSON parsing, and both `/health/ai` outcomes). The one gap is `ProviderErrorChatClient.GetStreamingResponseAsync`, called out above as a Major — it's new logic with no test anywhere touching it.
