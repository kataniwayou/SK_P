---
phase: 43-message-contracts-l2-key-reshape
security-run: State-B (no prior SECURITY.md)
asvs_level: 1
block_on: high
verdict: SECURED
threats_closed: 12
threats_total: 12
threats_open: 0
---

# Security Audit — Phase 43 (message-contracts-l2-key-reshape)

## Summary

All 12 registered threats are CLOSED. Four required code verification (mitigate); eight are
documented accepted risks (accept). No open threats; no unregistered flags.

---

## Threat Verification

| Threat ID | Category | Disposition | Verdict | Evidence |
|-----------|----------|-------------|---------|----------|
| T-43-01 | Tampering (test integrity) | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-02 | Information disclosure | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-03 | Tampering (sentinel confusion) | mitigate | CLOSED | `src/Messaging.Contracts/SourceStep.cs:8` — single predicate `IsSource(Guid entryId) => entryId == Guid.Empty`; `tests/BaseApi.Tests/Contracts/StepResultContractTests.cs:115-120` — STJ round-trip asserts `"EntryId":"00000000-0000-0000-0000-000000000000"` present in serialized output (Pitfall 1 pin); no ad-hoc `EntryId == Guid.Empty` comparisons exist anywhere in `src/` |
| T-43-04 | Information disclosure | mitigate | CLOSED | `src/Messaging.Contracts/ExecutionLogScope.cs:32` — `if (!SourceStep.IsSource(ec.EntryId)) state[EntryId] = ec.EntryId.ToString();` routes through the single D-07 predicate; no legacy `string.IsNullOrEmpty(ec.EntryId)` present in the file |
| T-43-05 | DoS (STJ deserialization) | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-06 | Tampering (sentinel bypass) | mitigate | CLOSED | `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:82-84` — `var raw = SourceStep.IsSource(dispatch.EntryId) ? RedisValue.Null : await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));` routes exclusively through the single D-07 predicate; no inline `== Guid.Empty` guard; no `string.IsNullOrEmpty` guard |
| T-43-07 | Elevation/logic (lost dedup) | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-08 | Information disclosure | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-09 | Repudiation/dead code | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-10 | Tampering (identity-key derivation) | accept | CLOSED | Accepted risk — see accepted-risks log below |
| T-43-11 | Tampering (gate integrity) | mitigate | CLOSED | Phase-05 PLAN gate task explicitly prohibits weakening assertions; Plan-01 `StepResultContractTests` + `SourceStepTests` + `ExecutionLogScopeKeyTests` form independent coverage that cannot be hollowed without test failures; hermetic result independently confirmed: 480 passed / 0 failed, Release build 0 warnings |
| T-43-12 | Repudiation (doc drift) | accept | CLOSED | Accepted risk — see accepted-risks log below |

---

## Mitigate Threats — Detailed Evidence

### T-43-03: Tampering (sentinel confusion)

Claim: `SourceStep.IsSource` is the ONLY recognizer of the `Guid.Empty` source sentinel; the
sentinel serializes to a present all-zero GUID (not an absent field).

Evidence:
- `src/Messaging.Contracts/SourceStep.cs:6-9` — `public static class SourceStep` with single
  method `public static bool IsSource(Guid entryId) => entryId == Guid.Empty;`. No other
  implementation site exists.
- `src/Messaging.Contracts/StepFailed.cs:11`, `StepCancelled.cs:11`, `StepProcessing.cs:10` —
  `Guid EntryId { get; init; } = Guid.Empty;` on all non-Completed records.
- `tests/BaseApi.Tests/Contracts/StepResultContractTests.cs:115-120` — STJ serialization test
  asserts `"EntryId":"00000000-0000-0000-0000-000000000000"` is present in the JSON output,
  confirming the sentinel is a present zero-GUID on the wire, not an absent field.
- Grep across `src/` for `EntryId.*==.*Guid\.Empty` returns no matches — no ad-hoc bypass.

### T-43-04: Information disclosure

Claim: The `IsSource` guard in `ExecutionLogScope.BuildState` suppresses the all-zero GUID for
source steps; no spurious zero-GUID leaks into structured logs.

