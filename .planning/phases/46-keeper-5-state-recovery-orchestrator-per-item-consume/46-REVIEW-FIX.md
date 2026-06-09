---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
fixed_at: 2026-06-09T00:00:00Z
review_path: .planning/phases/46-keeper-5-state-recovery-orchestrator-per-item-consume/46-REVIEW.md
iteration: 2
findings_in_scope: 7
fixed: 5
skipped: 2
status: partial
---

# Phase 46: Code Review Fix Report

**Fixed at:** 2026-06-09T00:00:00Z
**Source review:** .planning/phases/46-keeper-5-state-recovery-orchestrator-per-item-consume/46-REVIEW.md
**Iteration:** 2 (cumulative — full scope: Critical + Warning + Info)

**Summary:**
- Findings in scope: 7 (WR-01, WR-02, WR-03, IN-01, IN-02, IN-03, IN-04)
- Fixed: 5 (WR-01, WR-02, IN-01, IN-03, IN-04)
- Skipped: 2 (WR-03 intentional; IN-02 no-action / already-documented)

Build: `dotnet build SK_P.sln` -> 0 errors, 0 warnings.
Tests: `--filter-trait "Phase=46"` -> 19 passed / 0 failed; `--filter-class "*BreakerMetricsFacts"` -> 4 passed
(guards the IN-03 doc-only change to the test-referenced `ResultDeduped` counter).

