---
phase: 08-entity-build-out-migrations-docker-runtime-tests
plan: 08
subsystem: test
tags: [error-mapping, migration-failure, persist-10, test-06, d-15, d-16, d-18, regression, sc-1, sc-2, sc-3, sc-5, sc-6, phase8-complete]

# Dependency graph
requires:
  - phase: 04-cross-cutting-middleware-error-handling
    provides: PostgresExceptionMapper Option A regex (23505→409 + 23503→422 with field name), IExceptionHandler chain, ProblemDetails with correlationId
  - phase: 03-ef-core-persistence-base
    provides: D-15 per-class throwaway DB pattern + D-18 3-consecutive-GREEN cadence
  - phase: 05-observability-health-probes
    provides: IStartupGate, /health/live + /health/ready + /health/startup with HEALTH-02/03 tag predicates
  - plan: 08-01
    provides: Phase8WebAppFactory (public class, protected ctor accepting connectionStringOverride)
  - plan: 08-02
    provides: SchemaCreateDto, SchemaDtoValidator (MetaSchemas.Draft202012 + SSRF guard)
  - plan: 08-03
    provides: ProcessorCreateDto, uq_processor_source_hash unique index
  - plan: 08-04
    provides: StepCreateDto, StepEntryCondition, Restrict FK semantics
  - plan: 08-06
    provides: WorkflowCreateDto, fk_workflow_entry_steps_step_id Restrict FK (SC#5 load-bearing)
  - plan: 08-07
    provides: AppDbContext + InitialCreate migration + StartupCompletionService MigrateAsync swap with try/catch/no-rethrow

provides:
  - tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs — 4 [Fact] methods (TEST-06 floor)
  - tests/BaseApi.Tests/Composition/MigrationFailureWebAppFactory.cs — Phase8WebAppFactory subclass with Port=5434 (closed) connection string
  - tests/BaseApi.Tests/Integration/MigrationFailureFacts.cs — 1 [Fact] proving D-16 + PERSIST-10
  - Phase 3 D-18 regression cadence cleared (3 consecutive 128/128 GREEN runs)
  - Phase 3 D-15 byte-identical psql \l BEFORE/AFTER snapshot (SHA-256 hash match)
  - Phase 5 D-11 cleanup verified (tests/.otel-out/telemetry.jsonl absent post-suite)

affects:
  - Phase 8 phase-completion gate cleared: SC#1..SC#6 all behaviorally verified
  - ROADMAP can mark [x] Phase 8
  - System is shippable as a working Steps API base (v1 milestone complete)

# Tech tracking
tech-stack:
  added: []     # no new packages — ErrorMappingFacts + MigrationFailureFacts ride on Phase 4-8 infrastructure
  patterns:
    - "Cross-entity error-mapping fact pattern: [Fact] uses IClassFixture<Phase8WebAppFactory> + TestContext.Current.CancellationToken + exercises end-to-end HTTP → Phase 4 mapper → ProblemDetails shape with correlationId"
    - "SSRF timing-bound assertion pattern: Stopwatch.ElapsedMilliseconds < 500 proves no outbound HTTP happened (a real attacker.example resolution would take seconds to time out)"
    - "Migration-failure WebAppFactory subclass pattern: subclass Phase8WebAppFactory via protected ctor, inject bad connection string (Host=localhost;Port=5434) — base ctor's _connectionStringOverride path skips real PostgresFixture allocation, so MigrateAsync throws cleanly without polluting DB state"
    - "3-probe failing-readiness assertion pattern: /health/live=200 (process responsive) + /health/startup=503 (gate never flipped) + /health/ready=503 (predicate includes StartupHealthCheck) — proves PERSIST-10 + HEALTH-01/02/03 + D-16 in a single fact"
    - "D-18 cadence accommodation: 3 CONSECUTIVE GREEN runs is the gate, not first-attempt-3; flaky tests (ConcurrencyTokenTests racing-writes, Phase 5 OTel warmup) interleave but eventually stabilize"

key-files:
  created:
    - tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs
    - tests/BaseApi.Tests/Composition/MigrationFailureWebAppFactory.cs
    - tests/BaseApi.Tests/Integration/MigrationFailureFacts.cs
    - .planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/psql-l-before-phase08.txt
    - .planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/psql-l-after-phase08.txt
  modified: []

key-decisions:
  - "ErrorMappingFacts SSRF payload changed from plan-as-written `{\"$ref\":\"https://attacker.example/schema.json\"}` to combined-invalid `{\"type\":\"not-a-real-type\",\"$ref\":\"https://attacker.example/schema.json\"}` — empirically verified that the bare $ref-only payload is structurally valid against the Draft 2020-12 meta-schema (Rule 1 fix-forward; plan internal inconsistency). Combined payload satisfies BOTH the 400 BadRequest assertion AND the SSRF timing-bound assertion."
  - "SSRF timing threshold set to <500ms (RESEARCH §SSRF test seam suggests <100ms; 500ms gives slack for CI cold starts). The Schema validator already passes well under 500ms in the GREEN run because SchemaRegistry.Global.Fetch is no-op and no outbound HTTP occurs."
  - "MigrationFailureWebAppFactory uses Port=5434 (NOT Port=1 like Phase 5 HealthDeadPostgresFixture) — Port=5434 is one off from the running Postgres on 5433, which is closed for connection but DNS resolves; matches the plan body verbatim."
  - "3 CONSECUTIVE GREEN runs achieved on Runs 4/5/6 (Runs 1+3 had one flake each: ConcurrencyTokenTests.Test_RacingWrites in Run 1 and LogLevelFilterTests.Test_Information_Log_Present_When_Default_Information in Run 3 — both pre-existing flakes documented in earlier phase SUMMARYs; out-of-scope per SCOPE BOUNDARY rule). Total runs executed: 6."
  - "Byte-identical psql \\l snapshot proven via SHA-256 hash match: BEFORE and AFTER files both hash to 0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127. Confirms zero leaked stepsdb_test_<guid> databases across the entire 6-run cycle."

patterns-established:
  - "Pattern: Cross-entity error-mapping fact suite — 1 file per phase covering UQ + FK + DELETE-Restrict + validator-rejection paths in 4 facts; each fact exercises the full HTTP → mapper → ProblemDetails surface with correlationId assertion. Future phases adding entities should append to this file rather than creating a parallel one (the file's IClassFixture<Phase8WebAppFactory> shares a single throwaway DB across all 4 facts)."
  - "Pattern: WebAppFactory subclass for failure-mode integration — subclass the per-class throwaway-DB-managing factory via a protected ctor that takes a connectionStringOverride. The base ctor skips PostgresFixture allocation in this code path (no real DB created), and the test verifies failure semantics at the HTTP layer. Pattern extends to any 'inject bad input at boot' scenario (Phase 5 had HealthDeadPostgresFixture as a Wave-2 precedent)."
  - "Pattern: 6+ runs to clear 3-CONSECUTIVE-GREEN gate — pre-existing flakes (ConcurrencyTokenTests racing-writes, Phase 5 OTel warmup) interleave with otherwise-GREEN runs. The gate is consecutive, not cumulative; re-run discipline is operator responsibility. Future hardening: stabilize the racing-writes test (Rule 4 deferred — architectural change to deterministic conflict injection)."

requirements-completed:
  - PERSIST-10
  - TEST-06

# Metrics
duration: 25min
completed: 2026-05-27
---

# Phase 8 Plan 08: ErrorMappingFacts + MigrationFailureFacts + D-18 Regression Cadence — Phase 8 Final Plan

**The FINAL plan of Phase 8 and of the entire v1 milestone. Ships 4 cross-entity error-mapping facts (TEST-06 floor) + 1 migration-failure isolation fact (PERSIST-10 + D-16 closure) + executes the Phase 3 D-18 regression cadence: 3 consecutive 128/128 GREEN runs + byte-identical psql \l BEFORE/AFTER snapshot (SHA-256 match) + Phase 5 D-11 cleanup verified (tests/.otel-out/telemetry.jsonl absent post-suite). SC#1..SC#6 all behaviorally verified end-to-end; the system is shippable.**

## Performance

- **Duration:** ~25 min (includes 6 full-suite runs at ~30s each = 3min of test time + ~12 min build/iteration)
- **Started:** 2026-05-27T~23:30Z
- **Completed:** 2026-05-27T~23:56Z
- **Tasks:** 3 (2 file-creation + 1 verification cadence)
- **Files created:** 5 (3 test/composition files + 2 artifact snapshots)
- **Files modified:** 0

## Accomplishments

- **ErrorMappingFacts.cs ships 4 cross-entity error-mapping facts (TEST-06 floor satisfied):**
  - `Create_Duplicate_SourceHash_Returns409` — POST Processor twice with same SourceHash → first 201, second 409 with `source_hash` or `uq_processor_source_hash` in `detail` + T-04-LEAK regression (no `Npgsql.PostgresException` or `at BaseApi.` stack frames) + correlationId asserted. **SC#2 end-to-end.**
  - `Create_Workflow_Non_Existent_EntryStepId_Returns422` — POST Workflow with random Guid in `entryStepIds` → 422 with `step_id` or `fk_workflow_entry_steps_step_id` in `detail` + correlationId asserted. **SC#5 FK half end-to-end.**
  - `Delete_Step_Referenced_By_Workflow_Returns422` — Create Processor + Step + Workflow referencing Step, then DELETE Step → 422 (Postgres rejects via fk_workflow_entry_steps_step_id Restrict) + correlationId asserted. **SC#5 DELETE-Restrict half end-to-end.**
  - `Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` — POST Schema with `{"type":"not-a-real-type","$ref":"https://attacker.example/schema.json"}` → 400 with field-level `Definition` error + Stopwatch elapsed <500ms (SSRF defense proven — no outbound HTTP) + correlationId asserted. **SC#3 end-to-end + VALID-08 + VALID-09.**

  All 4 facts use `IClassFixture<Phase8WebAppFactory>` and `TestContext.Current.CancellationToken` (xUnit1051 + TreatWarningsAsErrors=true). Each Create body uses unique Guids for collision safety with Wave B smoke tests.

- **MigrationFailureWebAppFactory.cs + MigrationFailureFacts.cs close PERSIST-10 + D-16:**
  - `MigrationFailureWebAppFactory : Phase8WebAppFactory` subclass calls `base("Host=localhost;Port=5434;Database=stepsdb;Username=postgres;Password=postgres")` — Port=5434 is closed (Postgres runs on 5433). The base ctor's `_connectionStringOverride` path skips PostgresFixture allocation, so no real DB is created and the byte-identical snapshot still holds.
  - `MigrationFailureFacts.BootWithBadConnectionString_LeavesProcessAlive_AndStartupUnhealthy` (1 [Fact]) — exercises the 3-probe failing-readiness assertion: `/health/live=200` (HEALTH-02, process responsive), `/health/startup=503` (HEALTH-01, gate never flipped), `/health/ready=503` (HEALTH-03, predicate includes StartupHealthCheck). The fact that all 3 GETs return at all proves StartupCompletionService.StartAsync did NOT rethrow (PERSIST-10) — if it had, `WebApplicationFactory<Program>.Server` would have thrown on `CreateClient()`.

- **3 consecutive 128/128 GREEN runs achieved (Phase 3 D-18):**
  - Run 4: Passed 128, Failed 0, ~28.6s, exit 0
  - Run 5: Passed 128, Failed 0, ~28.8s, exit 0
  - Run 6: Passed 128, Failed 0, ~28.9s, exit 0
  - Total test count breakdown: 98 Phase 1-7 + 25 Wave B smoke + 4 ErrorMapping + 1 MigrationFailure = **128 facts** (exactly matches plan SC target of ≥128).
  - Runs 1 and 3 each had a single intermittent failure (different tests — ConcurrencyTokenTests.Test_RacingWrites in Run 1, LogLevelFilterTests.Test_Information_Log_Present in Run 3); both are pre-existing flakes documented in earlier Phase SUMMARYs (Phase 4 race condition / Phase 5 OTel cold-start). Per the plan's accommodation ("3 CONSECUTIVE GREEN is the gate, not first-attempt-3") and SCOPE BOUNDARY rule, re-runs are acceptable.

- **Byte-identical psql \\l BEFORE/AFTER snapshot (Phase 3 D-15):**
  - BEFORE: 4 baseline databases (postgres, stepsdb, template0, template1)
  - AFTER: 4 baseline databases (identical)
  - `diff /tmp/psql-before-phase08.txt /tmp/psql-after-phase08.txt` outputs nothing (exit code 0)
  - SHA-256 hash match: both files hash to `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`
  - Snapshots preserved in `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/` (folder is in `.gitignore`; content reproduced below for git-traceable proof)

  **BEFORE snapshot content (verbatim from `docker exec sk_p-postgres-1 psql -U postgres -l`):**

  ```
                                                      List of databases
     Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
  -----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
   postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
   stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
   template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
             |          |          |                 |            |            |        |           | postgres=CTc/postgres
   template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
             |          |          |                 |            |            |        |           | postgres=CTc/postgres
  (4 rows)
  ```

  **AFTER snapshot content:** byte-identical to the above (verified via diff exit 0 + SHA-256 match).

- **Phase 5 D-11 cleanup discipline verified:**
  - `tests/.otel-out/telemetry.jsonl` is absent after the full 6-run cycle — the xUnit v3 `[assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))]` from Phase 5 ran end-of-suite and shelled out to `docker compose stop|delete|start otel-collector`.

## Task Commits

Each task committed atomically:

1. **Task 1: Create ErrorMappingFacts.cs (4 [Fact] methods)** — `350dd3e` (feat)
2. **Task 2: Create MigrationFailureWebAppFactory + MigrationFailureFacts (1 [Fact])** — `5b2738f` (feat)
3. **Task 3: D-18 regression cadence (3 consecutive GREEN + byte-identical snapshot)** — no code commit; artifacts captured in `.planning/.../artifacts/` and documented here

**Plan metadata commit:** to follow this SUMMARY.md write (`docs(08-08): complete Phase 8 final plan — ErrorMappingFacts + MigrationFailureFacts + D-18 regression cadence`).

## Files Created/Modified

### Created (5)

**Test code (3 files):**
- `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs` — `public sealed class ErrorMappingFacts : IClassFixture<Phase8WebAppFactory>` with 4 [Fact] methods, `RandomSha256Hex` helper
- `tests/BaseApi.Tests/Composition/MigrationFailureWebAppFactory.cs` — `public sealed class MigrationFailureWebAppFactory : Phase8WebAppFactory` with Port=5434 (closed) ctor base call
- `tests/BaseApi.Tests/Integration/MigrationFailureFacts.cs` — `public sealed class MigrationFailureFacts : IClassFixture<MigrationFailureWebAppFactory>` with 1 [Fact] (3-probe assertion)

**Artifacts (2 files — preserved locally; folder is .gitignored):**
- `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/psql-l-before-phase08.txt` — BEFORE snapshot of `psql -U postgres -l` (4 databases). NOTE: `artifacts/` is matched by `.gitignore` line 63 (`artifacts/`). Snapshots remain on disk for the operator; content + SHA-256 hash recorded inline in this SUMMARY for git-traceable proof.
- `.planning/phases/08-entity-build-out-migrations-docker-runtime-tests/artifacts/psql-l-after-phase08.txt` — AFTER snapshot, byte-identical to BEFORE (same .gitignore caveat)

### Modified

*(none — Plan 08-08 is purely additive)*

## Decisions Made

- **SSRF payload changed from $ref-only to combined-invalid (Rule 1 fix-forward).** The plan body specified `{"$ref":"https://attacker.example/schema.json"}` as the schema definition. Empirical probe with JsonSchema.Net 9.2.1 (matching the production pin) confirmed this payload IS structurally valid against Draft 2020-12 meta-schema — the bare $ref-only document conforms to the meta-schema even though the ref target is unresolvable. The validator returned IsValid=true and the test asserted 400 but got 201. **Fix:** combined `{"type":"not-a-real-type","$ref":"https://attacker.example/schema.json"}` — the invalid `type` value makes meta-schema validation fail (400 BadRequest), and the `$ref` to attacker.example still serves as the SSRF probe for the timing-bound assertion. Both intents preserved.

- **SSRF timing threshold 500ms over 100ms.** RESEARCH §"SSRF test seam" line 251 suggests <100ms. The plan body specified <500ms for CI cold-start slack. Observed elapsed in GREEN run is well under 50ms because no outbound HTTP happens — but the 500ms threshold protects against the test becoming flaky under temporary CI slowdowns.

- **6 total full-suite runs to achieve 3 CONSECUTIVE GREEN.** Per the plan's explicit accommodation: "the eventual 3 CONSECUTIVE GREEN is the gate, not the first-attempt 3." Pre-existing flakes interleaved on Run 1 (ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak — a documented inherent race condition in the Phase 4 test where both POSTs can occasionally succeed before either races to commit) and Run 3 (LogLevelFilterTests.Test_Information_Log_Present_When_Default_Information — a documented Phase 5 OTel Collector cold-start flake). Both are out-of-scope per SCOPE BOUNDARY rule (pre-existing tests not authored or modified by this plan). Runs 4, 5, 6 all 128/128 GREEN consecutively.

- **MigrationFailureWebAppFactory uses Port=5434 verbatim per plan.** Port=5434 is one off from the running Postgres on 5433 — it's the simplest "definitely-closed-port" choice that doesn't collide with any documented test infrastructure port (Phase 5 HealthDeadPostgresFixture uses Port=1, which would also work, but Port=5434 is plan-spec-literal and more obviously a "wrong-port" failure mode for educational clarity).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] ErrorMappingFacts SSRF payload — plan-as-written `{"$ref":"https://attacker.example/schema.json"}` is structurally VALID against the Draft 2020-12 meta-schema**

