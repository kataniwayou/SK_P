# Phase 47: DLQ Consolidation + At-Least-Once Semantics - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-09
**Phase:** 47-dlq-consolidation-at-least-once-semantics
**Areas discussed:** Audit doc home & format, At-least-once statement home, Structural-assertion technique, Test placement & reuse

> Requirements were locked upstream by `47-SPEC.md` (5 requirements, ambiguity 0.13). This discussion covered HOW to implement only.

---

## Audit doc home & format

| Option | Description | Selected |
|--------|-------------|----------|
| Dedicated 47-DLQ-AUDIT.md | Standalone phase artifact, one row per criterion → proving test | ✓ |
| Fold into 47-VALIDATION.md | Extend the Nyquist traceability table | |
| Section in the design doc | Append audit to the locked design doc | |

**User's choice:** Dedicated `47-DLQ-AUDIT.md`
**Notes:** Keeps the DLQ/at-least-once audit separate from Nyquist coverage and the design spec; it's the phase's primary human-readable deliverable.

---

## At-least-once statement home

| Option | Description | Selected |
|--------|-------------|----------|
| Amend the design doc | Add the guarantee to docs/design/2026-06-08-...md (source of truth; Phase-46 pattern) | ✓ |
| Combine into 47-DLQ-AUDIT.md | Statement at the top of the audit doc | |
| Dedicated doc | A separate docs/ file for the at-least-once contract | |

**User's choice:** Amend the design doc
**Notes:** Matches the Phase-46 design-doc-amendment pattern; bundle with the still-pending `Payload`-on-`KeeperReinject` amendment (one edit closes both user-owned amendments).

---

## Structural-assertion technique

| Option | Description | Selected |
|--------|-------------|----------|
| Reflection + source-scan | Reflection for no-dedup type guard; source-scan for dir-scoped no-keeper-dlq guard | ✓ |
| Reflection only | Assembly reflection for both | |
| Source-scan only | Read .cs files, assert forbidden tokens absent | |

**User's choice:** Reflection + source-scan (right tool per check)
**Notes:** Reflection mirrors the existing firewall-test pattern for the no-`MessageIdentity`/no-dedup type guard; source-scan scoped to `src/BaseProcessor.Core/Processing/` + `src/Keeper/Recovery/` for the "must not reference keeper-dlq" guard (only the dormant `KeeperRecoveryHandler` may).

---

## Test placement & reuse

| Option | Description | Selected |
|--------|-------------|----------|
| Extend existing + reuse kits | Extend KeeperDlqConsolidationTests / RecoveryDeadLetterFacts / TypedResultConsumerFacts; structural guards as own file; duplicate-delivery via double-Consume | ✓ |
| Fresh Phase-47 files | Self-contained new files for every assertion | |

**User's choice:** Extend existing + reuse kits
**Notes:** Least duplication; reuse `RecoveryTestKit`/`DispatchTestKit`. Processor send-exhaustion case added to the proven `KeeperDlqConsolidationTests` harness rig. New structural-guard file carries `[Trait("Phase","47")]`.

## Claude's Discretion

- `47-DLQ-AUDIT.md` table columns/layout (follow VALIDATION.md style).
- Structural-guard test file name/namespace and which assemblies reflection loads.
- Exact wording of the design-doc at-least-once amendment.
- Whether the processor send-exhaustion proof is a new `[Fact]` or sibling within `KeeperDlqConsolidationTests`.
- Whether any SC is already fully proven by an existing test (then the audit row references it; only genuine gaps get new assertions).

## Deferred Ideas

- Removing `keeper-dlq` + the reactive `Fault<T>` path — Phase 48 (RETIRE-03).
- Literal rename `skp-dlq-1` → `_DLQ1` — out of scope (SPEC); revisit only if a literal `_DLQ1` queue name is later required.
- Live / real-stack DLQ + at-least-once proof — Phase 49 (TEST-01..03).
