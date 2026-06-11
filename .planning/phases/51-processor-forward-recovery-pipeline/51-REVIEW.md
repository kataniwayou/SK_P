---
phase: 51-processor-forward-recovery-pipeline
reviewed: 2026-06-11T00:00:00Z
depth: standard
files_reviewed: 13
files_reviewed_list:
  - src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
  - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
  - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
  - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
findings:
  critical: 0
  warning: 2
  info: 4
  total: 6
status: issues_found
---

# Phase 51: Code Review Report

**Reviewed:** 2026-06-11T00:00:00Z
**Depth:** standard
**Files Reviewed:** 13
**Status:** issues_found

## Summary

Phase 51 lands the A18 slot-array forward/recovery pipeline: `EntryStepDispatchConsumer` is reduced to a thin metric + delegate shell, and `ProcessorPipeline` now owns the dispatcher (exist-check branch), the FORWARD pass (Pre/In/Post + inline source-delete tail), and the RECOVERY pass (HGETALL → per-slot classify → send-before-retire → REINJECT ⊻ delete). A new `SlotArrayOptions` carries the random whole-HASH TTL knobs, wired in the composition root.

The code is unusually well documented and the resilience routing (read→REINJECT, write→INJECT, delete→DELETE, send→propagate) is consistent across both passes. Test coverage is thorough and exercises each fault surface hermetically. No security issues and no correctness-breaking bugs were found.

Two warnings concern a latent crash on misconfigured TTL bounds and a subtle DI lifetime mismatch between the pipeline and its `BaseProcessor` collaborator. Four info items cover defensive/documentation polish. None block the phase.

## Warnings

### WR-01: `SlotTtl()` throws `ArgumentOutOfRangeException` when configured min > max

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:75-76`
**Issue:** `SlotTtl()` calls `Random.Shared.Next(min, max + 1)`. `Random.Next(minValue, maxValue)` throws `ArgumentOutOfRangeException` when `minValue > maxValue`. `SlotArrayOptions` (lines 19-24) binds `SlotArrayTtlMin`/`SlotArrayTtlMax` straight from operator config with no validation that `Min <= Max` and no `> 0` floor. A misconfiguration (e.g. `SlotArrayTtlMin=600`, `SlotArrayTtlMax=300`) makes the FIRST slot write inside the forward Post stage throw — and that throw escapes `RunForwardAsync` entirely (it is NOT inside a `RetryLoop` or try/catch), propagating up through `Consume` to the bus, after the allocation index has potentially already been written for prior items. This is a config-driven crash with partial-write side effects, not a clean fail-fast at startup.
**Fix:** Validate the range once at registration so a bad config fails fast and loudly rather than mid-pipeline. Add a `ValidateDataAnnotations`/`Validate` guard, e.g.:
```csharp
services.AddOptions<SlotArrayOptions>()
    .Bind(cfg.GetSection("Processor"))
    .Validate(o => o.SlotArrayTtlMinSeconds > 0
                && o.SlotArrayTtlMaxSeconds >= o.SlotArrayTtlMinSeconds,
        "SlotArrayTtlMin must be > 0 and <= SlotArrayTtlMax.")
    .ValidateOnStart();
