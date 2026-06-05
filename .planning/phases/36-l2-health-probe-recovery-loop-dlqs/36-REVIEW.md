---
phase: 36-l2-health-probe-recovery-loop-dlqs
reviewed: 2026-06-05T21:28:14Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs
  - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
  - src/Keeper/Consumers/FaultExecutionResultConsumer.cs
  - src/Keeper/ProbeOptions.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/L2ProbeRecovery.cs
  - src/Keeper/appsettings.json
  - src/Messaging.Contracts/KeeperQueues.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - tests/BaseApi.Tests/Keeper/FakeRedis.cs
  - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs
  - tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 36: Code Review Report

**Reviewed:** 2026-06-05T21:28:14Z
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Phase 36 wires the L2 health-probe recovery loop and the two-tier DLQ topology: a bounded
`L2ProbeRecovery` loop awaited inside the two Keeper `Fault<T>` consumers, the consolidated
`ConsolidatedErrorTransportFilter` (skp-dlq-1, TTL'd) installed bus-wide in `BaseConsole.Core`, and the
terminal `keeper-dlq` give-up sink. The implementation is well-structured, heavily documented, and the
hermetic + RealStack test coverage is thorough (probe read+write semantics, recover/give-up both paths,
ack-after-loop, DLQ consolidation, exactly-once via the receiver flag gate).

The design is sound — the probe loop correctly distinguishes infra faults (`RedisException` superset,
which includes WRONGTYPE `RedisServerException`) from genuine bugs (which are NOT caught), and the
re-inject/park dichotomy matches the documented recovery model. No critical bugs or security issues were
found. The findings below are correctness edges around cancellation timing and the give-up Send path, plus
minor quality observations. None block the phase.

## Warnings

### WR-01: Probe loop give-up does not surface broker shutdown / cancellation distinctly

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:23-39`
**Issue:** The loop catches `RedisException` and keeps looping. If Redis is genuinely down for the whole
window, `RunAsync` returns `GaveUp` and the caller parks to `keeper-dlq`. However, when the host is
shutting down (broker stop / `ct` cancelled), the `await Task.Delay(..., ct)` in the catch block throws
`OperationCanceledException`, which propagates out of `RunAsync` un-awaited-for and aborts the consume
mid-loop. That is acceptable (the delivery is redelivered on restart), but on the FINAL attempt
(`attempt + 1 < max` is false) there is no delay, so a Redis fault on the last attempt falls straight
through to `return GaveUp` even if cancellation was requested — the message is then parked to the DLQ
during shutdown rather than being redelivered. Behaviour is correct but subtle; the asymmetry between
"cancelled during delay → throw/redeliver" and "cancelled at last attempt → park anyway" is undocumented.
**Fix:** Add an explicit `ct.ThrowIfCancellationRequested();` at the top of the loop body (or before the
`return GaveUp`) so a shutdown during the final attempt redelivers rather than parking, and document the
intent:
```csharp
for (var attempt = 0; attempt < max; attempt++)
{
    ct.ThrowIfCancellationRequested();   // shutdown → redeliver, never park-on-cancel
    try { /* ... */ }
    catch (RedisException)
    {
        if (attempt + 1 < max)
            await Task.Delay(TimeSpan.FromSeconds(opts.Value.DelaySeconds), ct);
    }
}
```

### WR-02: Re-inject and DLQ-park Sends ignore probe `CancellationToken` semantics on the give-up path

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:49-64`, `src/Keeper/Consumers/FaultExecutionResultConsumer.cs:50-65`
**Issue:** On `GaveUp`, the consumer does `await context.GetSendEndpoint(...)` then `await dlq.Send(...)`.
If the probe loop gave up because Redis was down due to a broader infra outage, the broker may also be
degraded — a failing `Send` here throws, which (per the doc-comment) is intended to route through
Immediate(N) → DLQ-1. That is the documented design, but the park Send passes `context.CancellationToken`,
so a Send that races host shutdown surfaces `OperationCanceledException` rather than a fault — meaning the
ORIGINAL `Fault<T>` envelope is neither parked to `keeper-dlq` NOR moved to skp-dlq-1; it is simply
redelivered. This is recoverable (redelivery re-runs the whole probe loop) but means the give-up path is
not guaranteed terminal under shutdown. No code change strictly required, but the "park is terminal"
guarantee in the doc-comment overstates the actual behaviour during shutdown.
**Fix:** Either tighten the doc-comment to note the park is best-effort-terminal (redelivery re-probes on
shutdown), or — if a park must always complete — drain it on a token that is not the consume token. The
former is lower-risk and matches the existing redelivery model:
```csharp
// PROBE-04: park the ORIGINAL Fault<T>. On host shutdown this Send may be cancelled and the
// envelope redelivered (re-probed) instead of parked — park is terminal only under steady state.
```

### WR-03: `FakeRedis` half-open read does not consume the armed-failure counter symmetrically with writes

**File:** `tests/BaseApi.Tests/Keeper/FakeRedis.cs:84-99`
**Issue:** `OnRead()` and `OnWrite()` both call `TryConsumeFailure()` first. In the `SetFailuresBeforeUp(n)`
scenario, each probe iteration performs ONE read + ONE write + ONE delete = three ops, so `n` armed
failures are consumed across reads AND writes/deletes within a single iteration, not per-iteration. The
test `Probe_FailThenSucceed` arms `failuresBeforeUp = 2` and asserts `Recovered` within
`maxAttempts = 5` — this passes because the first iteration's read consumes failure #1 (throws → catch →
next attempt), then attempt 2's read consumes failure #2 (throws → catch), then attempt 3's read returns
Null (Up) and write/delete succeed. The mapping "2 failures = 2 failed attempts" holds ONLY because the
read is the first op and it throws before the write is reached. If a future test arms an odd count that
lands the auto-flip-to-Up between a successful read and its write, the iteration could half-succeed in a
way the comment ("fail N ops then recover") does not describe. The double is correct for the current
tests but the counter semantics are op-scoped, not attempt-scoped, which the doc-comment blurs.
**Fix:** Clarify the XML doc on `SetFailuresBeforeUp` that `count` is in PROBE OPS (read/write/delete), not
probe ATTEMPTS, since an attempt issues up to three ops:
```csharp
/// Arm "fail the next <paramref name="count"/> probe OPS (each read/write/delete counts as one),
/// then auto-recover to Up". Note an attempt issues a read + a write + a delete, so count is op-scoped.
```

