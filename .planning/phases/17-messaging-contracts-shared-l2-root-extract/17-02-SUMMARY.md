---
phase: 17-messaging-contracts-shared-l2-root-extract
plan: 02
subsystem: messaging-contracts
tags: [contracts, l2-projection, refactor, using-swap, close-gate, masstransit]

# Dependency graph
requires:
  - phase: 17-messaging-contracts-shared-l2-root-extract (Plan 01)
    provides: "WorkflowRootProjection + LivenessProjection moved into Messaging.Contracts.Projections (public, byte-identical wire shape); BaseApi.Service -> Messaging.Contracts ProjectReference"
provides:
  - "All 8 consumers (3 production + 5 test) resolve the L2 root read-shape from Messaging.Contracts.Projections — single source of truth (MSG-CONTRACTS-04 closed)"
  - "Terminal close-gate evidence: zero-warning Release+Debug, 3x235 GREEN, dual-snapshot BEFORE=AFTER — proving the extract is behavior-preserving (SC#5)"
  - "Optional scripts/phase-17-close.ps1 (dual-snapshot 3-GREEN close gate, phase-16 analog with EF-migration + HEALTH-immutable arms stripped)"
affects:
  - "Phase 18 (BaseConsole.Core) — Orchestrator will read WorkflowRootProjection from this same Messaging.Contracts source"
  - "Phase 19 (Orchestrator + WebApi bus wiring) — confirms the shared L2 contract surface is locked"

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Behavior-preserving using-swap: re-import only, no logic/visibility/assertion edits; solution-wide dotnet build CS0246 is the miss-detection safety net"
    - "DUAL-snapshot close gate (psql \\l + redis-cli --scan SHA-256 BEFORE=AFTER) when no broker is wired — rabbitmqctl triple-SHA arm N/A"

key-files:
  created:
    - scripts/phase-17-close.ps1
  modified:
    - src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs
    - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/IdempotencyFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs

key-decisions:
  - "All 8 consumers swapped to `using Messaging.Contracts.Projections;` (the Projections sub-namespace Plan 01 used for the two moved records — confirmed against 17-01-SUMMARY.md), not the flat root"
  - "Test csproj got NO new ProjectReference to Messaging.Contracts — public types flow transitively via the BaseApi.Service ProjectReference (Pitfall 2)"
  - "Existing `using BaseApi.Service.Features.Orchestration.Projection;` KEPT in the 5 test files (StepProjection/ProcessorProjection still live there)"
  - "DUAL-snapshot gate (no rabbitmqctl arm) — no broker wired this phase (RESEARCH line 492)"

patterns-established:
  - "Using-swap as behavior-preserving refactor: the byte-identical wire shape is decoupled from C# type identity by the [JsonPropertyName] pins, so the move changes no stored value"
  - "Operator-approved blocking human-verify close gate: present 3-GREEN fact count + dual SHA-256 pairs + CPM-pin grep + moved-record spot-check, then append evidence to STATE.md"

requirements-completed: [MSG-CONTRACTS-04]

# Metrics
duration: ~12min
completed: 2026-05-30
---

# Phase 17 Plan 02: Using-swap consumers + terminal close gate Summary

**Behavior-preserving using-swap of all 8 consumers of the moved L2 root read-shape to `Messaging.Contracts.Projections`, proven by zero-warning Release+Debug + 3x235 GREEN + dual-snapshot BEFORE=AFTER — closing MSG-CONTRACTS-04 and satisfying cross-cutting SC#5.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-05-30T02:25:57+03:00 (Task 1 commit)
- **Completed:** 2026-05-30 (Task 3 operator-approved)
- **Tasks:** 3 (2 auto + 1 blocking human-verify)
- **Files modified:** 8 consumers + 1 close-gate script

## Accomplishments

- All 8 consumers (3 production + 5 test) of `WorkflowRootProjection` / `LivenessProjection` now resolve them from `Messaging.Contracts.Projections` — the single source of truth (MSG-CONTRACTS-04 closed). The solution-wide `dotnet build` CS0246 safety net confirmed no consumer was missed, including the CONTEXT-omitted `RedisL2Cleanup.cs` + 4 extra test files (RESEARCH Pitfall 1).
- Terminal close gate ran clean: zero-warning Release **and** Debug (after `dotnet clean`), the `ProjectionRecordRoundTripTests` wire-shape guard GREEN (8 passed), and the full v3.3.0 suite GREEN **3x consecutively at the unchanged 235-fact baseline** — proving the extract dropped/added no test and changed no behavior.
- Dual-snapshot invariant held byte-identical BEFORE=AFTER (no schema change, no keyspace leak, no runtime state change).
- The operator approved the blocking human-verify checkpoint (Task 3 resume signal = "approved"), authorizing the phase close.

## Task Commits