- **Found during:** Task 1 verification run (1 of 4 ErrorMappingFacts failed: `Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` expected BadRequest but got Created)
- **Issue:** The plan assumed a JSON document containing only `{"$ref": "<unresolvable URL>"}` would be invalid against the Draft 2020-12 meta-schema. Empirical probe with JsonSchema.Net 9.2.1 (the production pin) shows the meta-schema permits this payload — `$ref` is a structurally-valid keyword whose value is a URI; the ref target's resolvability is checked only at evaluation time (when actual data is validated against the schema), not at meta-schema validation time. With `SchemaRegistry.Global.Fetch = (_, _) => null` (VALID-09 defense-in-depth), no outbound HTTP occurs and the schema persists at 201.
- **Root cause:** Plan internal inconsistency between "schema is invalid → 400" intent and the actual JsonSchema.Net meta-schema validation behavior on $ref-only documents.
- **Fix:** Changed the test's `Definition` from `{"$ref":"https://attacker.example/schema.json"}` to `{"type":"not-a-real-type","$ref":"https://attacker.example/schema.json"}`. The `"type":"not-a-real-type"` violates the meta-schema (the `type` keyword's value must be a primitive JSON Schema type name like "object", "string", etc.), so `MetaSchemas.Draft202012.Evaluate(...).IsValid` returns false and the validator surfaces a 400. The `$ref` to attacker.example is preserved so the SSRF timing-bound assertion still proves no outbound HTTP.
- **Files modified:** `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs`
- **Verification:** All 4 ErrorMappingFacts pass GREEN after fix (`dotnet test --filter-method "BaseApi.Tests.Integration.ErrorMappingFacts.*"` → Failed: 0, Passed: 4 in ~5s).
- **Committed in:** `350dd3e` (Task 1 commit; fix applied before commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 — plan internal inconsistency between assumed and actual library behavior).

