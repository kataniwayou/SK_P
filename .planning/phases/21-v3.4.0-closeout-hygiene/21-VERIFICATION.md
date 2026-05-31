---
phase: 21-v3.4.0-closeout-hygiene
verified: 2026-05-31T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
---

# Phase 21: v3.4.0 Closeout Hygiene (HARDEN-03) Verification Report

**Phase Goal:** Close HARDEN-03 (WARNING-1) — eliminate the duplicated L2 Redis-key shape across the WebApi→Orchestrator boundary by hoisting the L2 key computation into a single source of truth in the Messaging.Contracts leaf consumed by both writer and reader; behavior-preserving (keys byte-identical before/after), full suite incl. CorrelationPropagationE2ETests stays GREEN, triple-SHA close gate exits 0. Also fix the WARNING-2 doc-nit.
**Verified:** 2026-05-31
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #   | Truth | Status | Evidence |
| --- | ----- | ------ | -------- |
| 1   | A single shared public class computes all three L2 key shapes (Root/Step/Processor); both writer and reader consume it (no hand-copied key shape survives) | ✓ VERIFIED | `L2ProjectionKeys.cs:24` `public static class L2ProjectionKeys`, builders at L26-30; writer + reader both have `using Messaging.Contracts.Projections;` and delegate (4 `L2ProjectionKeys.*` refs across the two forwarders). No raw `$"{prefix}` literal remains in either forwarder. |
| 2   | Produced key STRINGS are byte-identical before/after for every (prefix, id) input | ✓ VERIFIED | Root uses explicit `$"{prefix}{workflowId:D}"` (L26) — byte-identical to writer's prior bare interpolation (default "D") and reader's prior `:D`. Step/Processor copied verbatim. Golden `L2ProjectionKeysTests` (5 facts) pins exact strings; pre-existing writer golden `RedisProjectionKeysTests` present and (per gathered evidence) GREEN at 270 facts. Triple-SHA gate redis-cli `--scan` HELD (0 keys), proving no wire-shape drift. |
| 3   | The four call sites (RedisProjectionWriter, RedisL2Cleanup, StartOrchestrationConsumer, StopOrchestrationConsumer) are byte-unchanged | ✓ VERIFIED | `git diff --stat 424a5a1 -- <4 files>` returns empty (exit 0, no output). 8 forwarder call references still present across the four files. |
| 4   | Stale CorrelationPropagationE2ETests doc comment no longer claims `skp:wf:{id}:root` and reflects flat `skp:{wfId}`; diff prose-only | ✓ VERIFIED | Whole-file grep: `skp:wf:` = 0, `:wf:{id}:root` = 0. Flat `skp:{id}` present in BOTH corrected comments (L31 `<item>`, L332 summary). `git diff 424a5a1` of this file shows ZERO non-`///` changed lines (no `[Fact]`/`Assert`/method-body touched). |
| 5   | Solution builds zero-warning Release + Debug; full suite (incl. real-stack E2E) GREEN; triple-SHA close gate exits 0 | ✓ VERIFIED | Per orchestrator-gathered evidence + STATE.md L38-40: 3-consecutive GREEN (270 facts each, real-stack E2E live), zero-warning Release+Debug, triple-SHA invariants HELD (psql `94ac978c…b0240`, redis `e3b0c442…852b855` 0 keys, rabbitmq `cca7a68b…c73be1`). `scripts/phase-21-close.ps1` exists with full triple-SHA structure. |
| 6   | L2ProjectionKeysTests has 5 golden facts pinning byte-exact strings | ✓ VERIFIED | `L2ProjectionKeysTests.cs` has exactly 5 `[Fact]` methods asserting `skp:11111111…`, Step composite, Processor, Root==Processor byte-identity, and `test:cls-abc:` per-class prefix composition. |

**Score:** 6/6 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | Shared public static Root/Step/Processor (single source of truth) | ✓ VERIFIED | `public static class L2ProjectionKeys`, file-scoped ns `Messaging.Contracts.Projections`, zero usings, Root uses `:D`. |
| `src/BaseApi.Service/.../RedisProjectionKeys.cs` | Writer forwarder delegating to L2ProjectionKeys | ✓ VERIFIED | `internal static`, name/ns/signatures unchanged, delegates all three; no raw interpolation literal. |
| `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` | Reader forwarder delegating to L2ProjectionKeys | ✓ VERIFIED | `internal static`, keeps ONLY Root (0 Step/Processor refs), delegates to `L2ProjectionKeys.Root`. |
| `tests/BaseApi.Tests/.../L2ProjectionKeysTests.cs` | Byte-identical golden assertions | ✓ VERIFIED | 5 facts pinning exact strings. |
| `scripts/phase-21-close.ps1` | Triple-SHA close gate mirroring phase-20 | ✓ VERIFIED | psql/redis-cli --scan/rabbitmqctl all present; 7× "Phase 21", 0× "Phase 20"; canonical `$services` array; operator-print line targets STATE.md Phase 21 P01. |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | -- | --- | ------ | ------- |
| RedisProjectionKeys.cs (writer) | L2ProjectionKeys.cs | expression-bodied delegation | ✓ WIRED | 3 `L2ProjectionKeys.(Root\|Step\|Processor)` delegations + `using`. |
| OrchestratorL2Keys.cs (reader) | L2ProjectionKeys.cs | expression-bodied delegation | ✓ WIRED | 1 `L2ProjectionKeys.Root` delegation + `using`. |
| Call sites (4 files) | forwarders | unchanged usage | ✓ WIRED | 8 forwarder references intact; call-site files byte-unchanged vs 424a5a1. |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
| ----------- | ----------- | ----------- | ------ | -------- |
| HARDEN-03 | 21-01-PLAN (`requirements: [HARDEN-03]`) | L2 root key shape is a single shared source of truth consumed by both writer and reader | ✓ SATISFIED | Hoisted to `L2ProjectionKeys`; both classes delegate; byte-identity proven by goldens + triple-SHA gate. REQUIREMENTS.md L108/L190 map HARDEN-03 → Phase 21. No orphaned requirements for this phase. |

### Anti-Patterns Found

None. The forwarder bodies are pure delegation (no stub returns, no TODO/placeholder). The doc-comment edits are the only prose changes in the E2E file. HARDEN-01/HARDEN-02 are correctly out of scope (satisfied in Phase 18) per CONTEXT D-deferred and ROADMAP.

### Human Verification Required

None outstanding. The single operator-gated item (Task 4 real-stack triple-SHA close gate) was already executed to exit 0 — independently corroborated in STATE.md (L38-40, three SHA invariants marked HELD) and 21-01-SUMMARY.md "Task 4 — CLOSE GATE PASSED". Phase 21 is the final phase of v3.4.0, so no deferred items apply.

### Gaps Summary

No gaps. All 6 must-haves and the single ROADMAP success criterion (HARDEN-03 / WARNING-1) are satisfied: the L2 key computation is a single source of truth in `Messaging.Contracts.Projections.L2ProjectionKeys`, consumed by both forwarders with unchanged internal surface and byte-unchanged call sites; key strings are provably byte-identical (explicit `:D`, golden tests, triple-SHA gate HELD); the WARNING-2 stale doc-nit is corrected prose-only; the suite stays GREEN and the Phase-21 triple-SHA close gate exits 0.

---

_Verified: 2026-05-31_
_Verifier: Claude (gsd-verifier)_
