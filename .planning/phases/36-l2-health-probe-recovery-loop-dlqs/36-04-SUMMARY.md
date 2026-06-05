---
phase: 36-l2-health-probe-recovery-loop-dlqs
plan: 04
subsystem: testing
tags: [realstack, e2e, keeper, l2-recovery, probe-loop, keeper-dlq, masstransit, redis, elasticsearch]

# Dependency graph
requires:
  - phase: 36-02
    provides: "L2ProbeRecovery bounded loop + both consumers re-inject-on-Recovered | park-to-keeper-dlq-on-GaveUp"
  - phase: 36-03
    provides: "consolidated skp-dlq-1 error transport in BaseConsole.Core (embedded in all 3 console images)"
  - phase: 33
    provides: "FaultRecoverySpikeE2ETests RealStack rig (SourceHash reflection, RealStackWebAppFactory, ArmWrongTypePoisonAsync, PollEsForLog/CountEsHitsAsync, net-zero teardown)"
  - phase: 35
    provides: "KeeperFaultIntakeE2ETests — the running-Keeper-container observe pattern (D-09)"
provides:
  - "KeeperRecoveryE2ETests RealStack E2E — live recover-both-paths (exactly-once re-inject) + give-up (keeper-dlq park) + net-zero skp:keeper:probe:* scan"
