---
phase: 40-keeper-recovery-hardening
verified: 2026-06-06T00:00:00Z
status: human_needed
score: 9/9 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run scripts/phase-39-close.ps1 three consecutive times after rebuilding baseapi-service, orchestrator, processor-sample, and keeper containers"
    expected: "3x GREEN, keeper-dlq depth==0 in every AFTER snapshot, triple-SHA net-zero (psql/redis/rabbitmqctl), Release+Debug 0-warning"
    why_human: "KHARD-02's determinism guarantee (DrainKeeperDlqUntilStablyEmptyAsync replaces the fragile Task.Delay) is only provable against the live compose stack with two running Keeper replicas that independently park at >10s apart. The KeeperRecovery_GivesUp_ParksToDlq test is [Trait Category=RealStack] and cannot run hermetically."
---

# Phase 40: Keeper Recovery Hardening — Verification Report

**Phase Goal:** KHARD-01 (bound the recover→reinject cycle with a per-H attempt cap so a persistent fault can no longer flood the stack), KHARD-02 (replace the fragile give-up E2E teardown Task.Delay(10s) with a deterministic poll-until-stably-empty keeper-dlq drain), KHARD-03 (collapse the two verbatim-duplicate Keeper fault-consumer bodies into one shared handler).
**Verified:** 2026-06-06
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Exactly one `KeeperRecoveryHandler` class owns the full recover/probe/reinject/park/pause/resume body | VERIFIED | `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — 149 lines, `public sealed class KeeperRecoveryHandler` with one generic `HandleAsync<T>` method. Both `FaultEntryStepDispatchConsumer.Consume` and `FaultExecutionResultConsumer.Consume` are single-line expression-body delegations to `handler.HandleAsync(...)`. |
| 2 | Both fault consumers delegate in one line; `recovery.RunAsync` no longer exists in either consumer | VERIFIED | `FaultEntryStepDispatchConsumer.cs:20-22` — one-line Task delegation; `FaultExecutionResultConsumer.cs:23-25` — one-line Task delegation. `grep recovery.RunAsync src/Keeper/Consumers/` = 0 matches. |
| 3 | `IExecutionCorrelated` exposes `string H { get; }` so the generic body reads `inner.H` through the bound | VERIFIED | `src/Messaging.Contracts/IExecutionCorrelated.cs:19` — `string H { get; }` added after `EntryId`. No edits to `EntryStepDispatch.cs` or `ExecutionResult.cs` (their existing `{ get; init; }` satisfies the interface). |
| 4 | `RecoverAttemptCap` (default 3) bounds the OUTER recover→reinject cycle per H; cap check is in exactly one place (the shared handler) | VERIFIED | `src/Keeper/ProbeOptions.cs:14` — `public int RecoverAttemptCap { get; set; } = 3;`. `src/Keeper/appsettings.json:32` — `"RecoverAttemptCap": 3` in the `"Probe"` section. `KeeperRecoveryHandler.cs:94` — `var cap = opts.Value.RecoverAttemptCap;`. Cap check is in `HandleAsync` only — one location. |
| 5 | The atomic INCR single-winner gate (`n == cap+1`) parks once, DELs the counter key, and never reinjects past cap | VERIFIED | `KeeperRecoveryHandler.cs:92-110` — `StringIncrementAsync` → `KeyExpireAsync(HasNoExpiry)` → `n > cap` guard → `n == cap+1` single-winner park + `KeyDeleteAsync(key)` + return → `n > cap+1` race-loser return. Exactly one `n == cap + 1` gate and one `KeyDeleteAsync(key)` confirmed by grep (1 count each). `ReasonProbeExhausted` on the probe-exhausted branch is untouched (1 count). |
| 6 | Hermetic cap proof: exactly cap reinjects then one idempotent park, counter DEL'd | VERIFIED | `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs` — `Cap_Honored_ExactlyCapReinjectsThenOnePark` drives the same H cap+1 times, asserts `Assert.Equal(cap, harness.Sent.Select<EntryStepDispatch>(ct).Count())` and `Assert.Single(harness.Sent.Select<Fault<EntryStepDispatch>>(ct))` and `Assert.False(fake.CounterKeyExists(...))`. `Cap_Idempotent_RaceCrossesCap_StillOnePark` verifies the atomic-INCR single-crossing primitive. No `[Trait("Category","RealStack")]`. Both facts confirmed by orchestrator (493 passed). |
| 7 | `DrainKeeperDlqUntilStablyEmptyAsync` replaces the fragile `Task.Delay(10s)` + one-shot purge in the give-up E2E teardown | VERIFIED | `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` — `GetKeeperDlqDepthAsync` (depth reader via mgmt API) and `DrainKeeperDlqUntilStablyEmptyAsync` (2s poll / 15s stably-empty window / 90s cap / `Assert.Fail` on timeout) are present. `Task.Delay(TimeSpan.FromSeconds(10)` = 0 matches (the fragile wait is gone). Teardown calls `await DrainKeeperDlqUntilStablyEmptyAsync(ct)` (3 occurrences: definition + 1 call + 1 in comment reference). `GetProperty("messages")` = 1 match (JSON depth parse). `Assert.Fail` = 6 matches (including the drain timeout loud failure). `PurgeKeeperDlqAsync` retained and called from inside the loop. |
| 8 | `scripts/phase-39-close.ps1` is unchanged (gate stays snapshot-only) | VERIFIED | Acceptance criterion confirmed by SUMMARY-03 and git diff evidence — the gate script was never staged or modified. Gate stays snapshot-only per Pitfall 2 design contract. |
| 9 | Full hermetic suite GREEN (493 passed / 0 failed) and Release build 0 Warning / 0 Error | VERIFIED | Orchestrator-reported: 493 passed / 0 failed / 0 skipped. Release build: 0 Warning / 0 Error (TreatWarningsAsErrors). Confirmed across all three plans. |

**Score:** 9/9 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs` | Single shared generic recovery body | VERIFIED | 149 lines; `public sealed class KeeperRecoveryHandler`; generic `HandleAsync<T> where T : class, IExecutionCorrelated`; cap check; both park branches present |
| `src/Messaging.Contracts/IExecutionCorrelated.cs` | `string H { get; }` hoisted onto interface | VERIFIED | Line 19: `string H { get; }` with doc comment (KHARD-03) |
| `src/Keeper/ProbeOptions.cs` | `RecoverAttemptCap` field, default 3 | VERIFIED | Line 14: `public int RecoverAttemptCap { get; set; } = 3;` with KHARD-01 comment |
| `src/Keeper/appsettings.json` | `RecoverAttemptCap: 3` in Probe section | VERIFIED | Line 32: `"RecoverAttemptCap": 3` in `"Probe"` object alongside DelaySeconds/MaxAttempts |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | `KeeperRecoverAttempts(h)` key factory | VERIFIED | Line 57: `public static string KeeperRecoverAttempts(string h) => $"{Prefix}keeper:attempts:{h}";` |
| `src/Keeper/Observability/KeeperMetrics.cs` | `ReasonRecoverCap = "recover_cap"` | VERIFIED | Line 129: `public const string ReasonRecoverCap = "recover_cap";` with KHARD-01 doc comment |
| `src/Keeper/Program.cs` | `AddSingleton<KeeperRecoveryHandler>()` registered beside `L2ProbeRecovery` | VERIFIED | Line 36: `builder.Services.AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>();` |
| `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs` | Hermetic cap proof (both facts, no RealStack trait) | VERIFIED | `Cap_Honored_ExactlyCapReinjectsThenOnePark` + `Cap_Idempotent_RaceCrossesCap_StillOnePark`; `RealStack` appears only in a doc comment, no `[Trait]` attribute |
| `tests/BaseApi.Tests/Keeper/FakeRedis.cs` | `_counters` dict + `StringIncrementAsync` + `CounterKeyExists` accessor + `KeyDeleteAsync` removes counter | VERIFIED | Lines 52, 134-135, 73, 143-144 respectively; `KeyExpireAsync` no-op stub present |
| `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` | `DrainKeeperDlqUntilStablyEmptyAsync` + `GetKeeperDlqDepthAsync`; `Task.Delay(10s)` removed; drain wired into teardown | VERIFIED | Lines 529-578 (both helpers); line 398 (teardown call); `Task.Delay(TimeSpan.FromSeconds(10)` = 0 matches |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `FaultEntryStepDispatchConsumer.Consume` | `KeeperRecoveryHandler.HandleAsync` | one-line expression-body delegation with `FaultTypeDispatch` + `queue:{ProcessorId:D}` | WIRED | `handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch, inner => new Uri($"queue:{inner.ProcessorId:D}"), context.CancellationToken)` |
| `FaultExecutionResultConsumer.Consume` | `KeeperRecoveryHandler.HandleAsync` | one-line expression-body delegation with `FaultTypeResult` + `queue:{OrchestratorQueues.Result}` | WIRED | `handler.HandleAsync(context, KeeperMetricTags.FaultTypeResult, inner => new Uri($"queue:{OrchestratorQueues.Result}"), context.CancellationToken)` |
| `KeeperRecoveryHandler.HandleAsync` (Recovered branch) | `skp:keeper:attempts:{H}` counter | atomic `StringIncrementAsync` + `KeyExpireAsync(HasNoExpiry)` gated on `n == cap+1` | WIRED | Lines 91-93; `L2ProjectionKeys.KeeperRecoverAttempts(inner.H)` call confirmed |
| `KeeperRecoveryHandler` | `L2ProjectionKeys.KeeperRecoverAttempts` | key factory call | WIRED | `var key = (RedisKey)L2ProjectionKeys.KeeperRecoverAttempts(inner.H)` at line 91 |
| `KeeperRecovery_GivesUp_ParksToDlq` teardown | `DrainKeeperDlqUntilStablyEmptyAsync` | replaces `Task.Delay(10s)` + one-shot purge | WIRED | `await DrainKeeperDlqUntilStablyEmptyAsync(ct)` at line 398; old `Task.Delay(10s)` is gone (0 matches) |
| `DrainKeeperDlqUntilStablyEmptyAsync` | rabbitmq mgmt API depth poll | `GET /api/queues/%2F/keeper-dlq` via `GetKeeperDlqDepthAsync` + re-purge each iteration | WIRED | Lines 529-548 (`GetKeeperDlqDepthAsync`) + lines 554-578 (`DrainKeeperDlqUntilStablyEmptyAsync` calling `PurgeKeeperDlqAsync` + depth check) |

---

### Data-Flow Trace (Level 4)

Not applicable for this phase — the deliverables are: a behavior-preserving refactor (KHARD-03), a Redis counter-backed cap check (KHARD-01), and an E2E test teardown helper (KHARD-02). There are no new UI components or data-rendering pipelines. The cap counter's atomic INCR → park flow is verified at Level 3 (wired) and hermetically tested (cap facts).

---

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| `KeeperRecoveryHandler` class exists and is substantive | Read `KeeperRecoveryHandler.cs` | 149 lines; `HandleAsync<T>` generic method with full recovery body | PASS |
| Both consumers are one-line delegators | Read both consumer files | 23 lines each; `Consume` is a single expression-body `=>` delegation | PASS |
| `RecoverAttemptCap` defaults to 3 in both code and config | Read `ProbeOptions.cs` + `appsettings.json` | Default 3 in both; Probe section in appsettings | PASS |
| Cap hermetic proof compiles and passes | Orchestrator: 493/0 hermetic suite | All tests passed; 2 cap facts confirmed in isolation | PASS |
| `Task.Delay(10s)` removed from E2E teardown | Grep `KeeperRecoveryE2ETests.cs` | 0 matches | PASS |
| Live close-gate 3× GREEN with rebuilt containers | Cannot verify without live stack | Requires live compose stack + container rebuild | SKIP (human needed) |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KHARD-01 | 40-02 | Recover→reinject cycle bounded by per-H cap; persistent fault parks once | SATISFIED | `RecoverAttemptCap` in `ProbeOptions` (default 3) + `appsettings.json`; atomic INCR single-winner gate in `KeeperRecoveryHandler`; `KeeperRecoverAttempts` key factory; hermetic proof in `KeeperRecoverCapTests` (2 facts, both GREEN) |
| KHARD-02 | 40-03 | E2E give-up teardown uses bounded poll-until-stably-empty drain (not fixed delay) | SATISFIED (static) + NEEDS LIVE PROOF | `DrainKeeperDlqUntilStablyEmptyAsync` implemented with correct knobs (2s/15s/90s/Assert.Fail); `Task.Delay(10s)` removed; `PurgeKeeperDlqAsync` retained and called from loop; gate script unchanged. Live 3× GREEN run is Manual-Only |
| KHARD-03 | 40-01 | Two fault consumers collapsed into one shared handler; no duplication | SATISFIED | `KeeperRecoveryHandler` is the single shared body; both `Consume` methods are one-line delegations; `FaultEntryStepDispatchConsumerDefinition` / `FaultExecutionResultConsumerDefinition` byte-unchanged; `IExecutionCorrelated.H` hoisted |

All three KHARD requirements are checked `[x]` in `REQUIREMENTS.md` (lines 72-74) and traced in the traceability table (lines 125-127).

---

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs:92-93` | `StringIncrementAsync` then `KeyExpireAsync` are two non-atomic round-trips (WR-01 from code review) | Warning | A crash between INCR and EXPIRE leaves an un-TTL'd counter key. Noted in 40-REVIEW.md as advisory. Does NOT block the KHARD-01 goal (the cap behavior is correct on the happy path; missed DEL self-cleans via 300s TTL when set). |
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs:108` | `DEL`-on-park resets the cap budget — persistent fault can re-acquire a fresh cap budget after TTL (WR-02 from code review) | Warning | The cap is a per-storm rate-limiter, not a permanent quarantine. Documented in 40-REVIEW.md as advisory. The phase goal states "converges to a single park" for a given storm window; the 300s TTL bounds re-acquisition. Does NOT block goal. |
| `src/Keeper/Recovery/KeeperRecoveryHandler.cs:99-108` | Cap-park `Send` failure leaves counter DEL'd, retry lands on `n > cap+1` no-op path (WR-03 from code review) | Warning | A failed DLQ Send + retry could silently drop the envelope. Advisory per 40-REVIEW.md. Does NOT block the goal for the non-failure path. |
| `tests/BaseApi.Tests/Keeper/FakeRedis.cs:137-138` | `KeyExpireAsync` is a no-op stub — no test verifies `ExpireWhen.HasNoExpiry` no-clobber semantics (IN-01) | Info | TTL no-clobber not hermetically pinned. Does NOT affect test correctness for the cap walk. |
| `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs:48-49` | `NewMetrics()` helper declared but never called (IN-04) | Info | Dead code. No behavioral impact. |
| `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` | `ReasonRecoverCap = "recover_cap"` not asserted in the interned-literal lock test (IN-03) | Info | A rename of the cap-park label would go undetected by the lock test. No behavioral impact. |

None of the three Warnings are blockers for the phase goal. All were flagged in 40-REVIEW.md as advisory durability/correctness notes. The phase goal — bound the flood, deterministic drain, eliminate duplication — is achieved. The warnings represent durability improvements that could be addressed in a follow-up.

---

### Human Verification Required

#### 1. Live close-gate: 3× GREEN with rebuilt containers

**Test:** Rebuild `baseapi-service`, `orchestrator`, `processor-sample`, and `keeper` container images from HEAD, bring up the full compose stack, then run `pwsh -File scripts/phase-39-close.ps1` three consecutive times.

**Expected:**
- Each run exits GREEN (GATE_EXIT=0)
- `keeper-dlq depth==0` in every BEFORE and AFTER snapshot across all three runs
- `triple-SHA net-zero`: psql `\l` hash, redis `--scan skp:*` hash, and `rabbitmqctl list_queues` hash are identical BEFORE and AFTER in each run
- Release+Debug 0-warning
- The bounded drain (`DrainKeeperDlqUntilStablyEmptyAsync`) tolerates both the probe-exhausted 2-replica late park (>10s apart) AND the Plan-02 `recover_cap` single-winner park without racing the AFTER snapshot

**Why human:** `KeeperRecovery_GivesUp_ParksToDlq` is `[Trait("Category","RealStack")]` — it requires a running compose stack with two Keeper replicas that independently exhaust the probe loop and park to `keeper-dlq` at different times (>10s apart). The determinism of `DrainKeeperDlqUntilStablyEmptyAsync`'s 15s stably-empty window can only be proven against this real multi-replica timing behavior. The hermetic suite cannot model two concurrent live Keeper replicas with real probe-loop timing. Per 40-VALIDATION.md: "A live KHARD-01 cap test is FORBIDDEN (would flood the stack ~67 cyc/s/replica) — KHARD-01 is proven hermetically only."

---

### Gaps Summary

No gaps blocking goal achievement. All nine observable truths are verified from source. The three code-review Warnings (WR-01/02/03) are advisory durability concerns that do not prevent KHARD-01/02/03 from satisfying their stated goals. The only outstanding item is the Manual-Only live close-gate proof for KHARD-02.

---

_Verified: 2026-06-06_
_Verifier: Claude (gsd-verifier)_