**Impact on plan:** The fix preserves BOTH plan intents (the 400 BadRequest assertion AND the SSRF timing-bound assertion). The combined-invalid payload is more thorough — it tests two of the validator's defense layers (meta-schema correctness AND SSRF $ref no-outbound) in a single fact. The plan's verbatim verify regex (`grep -q "attacker.example"`) still passes because the string is preserved.

## Authentication Gates

None — no external authentication required. Postgres connection uses the test fixture's connection string.

## Issues Encountered

**Two pre-existing test flakes interleaved in the D-18 cadence (out-of-scope, documented):**

1. **`ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`** (Run 1) — a Phase 4 test that fires two parallel POSTs to a load-then-mutate-then-save endpoint expecting exactly one 409 conflict. When timing is such that both POSTs serialize cleanly (no race), both return 200 OK and `Assert.NotNull(conflict)` fails. This is an inherent race condition in the test design; not a regression of any phase. Fix would require deterministic conflict injection (a v2 architectural concern — Rule 4 territory; deferred). Test passed on subsequent runs.

2. **`LogLevelFilterTests.Test_Information_Log_Present_When_Default_Information`** (Run 3) — a Phase 5 test that asserts the OTel Collector has captured an Information log within the test timeout. When the Collector batch exporter hasn't drained yet (cold-start), the assertion fails. Documented extensively in Phase 5/6/7 SUMMARYs as the canonical "OTel cold-start flake". Test passed on Runs 4/5/6.

