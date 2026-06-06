---
phase: 38-metrics-service-instance-labels
fixed_at: 2026-06-06T00:00:00Z
review_path: .planning/phases/38-metrics-service-instance-labels/38-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 38: Code Review Fix Report

**Fixed at:** 2026-06-06
**Source review:** .planning/phases/38-metrics-service-instance-labels/38-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (WR-01, WR-02 — fix_scope = critical_warning; the 4 INFO items were out of scope)
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: `_current` field published without a memory barrier; host-shutdown `Dispose` may observe the stale provider #1

**Files modified:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs`
**Commit:** 5e21496
**Applied fix:** The cross-thread `_current` field (written by `SwapTo` on the Loop A background-service
thread, read by `Dispose` on the DI/host-shutdown thread) is now published with a release/acquire barrier.
`SwapTo` publishes provider #2 via `Interlocked.Exchange(ref _current, next)` — a single atomic release-store
that also atomically takes the prior provider. `Dispose` reads via `Volatile.Read(ref _current)?.Dispose()`
(acquire). This guarantees the shutdown thread always observes the latest provider and never skips disposing
provider #2 (closing the OTLP gRPC channel leak under a SIGTERM-during-boot race), and makes the take-prior
atomic against any concurrent swap. The field comment + the `SwapTo`/`Dispose` XML-doc were updated to
document the new thread-safety guarantee.

### WR-02: prior provider leaks if `ForceFlush` throws; unhandled swap exception faults the BackgroundService

**Files modified:** `src/BaseProcessor.Core/Observability/MeterProviderHolder.cs`, `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs`
**Commit:** 5e21496 (holder-side `try/finally`), 95b64a2 (call-site degradation)
**Applied fix:** Two parts.
1. In `MeterProviderHolder.SwapTo`, the prior provider's best-effort flush is now wrapped
   `try { prior.ForceFlush(timeoutMilliseconds: 5000); } finally { prior.Dispose(); }`, so provider #1's
   reader/exporter/gRPC channel is ALWAYS released even if `ForceFlush` throws — eliminating the leak window.
   (This edit shares lines with the WR-01 `Interlocked.Exchange` publish, so it landed in commit 5e21496.)
2. At the swap call site (`ProcessorStartupOrchestrator.cs:~89`, Loop A), the `SwapTo(...)` call is now wrapped
   in `try { ... } catch (Exception ex) { logger.LogWarning(ex, "Metrics provider swap failed for hash {Hash};
   continuing on the placeholder service_name (non-fatal).", hash); }`. Identity has already resolved and the
   metric label is non-load-bearing for correctness, so a swap fault now degrades to "keep emitting on the
   placeholder provider + log a warning" instead of escaping `ExecuteAsync` and faulting the hosted service
   (default `BackgroundServiceExceptionBehavior.StopHost`). The existing `logger.LogWarning` idiom in that file
   was matched.

## Verification Evidence

**Build:** `dotnet build SK_P.sln -c Release` → **0 Warning(s), 0 Error(s)** (repo treats warnings as errors).
(One transient `CS1574` doc-cref error from an initial generic `cref` was corrected to plain `<c>...</c>`
references before the clean build.)

**Hermetic test — MeterProviderHolder facts:**
`BaseApi.Tests.exe --filter-class "*MeterProviderHolderFacts*"` → **Passed! total: 1, failed: 0, succeeded: 1**.
The fact proves the name swap, instance-id preservation, provider-#1 shutdown, and double-dispose idempotency —
the observable swap contract is unchanged; only the barriers + guaranteed dispose were tightened, so no test
assertions required adjustment.

**Regression — full Observability namespace:**
`BaseApi.Tests.exe --filter-namespace "BaseApi.Tests.Observability"` → **Passed! total: 28, failed: 0,
succeeded: 28, skipped: 0**. (Negative-path tests emit expected `rabbitmq://` unreachable-broker warnings; all
still pass.)

## Skipped Issues

None — both in-scope findings were fixed.

The 4 INFO findings (IN-01 discarded `ForceFlush` return; IN-02 single-shot `SwapTo` guard; IN-03 four-way
instance-id precedence duplication; IN-04 reinforcing comment on reading Name/Version from the message) were
out of scope (fix_scope = critical_warning) and were not addressed.

---

_Fixed: 2026-06-06_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
