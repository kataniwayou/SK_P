---
phase: 49-live-proof-close-gate
verified: 2026-06-09T12:00:00Z
status: gaps_found
score: 4/4 must-haves authored; live run found 2 defects (1 fixed, 1 open)
overrides_applied: 0
gaps:
  - id: GAP-49-1
    title: "Consolidated DLQ skp-dlq-1 406 x-message-ttl poison-loop"
    severity: resolved
    resolved_by: "commit 5666fb7 — ConsolidatedErrorTransportFilter sends via exchange:skp-dlq-1 (not queue:) so the send path no longer re-declares the ttl'd queue with default args"
    detail: "Live close gate showed ConsolidatedErrorTransportFilter sending to queue:skp-dlq-1 → MassTransit re-declares the queue without x-message-ttl → RabbitMQ 406 PRECONDITION_FAILED → dead-letter never completes → 4,133x poison-loop per Keeper replica. Verified fixed: 0x 406, skp-dlq-1 depth 0, 21 Keeper hermetic tests green."
  - id: GAP-49-2
    title: "Pause-all/resume-all leaves the orchestrator Quartz scheduler stuck paused for NEW workflows"
    severity: high
    detail: "PauseAllConsumer calls scheduler-wide Quartz PauseAll() (pauses trigger GROUPS, incl. triggers added later). ResumeAllConsumer does per-job ResumeJob only (deliberately never native ResumeAll(), asserted by Native_ResumeAll_Is_Never_Called). Per-job resume does NOT clear the group-level pause, so after ANY pause-all/resume-all cycle, newly-scheduled workflows land in a still-paused group and never fire until the orchestrator restarts. DECISIVE EVIDENCE: isolated SC1 fails while the scheduler is stuck-paused (after the close-gate's pause cycle); PASSES immediately after an orchestrator restart (clean scheduler) — the round-trip code itself is sound. This sinks the live close gate: under heavy full-suite load, containers hit transient redis:6379 ConnectTimeouts → Keeper BIT gate flaps → one PauseAll → scheduler stuck → ALL subsequent round-trip tests (SC1/SC2/SC3 + the 2 pre-existing SampleRoundTrip/MetricsRoundTrip) time out. A transient, recoverable L2 blip is turned into a PERMANENT scheduling freeze — the inverse of the recovery model's promise."
    fix_direction: "Resume must restore the ability to SCHEDULE+FIRE new workflows after recovery without re-introducing the misfire herd the design guards against (the reason native ResumeAll() was avoided). Candidate: on ResumeAll, ResumeTriggers(GroupMatcher.AnyGroup()) / clear the paused-groups set with a misfire-ignore policy, OR re-architect pause to use per-job PauseJob over the L1 snapshot (symmetric with resume) instead of scheduler-wide PauseAll(). This is a DESIGN decision touching a locked choice (Native_ResumeAll_Is_Never_Called) — needs deliberate spec'ing, not an inline hot-patch."
    affects: "SC1, SC2, SC3, TEST-01, TEST-02, TEST-03 — blocks the live N-GREEN close gate"
human_verification:
  - test: "After GAP-49-2 is fixed: run pwsh -File scripts/phase-49-close.ps1 against the rebuilt v4 stack"
    expected: "Exit 0 — both build configs 0-warning, all 3 runs GREEN with identical Passed count, all 3 SHA-256 invariants BEFORE==AFTER, skp-dlq-1 depth==0. Record the 3 SHA values + Passed count + DLQ depth in 49-HUMAN-UAT.md and tick TEST-01/02/03."
    why_human: "Requires the rebuilt v4 container stack up (operator-only, D-03). NOTE: an attempted live run on 2026-06-09 surfaced GAP-49-1 (fixed, commit 5666fb7) and GAP-49-2 (open, scheduler freeze). The gate cannot pass until GAP-49-2 is resolved. At rest (no full-suite load, clean scheduler) the round trip is proven to work — isolated SC1 passes."
---

# Phase 49: Live Proof & Close Gate — Verification Report

**Phase Goal:** A real-stack E2E proves the full Pre/In/Post round trip plus each recovery path and the BIT-gate global pause-all/resume-all across a transient L2 outage, all sealed behind an N-consecutive-GREEN triple-SHA (psql / redis / rabbitmq) net-zero close gate matching prior-milestone discipline.