Both are well-documented out-of-scope per SCOPE BOUNDARY rule (pre-existing flakes not caused by Plan 08-08 changes). The 3-CONSECUTIVE-GREEN gate (Runs 4/5/6) was achieved without any Plan 08-08 code change.

## SC#1..SC#6 Closure Ledger

| SC | What it asserts | Proving fact(s) | Status |
|----|-----------------|-----------------|--------|
| SC#1 | docker compose up + GET /api/v1/schemas → 200 + []; migration-failure unhealthy ready | SchemasIntegrationTests.List_ReturnsEmptyArray_OnEmptyDb (HTTP layer) + MigrationFailureFacts.BootWithBadConnectionString_LeavesProcessAlive_AndStartupUnhealthy (failure isolation half) | ✓ GREEN |
| SC#2 | 23505 → 409 + field name | ErrorMappingFacts.Create_Duplicate_SourceHash_Returns409 | ✓ GREEN |
| SC#3 | Invalid JSON Schema → 400; SSRF blocked | ErrorMappingFacts.Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall (combined meta-schema-invalid + $ref timing-bound) | ✓ GREEN |
| SC#4 | 5-field cron OK / 6-field 400 / null OK | WorkflowDtoValidator BeValidStandardCron (Plan 08-06) + WorkflowsIntegrationTests cron=null happy path (Plan 08-06) | ✓ GREEN |
| SC#5 | 23503 + Restrict 422 | ErrorMappingFacts.Create_Workflow_Non_Existent_EntryStepId_Returns422 (FK half) + ErrorMappingFacts.Delete_Step_Referenced_By_Workflow_Returns422 (Restrict half) | ✓ GREEN |
| SC#6 | ≥ 25 smoke + ≥ 4 error-mapping facts × 3 GREEN runs | 25 Wave B smoke + 4 ErrorMapping + 1 MigrationFailure = 30 new facts; Runs 4/5/6 all 128/128 GREEN | ✓ GREEN |

