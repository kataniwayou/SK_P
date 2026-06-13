---
phase: 58-orchestration-gate-integration-proof-close
fixed_at: 2026-06-13T00:00:00Z
review_path: .planning/phases/58-orchestration-gate-integration-proof-close/58-REVIEW.md
iteration: 1
findings_in_scope: 1
fixed: 1
skipped: 0
status: all_fixed
---

# Phase 58: Code Review Fix Report

**Fixed at:** 2026-06-13
**Source review:** .planning/phases/58-orchestration-gate-integration-proof-close/58-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 1 (Critical + Warning only; 6 Info findings deferred this pass)
- Fixed: 1
- Skipped: 0

## Fixed Issues

### WR-01: SC2 declares a DLQ-purge teardown channel that is never populated

**Files modified:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs`
**Commit:** 38d5128
**Applied fix:** Option (a) from the review — the safer choice for a close-gate fixture, and fully
behavior-preserving. Added a single defensive registration at the end of the STATE 2
("REINJECT data-gone") block, immediately after the silent-drop assertions:

```csharp
factory.BrokerQueuesToPurge.Add(ConsolidatedErrorTransportFilter.Dlq1);
```

This wires the previously-inert `BrokerQueuesToPurge` teardown channel (drained in `DisposeAsync` via
`PurgeQueueAsync`) so it is now populated and self-healing. Today the data-gone path is a BY-DESIGN
silent drop (D-06) — nothing lands in `skp-dlq-1`, so the purge is a no-op and the live-proven
net-zero close-gate behavior is unchanged. If a future contract change ever makes data-gone
throw → dead-letter, the parked message is now drained to net-zero locally instead of leaking to the
close gate's `skp-dlq-1` depth==0 invariant (~50min later) with no test-local signal.

This also resolves IN-01 as a side effect: `PurgeQueueAsync` is now reachable via a populated list
(no longer dead). IN-01 itself remains an Info finding and was not separately committed.

**Semantics preserved:** No test assertion was altered. The `factory` variable is the same
`RealStackWebAppFactory` instance declared at the method top (line 89), matching the existing STATE 1
usage `factory.BrokerQueuesToDelete.Add(originQueue)` (line 135). `ConsolidatedErrorTransportFilter.Dlq1`
is already the constant referenced by the STATE 2 before/after depth reads.

**Verification:**
- Tier 1: Re-read the modified STATE 2 block; fix present, surrounding assertions and DLQ depth
  reads intact.
- Tier 2: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → Build succeeded,
  0 Warning(s), 0 Error(s). The hermetic suite still compiles 0-warning. The RealStack/E2E tests
  were NOT executed (they require the live Docker stack), per the phase brief.

---

_Fixed: 2026-06-13_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
