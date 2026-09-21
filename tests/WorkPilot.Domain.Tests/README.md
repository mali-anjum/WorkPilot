# WorkPilot.Domain.Tests

Unit tests for `WorkPilot.Domain`, no infrastructure or database involved
(per `AGENTS.md`: Domain is unit tested with no infrastructure mocks).
Covers the acceptance criteria of `docs/specs/0002-data-model/index.md` that
are pure domain logic: the `JobApplication` state machine (AC-4), the shared
`Entity`/`SoftDeletableEntity` base behavior (AC-3's domain half).

The database-dependent criteria (AC-1, AC-2, AC-6, AC-7, AC-8, and the
query-filter half of AC-3) are exercised live in
`docs/specs/0002-data-model/verify.md`, per this project's rule that
infrastructure is integration tested against a real Postgres, never mocked.