## 41 REQ-ID Closure Ledger

**39 active REQ-IDs verified GREEN via Phase 8 plans (Wave A + Wave B + Wave C):**

- Plan 08-01: INFRA-05, TEST-01, TEST-02
- Plan 08-02: ENTITY-03, VALID-08, VALID-09, + Schema HTTP/validation/mapping coverage
- Plan 08-03: ENTITY-04, ENTITY-09, ENTITY-10, PERSIST-13, PERSIST-14, HTTP-04..07, HTTP-11..12, VALID-10, VALID-11, VALID-20, TEST-05
- Plan 08-04: ENTITY-05, ENTITY-06, ENTITY-09, ENTITY-10, PERSIST-12, PERSIST-13, HTTP-04..07, HTTP-11..12, VALID-12, VALID-13, VALID-14, VALID-20, TEST-05
- Plan 08-05: ENTITY-07, ENTITY-10, PERSIST-12, PERSIST-13, HTTP-04..07, HTTP-11..12, VALID-15, VALID-16, VALID-20, TEST-05
- Plan 08-06: ENTITY-08, ENTITY-09, ENTITY-10, PERSIST-12, PERSIST-13, HTTP-04..07, HTTP-11..12, VALID-17, VALID-18, VALID-19, VALID-20, TEST-05
- Plan 08-07: PERSIST-01, PERSIST-09, PERSIST-10
- **Plan 08-08: PERSIST-10 (verified via fact), TEST-06**

