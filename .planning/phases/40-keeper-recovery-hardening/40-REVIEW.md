---
phase: 40-keeper-recovery-hardening
reviewed: 2026-06-06T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
  - src/Keeper/Observability/KeeperMetrics.cs
  - src/Keeper/ProbeOptions.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/KeeperRecoveryHandler.cs
  - src/Keeper/appsettings.json
  - src/Messaging.Contracts/IExecutionCorrelated.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
  - tests/BaseApi.Tests/Keeper/FakeRedis.cs
  - tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
  - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
  - tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs
  - tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs
findings:
  critical: 0
  warning: 3
  info: 5
  total: 8
status: issues_found
---

# Phase 40: Code Review Report

**Reviewed:** 2026-06-06T00:00:00Z
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

Reviewed the Keeper recovery-hardening changes: the extracted shared `KeeperRecoveryHandler` (40-01), the per-`H` recover-attempt cap built on an atomic Redis `INCR` (40-02), and the poll-until-stably-empty `keeper-dlq` drain teardown (40-03). The delegation extraction is clean and faithful — both consumers are genuine one-liners and the supporting tests were updated consistently to register/inject the new handler. The metrics surface keeps bounded cardinality (no `workflowId`/`correlationId` labels) and the snake_case-no-suffix instrument naming is well guarded by `KeeperMetricsFacts`.

