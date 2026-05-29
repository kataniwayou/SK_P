---
phase: 12-redis-infra-composition-healthcheck-di-registration
plan: 08
subsystem: phase-close-gate
tags: [redis, phase-close, 3-green, sha256, invariant, phase-12]

# Dependency graph
requires:
  - phase: 12-01..12-07
    provides: full Phase 12 surface (CPM pin + compose redis tier + appsettings + AddBaseApiRedis DI + RedisFixture + dead-Redis health + composition facts) — 177 facts GREEN
  - phase: 03-ef-core-persistence-base
    provides: D-15 byte-identical psql \l SHA-256 invariant + D-18 3-consecutive-GREEN cadence
provides:
  - "scripts/phase-12-close.ps1 — PowerShell phase-close ritual (Windows-native)"
  - "scripts/phase-12-close.sh — Bash phase-close ritual (cross-platform / CI)"
  - "v3.3.0 dual-SHA-256 phase-close gate (psql \\l + redis-cli --scan) inherited by Phases 13-16"
  - ".planning/STATE.md Phase 12 close narrative + recorded baselines"
affects: [phase-13, phase-14, phase-15, phase-16]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Net-new scripts/ repo-root directory (RESEARCH No-Analog item) — first shell-script directory in the codebase"
    - "docker compose exec -T <service> over docker exec <container_name> — service-name form is name-stable when a service declares no container_name (postgres)"
    - "@(...) array-coercion guard around Select-Object -Unique under Set-StrictMode — -Unique returns a scalar when all inputs are identical"
    - "LC_ALL=C sort locale-lock (Bash) + Sort-Object -CaseSensitive (PowerShell) for byte-stable SHA-256 over redis-cli --scan output (RESEARCH Pitfall 6)"

key-files:
  created:
    - scripts/phase-12-close.ps1
    - scripts/phase-12-close.sh
  modified:
    - .planning/STATE.md

key-decisions:
  - "v3.3.0 psql \\l SHA-256 baseline rebased to 37b27e56… — the corrected docker compose exec -T capture framing differs byte-wise from the v3.2.0 docker exec method, so Phases 13-16 record 37b27e56… (NOT the historical 0d98b0de…0aac127); the BEFORE=AFTER invariant within a run held byte-identical"
  - "redis-cli --scan SHA-256 baseline = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 (SHA-256 of empty input) — empty keyspace is the correct steady state; RedisFixture SCAN-assert-zero dispose leaves zero residual test:cls-* keys"
  - "Both gate scripts FORBID FLUSHDB (D-10) and use SCAN-only enumeration — destroying parallel-class keys is never allowed"

patterns-established:
  - "Phase-close ritual as paired PowerShell + Bash scripts encoding 3-GREEN cadence + dual SHA-256 invariant + HEALTH byte-immutable + negative-EF-migration assertions"
  - "Live-gate-run-surfaced script bugs are Rule 1 fix-forward (commit d5d97e3), bundled before the operator approves the checkpoint"

requirements-completed: [TEST-REDIS-04]

# Metrics
duration: ~9min gate run (3×~2:54 GREEN) + finalization
completed: 2026-05-29
---

# Phase 12 Plan 08: Phase-Close Gate (PowerShell + Bash) + v3.3.0 Dual SHA-256 Invariant Summary

**Two paired phase-close scripts (`scripts/phase-12-close.ps1` + `.sh`) land the v3.3.0 close gate — 3-GREEN cadence + DUAL SHA-256 invariant (psql `\l` Phase 3 D-15 + redis-cli `--scan` net-new TEST-REDIS-04) + HEALTH-01..05 byte-immutable + negative-EF-migration assertions. Operator close-run exited 0 ("Phase 12 close gate PASSED.") at 177 facts × 3 deterministic runs; Phase 12 SHIPPED.**

## Performance

- **Duration:** ~9 min operator gate run (3 × ~2:54 GREEN) + finalization
- **Completed:** 2026-05-29
- **Tasks:** 3 (2 auto-scripts + 1 operator human-verify checkpoint)
- **Files created:** 2 (scripts/phase-12-close.ps1 + scripts/phase-12-close.sh)
- **Files modified:** 1 (.planning/STATE.md)

## Accomplishments

- **Task 1 — `scripts/phase-12-close.ps1`** (PowerShell phase-close ritual, Windows-native). Pre-flight compose health check (postgres/redis/elasticsearch/prometheus), BEFORE/AFTER SHA-256 capture of psql `\l` + redis-cli `--scan` (Sort-Object -CaseSensitive for ordinal stability), 3-GREEN `dotnet test` loop with identical-Passed-count assertion, dual SHA-256 BEFORE=AFTER assertions, negative-EF-migration assertion, HEALTH-01..05 byte-immutable git-diff assertion. No FLUSHDB (D-10). Commit `5cc962a`.
- **Task 2 — `scripts/phase-12-close.sh`** (Bash phase-close ritual, cross-platform/CI). Structural parity with the PowerShell version; `set -euo pipefail` strict mode; `LC_ALL=C sort` locale-lock (RESEARCH Pitfall 6) on the redis-cli `--scan` pipe; `sha256sum`-based hashing; same dual-invariant + HEALTH + negative-migration assertions. Commit `4475d25`.
- **Task 3 — operator human-verify checkpoint (gate=blocking) APPROVED.** The orchestrator ran the close ritual end-to-end; exit 0 with final line "Phase 12 close gate PASSED."

## Gate Results (operator close-run)

