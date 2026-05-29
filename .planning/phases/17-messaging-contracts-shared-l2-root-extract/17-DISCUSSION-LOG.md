# Phase 17: Messaging.Contracts + Shared L2 Root Extract - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-30
**Phase:** 17-messaging-contracts-shared-l2-root-extract
**Areas discussed:** Assembly composition (filters in/out), L2 root extract scope, type naming, ICorrelated mutability
**Format:** Prose confirm-loop (numbered forks + recommendations), per project discussion preference — not AskUserQuestion menus.

---

## Fork 1 — Correlation filters: Messaging.Contracts (Phase 17) vs BaseConsole.Core (Phase 18)?

Driven by a real contradiction: REQUIREMENTS MSG-CONTRACTS-01 says the assembly has "NO dependency
on MassTransit… POCO records only", but the ROADMAP milestone one-liner + research SUMMARY place the
filters (which require MassTransit's `IFilter<>`) in Phase 17. Both cannot hold.

| Option | Description | Selected |
|--------|-------------|----------|
| A | Pure-POCO contracts; filters + accessor deferred to Phase 18 (where CORR-01/02 already map). CPM pins still added in 17. | ✓ |
| B | Relax MSG-CONTRACTS-01; reference MassTransit and ship filters in 17 (matches research prose). | |
| C | Two assemblies: pure-POCO contracts + a separate filters assembly. | |

**User's choice:** A
**Notes:** Requirements + Phase 17 success criteria are authoritative over the milestone one-liner shorthand.
Keeps the leaf truly leaf. CPM version pins (INFRA-RMQ-01) still land this phase; only the PackageReference waits.

---

## Fork 2 — What moves out of BaseApi.Service, given LivenessProjection is shared?

`WorkflowRootProjection` nests `LivenessProjection`, which `ProcessorProjection` (stays) also nests.

| Option | Description | Selected |
|--------|-------------|----------|
| A | Move root + LivenessProjection; ProcessorProjection (stays) references Liveness from Contracts. One shape, no dup. | ✓ |
| B | Move only the root; duplicate a liveness shape in Contracts. | |
| C | Move the whole projection set (root + liveness + step + processor). | |

**User's choice:** A
**Notes:** Orchestrator reads only the root this milestone, so step/processor shapes don't move (C too broad).
B rejected — violates the "no duplicated shape" spirit of MSG-CONTRACTS-04. Moved records flip
internal→public (mechanical, forced by cross-assembly read).

---

## Fork 3 — Keep name `WorkflowRootProjection` or rename to `WorkflowRootProjectionContract`?

| Option | Description | Selected |
|--------|-------------|----------|
| A | Keep `WorkflowRootProjection`; relocate namespace only. | ✓ |
| B | Rename to `WorkflowRootProjectionContract` (research suffix). | |

**User's choice:** A
**Notes:** The C# identifier is not the wire contract (fixed by JsonPropertyName). Renaming adds churn/risk
to "v3.3.0 tests stay GREEN" (touches RedisProjectionWriter + ProjectionRecordRoundTripTests) for no gain.

---

## Light decision — ICorrelated property mutability

Offered as Claude's discretion; user did not object.

**Resolution:** `ICorrelated`'s six Guid fields are **get-only**. Zero implementers this milestone
(control records deliberately don't implement it per MSG-CONTRACTS-02); revisit mutability when the
outbound filter and its first implementer appear.

---

## Claude's Discretion

- Namespace granularity inside Messaging.Contracts (flat vs `.Projections` sub-namespace).
- Exact host class/name for the shared `"CorrelationId"` constant.
- New `.csproj` shape (follows Phase 1 csproj-inheritance idiom by absence).

## Deferred Ideas

- Correlation filters + AsyncLocal accessor → Phase 18 (CORR-01/02).
- ICorrelated mutability → revisit with the outbound filter / first implementer.
- Concrete `JobTrigger`/`ExecutionResult` implementers → Processor milestone (v3.5.x+).
- Step/Processor projection shapes moving to Contracts → only when a consumer reads them.
