# Phase 13: OrchestrationService split + L3 fetch + L1 build - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-29
**Phase:** 13-orchestrationservice-split-l3-fetch-l1-build
**Areas discussed:** Seam scaffolding scope, Traversal placement, Dispose observability, Mapper ownership (+ junction enrichment)

---

## Gray Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| Seam scaffolding scope | no-op stubs now vs defer validators/writer | ✓ |
| Traversal placement | loader vs snapshot vs shared walker | ✓ |
| Dispose observability | how the forced-throw test asserts cleanup | ✓ |
| Mapper ownership | relocate the 5 pre-injected mappers | ✓ |

**User's choice:** All four areas.

---

## Seam Scaffolding Scope

| Option | Description | Selected |
|--------|-------------|----------|
| Scaffold all 4 now (no-op) | StartAsync body structurally final; matches pre-wire pattern | ✓ |
| Loader only now | smaller surface, but StartAsync re-wired in P14/P15 | |
| Loader + writer, skip validators | hybrid; validators arrive with their logic in P14 | |

**User's choice:** Scaffold all 4 now (no-op). → **D-01**

**Follow-up — validator contract:**

| Option | Description | Selected |
|--------|-------------|----------|
| Explicit ordered calls, sync-throwing | `void Validate(snapshot)`; order visible in orchestrator | ✓ |
| Shared IGraphValidator chain | ordered IEnumerable; order becomes registration-dependent | |
| Async validators | ceremony with no I/O benefit | |

**User's choice:** Explicit ordered calls, sync-throwing. → **D-02**

---

## Traversal Placement

| Option | Description | Selected |
|--------|-------------|----------|
| Inside the loader (private) | snapshot stays pure data; visited-list ensures termination on cycles | ✓ |
| Method on the snapshot | couples walk to data container; awkward dual cycle semantics | |
| Shared GraphWalker component | premature abstraction; opposite cycle semantics vs P14 | |

**User's choice:** Inside the loader (private). → **D-03**
**Notes:** In P13 validators are no-op, so the visited list is what makes loading terminate on a cyclic graph; P14's CycleDetector walks separately with reject semantics.

---

## Dispose Observability

| Option | Description | Selected |
|--------|-------------|----------|
| IsDisposed flag + log line | capture instance via recording loader double; assert flag + empty dicts | ✓ |
| Log assertion only | proves "called" not "cleared"; lower setup | |
| onDispose callback probe | extra ctor param used only by tests | |

**User's choice:** IsDisposed flag + log line. → **D-04**
**Notes:** Forced throw injected via a throwing seam double (enabled by D-01's full scaffolding).

---

## Mapper Ownership

| Option | Description | Selected |
|--------|-------------|----------|
| Move to loader, use Mapperly | drops orchestrator fields + IDE0052 suppressions; single mapping seam | ✓ |
| Move to loader, inline .Select | bypasses RMG-guarded seam; drift risk | |
| Keep on orchestrator | keeps the smell alive; bloats LoadL1Async signature | |

**User's choice:** Move to loader, use Mapperly. → **D-05**

**Follow-up — junction enrichment (surfaced from reading `StepEntityMapper.ToRead`):**

| Option | Description | Selected |
|--------|-------------|----------|
| Mapperly + with-enrich | Mapperly scalars + batch junction queries + `with {}` rebuild for Step & Workflow | ✓ |
| Manual build for M2M entities | two of five bypass the seam | |
| Add junction nav + Include | breaks the no-nav-properties invariant | |

**User's choice:** Mapperly + with-enrich. → **D-06**
**Notes:** Discovery — `ToRead` returns null junction collections (v1 GET/List deferral). P13 closes that gap for the L1 path only.

---

## Derived Items (confirmed via "Create context")

- **D-07:** StopAsync extracted, keeps workflow-id existence check in P13 (Phase 15 → Redis EXISTS).
- **D-08:** Existence-check stays as StartAsync's first step (ORCH-SPLIT-03 order locked).
- Claude's discretion: loader batch-query staging, seam DI lifetimes (Scoped), no-op writer signature.

## Claude's Discretion

- Loader batch-query staging (depth-wave step loads → batched processor → batched schema).
- DI lifetimes for the 4 new seams (mirror Scoped).
- Forward-compatible no-op `IRedisProjectionWriter` signature.

## Deferred Ideas

- Validator rejection logic (Phase 14), Redis write + Stop-as-EXISTS (Phase 15), shared graph-walk abstraction, HTTP GET/List junction enrichment.
