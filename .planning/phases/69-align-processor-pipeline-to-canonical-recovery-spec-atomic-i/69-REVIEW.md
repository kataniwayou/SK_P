---
phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i
reviewed: 2026-06-16T00:00:00Z
depth: standard
files_reviewed: 4
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
  - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 69: Code Review Report

**Reviewed:** 2026-06-16
**Depth:** standard
**Files Reviewed:** 4
**Status:** issues_found

## Summary

The phase collapses the three forward-Post L2 ops (index HSET + index KeyExpire + data StringSet) into one atomic Lua `ScriptEvaluateAsync`, routes its exhaustion to a single `SendKeeper(BuildInject(...))`, and gates the forward source-delete tail on a local `bool escalated` flag set at the inject site. The core mechanics are correct:

- The Lua script (`AtomicForwardWrite`) is **injection-safe** — it is a compile-time `const` with all user data flowing through `KEYS`/`ARGV`, never concatenated into script text (T-69-04 holds).
- The script ordering (HSET index slot → PEXPIRE whole-hash → SET data with PX) is byte-faithful to the former three ordered C# calls, and the `return 1` guarantees a non-null `RedisResult` so a clean run never false-greens an exhaust.
- TTLs are computed in C# (`Random.Shared`) and passed as ARGV — no RNG in Lua — preserving the Phase-68 TEST-06 index/data desync guard.
- The `escalated` flag control flow is sound: set `true` at the single inject site, the cleanup tail is gated on `!escalated`, and on the escalated path the index + input keys are deliberately left intact for the keeper/Recovery. `slot++` is correctly applied on the escalate branch (the slot was claimed).
- Retry/exhaustion routing matches the `RetryLoop` contract (exhaustion surfaces via `RetryOutcome.Succeeded == false`, not a throw), and `SendKeeper`/`SendResult` propagate send-exhaustion as designed.
- The test facts cover the happy atomic write, the atomic-write-exhaust inject, the GATE-01 cleanup skip, mixed channels, and both tail outcomes, with NSubstitute stubs that guard multiple `ScriptEvaluateAsync` / `KeyDeleteAsync` overload bindings against false-green unstubbed `Task` results.

No critical issues. Two warnings concern edge-case correctness (non-positive TTL → Lua server error semantics, and an asymmetry in how a Lua-side error is classified vs. a connection fault). Info items are maintainability notes.

## Warnings

### WR-01: Non-positive ExecutionDataTtl marshals to `SET ... PX 0` / `PEXPIRE 0`, a Lua server error that silently routes a healthy write to INJECT

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:95-99, 109-113, 301-302`

**Issue:** `SlotTtl()` computes `Random.Shared.Next(ttl, 2*ttl + 1)` and the data TTL is `executionDataTtl.TotalMilliseconds`, both derived from `ExecutionDataTtlSeconds`. If that option is ever 0 (or negative), `(long)SlotTtl().TotalMilliseconds` and `(long)executionDataTtl.TotalMilliseconds` become `0` (or negative). Inside the script, `PEXPIRE KEYS[1] 0` and `SET KEYS[2] v PX 0` raise a Redis server error ("invalid expire time"). That `RedisServerException` is caught by `RetryLoop`, exhausts after `limit` deterministic retries, and routes to `SendKeeper(BuildInject(...))` — i.e. a *configuration* fault is laundered as an *infra write exhaust*, escalating every completed item to the keeper indefinitely (the next redelivery hits the same config, re-escalates). Also note `Random.Shared.Next(0, 1)` always returns 0, so even a single zero-second TTL is deterministically fatal. The former separate-ops code had the same latent issue, but collapsing into one all-or-nothing script means a single bad ARGV now fails the entire index+data write rather than just one sub-op.

**Fix:** Guard the option at construction/startup (preferred) or clamp at the call site:
```csharp
private TimeSpan SlotTtl()
{
    var ttl = Math.Max(1, livenessOptions.Value.ExecutionDataTtlSeconds);   // floor at 1s
    return TimeSpan.FromSeconds(Random.Shared.Next(ttl, 2 * ttl + 1));
}
```
and apply the same `Math.Max(1, ...)` floor when building `executionDataTtl` in `RunAsync` (line 121). Best handled with a validated-options check (`ValidateDataAnnotations`/`Validate`) so the invariant is enforced once. No test currently exercises a non-positive TTL — consider a fact asserting the floor.

### WR-02: A deterministic Lua-side error (bad ARGV, OOM, script error) is indistinguishable from a transient connection fault and consumes the full retry budget before INJECT

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:293-313`

