# Rationale: core data model

## Context

Spec 0001 fixed the stack (PostgreSQL, EF Core, a shared database with Supabase) but deliberately left the schema itself undecided. Every feature from here on (job matching, applications, university outreach, the agent orchestrator, approvals) reads or writes this schema, so getting the entity boundaries and relationships wrong now means a migration and code change later touching every module that depends on it.

Two forces shape this more than a typical CRUD schema would. First, the product's spec requires provenance on every externally sourced record (a job posting, a professor's page): where it was found, when it was retrieved, and how confident the match is. Second, the Agent orchestrator (scope feature 6, not yet designed) needs its own durable run history distinct from Hangfire's own job scheduling tables, so this schema has to name that boundary now even though the orchestrator's internal logic is designed later.

The system is single user today (per the scope's own assumptions) but is meant to serve more later; nothing here should make that harder, though multi tenant isolation itself is out of scope for this spec.

## Options considered

### Option 1: One flat schema, entity per table, no shared provenance type

Every entity gets its own table with its own copies of `SourceUrl`/`RetrievedAt`/etc columns where needed, no shared EF Core type.

**Pros**:
- No abstraction to learn; every table is self contained and readable in isolation.

**Cons**:
- Provenance fields drift entity to entity over time (a missed column, a renamed field), exactly the failure the "provenance on every externally sourced record" requirement exists to prevent.

### Option 2: Shared `Provenance` owned type, soft delete on business entities, hard delete + Storage offload on agent/workflow internals (chosen)

A single EF Core owned type for provenance, applied wherever an entity is externally sourced; soft delete on the entities users and audits reference; hard delete with a retention window on the Agent/workflow's own internal trace, with bulky payloads (tool call inputs, raw HTML snapshots, browser screenshots) held in Supabase Storage and only a reference URL kept in Postgres.

**Pros**:
- Provenance stays consistent by construction, not convention.
- Keeps Postgres itself lean; the Agent can run thousands of steps a day without every one becoming a permanent Postgres row.
- Audit relevant history is genuinely durable (never physically removed), matching the audit trail requirement.

**Cons**:
- Two different deletion policies in one schema (soft vs hard) is one more rule a future engineer has to learn and apply correctly per entity, rather than one uniform rule everywhere.

### Option 3: Everything soft deleted, nothing hard deleted, no Storage offload

Uniform soft delete across every table, including `AgentStep`/`ToolCall`/`WorkflowEvent`, all payloads kept inline in Postgres.

**Pros**:
- One deletion rule everywhere, simplest to reason about and query.

**Cons**:
- The Agent orchestrator can generate very high volumes of steps and tool calls; keeping every one forever, inline, inflates the primary database with data that has no audit or product value past its retention window, and large payload columns bloat every backup and every full table scan.

## Rationale

The provenance requirement in the scope's own "done when" line is non negotiable, and entity by entity copies of the same four columns is exactly how such a requirement silently rots (Option 1); an owned type makes the shape impossible to get wrong per entity.

The deletion split (Option 2 over Option 3) follows directly from what each class of data is for. `JobApplication`, `AuditLog`, and their peers exist precisely so a wrong or reversed decision can be reconstructed later, which the spec's audit trail requirement demands directly, so soft delete is not optional there. The Agent's own step by step trace exists to make one run explainable while the workflow is live and for a bounded debugging window after, not as a permanent record; keeping it forever inline in the primary database trades a real, growing operational cost (backup size, table bloat, scan cost) for retention benefit nobody asked for. Offloading bulky payloads to Storage reuses the pattern spec 0001 already established for resumes, so this is not a new operational surface, just a second consumer of an existing one.

The engineer's own answers directly settled the remaining load bearing calls: GUID v7 primary keys (avoids collisions between EF Core and Supabase both touching this one database), immutable version rows for resumes and cover letters (matches the spec's explicit immutability requirement), a domain enforced state machine on `JobApplication` (matches this project's `AGENTS.md` rule that the Domain layer enforces its own invariants, no ORM or framework logic), a dedup fingerprint added to `JobSnapshot` now rather than deferred (cheap now, avoids a follow up migration when scope feature 10 is built), and application level encryption of OAuth tokens via ASP.NET Core Data Protection (keeps raw tokens out of reach of Supabase's own layer entirely, matching spec 0001's rule that the .NET backend alone owns sensitive credentials).
