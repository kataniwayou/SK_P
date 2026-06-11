---
phase: 51-processor-forward-recovery-pipeline
fixed_at: 2026-06-11T00:00:00Z
review_path: .planning/phases/51-processor-forward-recovery-pipeline/51-REVIEW.md
iteration: 2
findings_in_scope: 6
fixed: 5
skipped: 1
status: partial
---

# Phase 51: Code Review Fix Report

**Fixed at:** 2026-06-11T00:00:00Z
**Source review:** .planning/phases/51-processor-forward-recovery-pipeline/51-REVIEW.md
**Iteration:** 2

**Summary:**
- Findings in scope: 6 (fix_scope=all — Critical + Warning + Info)
- Fixed: 5 (WR-01, WR-02 carried forward from iteration 1; IN-01, IN-02, IN-03 this pass)
- Skipped: 1 (IN-04 — deliberate four-copy drift-lock, out of scope to change unilaterally)

## Fixed Issues

### WR-01: `SlotTtl()` throws `ArgumentOutOfRangeException` when configured min > max

**Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** dcc989c (iteration 1 — verified still present at lines 108-113)
**Applied fix:** Replaced the plain `services.Configure<SlotArrayOptions>(cfg.GetSection("Processor"))`
registration with `services.AddOptions<SlotArrayOptions>().Bind(...).Validate(...).ValidateOnStart()`. The
guard asserts `SlotArrayTtlMinSeconds > 0 && SlotArrayTtlMaxSeconds >= SlotArrayTtlMinSeconds` with the
message "SlotArrayTtlMin must be > 0 and <= SlotArrayTtlMax." `ValidateOnStart()` turns a misconfigured TTL
range into a clean fail-fast at host build rather than the latent `Random.Next(min, max+1)`
`ArgumentOutOfRangeException` that would otherwise crash the FIRST forward-Post slot write with
partial-write side effects.

This is the registration-side guard the review recommended as the primary option (preferred over the
defensive in-method clamp). Re-verified this iteration: the fix is still in place and the Release build of
the full solution stays 0-warning / 0-error.

### WR-02: `ProcessorPipeline` is `Scoped` but depends on `BaseProcessor` — verify the author seam's lifetime matches

**Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** f9d2abf (iteration 1 — verified still present at lines 87-96)
**Applied fix:** Added a `WR-02` comment block at the `AddScoped<ProcessorPipeline>()` registration site
documenting the required `BaseProcessor` lifetime contract: `BaseProcessor` is registered by the concrete
author's `Program.cs` (not by `AddBaseProcessor`), and must be Singleton (the expected stateless-transform
choice) or Scoped — never a stateful Transient/per-call type that holds per-message state. The comment also
cross-references the recommended host-side mitigation (`ValidateScopes`/`ValidateOnBuild`, both on by default
in the .NET Host Development environment) so a missing or mis-scoped `BaseProcessor` registration becomes a
build-time failure rather than a first-consume runtime crash.

No DI registration or runtime behavior was changed. Re-verified this iteration: the comment block is still
in place and the Release build stays 0-warning / 0-error.

### IN-01: Recovery TTL-refresh is silently skipped when the retire write exhausts

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** f461162
**Applied fix:** Documentation-only. Added a one-line clarifying comment at the retire-exhaust path
(`RunRecoveryAsync`, after the retire-succeeded branch) confirming that on a retire exhaust the existing
whole-HASH TTL is deliberately left intact — no path leaves a freshly-written HASH without an EXPIRE,
because the HASH already carried a TTL from its forward-Post alloc write. No behavior change; the review
explicitly called for an optional comment only.

### IN-02: `Guid.Empty.ToString()` allocated per retire/alloc call

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** f461162
**Applied fix:** Extracted `private static readonly RedisValue RetiredSlot = Guid.Empty.ToString();` and
used it at the recovery retire write (replacing the inline `Guid.Empty.ToString()` in the `HashSetAsync`
call). The constant centralizes the on-wire retired-slot representation and avoids re-materializing the
string per retire.

Scope note: the review also mentioned using `RetiredSlot` at the `Guid.Empty` parse-skip on line 131. That
site parses `entry.Value.ToString()` into a `Guid` and tests `entryId == Guid.Empty`, which intentionally
canonicalizes non-canonical Guid forms before comparison. Swapping it to a raw `RedisValue` equality check
would change behavior (it would no longer normalize formatting), so the parse-skip was left as-is. Only the
write site — where byte-identity to the tests matters — was changed.

**Verification:** The `RetiredSlot` value stays byte-identical to the `(RedisValue)Guid.Empty.ToString()`
assertions hardcoded in `PipelineRecoveryFacts.cs:87,155`. Confirmed green: full Release build is
0-warning / 0-error, and the scoped MTP run `--filter-query "/*/*/PipelineRecoveryFacts"` passed 5/5.

### IN-03: Dispatcher branch comment claims recovery "currently a stub" — stale doc

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** f461162
**Applied fix:** Documentation-only. Updated the class-level XML doc (was "plan 51-03 — currently a stub")
to "implemented this phase — `<see cref="RunRecoveryAsync"/>`", and updated the inline comment at the
dispatcher recovery branch (was "plan 51-03 lands this body") to "RECOVERY pass (implemented this phase)".
`RunRecoveryAsync` is fully implemented and tested in `PipelineRecoveryFacts`, so the stale notes were
removed to avoid misleading the next reader.

## Skipped Issues

### IN-04: `ResolveInstanceIdForHolder()` `?? Environment.MachineName` can never reach the final `Guid` fallback

**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:185-189`
**Reason:** by-design, no change required. The trailing `?? Guid.NewGuid().ToString("N")` fallback is an
intentional drift-lock parity copy — the precedence expression is mirrored in FOUR sites (documented in the
IN-03 drift guard on lines 199-204) that must change in lock-step. The review explicitly states "No change
in isolation … Out of scope to change unilaterally." Changing this one site would break the lock-step
contract, so it is left untouched per the review's and the orchestrator's guidance.
**Original issue:** `Environment.MachineName` is non-nullable and never returns null, so the trailing `Guid`
fallback is effectively dead code — deliberate parity, not an oversight.

---

_Fixed: 2026-06-11T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