**Issue:** The atomic write wraps `ScriptEvaluateAsync` in `RetryLoop`, which catches `Exception` broadly (`RetryLoop.cs:18`). A transient `RedisConnectionException` benefits from retry; a *deterministic* `RedisServerException` (e.g. the WR-01 PX=0 case, a Lua runtime error, NOSCRIPT-then-reload edge, or a value too large) will re-throw identically on every one of the `limit` attempts, burning the budget with no chance of success before routing to INJECT. This is correct *fail-safe* behavior (no silent drop — spec §10 holds), but it means a permanent script-level defect manifests as latency + keeper-queue pressure rather than a fast, distinguishable failure. The doc comment at lines 305-308 frames the exhaust purely as "index- OR data-failure" (infra), which under-describes the deterministic-error path.

**Fix:** No behavioral change required for correctness (INJECT is the right safe terminal). Two low-cost improvements: (1) log at `Warning`/`Error` with the captured `write.Error` before the INJECT so a deterministic script error is diagnosable rather than silent —
```csharp
if (!write.Succeeded)
{
    logger.LogWarning(write.Error, "Atomic forward write exhausted; escalating to keeper INJECT (entryId={EntryId})", entryId);
    await SendKeeper(BuildInject(d, item, entryId), limit, ct);
    escalated = true;
    slot++;
    continue;
}
```
(2) Tighten the doc comment to acknowledge that a deterministic server-side script error also lands here. Optionally, classify `RedisServerException` as non-retryable in `RetryLoop` so it short-circuits the budget — but that is a cross-cutting change beyond this phase's scope.

## Info

### IN-01: `SlotTtl()` evaluated inline inside the ARGV array initializer obscures that an RNG call happens per write

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:301`

**Issue:** `(long)SlotTtl().TotalMilliseconds` is computed inline in the `RedisValue[]` initializer. It reads as a constant but is a fresh `Random.Shared.Next` per item per write attempt. Note this is *inside* the `RetryLoop` closure, so each retry attempt re-rolls the index TTL — harmless (any value in `[ttl,2ttl]` is valid and the write is idempotent on the slot), but non-obvious.

**Fix:** Hoist to a named local before the `RetryLoop` call for readability and a single roll per item: `var indexTtlMs = (long)SlotTtl().TotalMilliseconds;` then reference `indexTtlMs` in ARGV[4]. Cosmetic.

### IN-02: Test ARGV index assertions rely on positional `argv[3]`/`argv[4]` magic offsets with only a comment to map them

**File:** `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs:119-120`; `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs:77`

**Issue:** The facts assert `(long)argv[3]` (index TTL) and `(long)argv[4]` (data TTL) with inline comments mapping 0-based array index to 1-based Lua ARGV. If the ARGV order in `AtomicForwardWrite` is ever reordered, these positional reads silently read the wrong slot and could still pass `InRange`/`Equal` by coincidence (both TTLs share the `[300_000,600_000]` vs `300_000` ranges that overlap at 300_000). Low risk, but positional coupling across the prod/test boundary is fragile.

**Fix:** Acceptable as-is for a hermetic fact. If hardening is desired, assert on `keys`/`argv` length (`Assert.Equal(5, argv.Length)`) and assert `entryId`/`slot`/`data` ARGV slots too so a reorder is caught structurally rather than by a coincidence-prone range check.

### IN-03: `HappyTail_DeletesSource` asserts `ks.Length == 2` but the source step / Guid.Empty operand case is not re-asserted post-atomic-write

**File:** `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs:209-217`

**Issue:** The tail-DEL fact only covers the non-`Guid.Empty` source dispatch. `DeleteTerminalAsync` still issues the two-key array DEL with `ExecutionData(Guid.Empty)` as a harmless absent operand on a source step (documented at `ProcessorPipeline.cs:349`), but no forward fact in this file exercises the `SourceStep.IsSource` true-branch through to the gated tail under the new atomic-write path. Coverage gap, not a defect.

**Fix:** Optional: add a forward fact dispatching `Guid.Empty` (source step, `validatedData` skipped) with a completed item, asserting the atomic write still fires and the gated two-key DEL still runs with the `Guid.Empty` operand. Confirms GATE-01 + the source-step short-circuit interact correctly.

### IN-04: Doc comment on `AtomicForwardWrite` describes "data TTL ms (== ExecutionDataTtl)" but the value is whatever `executionDataTtl` was built with — keep the single-source-of-truth claim verifiable

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:91-92, 302`

**Issue:** The XML doc asserts ARGV[5] `== ExecutionDataTtl` and the index TTL derives from "the SAME `ExecutionDataTtlSeconds`". This is true today (both trace to `livenessOptions.Value.ExecutionDataTtlSeconds`), and is the explicit Phase-68 TEST-06 anti-desync guarantee. The risk is purely future drift: if someone later wires a separate index-TTL knob, the desync the comment promises cannot happen would silently return. No action needed now.

**Fix:** None required. Optionally add a one-line code comment at `SlotTtl()` (line 111) cross-referencing that both TTL sources MUST remain the single `ExecutionDataTtlSeconds` to preserve the TEST-06 guard, so a future edit is flagged at the point of change.

---

_Reviewed: 2026-06-16_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
