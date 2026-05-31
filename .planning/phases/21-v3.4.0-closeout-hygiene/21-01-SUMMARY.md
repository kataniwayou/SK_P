---
phase: 21-v3.4.0-closeout-hygiene
plan: 01
subsystem: infra
tags: [redis, l2-projection, messaging-contracts, refactor, close-gate, triple-sha]

# Dependency graph
requires:
  - phase: 15-l2-redis-projection-write-stop-existence-check
    provides: RedisProjectionKeys writer (Root/Step/Processor) + golden RedisProjectionKeysTests
  - phase: 19-orchestrator-console-webapi-bus-wiring
    provides: OrchestratorL2Keys reader (Root) + StartOrchestration/StopOrchestration consumers
  - phase: 20-correlation-propagation-proof
    provides: CorrelationPropagationE2ETests real-stack net + phase-20-close.ps1 triple-SHA gate
provides:
  - "Shared public L2ProjectionKeys (Root/Step/Processor) in Messaging.Contracts.Projections — single source of truth for the flat L2 Redis key scheme"
  - "Writer RedisProjectionKeys and reader OrchestratorL2Keys converted to thin forwarders delegating to L2ProjectionKeys"
  - "Golden L2ProjectionKeysTests (5 facts) pinning byte-exact key strings"
  - "WARNING-2 doc-nit fixed (stale skp:wf:{id}:root corrected to flat skp:{id})"
  - "scripts/phase-21-close.ps1 triple-SHA close gate (Phase-21 banner)"
affects: [processor-milestone, future-l2-key-changes]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Forwarder-shim conversion: hoist duplicated logic to a shared leaf, leave thin delegating shims so call sites and internal-visibility surface stay byte-unchanged"
    - "Explicit :D GUID format specifier as the byte-identical canonical form across writer/reader"

key-files:
  created:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
    - scripts/phase-21-close.ps1
  modified:
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
    - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
    - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs

key-decisions:
  - "D-04: Root uses explicit :D format ($\"{prefix}{workflowId:D}\") — byte-identical to BOTH the writer's bare interpolation (default D) and the reader's prior :D form"
  - "D-03: delegation (forwarders) not call-site replacement — keeps the 4 call sites and internal-visibility surface byte-unchanged, no InternalsVisibleTo additions"
  - "T-21-02 accept: L2ProjectionKeys made public in the leaf (required for cross-assembly reuse); exposes only key-format logic, no secrets"

patterns-established:
  - "Forwarder-shim conversion preserves call sites + IVT surface while collapsing two hand-copies into one shared source of truth"

requirements-completed: [HARDEN-03]

# Metrics
duration: ~11min
completed: 2026-05-31
---

# Phase 21 Plan 01: v3.4.0 Closeout Hygiene (HARDEN-03) Summary

**Hoisted the flat L2 Redis key scheme into a shared public `L2ProjectionKeys` (Messaging.Contracts), converted the writer + reader key classes to byte-identical forwarders, fixed the stale L2-keyspace doc-nit, and authored the Phase-21 triple-SHA close gate.**

## Performance

- **Duration:** ~11 min (Tasks 1-3; Task 4 is the pending operator gate)
- **Started:** 2026-05-31T06:00:08Z
- **Completed (Tasks 1-3):** 2026-05-31T06:10:54Z
- **Tasks:** 3 of 4 executed (Task 4 = operator-only human-action checkpoint, NOT attempted)
- **Files modified:** 6 (3 created, 3 modified)

## Accomplishments

- Created shared `public static class L2ProjectionKeys` in `Messaging.Contracts.Projections` with all three builders (Root/Step/Processor); Root uses the explicit `:D` format. This is the single source of truth for HARDEN-03 — a future GUID-format/suffix change can no longer silently desynchronize writer and reader.
- Converted `RedisProjectionKeys` (writer) and `OrchestratorL2Keys` (reader) into thin forwarders delegating to `L2ProjectionKeys`. `internal` modifiers, names, namespaces, and method signatures unchanged; all four call sites byte-unchanged.
- Added golden `L2ProjectionKeysTests` (5 facts) pinning the shared class output to the same byte-exact strings the writer golden asserts. The pre-existing `RedisProjectionKeysTests` (writer golden) stayed GREEN — proving writer output byte-unchanged.
- Fixed WARNING-2: corrected two stale `skp:wf:{id}:root` XML-doc comments in `CorrelationPropagationE2ETests.cs` to the flat `skp:{id}` shape (prose-only, zero behavior change).
- Authored `scripts/phase-21-close.ps1` — a near-verbatim copy of `phase-20-close.ps1` with only the Phase-21 banner/operator text changed.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create shared L2ProjectionKeys + golden unit test** - `e9b2f66` (feat) — TDD: RED confirmed (CS0103, type absent) → GREEN (class added, 5 facts pass)
2. **Task 2: Convert writer + reader key classes to forwarders** - `31ee032` (refactor)
3. **Task 3: Fix WARNING-2 doc-nit + author phase-21 close script** - `0a296da` (docs)

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP + REQUIREMENTS)

