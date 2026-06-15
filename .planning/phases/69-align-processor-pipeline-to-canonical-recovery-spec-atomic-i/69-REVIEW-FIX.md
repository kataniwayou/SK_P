---
phase: 69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i
fixed_at: 2026-06-16T00:00:00Z
review_path: .planning/phases/69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i/69-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 69: Code Review Fix Report

**Fixed at:** 2026-06-16
**Source review:** .planning/phases/69-align-processor-pipeline-to-canonical-recovery-spec-atomic-i/69-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (critical_warning — WR-01, WR-02)
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: Non-positive ExecutionDataTtl marshals to `SET ... PX 0` / `PEXPIRE 0`, a Lua server error that silently routes a healthy write to INJECT

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** 0976b41
**Applied fix:** Floored the TTL at 1 second at both call-site derivations of `ExecutionDataTtlSeconds`:
- `SlotTtl()` now reads `Math.Max(1, livenessOptions.Value.ExecutionDataTtlSeconds)` before the `Random.Shared.Next(ttl, 2*ttl + 1)` roll, so a non-positive (or zero) option can no longer produce `PEXPIRE 0`.
- `executionDataTtl` in `RunAsync` now uses `Math.Max(1, ...)` so the data key's `SET ... PX` can no longer marshal to `PX 0`.

Both TTL sources still trace to the single `ExecutionDataTtlSeconds`, preserving the Phase-68 TEST-06 index/data anti-desync guard (the floor is applied symmetrically, so the index TTL still strictly outlives the data TTL). Added explanatory comments at both sites cross-referencing the config-fault-laundering risk. Chose the call-site clamp over a validated-options annotation to keep the change narrowly scoped to this phase's file (no DI/registration changes).

### WR-02: A deterministic Lua-side error is indistinguishable from a transient connection fault and consumes the full retry budget before INJECT

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** b721833
**Applied fix:** Two low-cost diagnosability improvements per the review's suggestion (no behavioral change — INJECT remains the correct safe terminal):
1. Added `logger.LogWarning(write.Error, "Atomic forward write exhausted; escalating to keeper INJECT (entryId={EntryId})", entryId);` immediately before the `SendKeeper(BuildInject(...))` at the forward-Post exhaust site, so a deterministic script error surfaces the captured exception (`RetryOutcome.Error`) rather than being silent.
2. Tightened both the inline exhaust-site comment and the class-level XML doc to acknowledge that a deterministic server-side script error (bad ARGV / Lua runtime error / value-too-large) also lands on the INJECT path, not only a transient infra index-/data-failure.

Did NOT pursue the optional cross-cutting change (classifying `RedisServerException` as non-retryable in `RetryLoop`) — the review explicitly scopes that beyond this phase.

## Verification

- Tier 1 (re-read): confirmed for both fixes — fix text present, surrounding control flow (escalated flag, slot++, gated tail) intact.
- Tier 2 (build): `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release` succeeded with 0 Warnings / 0 Errors after each fix (TreatWarningsAsErrors is on).

---

_Fixed: 2026-06-16_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