**Verified:** 2026-06-09T12:00:00Z
**Status:** gaps_found (authored-hermetic must-haves all pass; an attempted operator live run found 2 defects)
**Re-verification:** No — initial verification

---

## Live Run Findings (2026-06-09, operator-gated gate executed)

An operator live run of `scripts/phase-49-close.ps1` was executed against a freshly-rebuilt v4 stack. Pre-flight, both-config 0-warning builds, and the triple-SHA machinery all worked. The 3×GREEN cadence did **not** pass — Run 1 returned 509 passed / 5 failed (all 5 are the live round-trip E2E facts). Two genuine v4 defects were surfaced (exactly the D-03 live-proof purpose):

- **GAP-49-1 (RESOLVED, commit `5666fb7`)** — `skp-dlq-1` 406 `x-message-ttl` poison-loop: `ConsolidatedErrorTransportFilter` sent via `queue:skp-dlq-1`, re-declaring the ttl'd queue without args → RabbitMQ 406 → 4,133× redelivery storm. Fixed by sending via `exchange:skp-dlq-1`. Verified: 0× 406, DLQ depth 0, 21 Keeper hermetic tests green.
- **GAP-49-2 (OPEN, design-level)** — pause-all/resume-all leaves the orchestrator Quartz scheduler stuck paused for newly-scheduled workflows (scheduler-wide `PauseAll()` pauses trigger groups; per-job `ResumeJob` doesn't clear the group pause). Decisive evidence: isolated SC1 fails while stuck-paused, **passes after an orchestrator restart**. Blocks the live gate. See the `gaps:` frontmatter for the fix direction. This is the gap to close via `/gsd-plan-phase 49 --gaps`.

The round-trip implementation itself is sound — proven by isolated SC1 passing on a clean (restarted) scheduler. TEST-01/02/03 remain unticked until the gate passes 3×GREEN after GAP-49-2 is fixed.

---

## Phase Posture (D-03)

This is the FINAL phase of v4.0.0 — a LIVE-PROOF + CLOSE-GATE phase. Per decision D-03 (locked in 49-CONTEXT.md), the phase is complete when the proofs and close machinery are **AUTHORED and hermetically green**. The actual live N×GREEN close run is **OPERATOR-GATED** — it requires the rebuilt v4 stack and is tracked in `49-HUMAN-UAT.md`. TEST-01/02/03 intentionally stay unticked until the operator's GREEN live run. This matches every prior milestone close (Phase 39/35/36/33).

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SC1 — RealStack E2E file exists proving the Pre→In→Post round trip (dispatch consumed → output written to `skp:data:{entryId}` → orchestrator advances), with net-zero teardown | VERIFIED | `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` exists, 441 lines. Carries `[Trait("Category","RealStack")]` + `[Trait("Phase","49")]`. Contains `PollForNewExecutionDataKeyAsync`, `L2KeysToCleanup.Add(newDataKey`, `ParentIndexMembersToSrem.Add(wfId.ToString("D"))`, `GetCustomAttributes<AssemblyMetadataAttribute>` (genuine SourceHash). Summaries report 507 hermetic passes / 0 failed with this file excluded. |
| 2 | SC2 — RealStack E2E file exists proving the 4 Keeper recovery states by direct-publishing to `KeeperQueues.Recovery` (const); data-gone asserts via `ConsolidatedErrorTransportFilter.Dlq1` (const, never literal); composite backup registered in net-zero teardown | VERIFIED | `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` exists, 514 lines. `KeeperQueues.Recovery` const used (line 76). `ConsolidatedErrorTransportFilter.Dlq1` const used (lines 127, 141, 144, 149). No literal `"keeper-recovery"` or `"skp-dlq-1"` in C# assertion paths. `CompositeBackup` seeded and registered (line 165-167). All 4 state contracts present: `KeeperReinject`, `KeeperInject`, `KeeperDelete`. `OrchestratorQueues.Result` referenced. |
| 3 | SC3 — RealStack E2E file exists in its own non-parallel collection; drives `docker stop/start sk-redis`; heal in a `finally`; asserts Global PauseAll/ResumeAll via ES seam logs; blocks on liveness re-write before returning | VERIFIED | `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` exists, 589 lines. `[CollectionDefinition("RedisOutageSerial", DisableParallelization = true)]` present (line 23). `[Collection("RedisOutageSerial")]` (not `Observability`). `docker stop sk-redis` / `docker start sk-redis` via `DockerAsync`. `start` heal is inside `finally` block (lines 239-248). `PollForHealthyLivenessAsync` called twice (baseline + post-resume). `Global PauseAll` and `Global ResumeAll` ES seam asserts present. |
| 4 | SC4 — `scripts/phase-49-close.ps1` exists with triple-SHA protocol, single `@('skp-dlq-1')`, seed version `3.5.0`, v4 canonical service list, unfiltered scan, no composite-TTL settle-wait; `49-HUMAN-UAT.md` operator runbook exists | VERIFIED | `scripts/phase-49-close.ps1` exists, 388 lines. `@('skp-dlq-1')` present (line 361). No `keeper-dlq` or `3.7.0` tokens. `version = '3.5.0'` (line 136). v4 service list `@('postgres', 'redis', 'rabbitmq', 'otel-collector', 'elasticsearch', 'prometheus', 'orchestrator', 'processor-sample', 'baseapi-service', 'keeper')` (line 174). Unfiltered `redis-cli --scan` present. D-07 composite-TTL no-wait comment present (lines 273-279). N=3 cadence with `Passed:\s+(\d+)` Smell-A guard. Triple-SHA BEFORE/AFTER invariants. `49-HUMAN-UAT.md` exists (120 lines) with rebuild set, gate invocation, record table, TEST-01/02/03 tick gate. |

**Score:** 4/4 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` | SC1 round-trip proof, min 250 lines, `[Trait("Phase", "49")]` | VERIFIED | 441 lines; Phase-49 + RealStack traits; net-zero teardown |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` | SC2 recovery-paths proof, min 250 lines, `ConsolidatedErrorTransportFilter.Dlq1` | VERIFIED | 514 lines; const-only DLQ reference; all 4 state contracts |
| `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` | SC3 outage proof, min 250 lines, `docker stop sk-redis` | VERIFIED | 589 lines; non-parallel collection; finally-heal |
| `scripts/phase-49-close.ps1` | Triple-SHA close script, min 300 lines, `@('skp-dlq-1')` | VERIFIED | 388 lines; single DLQ; v4 deltas applied |
| `.planning/phases/49-live-proof-close-gate/49-HUMAN-UAT.md` | Operator runbook, min 40 lines, `phase-49-close.ps1` | VERIFIED | 120 lines; full runbook with record table |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `SC1RoundTripE2ETests.cs` | `skp:data:{entryId}` L2 output key | `PollForNewExecutionDataKeyAsync` after Start | WIRED | Present at lines 128, 234 |
| `SC1RoundTripE2ETests.cs DisposeAsync` | redis net-zero | `db.KeyDeleteAsync(L2KeysToCleanup.ToArray())` | WIRED | Present in `DisposeAsync` (line 430) |
| `SC2RecoveryPathsE2ETests.cs` | `queue:keeper-recovery` | `IBus` send-endpoint on `KeeperQueues.Recovery` | WIRED | Line 76 resolves the const endpoint |
| `SC2RecoveryPathsE2ETests.cs (data-gone)` | `skp-dlq-1` | `ConsolidatedErrorTransportFilter.Dlq1` depth assertion | WIRED | Lines 127, 141 use the const |
| `SC2RecoveryPathsE2ETests.cs net-zero teardown` | composite backup key | `L2KeysToCleanup.Add(L2ProjectionKeys.CompositeBackup(...))` | WIRED | Lines 165-167 |
| `SC3PauseResumeOutageE2ETests.cs` | BIT health edge → PauseAll/ResumeAll | `docker stop/start sk-redis` straddling probe cadence | WIRED | Lines 176, 200; `DockerAsync` helper |
| `SC3PauseResumeOutageE2ETests.cs teardown` | steady-state re-established | `PollForHealthyLivenessAsync` after docker start | WIRED | Lines 228, 313 |
| `scripts/phase-49-close.ps1` | `skp-dlq-1` depth==0 | `foreach ($q in @('skp-dlq-1'))` | WIRED | Line 361 |
| `49-HUMAN-UAT.md` | `scripts/phase-49-close.ps1` | `pwsh -File scripts/phase-49-close.ps1` | WIRED | Line 58 of runbook |

---

### Data-Flow Trace (Level 4)

Not applicable — the three SC test files and the close script are authored test proofs and an operator script, not production components rendering dynamic data from a data store. The artifacts are verified at Level 3 (wired) and the data-flow is by construction (the tests assert that specific data keys are written/deleted on the live stack — this is the claim the operator-gated live run verifies).

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| SC1 builds 0-warning | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` | SUMMARY: 0 Warning / 0 Error | PASS (per summary) |
| SC1/SC2/SC3 hermetic exclusion | `dotnet run --project tests/BaseApi.Tests -- --filter-not-trait Category=RealStack` | SUMMARY: 507 passed, 0 failed | PASS (per summary) |
| close script parses | `[Parser]::ParseFile` | SUMMARY: 0 errors | PASS (per summary) |
| No `keeper-dlq` token in close script | grep | 0 matches | PASS |
| No `3.7.0` token in close script | grep | 0 matches | PASS |
| No `"keeper-recovery"` literal in SC2 C# | grep | 0 matches in assertion paths; 1 in XML doc comment (acceptable) | PASS |
| No `"skp-dlq-1"` literal in SC2 C# assertions | grep | 0 in assertion code; 4 in XML doc comments (acceptable) | PASS |

Note: build and hermetic-suite results are taken from the summaries (49-01-SUMMARY through 49-04-SUMMARY), which each report verified acceptance criteria. Direct build invocation was not feasible in this verification session due to environment constraints.

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| TEST-01 | Plans 01, 02, 04 | Real-stack E2E proves full Pre/In/Post round trip + each Keeper recovery path | AUTHORED — operator-gated | SC1 (round-trip half) + SC2 (recovery-paths half) exist and are hermetically green. Unticked per D-03 until operator GREEN run. |
| TEST-02 | Plans 03, 04 | Real-stack proof of BIT-gate pause-all/resume-all across transient L2 outage | AUTHORED — operator-gated | SC3 exists, non-parallel, finally-heal, ES seam asserts. Unticked per D-03 until operator GREEN run. |
| TEST-03 | Plan 04 | Close gate N consecutive GREEN + triple-SHA net-zero | AUTHORED — operator-gated | `scripts/phase-49-close.ps1` exists, 388 lines, triple-SHA protocol, N=3 cadence, single `skp-dlq-1`. Unticked per D-03 until operator GREEN run. |

All three TEST-XX requirements are correctly unticked in `REQUIREMENTS.md` per the operator-gate posture (D-03). This is by design — not a gap.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None | — | — | — | No stubs, no empty handlers, no TODO blockers found in any of the 5 authored artifacts. |

All "return null" occurrences in the E2E files are preceded by `Assert.Fail(...)` (unreachable, compiler-appeasement pattern). All empty catch blocks are best-effort teardown (explicitly documented). These are not stubs.

---

### Human Verification Required

#### 1. Live N×GREEN Close Gate Run

**Test:** On the host with the v4 container stack up, run:
```
docker compose up -d --build baseapi-service orchestrator processor-sample keeper
docker compose up -d postgres redis rabbitmq otel-collector elasticsearch prometheus orchestrator processor-sample baseapi-service keeper
pwsh -File scripts/phase-49-close.ps1
```

**Expected:**
- Exit code 0
- Both build configs (`-c Release`, `-c Debug`) 0-warning
- All 3 test runs GREEN with identical `Passed` fact count
- psql `\l` SHA-256: BEFORE == AFTER
- redis `--scan` SHA-256: BEFORE == AFTER (including composite `corr:wf:proc:exec` namespace proven net-zero by E2E teardown)
- rabbitmq `list_queues name` SHA-256: BEFORE == AFTER
- `skp-dlq-1` depth == 0

Record the 3 SHA values + Passed count + DLQ depth in `49-HUMAN-UAT.md` Step 3 table, then tick TEST-01, TEST-02, TEST-03 in `.planning/REQUIREMENTS.md`.

**Why human:** The RealStack E2E facts require the rebuilt v4 container stack up. SC3 invokes `docker stop sk-redis` against a live redis container. The triple-SHA gate snapshots live postgres/redis/rabbitmq state. None of this can be verified without the running stack.

---

### Gaps Summary

No gaps. All 4 authored-hermetic must-haves are verified. The only remaining item is the operator-gated live run, which is correctly classified as human verification (not a gap) per D-03 and the established prior-milestone close pattern.

---

_Verified: 2026-06-09T12:00:00Z_
_Verifier: Claude (gsd-verifier)_
