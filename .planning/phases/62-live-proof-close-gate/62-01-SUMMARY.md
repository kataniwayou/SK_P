---
phase: 62-live-proof-close-gate
plan: 01
subsystem: infra
tags: [compose, docker, replicas, xunit-traits, close-gate, liveness]

# Dependency graph
requires:
  - phase: 59-61
    provides: per-instance L2 keyspace + dual-loop writer + >=1-healthy orchestration-start gate + watchdog probe (the v7 liveness surface the 2-replica stack exercises)
  - phase: 58
    provides: the SC1/SC2/SC3 + GateAComposition RealStack regression suite (carried Phase=58 trait) + the phase-58-close.ps1 protocol this phase clones
provides:
  - "compose.yaml processor-sample tier at deploy.replicas:2 (no fixed container_name) — the live multi-replica subject for TEST-01"
  - "SC1/SC2/SC3 + GateAComposition retagged [Trait(\"Phase\",\"62\")] — the v5/v6 regression sealed forward into the Phase-62 close gate"
affects: [62-02, 62-03, phase-62-close-script, 62-HUMAN-UAT]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Multi-replica compose tier via deploy.replicas:2 + no container_name (mirrors the keeper tier in-file)"
    - "Milestone-close trait retag: flip [Trait(\"Phase\",\"NN\")] forward while preserving [Trait(\"Category\",\"RealStack\")] so the live gate keeps running the accumulated regression"

key-files:
  created: []
  modified:
    - compose.yaml
    - tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs

key-decisions:
  - "processor-sample reshaped to replicas:2 with exactly two edits (delete container_name, add deploy.replicas:2) — no ports block to remove (8082 is container-internal); processor-badconfig tier left untouched (reused as-is for D-05)"
  - "Retag scoped to the 58->62 trait/prose flip only; pre-existing historical 'Phase-55' prose/method-name references in SC1 left untouched (out of plan scope; the acceptance criterion is zero Phase-58 traits, satisfied)"

patterns-established:
  - "Pattern: multi-replica processor tier via deploy.replicas:2 mirroring keeper (no fixed container_name -> compose-generated replica names)"
  - "Pattern: forward trait-retag preserving RealStack category for milestone close-gate inclusion"

requirements-completed: [TEST-01, TEST-03]

# Metrics
duration: 2min
completed: 2026-06-13
---

# Phase 62 Plan 01: Compose Reshape + Regression Retag Summary

**processor-sample compose tier now runs TWO replicas (deploy.replicas:2, no fixed container_name) and the SC1/SC2/SC3 + GateAComposition RealStack regression suite is retagged `[Trait("Phase","62")]` into the v7.0.0 close gate.**

## Performance

- **Duration:** 2 min
- **Started:** 2026-06-13T16:21:44Z
- **Completed:** 2026-06-13T16:23:38Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Reshaped the `processor-sample` compose tier to `deploy.replicas: 2` (mirroring the `keeper` tier) and removed the fixed `container_name: sk-processor-sample`, so the default `docker compose up` brings up two distinct processor-sample replicas with compose-generated names — the honest v7 multi-replica liveness subject for TEST-01.
- Flipped `[Trait("Phase","58")]` → `[Trait("Phase","62")]` across SC1RoundTrip, SC2RecoveryPaths, SC3PauseResumeOutage, and GateAComposition, sealing the accumulated v5/v6 regression forward into the Phase-62 live close gate while preserving every `[Trait("Category","RealStack")]` (excluded from hermetic, included live).
- Confirmed valid compose YAML (`docker compose config -q` exit 0) and 0-warning builds in BOTH Debug and Release.

## Task Commits

Each task was committed atomically:

1. **Task 1: Reshape processor-sample to deploy.replicas:2 (D-01)** — `de40b89` (feat)
2. **Task 2: Retag SC1/SC2/SC3 + GateAComposition to Phase 62 (D-10)** — `4e9961d` (test)

## Files Created/Modified
- `compose.yaml` — processor-sample tier: deleted `container_name: sk-processor-sample`, added `deploy:\n  replicas: 2`. processor-badconfig tier unchanged.
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` — trait 58→62 (line 72) + comment + doc-comment prose (line 30)
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — trait 58→62 (line 76)
- `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — trait 58→62 (line 96) + doc-comment prose (line 20)
- `tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs` — trait 58→62 (line 53)

## Decisions Made
- **Exactly two compose edits.** Delete `container_name`, add `deploy.replicas:2`. No `ports:` block exists to remove (8082 is container-internal; both replicas share it harmlessly, same as keeper on 8083). `processor-badconfig` left untouched (reused as-is for D-05).
- **Retag scope.** Only the `58 → 62` trait + accompanying prose was flipped. Pre-existing historical `Phase-55` prose and the `..._Phase55()` method name in SC1 (the test's origin phase) are out of plan scope and were left as-is. The acceptance criterion — zero remaining `[Trait("Phase","58")]` — is satisfied (verified 0).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- The in-plan inline PowerShell verify one-liners could not run through the Bash tool (it strips `$`-prefixed variables). Resolved by writing the same verification logic to a temporary `.ps1` and invoking it via `powershell -File`. Tooling adaptation only — no change to the verification semantics; both task verifications passed (`PASS verify`).

## Verification Results
- **Task 1:** `processor-sample` matches `deploy:\n  replicas: 2`; `container_name: sk-processor-sample` absent; `replicas: 2` count = 3 (>= 2); `container_name: sk-processor-badconfig` still present; `docker compose config -q` exit 0.
- **Task 2:** `[Trait("Phase","62")]` count = 4; `[Trait("Phase","58")]` count = 0; `[Trait("Category","RealStack")]` count = 4 (preserved).
- **Build:** `dotnet build SK_P.sln -c Debug` and `-c Release` both 0 Warning / 0 Error.

## Next Phase Readiness
- The live multi-replica subject (2× processor-sample) and the Phase-62 close-gate regression set are ready for Plan 02 (the new GateKeyspace RealStack test + fabricated-key helper) and Plan 03 (the phase-62-close.ps1 clone + build gate + HUMAN-UAT).
- No blockers. No new code, no behavior change, no new threat surface.

## Self-Check: PASSED

All 5 modified files present; both task commits (`de40b89`, `4e9961d`) found in git history.

---
*Phase: 62-live-proof-close-gate*
*Completed: 2026-06-13*