**2 deferred to v2 per Plan 08-01 REQUIREMENTS.md amendment (D-05/D-06):**
- TEST-03 (Testcontainers.PostgreSql) — PostgresFixture pattern proven across 128 facts; Testcontainers cold-start 3-5s/fixture with no behavioral gain at v1 scale
- TEST-04 (Respawn) — would invalidate Phase 3 D-15 byte-identical psql \l no-leak proof; PostgresFixture's DROP DATABASE WITH FORCE on per-class disposal serves the same role with less overhead

## Threat Model Compliance

All Plan 08-08 mitigate-disposition threats addressed:

| Threat ID | Disposition | Status | Verification |
|-----------|-------------|--------|--------------|
| T-08-08-FLAKY-OTEL-FIRST-RUN | accept | NOTED | Acknowledged in plan; Run 3 LogLevelFilterTests flake is exactly this pattern; re-run discipline applied (Runs 4/5/6 all GREEN). |
| T-08-08-DB-LEAK | mitigate | DONE | BEFORE/AFTER psql \l snapshots byte-identical via SHA-256 (`0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127` match). PostgresFixture.DisposeAsync DROP WITH FORCE confirmed cleaning across the full 6-run cycle. |
| T-08-08-SSRF-LEAK-VIA-NETWORK | mitigate | DONE | `Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall` asserts `Stopwatch.ElapsedMilliseconds < 500` on a payload containing `https://attacker.example` $ref. GREEN run elapsed time is far under 50ms — no outbound HTTP attempted. SchemaRegistry.Global.Fetch no-op (Plan 08-02 VALID-09 static ctor) verified by absence of timeout. |
| T-08-08-MIGRATION-FAIL-RETHROW-CRASH | mitigate | DONE | `MigrationFailureFacts.BootWithBadConnectionString_LeavesProcessAlive_AndStartupUnhealthy` reaches 3 HTTP probes successfully — if StartupCompletionService had rethrown on MigrateAsync failure, `WebApplicationFactory<Program>.Server` would have thrown on `CreateClient()`. Process stays alive; PERSIST-10 verified. |
| T-08-08-CORRELATION-ID-LOSS | mitigate | DONE | Every ErrorMappingFacts fact asserts `Assert.True(doc.RootElement.TryGetProperty("correlationId", out _))` on the ProblemDetails body. Phase 4 ERROR-08 still propagates through Phase 8 error paths. |
| T-08-08-CONCURRENT-TEST-DB-COLLISION | accept | NOTED | xUnit v3 default parallelism + per-class `stepsdb_test_<guid:N>` DB naming verified by Phase 3 D-15 byte-identical snapshot (zero leaked test DBs across the 6-run cycle). |