## Info

### IN-01: Probe scratch key written with raw string `"1"`, delete result unchecked

**File:** `src/Keeper/Recovery/L2ProbeRecovery.cs:28-30`
**Issue:** `StringSetAsync(scratch, "1", ...)` followed by `KeyDeleteAsync(scratch)` — the delete's boolean
result is discarded. Since the goal is only to prove write+delete round-trip without a Redis exception, the
unchecked result is fine (a missing key on delete still proves connectivity). Noting for completeness: the
probe asserts "no exception" rather than "delete returned true", which is the correct PROBE-02 semantic but
is implicit.
**Fix:** None required. Optionally add a one-line comment that the delete's return value is intentionally
ignored (connectivity, not key-existence, is what is being probed).

### IN-02: `using System;` / `using System.Collections.Generic;` likely redundant under implicit usings

**File:** `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs:1-2`, `src/Keeper/Consumers/FaultExecutionResultConsumer.cs:1-2`
**Issue:** Both consumer files explicitly import `System` and `System.Collections.Generic`. If the Keeper
project has `<ImplicitUsings>enable</ImplicitUsings>` (the .NET 6+ default for new templates, and used
elsewhere — `L2ProbeRecovery.cs` and `Program.cs` rely on implicit `System`/`System.Threading`), these are
dead imports.
**Fix:** Remove the two explicit `using` lines if implicit usings are enabled project-wide (verify the
Keeper `.csproj`). Purely cosmetic.

### IN-03: Magic TTL literal repeated as comment vs computed value

**File:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:81`, `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs:157`
**Issue:** The 7-day TTL is expressed as `(int)TimeSpan.FromDays(7).TotalMilliseconds` in production and
re-asserted as the literal `604_800_000` in the test. The two agree, but the 7-day window itself is a magic
number with no shared constant — a future change to the DLQ-1 retention would require editing both sites.
**Fix:** Optionally hoist the retention window to a named constant alongside
`ConsolidatedErrorTransportFilter.Dlq1` (e.g. `public static readonly TimeSpan Dlq1MessageTtl =
TimeSpan.FromDays(7);`) and reference it from both the queue declaration and the test, so the value has one
source of truth.

### IN-04: `throw new InvalidOperationException("unreachable")` dead-code guard after `Assert.Fail`

**File:** `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs:434`, `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs:458`
**Issue:** Both poll helpers end with `Assert.Fail(...)` followed by `throw new
InvalidOperationException("unreachable")`. `Assert.Fail` does throw, so the trailing throw is unreachable at
runtime and exists only to satisfy the compiler's definite-assignment / return-path analysis. The inline
comments already explain this, so it is intentional and harmless.
**Fix:** None required. (xUnit's `Assert.Fail` returns `void`, not a never-type, so the guard is the
idiomatic workaround. Could be replaced with `[DoesNotReturn]`-aware patterns but not worth the churn.)

---

_Reviewed: 2026-06-05T21:28:14Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
