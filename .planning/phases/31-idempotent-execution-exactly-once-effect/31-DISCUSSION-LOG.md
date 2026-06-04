# Phase 31 — Idempotent Execution Round-Trip (Exactly-Once-Effect) — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in `31-CONTEXT.md` — this log preserves the alternatives considered.

**Date:** 2026-06-04
**Phase:** 31-idempotent-execution-exactly-once-effect
**Areas discussed:** Identity/contract shape, Hash canonicalization, CAS mechanism, Manifest format, Retry config, Live-proof test harness

**Framing:** `31-CONTEXT.md` pre-existed as the *design record* (protocol, written before the SPEC). `31-SPEC.md` then locked 8 requirements. This session captured only the **implementation decisions** the SPEC footer delegated to discuss-phase (hash canonicalization, CAS Lua vs SET, contract field shape, L1/L2 wiring, induced-retry test harness). Locked-and-not-revisited: collapse merge, `Immediate(N)` default, effect-first, outbox deferred, Cancelled → Phase 32. All options were grounded in a live codebase scout.

---

## GA1 — Identity / contract field shape

| Option | Description | Selected |
|--------|-------------|----------|
| (a) `EntryId` Guid→string 64-hex + add string `H` | Truthful content-addressing; matches `^[a-f0-9]{64}$` SourceHash convention; biggest blast radius (ripples through `IExecutionCorrelated`, key builder, `Guid.Empty` sentinel) | ✓ |
| (b) Keep `EntryId` as truncated-Guid hash + side string fields | Smaller type change, but a "Guid that's secretly a hash" reintroduces collision risk | |
| (c) Leave Guid `EntryId` as lineage, add new string `EntryHash`/`H` | Lowest blast radius, but two competing identity notions on one message | |

**User's choice:** (a) — accepted recommendation.
**Notes:** D-01/D-02. `executionId` stays Guid (lineage, excluded from `H`). Entry-step "skip input read" sentinel moves to `InputDefinition == null` per SPEC req-2.

---

## GA2 — Hash canonicalization

| Option | Description | Selected |
|--------|-------------|----------|
| Delimited UTF-8 text (Guids `"D"`, EntryId 64-hex, unit-separator) → SHA-256 → `x2` | Mirrors existing `SourceHash.targets` convention exactly; eyeball-able in tests | ✓ |
| Raw 16-byte Guid binary concat → SHA-256 | No delimiter ambiguity but diverges from the text-based SourceHash precedent | |

**User's choice:** Recommended option — accepted.
**Notes:** D-03/D-04. Helper lives in `Messaging.Contracts` next to `L2ProjectionKeys`; one helper produces `EntryId`, `hash(manifest)`, and `H`.

---

## GA3 — CAS / dedup mechanism (first-Lua-in-codebase fork)

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Lua `ScriptEvaluate` true value-CAS `if GET==Pending then SET Ack` | Makes SPEC req-4 "transitions once" literally true; introduces first Lua in codebase | |
| (b) `StringSet(flag[H], Ack, When.Exists)` | Reuses existing primitives, no Lua; only non-Ack value is Pending so When.Exists IS the transition; softens AC wording | ✓ |

**User's choice:** (b) — accepted recommendation.
**Notes:** D-05/D-06/D-07. Sender pre-writes `flag[H_child]=Pending`; receiver effect-first then `When.Exists` flip. Concurrent dups collapse downstream by `H`. SPEC req-4 AC reworded to "Ack observably set exactly once," not a literal CAS primitive.

---

## GA4 — Manifest storage & schema boundary

| Option | Description | Selected |
|--------|-------------|----------|
| JSON array of lowercase-hex strings; hash over UTF-8; `[]`→terminal | Consistent with existing string/JSON L2 payloads; `JsonSerializer.Deserialize<string[]>` fan-out | ✓ |

**User's choice:** Recommended option — accepted, with an explicit clarification.
**Notes:** D-08/D-09. **User clarification:** output-schema validates each result DATA blob (`ProcessResult.OutputData`) pre-write, per-result; the manifest is an *unvalidated pointer list* — the schema definition never describes the manifest shape, the list-of-hashes is never schema-validated. Captured as D-09 so the planner cannot conflate the two.

---

## GA5 — Retry config binding

| Option | Description | Selected |
|--------|-------------|----------|
| `RetryOptions{Limit=3,Strategy=Immediate}` via `IOptions`, per process, threaded into all 4 sites | Single source of truth; Strategy structured-for but Immediate-only; feeds Phase 32 final-attempt check | ✓ |

**User's choice:** Recommended option — accepted.
**Notes:** D-10. Orchestrator + BaseProcessor.Core each bind their own (separate processes). 4 hard-coded `Immediate(3)` sites identified in the scout.

---

## GA6 — Induced-duplicate live-proof test harness

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Test-only induced duplicate (re-publish same dispatch / throw-once processor) over the merge fixture | Reuses `SampleRoundTripE2ETests` + `PollEsForLog` + net-zero teardown; asserts zero downstream dups per CorrelationId | ✓ |
| (b) Broker-level fault injection (kill the ack) | More faithful but heavier/flakier | |

**User's choice:** (a) — accepted recommendation.
**Notes:** D-11/D-12. Close-gate teardown extends triple-SHA scan-clean to the new `skp:data`/`skp:flag` namespaces.

---

## Claude's Discretion

Exact member names (`H` vs `DeterministicId`; `RetryStrategy` enum values); the precise separator byte; whether `RetryOptions` is one shared record or two per-process copies — planner's call within the locked semantics.

## Deferred Ideas

None new — the Cancelled circuit-breaker (Phase 32), transactional outbox, back-off-as-default, and `predecessorStepId`-in-`H` strict-per-edge merge were all already deferred in `31-SPEC.md` out-of-scope. Discussion stayed within phase scope.