affects: [phase-39-close-gate, keeper, l2-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Deployed-container-observe RealStack E2E (D-09): the test induces the fault + controls L2 health; the RUNNING keeper container runs the probe loop + re-inject|park; in-test probes only OBSERVE (orchestrator-result re-inject + keeper-dlq park)"
    - "Give-up trip mechanics: poison the key the PROBE ITSELF reads (skp:data:{entryId}, a GET) to force loop exhaustion — distinct from the recover trip which poisons the processor dedup-gate flag[H]"
    - "Net-zero terminal queue: bind an in-test probe to queue:keeper-dlq so capturing == acking drains the parked envelope (keeper-dlq empty in both Phase-39 snapshots)"

key-files:
  created:
    - "tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs"
  modified: []

key-decisions:
  - "Recover dispatch path: trip flag[dispatchH] (processor dedup-gate GET) to publish Fault<EntryStepDispatch>; the deployed probe reads skp:data + writes skp:keeper:probe (NEITHER poisoned) so it recovers on its first clean iteration; clear the poison ~2s after the trip to simulate L2 return so the re-injected delivery succeeds and the receiver flag[H] gate collapses any dup → CountEsHitsAsync==1 (exactly-once)"
  - "Result re-inject path: synthetic Fault<ExecutionResult> with a CLEAN entryId (the live orchestrator result-hop trip is unreachable without orchestration started — spike-documented Pitfall-1 fragility); an in-test probe bound to queue:orchestrator-result catches the verbatim re-inject (same H + CorrelationId)"
  - "Give-up path: poison skp:data:{entryId} (the probe's OWN read) as a LIST → WRONGTYPE on every probe iteration → loop exhausts MaxAttempts → original Fault<T> envelope parked to keeper-dlq, caught + ack-drained by an in-test probe"
  - "Live-gated REQ-IDs (PROBE-03/04/05) left UNticked in REQUIREMENTS.md — live proof unobserved this session (35-03 precedent); the verifier/operator ticks on the GREEN live run"

patterns-established:
  - "skp:keeper:probe:* scratch family scanned BEFORE/AFTER in net-zero teardown (30s TTL self-cleans; deployed probe write-then-deletes inside the loop)"

requirements-completed: []  # PROBE-03/04/05 are LIVE-GATED — left unticked per the 35-03 operator-pending precedent

# Metrics
duration: ~18min
completed: 2026-06-06
---

# Phase 36 Plan 04: KeeperRecoveryE2ETests (live recover-both-paths + give-up) Summary

**RealStack E2E that drives Phase 36's deployed recovery engine end-to-end: a live L2 outage dead-letters a dispatch + a result to the running Keeper container, which probe-recovers + re-injects each verbatim with exactly-once downstream effect, OR — when L2 stays down past MaxAttempts — parks the original `Fault<T>` to `keeper-dlq`; net-zero `skp:keeper:probe:*` scratch scan.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-06T00:20:00Z
- **Completed:** 2026-06-06T00:38:00Z
- **Tasks:** 1 authored (Task 2 is the operator-gated human-verify checkpoint — auto-approved per the 33–35 do-not-block precedent)
- **Files modified:** 1 created

## Accomplishments
- **`KeeperRecovery_RecoversBothPaths` (PROBE-03 live):** trips a live `Fault<EntryStepDispatch>` (WRONGTYPE on `flag[dispatchH]`), the deployed keeper container probe-recovers + re-injects verbatim to `queue:{procId:D}`; clears the poison to simulate L2 return so the re-injected delivery produces its real effect; asserts **exactly-once** (`CountEsHitsAsync == 1`, the receiver `flag[H]` gate collapses the dup — PROBE-06). Second type: synthetic `Fault<ExecutionResult>` → re-inject verbatim to `queue:orchestrator-result`, caught by an in-test probe (same `H` + `CorrelationId`).
- **`KeeperRecovery_GivesUp_ParksToDlq` (PROBE-04 live):** poisons the probe's OWN `skp:data:{entryId}` read so the loop exhausts `MaxAttempts` → the container parks the original `Fault<T>` envelope to `keeper-dlq`; an in-test probe catches it (proving the envelope, not the bare inner, was parked) and **ack-drains** it → net-zero terminal queue.
- **Net-zero scratch scan:** the new `skp:keeper:probe:*` family is snapshotted BEFORE/AFTER (its 30s TTL self-cleans; the deployed probe write-then-deletes inside the loop); any straggler is registered in `L2KeysToCleanup`.
- **Hermetic-excluded-green:** `Category=RealStack` excludes the file from the hermetic suite (adds 0 hermetic tests); `dotnet build SK_P.sln -c Release` 0/0; full hermetic suite **467 passed / 0 failed** (unchanged from Plan 03 — zero regression).

## Task Commits

1. **Task 1: Author KeeperRecoveryE2ETests** - `1b0d7d9` (test)

**Plan metadata:** see the final docs commit (this SUMMARY + STATE.md + ROADMAP.md)

_Task 2 is the operator-gated human-verify checkpoint — no code commit; the runbook is recorded in Pending-Verification below._

## Files Created/Modified
- `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (731 lines) - RealStack sibling of `FaultRecoverySpikeE2ETests` / `KeeperFaultIntakeE2ETests`; two facts (recover-both-paths + give-up) observing the deployed keeper container's probe loop.

## Decisions Made
- **Recover-dispatch timing:** the deployed probe recovers on its first clean iteration (it reads `skp:data` + writes `skp:keeper:probe`, neither poisoned), so the poison is cleared ~2s after the trip (brief head-start so the fault fans out + the container picks it up before clear) to let the re-injected delivery succeed and the receiver gate collapse the dup.
- **Result re-inject is synthetic:** the live orchestrator result-hop trip is unreachable without orchestration started (spike-documented Pitfall-1 fragility); a synthetic `Fault<ExecutionResult>` with a clean `entryId` proves the second-type re-inject (bind + double-`.Message` unwrap on the deployed consumer + verbatim re-inject to `queue:orchestrator-result`).
- **Give-up trip targets the probe's own read:** unlike the recover trip (which poisons the processor dedup-gate `flag[H]`), the give-up trip poisons `skp:data:{entryId}` — the probe's FIRST L2 op each iteration — so the loop genuinely exhausts.
- **Live-gated REQ-IDs unticked:** PROBE-03/04/05 stay unticked in REQUIREMENTS.md (live unobserved this session, 35-03 precedent).

## Deviations from Plan

None - plan executed exactly as written. The plan's Task-1 `<action>` explicitly anticipated the D-06 synthetic `Fault<ExecutionResult>` fallback for the result hop and the give-up assertion via a bound probe consumer; both were used as authorized.

## Issues Encountered
None. Build 0/0, hermetic suite 467/467 on the first run.

## Pending-Verification — OPERATOR RUNBOOK (live recover/give-up; auto-approved human-verify gate)

The authored test is the deliverable. The LIVE GREEN run requires the rebuilt compose stack and is operator-gated (auto-approve-human-verify precedent, Phases 33–35). **Phase-39's 3×GREEN triple-SHA close gate is the authoritative live signal.** The live run was NOT observed this session (no Docker stack started).

1. **Rebuild ALL consoles** — `keeper` + `processor-sample` + `orchestrator` + `baseapi-service` MUST all be rebuilt (the Plan-03 BaseConsole.Core consolidated-error-transport change is embedded in every console image, and the keeper SourceHash must match this phase's code; a stale keeper runs the Phase-34 placeholder and never probes — Pitfall 5):
   ```
   docker compose up -d --build keeper processor-sample orchestrator baseapi-service
   ```
2. **One-time broker hygiene (Pitfall 1/2):** on the dev RabbitMQ, delete any pre-TTL `skp-dlq-1` and any orphan `{queue}_error` queues so the corrected `x-message-ttl` declaration takes effect and the snapshot is clean. Ensure `keeper-fault-recovery` (durable, enduring) is in the close-gate baseline.
3. **Wait for all containers healthy.**
4. **(Optional) shorten the give-up window:** the deployed default Probe is `DelaySeconds=5 × MaxAttempts=12 = 60s`, so `KeeperRecovery_GivesUp_ParksToDlq` waits ~60s+ for the loop to exhaust (the test poll window is 180s). Operators MAY set a small `Probe__MaxAttempts` (e.g. `2`) on the keeper container to shorten this.
5. **Run the RealStack facts:**
   ```
   dotnet test tests/BaseApi.Tests --filter "Category=RealStack&FullyQualifiedName~KeeperRecovery"
   ```
   Expected GREEN:
   - `KeeperRecovery_RecoversBothPaths`: dispatch re-inject downstream effect `CountEsHitsAsync == 1` (exactly-once); synthetic result re-inject caught on `queue:orchestrator-result` (verbatim `H` + `CorrelationId`).
   - `KeeperRecovery_GivesUp_ParksToDlq`: the original `Fault<ExecutionResult>` envelope arrives on `queue:keeper-dlq` (caught + ack-drained).
6. **Verify net-zero:** `redis-cli --scan` shows NO lingering `skp:keeper:probe:*` (30s TTL self-cleans); the `keeper-dlq` parked message was ack-drained by the in-test probe (terminal queue empty); all run-minted `skp:data:*`/`skp:flag:*` + poison keys deleted via `L2KeysToCleanup`.
7. **(Optional, VALIDATION.md Manual-Only — PROBE-05 at-least-once) kill-mid-loop:** trip a fault, `docker kill` keeper while the loop is mid-await, observe redelivery + loop restart in logs/ES, confirm no message loss.

## Next Phase Readiness
- Phase 36 = **4/4 plans complete** (01–03 hermetic + 04 authored RealStack recover/give-up). The phase's bar (hermetic-green + one authored RealStack recover-both-paths/give-up pass) is met; the live GREEN run is operator-gated and Phase-39's close gate is authoritative.
- PROBE-03/04/05 code-complete (Plans 02/03) + hermetically proven; live half operator-pending (unticked in REQUIREMENTS.md).

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs`
- FOUND: `.planning/phases/36-l2-health-probe-recovery-loop-dlqs/36-04-SUMMARY.md`
- FOUND: commit `1b0d7d9` (test: KeeperRecoveryE2ETests)

---
*Phase: 36-l2-health-probe-recovery-loop-dlqs*
*Completed: 2026-06-06*
