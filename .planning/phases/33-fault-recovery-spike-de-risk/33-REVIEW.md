---
phase: 33-fault-recovery-spike-de-risk
reviewed: 2026-06-05T00:00:00Z
depth: standard
files_reviewed: 2
files_reviewed_list:
  - tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs
  - scripts/phase-33-close.ps1
findings:
  critical: 0
  warning: 0
  info: 3
  total: 3
status: issues_found
---

# Phase 33: Code Review Report

**Reviewed:** 2026-06-05
**Depth:** standard
**Files Reviewed:** 2
**Status:** issues_found (Info-only — no bugs, no security issues)

## Summary

Phase 33 is a de-risk SPIKE that proves the load-bearing Keeper assumption (publish a
`Fault<EntryStepDispatch>` / `Fault<ExecutionResult>`, catch it via pub/sub, double-`.Message`
unwrap, re-inject verbatim by type, and collapse the deliberate duplicate on the receiver's
surviving Phase-31 `flag[H]` dedup gate). Two files were reviewed: the RealStack E2E test and the
triple-SHA close gate.

I verified the correctness-critical seams against production source rather than trusting the
prose:

- **Dispatch poison key matches the real write key.** The test poisons
  `ExecutionData(HashBlob(DispatchTripPayload))`. `SampleProcessor.ProcessAsync`
  (`src/Processor.Sample/SampleProcessor.cs:31-38`) deserializes the JSON-quoted `config` back to
  the raw string and echoes it as `OutputData`, and the consumer writes
  `ExecutionData(HashBlob(r.OutputData))` (`EntryStepDispatchConsumer.cs:162,178-181`). So
  `r.OutputData == DispatchTripPayload` and the poisoned content address is exactly the key the
  output write targets — the WRONGTYPE trip will fire as intended.
- **`dispatch.Payload` reaches `config`.** Confirmed at `EntryStepDispatchConsumer.cs:130`
  (`processor.ExecuteAsync(inputData, dispatch.Payload, ct)`).
- **The output write is INFRA (no catch).** `EntryStepDispatchConsumer.cs:176-181` — a WRONGTYPE
  here propagates, exhausts Immediate(N), and publishes `Fault<EntryStepDispatch>` as the test
  relies on.
- **The a-priori result hash chain mirrors production.** Test lines 154-158 reproduce
  `EntryStepDispatchConsumer.cs:162,196-209` (blob -> manifest JSON -> `HashManifest` -> `ComputeH`),
  so `flag[resultH]` is addressable before the round-trip.
- **The Pitfall-1 window is correctly ordered.** The processor pre-writes `flag[resultH]="Pending"`
  (`EntryStepDispatchConsumer.cs:210-212`) only at step 4 — after the output write — so polling for
  that key (`PollForFlagExistsAsync`) and only THEN arming the WRONGTYPE poison avoids tripping a
  processor-side fault. Correct.
- **Capture-tuple typing is sound.** Both `EntryStepDispatch.EntryId` and `ExecutionResult.EntryId`
  are `string` (`EntryStepDispatch.cs:16`, `ExecutionResult.cs:15`), matching the `string entry`
  tuple field.
- **The re-inject dedup proof is race-free.** No `flag[dispatchH]` is written during the trip (the
  output write throws before the Pending pre-write), so `PrewriteFlagPendingAsync` starts clean;
  `PollForFlagAckAsync` makes delivery 2 deterministically observe `Ack`.

The close gate `scripts/phase-33-close.ps1` was diffed against its proven clone source
`scripts/phase-32.1-close.ps1`. The ONLY deltas are comment text, the seeded `version` string
(`3.6.0` -> `3.7.0`), and `Write-Host` message wording. Zero control-flow, snapshot, invariant, or
exit-code logic changed. This is exactly the byte-faithful clone fidelity the phase intends — no
findings on the gate.

The three Info items below are all consistent with the documented spike intent (a clone that
deliberately retains optional fallback machinery). None is a bug. I am reporting them only for
completeness; none requires action for the spike to be correct.

## Info

### IN-01: `PublishSyntheticResultFaultAsync` is unused (intentional D-06 fallback)

**File:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:474-486`
**Issue:** The method is only referenced from doc-comments / narrative strings (lines 190, 433,
511); the live `TripResultFaultAsync` path is primary, so this helper is never invoked. Under a
strict unused-private-member lint this would normally be flagged.
**Why it is acceptable here:** The XML doc explicitly documents it as the D-06 operator-switchable
fallback "kept available... if the live Pitfall-1 window proves fragile." Retaining it is a
deliberate spike affordance, not dead code by accident. No action needed.
**Fix (only if a future lint forces it):** Either wire a `[Fact]`/env-gated branch that exercises
it, or annotate with a suppression noting the D-06 fallback intent.

### IN-02: `PollForNewKeyAsync` is unused (inherited from the clone source)

**File:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:617-642`
**Issue:** This helper (round-trip "new key appeared" poll) is defined but never called — the spike
proves its effect via the ES downstream-effect query (`CountEsHitsAsync`) and the
`flag[H]` Ack poll instead. It is carried over verbatim from the `IdempotentExactlyOnceE2ETests`
clone source.
**Why it is acceptable here:** Clone fidelity is an explicit feature of this phase, and an unused
private method has no runtime cost. It does not affect the spike's proof.
**Fix (optional, low priority):** Drop the method if you want the spike file to carry only the
machinery it actually exercises; otherwise leave it for clone parity.

### IN-03: Two parallel ES access paths (clone artifact)

**File:** `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs:216-221, 539-574`
**Issue:** The downstream-effect assertion uses `ElasticsearchTestClient.PollEsForLog` for the
first hit (line 218) but a hand-rolled `HttpClient` against `http://localhost:9200/` for the count
(`CountEsHitsAsync`, lines 541-556). Two ES client styles in one assertion is mild duplication and
hard-codes the ES endpoint that the helper otherwise centralizes.
**Why it is acceptable here:** This is the same idiom the clone source uses (the test client lacks a
"count hits" method), and the count path is what keeps the zero-duplicate assertion honest (it
settle-waits so a leaked second effect would be ingested before the count is read). Correct as
written.
**Fix (optional):** If `ElasticsearchTestClient` ever grows a total-hits helper, route
`CountEsHitsAsync` through it to retire the inline `HttpClient` and the literal `localhost:9200`.

---

_Reviewed: 2026-06-05_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