## User Setup Required

None — Phase8WebAppFactory uses the existing localhost:5433 Postgres container; MigrationFailureWebAppFactory uses localhost:5434 (closed port — no real DB allocated). Both verified working without user intervention.

## Next Phase Readiness

**Phase 8 COMPLETE — system is shippable.** The v1 milestone is delivered:

- ✓ 5 entity controllers + CRUD against real Postgres
- ✓ InitialCreate migration applied at startup with PERSIST-10 failure semantics
- ✓ Runtime Docker image built and runnable (`docker compose up baseapi-service`)
- ✓ Integration test harness with 128 facts across the full stack
- ✓ 3 consecutive 128/128 GREEN runs (Phase 3 D-18 cadence)
- ✓ Byte-identical psql \l BEFORE/AFTER (Phase 3 D-15 zero-leak)
- ✓ Phase 5 D-11 cleanup discipline (telemetry.jsonl absent post-suite)
- ✓ SC#1..SC#6 all behaviorally verified end-to-end

**Recommended next:** `/gsd-verify-work 8` to formalize the Phase 8 verification ledger.

**v2 carry-forward concerns** (deferred from v1 with explicit reasoning in PROJECT.md / REQUIREMENTS.md):
- VALID-21 (dynamic Schema-vs-Payload conformance) — N2 decision, validator architectural change
- HTTP-17/18/19 (pagination/filter/sort) — deferred until proven needed
- INFRA-08 (multi-instance migration lock via Postgres advisory locks) — single-replica v1 scope
- INFRA-09 (Postgres role separation between app and migrator) — production hardening, v2
- ReadDto.NextStepIds / .EntryStepIds / .AssignmentIds post-mapper enrichment — BaseService GET/List methods would need to become virtual or a separate enrichment hook added
- TEST-03 (Testcontainers) + TEST-04 (Respawn) — Plan 08-01 v2 deferral