Evidence:
- `src/Messaging.Contracts/ExecutionLogScope.cs:32` —
  `if (!SourceStep.IsSource(ec.EntryId)) state[EntryId] = ec.EntryId.ToString();`
  The negated predicate skips writing `EntryId` into the scope dict when it is `Guid.Empty`
  (source step). No `string.IsNullOrEmpty(ec.EntryId)` guard remains in this file.
- Doc-comment at line 23 confirms the invariant: "the Guid EntryId when it is the source-step
  sentinel (`SourceStep.IsSource`, D-07 — never inline `== Guid.Empty`)".

### T-43-06: Tampering (sentinel bypass)

Claim: The L2 read-skip in `EntryStepDispatchConsumer` routes through `SourceStep.IsSource`, not
an ad-hoc check; a `Guid.Empty` dispatch deterministically skips the read.

Evidence:
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:82-84` —
  ```
  var raw = SourceStep.IsSource(dispatch.EntryId)
      ? RedisValue.Null
      : await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
  ```
  Exclusively routes through the single D-07 predicate.
- Grep for `string.IsNullOrEmpty.*EntryId` across `src/` returns no matches.
- Grep for `EntryId.*==.*Guid\.Empty` across `src/` returns no matches.
- Comment at line 77 explicitly states: "the L2 READ-skip routes through the single
  SourceStep.IsSource predicate (never an ad-hoc == Guid.Empty)".

### T-43-11: Tampering (gate integrity)

Claim: The phase gate runs the unmodified full hermetic suite; assertions were not weakened.

Evidence:
- `43-05-PLAN.md` Task 2 gate note explicitly forbids weakening assertions: "If the gate is red,
  fix the reshaped tests/harness — do NOT weaken assertions to force green."
- `tests/BaseApi.Tests/Contracts/StepResultContractTests.cs` and
  `tests/BaseApi.Tests/Contracts/SourceStepTests.cs` exist as independent coverage; both assert
  structural and behavioral properties of the new contracts that cannot be satisfied by weakened
  stubs.
- Hermetic result independently confirmed: 480 passed / 0 failed, Release build 0 warnings.

---

## Accepted Risks Log

| Threat ID | Category | Rationale |
|-----------|----------|-----------|
| T-43-01 | Tampering (test integrity) | Deleted RETIRE test files are git-reviewable; the keep-list was excluded; Plan-05 gate re-proves coverage via the full-suite-green hermetic run. No unreviewed coverage gap. |
| T-43-02 | Information disclosure | Golden fixtures embed synthetic GUID literals (1111.../5555...) used only in test assertions, not live credentials or PII. No external boundary exposure. |
| T-43-05 | DoS (STJ deserialization) | Records use default STJ with no custom converters, no recursive types, and no untrusted-size collections. Same deserialization risk profile as prior v3.x records. ASVS L1: no high-severity finding. |
| T-43-07 | Elevation/logic (lost dedup) | Intentional at-least-once milestone posture (RESIL-03). Duplicates tolerated by design; the orchestrator `ResultConsumer` is L1-idempotent. No security boundary depends on exactly-once here. |
| T-43-08 | Information disclosure | `ResultConsumer` dropping `IConnectionMultiplexer` reduces attack surface (no L2 read on the result path). No new exposure introduced; this is a net reduction. |
| T-43-09 | Repudiation/dead code | `KeeperRecoveryHandler` is DARK-but-registered per D-14, user-authorized sequencing. The dormant handler introduces no active code path; activation is gated on a future phase. |
| T-43-10 | Tampering (identity-key derivation) | `localKey` from `CompositeBackup` is deterministic from trusted in-process ids; used only for an internal probe-attempts/pause-key slot, not for auth or dedup decisions. No untrusted input reaches key derivation. |
| T-43-12 | Repudiation (doc drift) | Docs-only ROADMAP/REQUIREMENTS reconciliation; surgical git-tracked diff scoped to two files. No behavioral or security impact; reconciliation reduces future mis-planning risk. |

---

## Unregistered Threat Flags

None. No `## Threat Flags` section present in SUMMARY.md files for this phase beyond the
registered threat register entries above.
