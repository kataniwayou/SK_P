---
phase: 51-processor-forward-recovery-pipeline
fixed_at: 2026-06-11T00:00:00Z
review_path: .planning/phases/51-processor-forward-recovery-pipeline/51-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 51: Code Review Fix Report

**Fixed at:** 2026-06-11T00:00:00Z
**Source review:** .planning/phases/51-processor-forward-recovery-pipeline/51-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (Critical + Warning; the 4 Info findings IN-01..IN-04 are out of scope)
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: `SlotTtl()` throws `ArgumentOutOfRangeException` when configured min > max

**Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** dcc989c
**Applied fix:** Replaced the plain `services.Configure<SlotArrayOptions>(cfg.GetSection("Processor"))`
registration with `services.AddOptions<SlotArrayOptions>().Bind(...).Validate(...).ValidateOnStart()`. The
guard asserts `SlotArrayTtlMinSeconds > 0 && SlotArrayTtlMaxSeconds >= SlotArrayTtlMinSeconds` with the
message "SlotArrayTtlMin must be > 0 and <= SlotArrayTtlMax." `ValidateOnStart()` turns a misconfigured TTL
range into a clean fail-fast at host build rather than the latent `Random.Next(min, max+1)`
`ArgumentOutOfRangeException` that would otherwise crash the FIRST forward-Post slot write with
partial-write side effects.

This is the registration-side guard the review recommended as the primary option (preferred over the
defensive in-method clamp), so the operator gets a loud startup failure instead of silent clamping of a
bad config. Verified: Release build of the full solution stayed 0-warning / 0-error.

## Skipped Issues

None.

## Notes

WR-02 was classified by the project notes as potentially a documentation/scope-validation item rather than a
code-behavior fix. The applied change is documentation-only (a comment block at the
`AddScoped<ProcessorPipeline>()` site) and therefore carries no behavior-change risk:

### WR-02: `ProcessorPipeline` is `Scoped` but depends on `BaseProcessor` — verify the author seam's lifetime matches

**Files modified:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
**Commit:** f9d2abf
**Applied fix:** Added a `WR-02` comment block at the `AddScoped<ProcessorPipeline>()` registration site
documenting the required `BaseProcessor` lifetime contract: `BaseProcessor` is registered by the concrete
author's `Program.cs` (not by `AddBaseProcessor`), and must be Singleton (the expected stateless-transform
choice) or Scoped — never a stateful Transient/per-call type that holds per-message state. The comment also
cross-references the recommended host-side mitigation (`ValidateScopes`/`ValidateOnBuild`, both on by default
in the .NET Host Development environment) so a missing or mis-scoped `BaseProcessor` registration becomes a
build-time failure rather than a first-consume runtime crash.

No DI registration or runtime behavior was changed — enabling scope validation belongs to the author host,
not this library composition root, and changing `BaseProcessor`'s registration here is impossible (it is the
author's responsibility). The documentation + cross-reference is exactly the "at minimum" fix the review
prescribed. Verified: `BaseProcessor.Core` Release build stayed 0-warning / 0-error.

---

_Fixed: 2026-06-11T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