> The two pre-existing E2E failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`) require a live
> RabbitMQ broker and are NOT regressions from this work.

## Fixed Issues

### WR-01: INJECT re-delivery can emit a duplicate StepCompleted (send-before-delete is not idempotent)

**Files modified:** `src/Keeper/Recovery/InjectConsumer.cs`, `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`
**Commit:** e63462a (iteration 1)
**Applied fix (review Option B — order preserved):** Kept the locked INJECT order
`read composite -> write L2[entryId] (no TTL) -> Send StepCompleted -> delete composite`. Changed ONLY the
trailing composite delete from the re-throwing `Guard(...)` path to a direct
`RetryLoop.ExecuteAsync(() => Db.KeyDeleteAsync(composite), RetryLimit, ct)` whose exhausted outcome is
discarded (`_ =`) rather than re-thrown — so a delete-only fault after a successful Send cannot re-drive the
delivery and double-fan the DAG (no orchestrator dedup, D-07). The composite is a redundant 2-day-TTL
crash-backstop; on the rare delete-exhaustion it falls back to its TTL and the next CLEANUP GCs it. Already
addressed in a prior pass — left as-is in iteration 2 (not redone).

### WR-02: GateWaitSeconds (300s in-Consume gate-wait) vs broker consumer_timeout coupling

**Files modified:** `src/Keeper/RecoveryOptions.cs`, `src/Keeper/Recovery/RecoveryConsumerBase.cs`
**Commit:** 07af903 (iteration 1)
**Applied fix (documentation only):** Extended the `RecoveryOptions.GateWaitSeconds` XML doc with an explicit
operational-coupling note (a parked recovery `Consume` holds its broker channel for up to `GateWaitSeconds`,
which MUST stay below the deployed RabbitMQ `consumer_timeout`), plus a matching comment at the linked-CTS site
in `RecoveryConsumerBase.Consume`. No logic change. Already addressed in a prior pass — left as-is.

### IN-01: REINJECT/INJECT used per-message ct for the broker Send (ProcessorPipeline uses CancellationToken.None)

**Files modified:** `src/Keeper/Recovery/InjectConsumer.cs`, `src/Keeper/Recovery/ReinjectConsumer.cs`
**Commits:** deaaf5a (InjectConsumer), a0e03f0 (ReinjectConsumer — rides with the IN-04 commit on the same file)
**Applied fix:** Changed the INNER broker Send token to `CancellationToken.None` to match the ProcessorPipeline
house style ("do not abort a broker send once started"):
- `InjectConsumer`: `ep.Send(completed, ct)` -> `ep.Send(completed, CancellationToken.None)`.
- `ReinjectConsumer`: `ep.Send(dispatch, ct)` -> `ep.Send(dispatch, CancellationToken.None)`.
The OUTER `Guard(..., ct)` token is unchanged in both, so the bounded RetryLoop still observes bus shutdown
between attempts. Added an alignment comment at each site. The L2 op tokens were NOT touched (only the broker
Sends).

### IN-03: OrchestratorMetrics.ResultDeduped declared but never incremented post-RETIRE-01

**Files modified:** `src/Orchestrator/Observability/OrchestratorMetrics.cs`
**Commit:** 39a654c
**Path taken: DOCUMENTED (not removed).** A whole-repo grep for `ResultDeduped` AND `orchestrator_result_deduped`
found a LIVE reference outside the declaration: `tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs`
(asserts `ResultDeduped` is non-null at :61 and drives it through a cardinality-guard MeterListener at :99).
Per the fix guidance, a referenced counter must NOT be removed. Instead the XML doc was rewritten to state the
counter is intentionally retained-but-dormant post-RETIRE-01 (the effect-first `flag[H]=="Ack"` dedup gate was
removed when `TypedResultConsumer<T>` replaced `ResultConsumer`; the typed consumer is dedup-free by design,
D-07), kept as the meter slot for a possible future dedup feature, and emits no live series today. No
behavioral change; `ResultConsumed` and `DispatchSent` left untouched. `BreakerMetricsFacts` stays green.

### IN-04: REINJECT presence-gate pulled the full blob just to confirm presence

**Files modified:** `src/Keeper/Recovery/ReinjectConsumer.cs`, `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs`
**Commit:** a0e03f0
**Applied fix (corrected the review's suggestion):** The review proposed `KeyExistsAsync`, which would be a
REGRESSION — an empty-string key EXISTS, so `KeyExistsAsync` returns true and would fail to treat
empty-as-gone. Replaced `StringGetAsync` + `raw.IsNullOrEmpty` + `ToString()` with `StringLengthAsync`
(Redis STRLEN returns 0 for BOTH a missing key AND an empty value), preserving the exact absent-OR-empty
terminal semantics without transferring the blob over the wire:
```csharp
await Guard(async () =>
{
    if (await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0)
        throw new RecoveryDataGoneException();   // D-04 terminal — absent OR empty
    return true;
}, ct);
```
REINJECT-only — `InjectConsumer`'s read was NOT changed, since INJECT actually USES the blob (writes it to the
new entryId), so its `StringGetAsync` must stay. Updated `ReinjectConsumerFacts`: the present fact stubs
`StringLengthAsync` -> 10 (>0); the data-gone fact relies on NSubstitute's unstubbed `StringLengthAsync`
default of 0. Both facts keep `[Trait("Phase","46")]` and pass.

## Skipped Issues

### WR-03: Hardcoded broker credentials in committed appsettings.json

**File:** `src/Keeper/appsettings.json:20-24`
**Reason:** Intentionally SKIPPED. The committed `guest`/`guest` are the env-overridable dev default
(`cfg.Require("RabbitMq:Password")` supports env/secret-store override), shared identically across all three
consoles for the local docker-compose stack, and `appsettings.json` is strict JSON (no inline-comment
annotation possible). No safe automated change exists without breaking local dev. Not introduced by this phase.
**Recommendation:** Operator confirms production overrides `RabbitMq:Username`/`RabbitMq:Password` via env /
secret store (the `cfg.Require` fail-fast path already supports this).
**Original issue:** Committed `guest`/`guest` broker credentials are easy to carry into a non-dev environment
unchanged.

### IN-02: Murmur3 + SHA256 double-hash on the recovery partition key

**File:** `src/Keeper/Recovery/UpdateConsumerDefinition.cs:56-79`
**Reason:** NO behavioral change — the review explicitly says "None required." The existing XML doc already
explained the SHA256->Guid derivation is to satisfy the 8.5.5 Guid-keyed endpoint partitioner overload, but it
did NOT explicitly state that the Murmur3 layer is required-but-redundant (the `Partitioner` ctor takes an
`IHashGenerator` and re-hashes the key bytes). Per guidance ("only if NO such comment exists, add a one-line
note"), a one-line clarifying comment was added at the `Partitioner` construction site stating that BOTH hashes
are required by the API shape and neither should be "simplified" away. This is a comment-only no-action on
behavior — recorded as skipped (no logic change). Committed at 2d24cd7.
**Original issue:** The double-hash (`murmur3(guid.bytes) % PartitionCount`) is non-obvious; flagged so a
future maintainer does not remove one hash without understanding the API constraint.

---

_Fixed: 2026-06-09T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
