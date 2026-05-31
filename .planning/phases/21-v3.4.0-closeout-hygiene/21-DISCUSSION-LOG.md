# Phase 21: v3.4.0 Closeout Hygiene - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-31
**Phase:** 21-v3.4.0-closeout-hygiene
**Areas discussed:** Hoist scope, Shared class location/shape, WARNING-2 doc fix

---

## Hoist Scope

| Option | Description | Selected |
|--------|-------------|----------|
| Root only (literal HARDEN-03) | Hoist just `Root(prefix, workflowId)` — the one method actually duplicated. Minimal, exactly what the requirement names. | |
| All three builders | Hoist Root + Step + Processor as one cohesive shared key class — true single source of truth for the whole L2 scheme; future-proofs the Processor milestone. | ✓ |

**User's choice:** All three builders.
**Notes:** Recommended option accepted. Reader currently only duplicates `Root`, but moving the full scheme establishes one authoritative L2 key class.

---

## Shared Class Location & Shape

| Option | Description | Selected |
|--------|-------------|----------|
| Projections/ folder, both sides delegate | New `public static` class in `Messaging.Contracts/Projections/` beside `WorkflowRootProjection`; `RedisProjectionKeys` (writer) and `OrchestratorL2Keys` (reader) become thin forwarders — keeps call sites/namespaces stable, minimal churn. | ✓ |
| Project root, both sides delegate | Same delegation, but shared class at `Messaging.Contracts` root next to `CorrelationKeys.cs`. | |
| Replace call sites entirely | Delete the two wrapper classes and point every caller directly at the shared class. Cleanest end state, more files touched. | |

**User's choice:** Projections/ folder, both sides delegate.
**Notes:** Delegation chosen over full replacement to minimize churn and keep existing internal call sites untouched. Shared class name (`L2ProjectionKeys`), `:D` format on Root, and prefix-as-parameter locked as Claude's-discretion sub-decisions and confirmed in the prose confirm-loop.

---

## WARNING-2 Doc Fix

| Option | Description | Selected |
|--------|-------------|----------|
| Fix it opportunistically | Correct the stale comment at `CorrelationPropagationE2ETests.cs:31` (`skp:wf:{id}:root` → actual `skp:{wfId}`) in the same phase — same code area, closes the last v3.4.0 doc nit. | ✓ |
| Leave it for now | Out of strict HARDEN-03 scope; keep phase to the single requirement. | |

**User's choice:** Fix it opportunistically.
**Notes:** Trivial, no behavior change; folds the only remaining v3.4.0 doc-hygiene nit into the same closeout phase.

---

## Claude's Discretion

- Exact shared class name (recommended `L2ProjectionKeys`).
- Forwarder shape (expression-bodied vs retaining a short source-of-truth XML summary).
- Optional unit test asserting writer-forwarder == reader-forwarder for the same input (cheap drift guard; recommended, not required — E2E already covers it).

## Deferred Ideas

- Centralizing the `Redis:KeyPrefix` constant (`"skp:"`) — intentionally not done; keeps config per-host and the leaf config-free.
- `IExecutionCorrelated` execution-id vocabulary — Processor milestone (v3.5.x+).
- HARDEN-01/02 — already satisfied in Phase 18; not part of this phase.