- **3-GREEN cadence:** 177 facts × 3 runs, deterministic (~2:54 each), identical Passed count.
- **psql `\l` SHA-256 (v3.3.0 baseline):** `37b27e562fe1b6c6544c3f44f375b30cca16bebbf4f4c358910c229605f41441` — BEFORE = AFTER byte-identical (Phase 3 D-15 invariant HELD).
- **redis-cli `--scan` SHA-256 (net-new TEST-REDIS-04 baseline):** `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` — BEFORE = AFTER, empty keyspace, zero residual `test:cls-*` keys (SCAN-assert-zero dispose discipline held).
- **No EF migrations generated** (negative schema-push assertion HELD).
- **HEALTH-01..05 byte-immutable** across the entire phase (D-05/D-06 HELD).

## v3.3.0 Baseline Note

The recorded psql `\l` SHA-256 `37b27e56…` differs from the v3.2.0 historical baseline `0d98b0de…0aac127` recorded by Phases 8/9/10/11. This is expected and benign: the corrected `docker compose exec -T postgres` capture framing (see Deviations) produces byte output that differs from the original `docker exec sk-postgres` capture method. The gate's real invariant — BEFORE = AFTER within a single run — held byte-identical. `37b27e56…` is therefore recorded as the canonical v3.3.0 psql baseline; Phases 13-16 record this baseline going forward.

## Deviations from Plan

### Auto-fixed Issues (gate-fix, committed by orchestrator during Task 3 close-run)

**1. [Rule 1 - Bug] `docker exec sk-postgres` → `docker compose exec -T postgres` (both scripts)**
- **Found during:** Task 3 operator close-run.
- **Issue:** The postgres service in compose.yaml declares no `container_name`, so the literal container `sk-postgres` does not exist (Compose-generated name is `sk_p-postgres-1`). `docker exec sk-postgres …` failed.
- **Fix:** Both scripts switched to the name-stable `docker compose exec -T postgres …` service-name form.
- **Files modified:** scripts/phase-12-close.ps1, scripts/phase-12-close.sh
- **Commit:** `d5d97e3`

**2. [Rule 1 - Bug] `Set-StrictMode` crash on `$distinctPassed.Count` (.ps1 only)**
- **Found during:** Task 3 operator close-run.
- **Issue:** `Select-Object -Unique` returns a SCALAR (not an array) when all 3 runs share an identical Passed count; under `Set-StrictMode -Version Latest`, accessing `.Count` on the scalar threw.
- **Fix:** Wrapped the result in `@(...)` to force array semantics.
- **Files modified:** scripts/phase-12-close.ps1
- **Commit:** `d5d97e3`

Both deviations were surfaced by the live gate run, fixed forward, and committed before the operator approved the checkpoint. Neither is architectural.

## ROADMAP Phase 12 Success Criteria (all GREEN)

1. **SC#1** — `docker compose ps` shows `sk-redis` healthy alongside postgres / elasticsearch / prometheus / otel-collector. ✓ (pre-flight health check passed)
2. **SC#2** — `AddBaseApiRedis` resolves at startup; `IConnectionMultiplexer` Singleton; `IDatabase` NOT registered; `RedisProjectionOptions` binds `Redis:*`. ✓ (BaseApiCompositionFacts + RedisProjectionOptionsBindingFacts pass in all 3 runs)
3. **SC#3** — Redis stopped → `/health/live` AND `/health/ready` both 200; HEALTH-01..05 source byte-identical. ✓ (HealthDeadRedis facts pass; gate git-diff assertion empty)
4. **SC#4** — `Phase8WebAppFactory` boots with `RedisFixture`; per-class KeyPrefix isolation; `DisposeAsync` SCAN-asserts zero residual. ✓ (RedisFixtureFacts pass; post-suite redis-cli `--scan` SHA-256 = empty-input hash)
5. **SC#5** — Phase-close gate extended; `redis-cli --scan` SHA-256 BEFORE=AFTER + `psql \l` SHA-256 BEFORE=AFTER. ✓ (script asserts; both invariants HELD)

## Task Commits

1. **Task 1: PowerShell phase-close ritual** — `5cc962a` (feat)
2. **Task 2: Bash phase-close ritual** — `4475d25` (feat)
3. **Gate-fix (Task 3 close-run):** `d5d97e3` (fix) — docker compose exec -T + @(...) array-coercion
4. **Finalization (this plan):** STATE.md close entry + SUMMARY + ROADMAP + REQUIREMENTS — docs commit (below)

## Phase 12 Closeout

Phase 12 SHIPPED — all 15 phase REQ-IDs closed (INFRA-REDIS-01..06, INFRA-COMP-01..04, TEST-REDIS-01..05). All 5 ROADMAP Success Criteria GREEN. 177 facts GREEN × 3 consecutive runs. The v3.3.0 `redis-cli --scan` SHA-256 invariant joins the v3.2.0 `psql \l` SHA-256 invariant as a phase-close gate that Phases 13-16 inherit. Phase 13 (OrchestrationService split + L3 fetch + L1 build) unblocked. v3.3.0 milestone progress: 1 of 5 phases complete (20%).

## Self-Check: PASSED

- FOUND: scripts/phase-12-close.ps1 (Task 1, commit 5cc962a)
- FOUND: scripts/phase-12-close.sh (Task 2, commit 4475d25)
- FOUND: .planning/phases/12-redis-infra-composition-healthcheck-di-registration/12-08-SUMMARY.md
- FOUND commit 5cc962a (Task 1), 4475d25 (Task 2), d5d97e3 (gate-fix)
