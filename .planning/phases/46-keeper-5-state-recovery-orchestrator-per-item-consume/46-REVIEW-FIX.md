---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
fixed_at: 2026-06-09T00:00:00Z
review_path: .planning/phases/46-keeper-5-state-recovery-orchestrator-per-item-consume/46-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 2
skipped: 1
status: partial
---

# Phase 46: Code Review Fix Report

**Fixed at:** 2026-06-09T00:00:00Z
**Source review:** .planning/phases/46-keeper-5-state-recovery-orchestrator-per-item-consume/46-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (WR-01, WR-02, WR-03 — the 4 Info findings are out of scope and untouched)
- Fixed: 2 (WR-01, WR-02)
- Skipped: 1 (WR-03 — intentional, see rationale)

Build: `dotnet build SK_P.sln` → 0 errors, 0 warnings.
Tests: `--filter-trait "Phase=46"` → 19 passed / 0 failed (18 prior + 1 new WR-01 fact).

## Fixed Issues

### WR-01: INJECT re-delivery can emit a duplicate StepCompleted (send-before-delete is not idempotent)

**Files modified:** `src/Keeper/Recovery/InjectConsumer.cs`, `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`
**Commit:** e63462a
**Applied fix (review Option B — order preserved):** Kept the locked INJECT order
`read composite → write L2[entryId] (no TTL) → Send StepCompleted → delete composite` (per ROADMAP success
criteria and 46-CONTEXT D-04/A2/B — NOT reordered). Changed ONLY the trailing composite delete from the
re-throwing `Guard(...)` path to a direct `RetryLoop.ExecuteAsync(() => Db.KeyDeleteAsync(composite), RetryLimit, ct)`
whose exhausted outcome is discarded (`_ =`) rather than re-thrown. Rationale: the Send above is the last
irreversible step; once it lands, a delete-only fault must NOT re-drive the delivery, which (with no
orchestrator dedup, D-07) would emit a SECOND StepCompleted and double-fan the DAG. The composite is a
redundant 2-day-TTL crash-backstop at that point — on the rare delete-exhaustion it falls back to its TTL and
the next partitioned CLEANUP GCs it. Added `using BaseConsole.Core.Resilience;` and updated the class XML doc
to record the best-effort semantics. Did NOT use Option A (delete-before-send), which would risk a LOST
completion if the Send subsequently faulted.

Test changes: the existing `Received.InOrder` write→send→delete assertion is unchanged (order preserved, still
green). Added a new `[Trait("Phase","46")]` fact
`Inject_delete_exhaustion_after_send_does_not_throw_or_redrive` that stubs `KeyDeleteAsync` to always fault,
then asserts `Consume` completes without throwing and that `Send` was received exactly once (no re-drive).

_Note: requires human verification — this is a deliberate idempotency/ordering behavior change; syntax + the
new fact pass, but a reviewer should confirm the best-effort semantics match the locked design intent._

### WR-02: GateWaitSeconds (300s in-Consume gate-wait) vs broker consumer_timeout coupling

**Files modified:** `src/Keeper/RecoveryOptions.cs`, `src/Keeper/Recovery/RecoveryConsumerBase.cs`
**Commit:** 07af903
**Applied fix (documentation only — no behavioral change):** Extended the XML doc on
`RecoveryOptions.GateWaitSeconds` with an explicit OPERATIONAL COUPLING note: a parked recovery `Consume`
holds its broker channel for up to `GateWaitSeconds`, which MUST remain below the deployed RabbitMQ
`consumer_timeout` (broker default 30 min); if an operator lowers `consumer_timeout` below it, a parked
recovery Consume is force-closed and the channel dropped. The note states the two configs live in different
systems and cannot be validated at build time — deliberately no runtime/broker assertion. Added a matching
comment at the linked-CTS site in `RecoveryConsumerBase.Consume` pointing back to the options doc. No code
logic changed.

## Skipped Issues

### WR-03: Hardcoded broker credentials in committed appsettings.json

**File:** `src/Keeper/appsettings.json:20-24`
**Reason:** Intentionally SKIPPED per phase fix guidance. The committed `RabbitMq.Username`/`RabbitMq.Password`
(`guest`/`guest`) are the env-overridable dev default — `cfg.Require("RabbitMq:Password")` already supports
env/secret-store override — and are shared identically by all three consoles for the local docker-compose
stack. Emptying or altering them would break local dev. This is a pre-existing shared dev default NOT
introduced by this phase, and `appsettings.json` is strict JSON (no inline comment annotation possible). There
is no safe automated change without breaking local compose dev.
**Recommendation:** Operator should confirm production deployments override `RabbitMq:Username`/
`RabbitMq:Password` via environment variables / secret store (the `cfg.Require` fail-fast path already
supports this).
**Original issue:** Committed `guest`/`guest` broker credentials are easy to carry into a non-dev environment
unchanged.

---

_Fixed: 2026-06-09T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