**v1 features delivered:** 5 entity controllers + CRUD; InitialCreate migration; runtime Docker image; integration test harness; correlation IDs end-to-end; OTel logs + metrics + traces; 3 health probes; RFC 7807 Problem Details; Phase 4 SQLSTATE → HTTP mapper; FluentValidation + Mapperly; AppFeatures DI aggregator; AddBaseApi/UseBaseApi composition root.

## Self-Check

Verification of claims before final commit:

**Created files (verified via filesystem and read):**
- `tests/BaseApi.Tests/Integration/ErrorMappingFacts.cs` — FOUND (4 [Fact] methods, all GREEN)
- `tests/BaseApi.Tests/Composition/MigrationFailureWebAppFactory.cs` — FOUND (Port=5434, sealed subclass)
- `tests/BaseApi.Tests/Integration/MigrationFailureFacts.cs` — FOUND (1 [Fact], 3 health probe assertions)
- `.planning/.../artifacts/psql-l-before-phase08.txt` — FOUND (4 baseline DBs)
- `.planning/.../artifacts/psql-l-after-phase08.txt` — FOUND (byte-identical to BEFORE)

**Task commits (verified via `git log --oneline -3`):**
- `350dd3e` Task 1 (ErrorMappingFacts) — FOUND
- `5b2738f` Task 2 (MigrationFailure*) — FOUND

**Quality gates:**
- 4 `[Fact]` attribute usages in ErrorMappingFacts.cs — PASSED
- 1 `[Fact]` attribute usage in MigrationFailureFacts.cs — PASSED
- `MigrationFailureWebAppFactory : Phase8WebAppFactory` declaration with `Port=5434` literal — PASSED
- All facts use `IClassFixture<...>` + `TestContext.Current.CancellationToken` — PASSED
- SSRF fact uses `Stopwatch` + `attacker.example` + asserts elapsed < 500ms — PASSED
- 409 fact checks `detail.Contains("source_hash" || "uq_processor_source_hash")` — PASSED
- 422 facts check `status == 422` — PASSED
- Every error-mapping fact asserts `correlationId` present (Phase 4 ERROR-08) — PASSED
- `dotnet build SK_P.sln -c Release` succeeds with 0 warnings, 0 errors — PASSED
- `dotnet test SK_P.sln --no-restore --no-build` runs 128 tests — PASSED
- 3 CONSECUTIVE GREEN runs (Runs 4/5/6): 128/128 each — PASSED
- BEFORE/AFTER psql \l snapshots byte-identical (SHA-256 hash match) — PASSED
- `tests/.otel-out/telemetry.jsonl` absent after suite (Phase 5 D-11) — PASSED

## Self-Check: PASSED

---
*Phase: 08-entity-build-out-migrations-docker-runtime-tests*
*Plan: 08*
*Completed: 2026-05-27*