```
Alternatively, clamp defensively in `SlotTtl()`:
```csharp
private TimeSpan SlotTtl()
{
    var min = slotOptions.Value.SlotArrayTtlMinSeconds;
    var max = Math.Max(min, slotOptions.Value.SlotArrayTtlMaxSeconds);
    return TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1));
}
```

### WR-02: `ProcessorPipeline` is `Scoped` but depends on `BaseProcessor` — verify the author seam's lifetime matches

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:86`
**Issue:** `ProcessorPipeline` is registered `AddScoped` (line 86) and takes a `BaseProcessor` (the author transform seam) as a constructor dependency (`ProcessorPipeline.cs:64`). The registration comment on line 84-85 asserts the pipeline's collaborators "are all already registered by the calls above / step 3b," but `BaseProcessor` itself is registered by the concrete author's `Program.cs`, not by `AddBaseProcessor`. If an author registers `BaseProcessor` as a singleton (a common choice for a stateless transform) while the pipeline is scoped, the pipeline correctly resolves it; but if `BaseProcessor` is registered scoped/transient and holds per-message state, a captive-dependency or state-bleed bug surfaces only at runtime under the MassTransit scope, which the hermetic facts (which `new` the pipeline directly) cannot catch. There is no compile-time or DI-validation guard that `BaseProcessor` is registered at all.
**Fix:** Document the required `BaseProcessor` lifetime contract at the `AddScoped<ProcessorPipeline>()` site, and consider enabling DI scope validation in the host (`ValidateScopes = true` / `ValidateOnBuild = true`) so a missing or mis-scoped `BaseProcessor` registration fails at build time rather than on first consume. At minimum add a comment cross-referencing where `BaseProcessor` must be registered by the author.

## Info

### IN-01: Recovery TTL-refresh is silently skipped when the retire write exhausts

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:153-160`
**Issue:** In `RunRecoveryAsync`, after a successful re-send the slot is retired (`HashSetAsync` → `Guid.Empty`); the D-06 whole-HASH TTL refresh (`KeyExpireAsync`) runs only inside `if (retire.Succeeded)`. When the retire exhausts, the comment on line 160 documents the intended "do nothing" (a future replay re-sends), which is correct per A18. This is intentional, but note the asymmetry vs. the forward Post path (line 262-266) where the TTL refresh likewise follows a successful alloc — both are fine, just worth confirming no path leaves a freshly-written HASH without any EXPIRE. No change required; flagging for reviewer awareness.
**Fix:** No code change. Optionally add a one-line comment confirming the retire-exhaust path deliberately leaves the existing TTL intact.

### IN-02: `Guid.Empty.ToString()` allocated per retire/alloc call

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:154`
**Issue:** `Guid.Empty.ToString()` is materialized inside the recovery retire loop (and the empty-marker comparison logic elsewhere). Minor and not a correctness issue; a `static readonly string` for the retired-slot sentinel would avoid repeated allocation and centralize the on-wire representation (the tests in `PipelineRecoveryFacts.cs:87,155` hardcode `(RedisValue)Guid.Empty.ToString()` to match, so a shared constant would also keep test and production in lock-step).
**Fix:**
```csharp
private static readonly RedisValue RetiredSlot = Guid.Empty.ToString();
```
Use `RetiredSlot` in both the retire write and the `Guid.Empty` parse-skip on line 131.

### IN-03: Dispatcher branch comment claims recovery "currently a stub" — stale doc

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:24-25`
**Issue:** The class-level dispatcher doc says "On a present marker the RECOVERY pass runs (plan 51-03 — currently a stub)," and line 96 carries "plan 51-03 lands this body." `RunRecoveryAsync` is now fully implemented (lines 114-176) and tested in `PipelineRecoveryFacts.cs`. The "currently a stub" / "lands this body" notes are stale and will mislead the next reader.
**Fix:** Update the XML doc on lines 24-25 and the inline comment on line 96 to reflect that recovery is implemented this phase.

### IN-04: `ResolveInstanceIdForHolder()` `?? Environment.MachineName` can never reach the final `Guid` fallback

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:185-189`
**Issue:** `Environment.MachineName` is non-nullable and never returns null (it throws `InvalidOperationException` only in pathological cases), so the trailing `?? Guid.NewGuid().ToString("N")` fallback is effectively dead. This is intentional drift-lock parity with three other copies (documented in the IN-03 drift guard on lines 178-183), so changing it here alone would break the lock-step contract. Flagging only so the dead branch is understood as deliberate, not an oversight. Out of scope to change unilaterally.
**Fix:** No change in isolation. If the four-copy precedence is ever refactored, drop the unreachable `Guid` fallback (or move it ahead of `MachineName`) across all four sites together.

---

_Reviewed: 2026-06-11T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
