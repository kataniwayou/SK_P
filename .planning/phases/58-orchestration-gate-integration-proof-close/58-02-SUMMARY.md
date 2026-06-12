---
phase: 58-orchestration-gate-integration-proof-close
plan: 02
subsystem: testing
tags: [e2e, realstack, gate-a, config-schema, seed-helper, idempotency, xunit, traits]

# Dependency graph
requires:
  - phase: 57-startup-config-schema-fetch-gate-a
    provides: "Gate A (startup config-schema↔config-type compat) + null-is-skip — the runtime gate this plan seeds a compatible (run-and-pass) input for"
  - phase: 55-live-proof-close-gate
    provides: "SC1/SC2/SC3 RealStack recovery suite (originally [Trait(\"Phase\",\"55\")]) — retagged here into the phase-58 close gate"
provides:
  - "SeedConfigSchemaAsync GET-or-create-by-Name config-schema seed helper (never PUT — T-58-04 idempotency primitive)"
  - "SampleCompatibleSchemaName ('gateA-sample-compatible') + SampleCompatibleSchemaDefinition consts shared by Plan 03's Gate-A E2E and Plan 04's close script"
  - "SeedProcessorAsync optional configSchemaId param — seeds a compatible non-null ConfigSchemaId so Gate A RUNS AND PASSES (CFG-09)"
  - "SC1/SC2/SC3 retagged [Trait(\"Phase\",\"58\")] — full v5 recovery regression sealed into the phase-58 milestone close gate"
