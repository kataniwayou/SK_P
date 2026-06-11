---
phase: 52-three-state-keeper
fixed_at: 2026-06-11T00:00:00Z
review_path: .planning/phases/52-three-state-keeper/52-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 52: Code Review Fix Report

**Fixed at:** 2026-06-11T00:00:00Z
**Source review:** .planning/phases/52-three-state-keeper/52-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (Critical + Warning; Info findings IN-01..IN-04 out of scope)
- Fixed: 3
- Skipped: 0

## Fixed Issues

### WR-01: `RecoveryEndpointHandle.Handle` is not `volatile` — cross-thread visibility gap

**Files modified:** `src/Keeper/Recovery/RecoveryEndpointHandle.cs`
**Commit:** 68c9f78
**Applied fix:** Replaced the plain auto-property `Handle` with a `volatile HostReceiveEndpointHandle? _handle` backing field plus a pass-through `get/set` property (review Option A). The write site in `RecoveryEndpointBinder.ExecuteAsync` (`holder.Handle = handle`) and the BitHealthLoop reads are unchanged — they now go through the volatile field, establishing the acquire/release fence so the binder's one-time store is promptly visible to the BIT loop's reader thread. Added a doc comment explaining the ECMA CLI memory-model rationale. Verified: `dotnet build src/Keeper/Keeper.csproj` succeeded (0 errors, 0 warnings).

### WR-02: `InjectConsumerFacts` — `Received.InOrder` chain does not include the send

**Files modified:** `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`
**Commit:** 35c6911
**Applied fix:** Made the write→send→delete three-way ordering explicit. Expanded the comment above `Received.InOrder` to state that NSubstitute's InOrder covers only the Redis substitute (locking write < delete) and that the send between them is captured by `CapturingSendProvider`. Added a belt assertion `Assert.Single(send.Sent)` immediately after the InOrder block to machine-lock that exactly one send was captured before the delete fires, so a future refactor dropping or reordering the send after the delete is caught. Used `Assert.Single` (not `Assert.Equal(1, ...)`) to satisfy the xUnit2013 analyzer. Verified: `dotnet build tests/BaseApi.Tests` succeeded (0 errors, 0 warnings).

### WR-03: `SC2RecoveryPathsE2ETests` — stale comment states gate-wait is still in `RecoveryConsumerBase.Consume`

**Files modified:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs`
**Commit:** 63e6bf3
**Applied fix:** Corrected both stale comments. (1) The class-summary "Gate-open precondition" comment (lines 44-45) no longer claims `RecoveryConsumerBase.Consume` awaits `gate.WaitForOpenAsync`; it now describes the Phase 52 D-04/D-09 model — a healthy RealStack keeps the BIT loop from `Stop()`ing the `keeper-recovery` endpoint, and the three consumers process at entry with no Consume-level gate-wait (gating is endpoint Stop/Start). (2) The in-body comment (lines 72-73) was fixed the same way and the incorrect "five-state" was corrected to "three recovery consumers (REINJECT, INJECT, DELETE)". Verified: `dotnet build tests/BaseApi.Tests` succeeded (0 errors, 0 warnings).

## Skipped Issues

None — all 3 in-scope (Warning) findings were fixed. The 4 Info findings (IN-01 GetSendEndpoint outside Guard, IN-02 per-access GetDatabase, IN-03 hardcoded guest credentials, IN-04 duplicated HostRedis constant) are out of scope for `fix_scope=critical_warning` and were not attempted.

---

_Fixed: 2026-06-11T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