1. **Task 1: Using-swap all 8 consumers to Messaging.Contracts.Projections + build** - `f1b05d0` (refactor)
2. **Task 2: Terminal gate — zero-warning Release+Debug, 3-GREEN, dual-snapshot BEFORE=AFTER + scripts/phase-17-close.ps1** - `ba26195` (test)
3. **Task 3: Phase 17 close checkpoint — operator confirms gate results** - operator-approved (no code change; verification gate)

**Plan metadata:** finalization commit (docs: complete plan — SUMMARY + STATE + ROADMAP + REQUIREMENTS)

## Files Created/Modified

- `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` - added `using Messaging.Contracts.Projections;` (internal sealed record, D-06 unchanged)
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` - added the using (internal sealed class, D-06 unchanged)
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` - added the using (the CONTEXT-omitted consumer; internal sealed class, D-06 unchanged)
- `tests/.../Projection/ProjectionRecordRoundTripTests.cs` - added the using; the SC#3 byte-identical wire-shape guard, kept GREEN
- `tests/.../HappyPathE2EFacts.cs`, `IdempotencyFacts.cs`, `StopCleanupFacts.cs`, `RedisProjectionWriterFacts.cs` - added the new using alongside the kept `using BaseApi.Service.Features.Orchestration.Projection;`
- `scripts/phase-17-close.ps1` - dual-snapshot 3-GREEN close-gate (phase-16 analog, EF-migration + HEALTH-immutable arms stripped)

## Verification Results (close gate — operator-approved)

- **3-consecutive-GREEN full v3.3.0 suite:** 235 / 235 / 235 Passed (== v3.3.0 baseline of 235; no facts dropped or added). Wire-shape guard `ProjectionRecordRoundTripTests`: 8 passed (MSG-CONTRACTS-04 / SC#3).
- **psql `\l` SHA-256 BEFORE == AFTER:** `37b27e562fe1b6c6544c3f44f375b30cca16bebbf4f4c358910c229605f41441` (v3.3.0 baseline; Phase 3 D-15 HELD).
- **redis-cli `--scan` SHA-256 BEFORE == AFTER:** `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` (SHA-256 of empty input — empty keyspace at rest, no leak).
- **Zero-warning Release + Debug** (after `dotnet clean`): both exit 0, 0 warnings (SC#5).
- **CPM-pin invariant (D-03):** no MassTransit PackageReference under `src/`.
- **Moved-record spot-check:** `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` is `public sealed record` in namespace `Messaging.Contracts.Projections` with all five `[property: JsonPropertyName(...)]` camelCase targets verbatim.

## Decisions Made

- Swapped to the `Messaging.Contracts.Projections` sub-namespace (not the flat root) — confirmed against 17-01-SUMMARY.md, which co-located the two moved records there so the nested `Liveness` ctor param resolves.
- No new test-csproj ProjectReference (transitive visibility via BaseApi.Service — Pitfall 2); existing `using BaseApi.Service.Features.Orchestration.Projection;` kept in the 5 test files.
- DUAL-snapshot gate (psql + redis only); the rabbitmqctl triple-SHA arm is N/A this phase (no broker wired — RESEARCH line 492).

## Deviations from Plan

None - plan executed exactly as written.

## Authentication Gates

None.

## Issues Encountered

None.

## Threat Surface

No new surface beyond the plan's `<threat_model>`. T-17-04 (wire-shape integrity through the swap) honored — `ProjectionRecordRoundTripTests` stayed GREEN (byte-identical proof) and the full integration suite exercised real write/read/cleanup round-trips. T-17-05 (residual Redis/psql state) honored — dual-snapshot SHA-256 BEFORE=AFTER byte-identical. T-17-06 (a missed consumer) honored — all 8 enumerated by path and the solution-wide build surfaced no CS0246. No threat flags.

## Requirements Satisfied

- **MSG-CONTRACTS-04:** the L2 root read-shape lives in `Messaging.Contracts` as the single source of truth; `BaseApi.Service` writes it and (future) `Orchestrator` reads it; no duplicated shape; wire shape byte-identical (proven by `ProjectionRecordRoundTripTests`).
- **SC#5 (cross-cutting):** zero-warning Release + Debug; v3.3.0 suite GREEN 3x consecutively at the unchanged fact count; dual-snapshot BEFORE=AFTER — the using-swap is behavior-preserving.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- The shared L2 contract surface is locked and proven behavior-preserving. Phase 18 (BaseConsole.Core) and Phase 19 (Orchestrator + WebApi bus wiring) can build against `Messaging.Contracts.Projections.WorkflowRootProjection` directly.
- Phase-level completion in ROADMAP/STATE frontmatter is left to the orchestrator post-verification; this plan completes only the PLAN-level tracking (17-02 = 2/2).

## Self-Check: PASSED

All key files (3 production consumers + scripts/phase-17-close.ps1 + this SUMMARY) exist on disk; both task commits (f1b05d0, ba26195) present in git history.

---
*Phase: 17-messaging-contracts-shared-l2-root-extract*
*Completed: 2026-05-30*