affects: [58-03-gate-a-cfg09-e2e, 58-04-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "GET-or-create-by-sentinel-Name: list-filter-then-POST-if-absent (no unique constraint → fixed Name is the idempotency key); NEVER PUT (frozen-once-referenced schema 409s)"
    - "Additive defaulted-param seed extension: configSchemaId=null keeps existing callers schema-less while enabling a compatible-schema flip for new (Plan 03) callers"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs (SeedConfigSchemaAsync helper + 2 consts + SeedProcessorAsync configSchemaId param)"
    - "tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs (Phase 55→58 trait + doc)"
    - "tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs (Phase 55→58 trait)"
    - "tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs (Phase 55→58 trait + doc)"

key-decisions:
  - "SchemaCreateDto.Definition is a string (NOT JsonElement as the plan's interface section claimed) — helper signature adapted to (HttpClient, string sentinelName, string definition, CancellationToken); meta-schema validation is server-side on write"
  - "SeedConfigSchemaAsync left as a private static primitive (no caller in this file yet) — it is consumed by Plan 03's new Gate-A CFG-09 test; C# does not warn on unused private methods, so the 0-warning gate holds"
  - "Hermetic suite verified via the MTP filter `-- --filter-not-trait \"Category=RealStack\"` (558/558); the VSTest-style `--filter \"Category!=RealStack\"` is IGNORED under Microsoft.Testing.Platform (warning MTP0001) and lets the 5 live-only RealStack E2E run+fail — use the MTP idiom"

patterns-established:
  - "GET-or-create-by-Name config-schema seed (T-58-04 idempotency; never PUT)"
  - "Shared sentinel Name/Definition consts across plans for an idempotent live N=3 close-gate run"

requirements-completed: [CFG-09]

# Metrics
duration: 18min
completed: 2026-06-12
---

# Phase 58 Plan 02: Gate-A Seed Primitives + SC Retag Summary

**Added a reusable `SeedConfigSchemaAsync` GET-or-create-by-Name config-schema seed helper (never-PUT, T-58-04 idempotent) plus a compatible-`ConfigSchemaId` seed path on `SeedProcessorAsync` so Gate A runs-and-passes for CFG-09, and retagged the SC1/SC2/SC3 v5 RealStack recovery suite into the phase-58 milestone close gate.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-12T22:07:48Z
- **Completed:** 2026-06-12T22:25:43Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments

- **`SeedConfigSchemaAsync(HttpClient client, string sentinelName, string definition, CancellationToken ct) → Task<Guid>`** — GET-all `/api/v1/schemas` → `FirstOrDefault(s => s.Name == sentinelName)` → reuse Id if present, else POST `SchemaCreateDto(sentinelName, "1.0.0", null, definition)` and return the created Id. NEVER PUT (a referenced schema's Definition is frozen → 409). This is the D-09a / D-13 / T-58-04 idempotency primitive Plan 03 consumes.
- **Shared sentinel consts** (so Plan 03's Gate-A test and Plan 04's close script reuse the exact same Name across the live N=3 run):
  - `internal const string SampleCompatibleSchemaName = "gateA-sample-compatible";`
  - `internal const string SampleCompatibleSchemaDefinition = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"string"}}}""";`
  - This definition is COVERED by `Processor.Sample`'s typed config `SampleConfig(string? Value)` → Gate A (`ConfigSchemaId ⊨ configType`) RUNS AND PASSES (not Gate-A-skipped).
- **`SeedProcessorAsync` gained an additive `Guid? configSchemaId = null` param** passed straight into the `ProcessorCreateDto.ConfigSchemaId` (verified 7th positional field). Existing callers stay schema-less (defaulted null); the GET-by-source-hash idempotency branch was left untouched (only the POST body's ConfigSchemaId changed).
- **SC1/SC2/SC3 retagged** `[Trait("Phase","55")]` → `[Trait("Phase","58")]` (tag-only, zero behavior change; D-07). `[Trait("Category","RealStack")]` retained on all three — still hermetic-excluded, now part of the phase-58 live close-gate regression. Cosmetic XML-doc "Phase 55" refs updated on SC1/SC3.

## Task Commits

1. **Task 1: Add SeedConfigSchemaAsync GET-or-create helper + compatible-ConfigSchemaId seed path** — `8f74413` (feat)
2. **Task 2: Retag SC1/SC2/SC3 RealStack suite into the phase-58 close gate** — `55dcd38` (test)

## Files Created/Modified

- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — added `using BaseApi.Service.Features.Schema;`, the two sentinel consts, the `SeedConfigSchemaAsync` helper, and the `configSchemaId` param on `SeedProcessorAsync`.
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` — `[Trait("Phase","58")]` (line 72) + doc.
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — `[Trait("Phase","58")]` (line 76).
- `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — `[Trait("Phase","58")]` (line 96) + doc.

## Decisions Made

- **`Definition` is `string`, not `JsonElement`.** The plan's `<interfaces>` block stated `SchemaCreateDto(... JsonElement Definition)` and the helper skeleton used `JsonElement definition`. The VERIFIED real DTO (`src/BaseApi.Service/Features/Schema/SchemaDtos.cs`) declares `string Definition`. Adapted the helper to `string definition`; the compatible definition is authored as a JSON string literal. Server-side meta-schema validation on write is unchanged. (See Deviations — Rule 3.)
- **Helper kept as an uncalled private primitive.** `SeedConfigSchemaAsync` has no caller in `SampleRoundTripE2ETests` itself (the existing tests still seed schema-less); it is the reusable primitive Plan 03's NEW Gate-A CFG-09 test consumes. C# emits no unused-private-method warning, so the 0-warning gate holds.
- **Hermetic verification idiom.** `dotnet test ... --filter "Category!=RealStack"` is silently ignored under Microsoft.Testing.Platform (warning MTP0001 — VSTest properties dropped), so 5 live-only RealStack E2E ran and failed (`rabbitmq://rabbitmq/` unreachable from host). The plan's prescribed MTP form `-- --filter-not-trait "Category=RealStack"` correctly excludes them → **558/558 passed**.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Plan interface mismatch: Schema `Definition` is `string`, not `JsonElement`**
- **Found during:** Task 1
- **Issue:** The plan's `<interfaces>` block and the `SeedConfigSchemaAsync` skeleton typed the schema definition as `JsonElement`. The actual `SchemaCreateDto`/`SchemaReadDto` (`SchemaDtos.cs`) declare `Definition` as `string`. Using `JsonElement` would not compile against the real DTO.
- **Fix:** Adapted the helper signature to `SeedConfigSchemaAsync(HttpClient client, string sentinelName, string definition, CancellationToken ct)` and authored `SampleCompatibleSchemaDefinition` as a JSON string literal. The GET-filter-by-Name → POST-if-absent → never-PUT semantics are preserved exactly as specified.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
- **Verification:** `dotnet build ... -c Release` → 0 warnings / 0 errors; hermetic suite 558/558.
- **Committed in:** `8f74413` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking — plan/code interface mismatch).
**Impact on plan:** Mechanical type adaptation only; all load-bearing behavior (GET-or-create-by-Name, never-PUT, shared sentinel, compatible-capable seed path) delivered as designed. No scope creep.

## Issues Encountered

- The VSTest-style hermetic filter is a no-op under MTP (see Decisions). Resolved by switching to the MTP `--filter-not-trait` idiom the plan already prescribed. No code impact.

## Known Stubs

None. `SeedConfigSchemaAsync` is an intentionally-uncalled-here reusable primitive (consumed by Plan 03), not a UI/data stub — documented in Decisions, not a deferred gap.

## Threat Flags

None. T-58-04 (schema-seed state churn) is mitigated exactly as the threat register requires: GET-all-filter-by-Name then POST-if-absent, never PUT. No new endpoints, auth, crypto, or input-validation surface introduced (the seed uses existing CRUD; the schema Definition is test-authored and server-side meta-schema-validated on write).

## User Setup Required

None - no external service configuration required. (No live docker stack is needed for any check in this plan; the SC retags only take effect in Plan 04's operator-gated live close gate.)

## Next Phase Readiness

- **Plan 03 (Gate-A CFG-09 E2E)** can now consume `SeedConfigSchemaAsync` + the `gateA-sample-compatible` sentinel + `SeedProcessorAsync(configSchemaId:)` to seed a compatible-schema processor that goes Healthy and starts normally (the CFG-09 "compatible-starts-normally" proof).
- **Plan 04 (close gate)** picks up SC1/SC2/SC3 under `Phase=58` for the full v5-recovery + v6-Gate-A live close-gate regression; the shared sentinel Name keeps the N=3 run idempotent (net-zero).

## Self-Check: PASSED

- FOUND: 58-02-SUMMARY.md
- FOUND: tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
- FOUND commit 8f74413 (Task 1)
- FOUND commit 55dcd38 (Task 2)

---
*Phase: 58-orchestration-gate-integration-proof-close*
*Completed: 2026-06-12*