No Critical issues found. The cap logic is correct on the happy path and the single-winner park gate (`n == cap+1`) is sound. The Warnings concern durability of the counter-key under specific crash/race orderings: the `INCR` and `EXPIRE` are two non-atomic round-trips (a crash between them leaks an un-TTL'd counter key), the `DEL`-on-park resets the cap budget so a persistent fault can resume flooding after each park, and DLQ-`Send` failures on the cap-park path are unobserved. The Info items are minor maintainability/parity notes.

The atomicity and TTL concerns below are the load-bearing ones flagged in the phase brief; none rises to a crash/data-loss Critical, but they weaken the "no counter-key leak" and "bound the flood" guarantees the cap was added to provide.

## Warnings

### WR-01: `INCR` then `EXPIRE` is non-atomic — a crash between them leaks an un-TTL'd counter key

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:92-93`
**Issue:** The cap counter is created by `StringIncrementAsync` (line 92) and only *then* given a TTL by a separate `KeyExpireAsync(..., HasNoExpiry)` round-trip (line 93). These are two non-atomic operations. If the process (or the connection) dies after the `INCR` lands but before the `EXPIRE` is applied — or if the `EXPIRE` op itself faults — the key `skp:keeper:attempts:{H}` is left with a value in `1..cap` and **no expiry**. Such a key is never DEL'd (DEL only happens on the `n == cap+1` crossing, which this H may never reach again) and never expires, so it leaks permanently. The phase brief explicitly calls out "counter-key leak if DEL is missed" and "TTL handling" as the areas to scrutinise; this is the concrete leak path. The 300s TTL is the stated "crash net-zero net," but the net only attaches on a second round-trip that the crash window skips. Note the hermetic tests cannot catch this: `FakeRedis.KeyExpireAsync` is a hard-coded no-op success (`FakeRedis.cs:137-138`), so the TTL is never actually asserted to be set on the key.
**Fix:** Make the increment-and-expire atomic so the key can never exist without a TTL. Either set the expiry inside the same op the first time, or use a tiny Lua script:
```csharp
// Option A: atomic INCR + conditional EXPIRE via a single Lua eval (NX expiry on first write)
const string IncrWithTtl = @"
    local n = redis.call('INCR', KEYS[1])
    if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
    return n";
var n = (long)await db.ScriptEvaluateAsync(
    IncrWithTtl, new[] { key }, new RedisValue[] { (long)TimeSpan.FromSeconds(300).TotalMilliseconds });
```
This guarantees the key is born with a TTL atomically; the crash window between two round-trips disappears.

### WR-02: `DEL`-on-park resets the cap budget — a persistent fault can resume flooding after every park

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:108-110`
**Issue:** On the crossing increment (`n == cap+1`) the winner parks the envelope and `DEL`s the counter key (line 108). The cap exists to bound a persistent fault that keeps "recovering" then re-faulting (the MEMORY-recorded unbounded-reinject landmine). But because the counter is deleted on park, the *next* recover for the same `H` starts a brand-new counter at `n = 1` and is granted another full `cap` budget of reinjects before parking again. For a genuinely persistent (non-transient) fault this means the flood is throttled to bursts of `cap` reinjects punctuated by one park — it is rate-limited, not stopped. If the upstream keeps re-faulting the same `H`, the system oscillates: `cap` reinjects → park → DEL → `cap` reinjects → park → ... indefinitely. The cap caps a *single* recover storm but does not durably quarantine a permanently-broken `H`.
**Fix:** Decide explicitly whether the cap should be a permanent quarantine or a per-storm rate-limit, and document it. If permanent quarantine is intended, do NOT `DEL` on park — let the key persist (it will TTL out after 300s, which is the intended bounded reset window) so re-faults inside the window are immediately re-parked without re-injecting:
```csharp
// On park: keep the key (TTL already bounds it) so re-faults within the window re-park immediately
// instead of being granted a fresh cap budget. Drop the DEL; rely on the 300s TTL for eventual reset.
// (If a faster reset is wanted, shorten the TTL rather than DEL-ing.)
```
If the per-storm rate-limit IS the intent, add a code comment saying so, because "bound the OUTER recover→reinject cycle per H" (line 87) reads as a hard cap, and the DEL silently weakens it.

### WR-03: A failed `Send` to keeper-dlq on the cap-park path is unobserved and re-throws after the counter is already DEL'd

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:99-108`
**Issue:** On the cap-park branch the order is: `Send(context.Message)` to keeper-dlq (line 100), emit metrics (101-107), then `DEL` the counter (108). If the `Send` throws (broker hiccup / infra fault), the exception propagates out of `HandleAsync`, the message is NOT acked, MassTransit's `Immediate(N)` retry re-delivers it, and the whole `Recovered` branch runs again — including a fresh `INCR`. Because the first attempt failed *before* reaching the `DEL`, the counter is still live, so the retry's `INCR` yields `n = cap+2` (a race-loser value) and takes the `return` at line 110 **without parking** — the envelope is silently dropped (acked with neither reinject nor park). Conversely, on the give-up branch the same `Send`-then-return shape exists (lines 136-137) but there the doc-comment (134-135) explicitly accepts "infra → Immediate(N) → DLQ-1"; the cap-park branch has no such consideration and its interaction with the already-incremented counter makes a retry land on a non-parking path. The park metrics (lines 101-107) are also recorded *before* the `Send` is confirmed durable — a failed Send still increments `keeper_dlq_pushed`.
**Fix:** Park, then record metrics, then DEL — and make the park resilient so a retry still parks. Order the side effects so the durable action (the Send) completes before the counter is mutated/deleted, and gate the metric on Send success. At minimum, move the `DlqPushed.Add` to after the `Send` returns (it already is) but ensure the retry path re-parks: do not `return` on `n > cap+1` without confirming a park has occurred for this H — e.g. treat any `n >= cap+1` as "must be parked" and make the park idempotent against keeper-dlq rather than single-winner, since keeper-dlq is terminal and a duplicate park is drained by the Plan-03 stably-empty loop anyway.

## Info

### IN-01: `FakeRedis.KeyExpireAsync` stubs a no-op success, so no test exercises the no-clobber TTL semantics

**File:** `tests/BaseApi.Tests/Keeper/FakeRedis.cs:137-138`
**Issue:** `KeyExpireAsync` is wired to always return `Task.FromResult(true)` and does nothing to `_counters`. The `ExpireWhen.HasNoExpiry` "no clobber" semantics in `KeeperRecoveryHandler.cs:93` are therefore never asserted by any hermetic test — `KeeperRecoverCapTests` proves the INCR walk and the DEL-on-park no-leak, but the TTL behaviour (and the crash-window leak in WR-01) is invisible to the fake.
**Fix:** Have the fake track a per-key expiry flag and honour `ExpireWhen`, then add a fact asserting the TTL is set exactly once (first INCR) and not clobbered on subsequent INCRs. This also gives WR-01 a regression test once fixed.

### IN-02: `KeeperRecoverCapTests` second fact proves the Redis contract, not the handler

**File:** `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs:114-143`
**Issue:** `Cap_Idempotent_RaceCrossesCap_StillOnePark` drives `db.StringIncrementAsync`/`KeyDeleteAsync` directly (lines 128-142) rather than exercising `KeeperRecoveryHandler.HandleAsync` under concurrency. It proves "exactly one INCR returns cap+1" (a property of INCR itself) and manually performs the DEL (line 141). It does not prove the *handler* parks exactly once under a concurrent 2-replica race, which is the actual invariant. The first fact (sequential) covers the single-winner park; the concurrency claim in the test name is only verified at the primitive level.
**Fix:** If true concurrent-handler coverage is desired, drive two `HandleAsync` calls against a shared counter and assert exactly one `Fault<T>` reaches keeper-dlq. Otherwise rename the fact to reflect that it verifies the INCR single-crossing primitive, so the coverage claim is not overstated.

### IN-03: `KeeperMetricTags.ReasonRecoverCap` is asserted nowhere in the interned-literal lock test

**File:** `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs:164-178`
**Issue:** `Interned_Tag_Labels_Are_The_Locked_Literals` asserts every closed-enum literal except `ReasonRecoverCap` (`"recover_cap"`), which is the new Phase-40 label emitted at `KeeperRecoveryHandler.cs:102`. The other reason value (`ReasonProbeExhausted`) is locked at line 177 but the new sibling is not, so a rename/typo of the cap-park label would not be caught by the lock test (and the live E2E only asserts `probe_exhausted`, not `recover_cap`).
**Fix:** Add `Assert.Equal("recover_cap", KeeperMetricTags.ReasonRecoverCap);` to the locked-literals fact.

### IN-04: Unused `NewMetrics()` helper in `KeeperRecoverCapTests`

**File:** `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs:48-49`
**Issue:** The private `NewMetrics()` helper is declared but never called — the test wires `KeeperMetrics` through DI in `BuildHarness` (line 70) and the second fact uses no metrics at all. Dead code.
**Fix:** Remove the unused `NewMetrics()` method (also unused-and-present in `KeeperPausePublishTests.cs:45-46`, which similarly builds metrics inline — worth a sweep).

### IN-05: `recover_cap` give-up path does not emit `keeper-dlq` park observability parity with `probe_exhausted`

**File:** `src/Keeper/Recovery/KeeperRecoveryHandler.cs:101-107`
**Issue:** The cap-park branch records `DlqPushed{reason=recover_cap}` and `RecoveryDuration{outcome=gave_up}` but, unlike the probe-exhausted give-up branch, it is reached from *inside* the `Recovered` outcome (the probe succeeded). It therefore has already emitted `WorkflowPaused` at intake but will never emit `WorkflowResumed`, leaving a paused workflow with no resume signal — same terminal shape as give-up, which is correct, but there is no test asserting the cap-park path leaves the workflow paused (no resume) the way `KeeperPausePublishTests.GaveUp_PublishesPause_ButNoResume` does for the probe-exhausted path.
**Fix:** Add a hermetic fact driving the cap-park branch (Up Redis, pre-armed counter at cap) asserting `PauseWorkflow` was published but `ResumeWorkflow` was not, mirroring `GaveUp_PublishesPause_ButNoResume`, so the cap-park terminal contract is pinned.

---

_Reviewed: 2026-06-06T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