## Files Created/Modified

- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (created) — shared public static L2 key builders Root/Step/Processor; Root uses `:D`; zero usings; file-scoped namespace.
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` (created) — 5 golden facts pinning byte-exact strings.
- `scripts/phase-21-close.ps1` (created) — triple-SHA close gate (psql \l + redis-cli --scan + rabbitmqctl list_queues), 3-consecutive-GREEN, Phase-21 banner.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` (modified) — now a forwarder delegating Root/Step/Processor to `L2ProjectionKeys`.
- `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` (modified) — now a forwarder delegating Root to `L2ProjectionKeys`.
- `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` (modified) — two XML-doc comments corrected to flat `skp:{id}` shape (no test logic touched).

## Decisions Made

- Followed plan verbatim; the plan's `<interfaces>`/`<action>` blocks supplied exact code. Root canonicalized to the explicit `:D` format (D-04), proven byte-identical to both prior forms by the writer golden + new shared golden.

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet build SK_P.sln -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (TreatWarningsAsErrors makes any warning fatal).
- `dotnet test --filter ~L2ProjectionKeysTests` — Passed! Failed: 0, Passed: 270 (includes the 5 new facts).
- `dotnet test --filter ~RedisProjectionKeysTests` (writer golden, post-forwarder) — Passed! Failed: 0, Passed: 270 → writer output byte-unchanged.
- 4 call sites (RedisProjectionWriter.cs, RedisL2Cleanup.cs, StartOrchestrationConsumer.cs, StopOrchestrationConsumer.cs) — `git diff --stat` empty (byte-unchanged).
- `CorrelationPropagationE2ETests.cs` whole-file grep for `skp:wf:` = 0 hits and for `:wf:{id}:root` = 0 hits; `git diff` touches only `///` doc lines (no `[Fact]`/`Assert`/method body).
- `scripts/phase-21-close.ps1` contains `rabbitmqctl`, `redis-cli --scan`, `psql`, `Phase 21` (and zero `Phase 20`), the canonical `$services` array, the Smell-A guard, and the 3-GREEN equality check.

## Deferred to Task 4 (operator gate)

- The real-stack `CorrelationPropagationE2ETests` (`<verify>` for Task 3) requires the full v3.4.0 compose stack up healthy. The executor environment did not have the live stack up, so this filtered real-stack E2E was NOT personally run. It is deferred honestly to the Task 4 operator gate, which runs the full suite (including this test) 3× GREEN. The doc-fix is prose-only with zero behavior change, and the solution builds zero-warning.

## Issues Encountered

None. The filtered `dotnet test --filter` runs the whole BaseApi.Tests assembly (no per-class isolation), so each filtered run exercised the full 270-fact suite — all GREEN.

## Task 4 — PENDING OPERATOR GATE (checkpoint:human-action, blocking)

**NOT attempted by the executor.** Task 4 requires bringing the full v3.4.0 docker compose stack up healthy and running `pwsh -File scripts/phase-21-close.ps1` on the operator host (docker up + docker exec snapshots are operator-only). The executor returned a structured human-action checkpoint to the orchestrator. On `approved`, the operator appends the three SHA values + Passed count to STATE.md's Phase 21 P01 close entry.

## User Setup Required

None - no external service configuration required (the close gate is an operator verification step, not service config).

## Next Phase Readiness

- HARDEN-03 implementation complete (single source of truth + forwarders + golden + doc-fix + close script). Ready for the operator triple-SHA close gate (Task 4). After gate exit 0 + operator approval, Phase 21 / v3.4.0 closeout is finalized.

## Self-Check: PASSED

All created files exist (L2ProjectionKeys.cs, L2ProjectionKeysTests.cs, phase-21-close.ps1, 21-01-SUMMARY.md) and all three task commits (e9b2f66, 31ee032, 0a296da) are present in git history.

---
*Phase: 21-v3.4.0-closeout-hygiene*
*Completed (Tasks 1-3): 2026-05-31*
