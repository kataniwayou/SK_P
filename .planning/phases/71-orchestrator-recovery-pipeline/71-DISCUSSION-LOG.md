# Phase 71: Orchestrator Recovery Pipeline - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-16
**Phase:** 71-orchestrator-recovery-pipeline
**Areas discussed:** Pipeline organization, Slot encoding, OrchestratorReinject shape, Rename scope
**Governing principle (user):** "Consistent with processor" — applied across all four decisions.

---

## Pipeline organization

| Option | Description | Selected |
|--------|-------------|----------|
| Mirror + adapt (new class) | New OrchestratorResultPipeline copies the ProcessorPipeline skeleton, adapts divergent parts. Lowest coupling, no risk to shipped processor path. | ✓ |
| Extract shared base | Factor common skeleton into a generic base both inherit. Less duplication, but refactors working ProcessorPipeline and risks over-abstraction. | |

**User's choice:** Mirror + adapt — and confirmed via "consistent with processor" (mirror the canonical pipeline).

---

## Heterogeneous slot encoding

| Option | Description | Selected |
|--------|-------------|----------|
| JSON object per slot | HASH field value = JSON {nextStepId, nextProcessorId, payload, newEntryId}. Natural fit (payload already JSON), debuggable, evolvable. | ✓ |
| Delimited string | Compact packed string. Fragile (payload escaping), opaque, custom parser. | |

**User's choice:** JSON per slot. Framed as consistent-with-processor: same MessageIndex HASH-of-slot→value structure; the value is a JSON tuple only because orchestrator slots are heterogeneous (processor's is a bare Guid because homogeneous).

---

## OrchestratorReinject contract shape

| Option | Description | Selected |
|--------|-------------|----------|
| Outcome enum + union fields | StepOutcome discriminator + result-field superset; a factory rebuilds the IStepResult subtype. Mirrors how (Processor)Reinject carries discrete fields + reconstructs. | ✓ |
| Embed serialized original result | Carry the original IStepResult serialized; re-emit verbatim. Polymorphic blob on the contract. | |

**User's choice:** Resolved by "consistent with processor" — KeeperReinject carries discrete fields (Payload) and reconstructs the typed message, so OrchestratorReinject carries outcome + union fields and a factory rebuilds the subtype (no serialized blob).

---

## Rename scope & sequencing

| Option | Description | Selected |
|--------|-------------|----------|
| Rename contracts + consumers, dedicated first plan | Rename records AND consumer classes for symmetry with the new Orchestrator* consumers; isolated first-wave diff before adding Orchestrator*. | ✓ |
| Rename contracts only | Smaller diff but asymmetric naming (InjectConsumer handles ProcessorInject). | |

**User's choice:** Resolved by "consistent with processor" — symmetric naming (ProcessorInject↔ProcessorInjectConsumer, OrchestratorInject↔OrchestratorInjectConsumer). Sequenced as an isolated first plan.

---

## Follow-ons locked under "consistent with processor"

- **Atomic FORWARD op:** one Lua script (mirror AtomicForwardWrite) — HSET slot + PEXPIRE index + COPY origin→new with data TTL; TTLs as ARGV (no Lua RNG); exhaust → one OrchestratorInject.
- **RecoveryTestKit WR-01 fix:** add the 5-arg StringSetAsync stub while the rename touches the kit (from Phase 70's 70-REVIEW.md).

## Claude's Discretion

- New file/class/namespace names; COPY-vs-GET/SET in the atomic script; reuse StepOutcome vs new discriminator; slot JSON property names; extend vs duplicate the delete-invariant fact.

## Deferred Ideas

- Processor INJECT index-slot-write spec §8 divergence (deferred since Phase 70) — not reopened.
- Stricter keeper-recovery endpoint startup posture (connect-stopped) — future option, untouched.

## Open integration question carried to research (not deferred)

- How the new L2-gated OrchestratorResultPipeline composes with the existing per-type result consumers
  (StepCompletedConsumer etc.) and StepAdvancement DAG-advancement — flagged for the researcher.
