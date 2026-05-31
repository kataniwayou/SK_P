# Phase 21: v3.4.0 Closeout Hygiene - Context

**Gathered:** 2026-05-31
**Status:** Ready for planning

<domain>
## Phase Boundary

Close the one genuinely-open code-hygiene item from the v3.4.0 milestone audit (WARNING-1 / **HARDEN-03**): the L2 root key shape is **duplicated** across the WebApi→Orchestrator service boundary. The writer's `RedisProjectionKeys.Root` (in `BaseApi.Service`, `internal`) and the reader's `OrchestratorL2Keys.Root` (in `Orchestrator`) are hand-copies — byte-identical today, but a future GUID-format/suffix change to the writer would silently desync the reader with **no compile or unit-test failure** (only the real-stack E2E couples them).

This phase hoists the L2 key computation into the shared `Messaging.Contracts` leaf so writer and reader consume **one source of truth**. It is a behavior-preserving refactor: keys must be byte-identical before and after, the full suite (including `CorrelationPropagationE2ETests`) stays GREEN, and the triple-SHA close gate still exits 0.

**In scope:** HARDEN-03 (hoist the shared key builders) + the WARNING-2 doc-nit fix.
**Out of scope:** new behavior, new keyspaces, HARDEN-01/02 (already fixed in Phase 18), centralizing the `Redis:KeyPrefix` value, any execution-id correlation vocabulary (Processor milestone).

</domain>

<decisions>
## Implementation Decisions

### Hoist Scope
- **D-01:** Hoist **all three** L2 key builders — `Root`, `Step`, `Processor` — into the shared class, not just the literally-duplicated `Root`. Rationale: establishes a true single source of truth for the entire flat L2 key scheme and future-proofs the Processor milestone (which will likely read Step/Processor keys). The writer (`RedisProjectionKeys`) currently owns all three; the reader (`OrchestratorL2Keys`) currently owns only `Root`.

### Shared Class Location & Shape
- **D-02:** The shared key class lives in `src/Messaging.Contracts/Projections/`, namespace `Messaging.Contracts.Projections`, alongside `WorkflowRootProjection` (the L2 *value* shape — keeping the L2 *key* shape next to it is cohesive). Recommended name: **`L2ProjectionKeys`**, declared `public static`. (Name is Claude's discretion — pick a clear analog if a better fit emerges, but it MUST be `public` so both projects can reference it.)
- **D-03:** Both existing classes are **kept and delegate** to the shared class (delegation, NOT call-site replacement) to minimize churn and preserve existing namespaces/call sites:
  - `BaseApi.Service…Projection.RedisProjectionKeys` (`internal`) → thin forwarder to `L2ProjectionKeys`. Its callers (`RedisProjectionWriter`, `RedisL2Cleanup`) are left untouched.
  - `Orchestrator.Messaging.OrchestratorL2Keys` (`internal`) → thin forwarder to `L2ProjectionKeys`. Its callers (`StartOrchestrationConsumer`, `StopOrchestrationConsumer`) are left untouched.
- **D-04:** The shared `Root` uses the **explicit `:D` format** — `$"{prefix}{workflowId:D}"`. This is byte-identical to BOTH current forms (the writer's bare `$"{prefix}{workflowId}"` renders the default "D"/hyphenated format; the reader already uses `:D`) and removes the implicit-vs-explicit ambiguity. `Step` and `Processor` mirror the writer's current shapes exactly: `$"{prefix}{workflowId}:{stepId}"` and `$"{prefix}{processorId}"`. **Correctness-critical:** verify the *produced string* is byte-identical, not just the source text.
- **D-05:** `prefix` stays a **method parameter** on every builder. Each host keeps owning its `Redis:KeyPrefix` config value (`"skp:"`); the prefix constant is NOT centralized. Forced anyway — `Messaging.Contracts` is a pure POCO leaf with no config access.

### Opportunistic Doc Fix
- **D-06:** Fix WARNING-2 in the same phase: the stale comment at `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs:31` claims the Start path writes `skp:wf:{id}:root`; the actual shape is `skp:{wfId}` (matches cleanup + the writer). Correct the prose; no behavior change.

### Validation Anchor
- **D-07:** `CorrelationPropagationE2ETests` (real-stack `[Category("E2E")]`/`[Category("RealStack")]`) is the regression net — it is currently the ONLY thing that couples writer+reader key shapes across the process boundary. It MUST stay GREEN, proving the delegation refactor did not drift the wire shape. The phase-close evidence is the existing triple-SHA gate (`psql` + `redis-cli --scan` + `rabbitmqctl list_queues name`, BEFORE==AFTER) at 3-consecutive-GREEN, exit 0.

### Claude's Discretion
- Exact shared class name (recommended `L2ProjectionKeys`).
- Whether the forwarders are expression-bodied one-liners or keep their existing XML-doc summaries (recommend keeping a short summary that points at the shared class as the source of truth).
- Whether a tiny unit test is added asserting writer-forwarder and reader-forwarder produce identical strings for the same `(prefix, id)` — a cheap compile-time-adjacent guard that would have caught the original drift risk (recommended, but not required since the E2E already covers it).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirement & Audit Source
- `.planning/ROADMAP.md` §"Phase 21" — goal + the single success criterion (HARDEN-03 / WARNING-1).
- `.planning/REQUIREMENTS.md` — HARDEN-03 definition (line ~108); note HARDEN-01/02 are already-satisfied (Phase 18) and NOT part of this phase.
- `.planning/milestones/v3.4.0-MILESTONE-AUDIT.md` — WARNING-1 detail (the duplication risk) at the `19-orchestrator-webapi-bus` tech_debt entry; WARNING-2 detail (the stale E2E comment) at the `20-correlation-proof-closeout` entry.

### Code Under Change
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` — the writer key builders (`Root`/`Step`/`Processor`); becomes a forwarder. Currently `internal static`.
- `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` — the reader key builder (`Root` only); becomes a forwarder. Currently `internal static`.
- `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs` — the L2 root *value* shape; the new key class sits beside it.
- `src/Messaging.Contracts/Messaging.Contracts.csproj` — confirm it is the shared leaf both hosts already reference (no new ProjectReference needed: BaseApi.Service references it since Phase 17; Orchestrator references it since Phase 19).

### Call Sites (must remain behavior-identical)
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` — writer caller.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` — writer caller.
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` — reader caller (`StringGetAsync(OrchestratorL2Keys.Root(...))`).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` — reader caller.

### Validation & Close Gate
- `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs` — the regression net (must stay GREEN); also the WARNING-2 doc-fix site (line ~31).
- `scripts/phase-20-close.ps1` — the triple-SHA close-gate template (psql + redis + rabbitmq, 3×GREEN, BEFORE==AFTER, exit 0); Phase 21's close gate mirrors it.

### Prior Decision Lineage
- `.planning/phases/19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier/19-RESEARCH.md` Open Question 3 (hoist-vs-duplicate) + `19-02-PLAN.md` Task 1 — where the duplication was a *conscious* Phase-19 deferral ("`RedisProjectionKeys` is internal there, hoist deferred"). Phase 21 reverses that deferral.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RedisProjectionKeys` (writer) already documents the flat key scheme in its XML summary: Root=`{prefix}{workflowId}`, Step=`{prefix}{workflowId}:{stepId}`, Processor=`{prefix}{processorId}` — this is the authoritative spec to move verbatim into the shared class.
- `Messaging.Contracts/Projections/` already exists (`WorkflowRootProjection`, `LivenessProjection`) — drop the new key class here. `CorrelationKeys.cs` at the project root is the precedent for a shared-constant helper in this leaf.

### Established Patterns
- `Messaging.Contracts` is a pure POCO leaf: no MassTransit/AspNetCore references, no config access. The shared key class must follow suit — `public static`, parameters only, zero dependencies.
- Both hosts already reference `Messaging.Contracts` (verified by the audit dependency-firewall check) — so this is a pure code move, not a wiring change.
- The flat key scheme has NO type discriminator (D-02 of v3.3.0): Root and Processor produce byte-identical strings for the same prefix+GUID, disambiguated only by GUID namespace. Preserve this exactly.

### Integration Points
- The writer→reader contract is the Redis wire string. The shared class makes that contract a compile-time-shared symbol instead of two hand-copies. The E2E is the only runtime proof the strings match — keep it as the gate.

</code_context>

<specifics>
## Specific Ideas

- Byte-identical-key correctness is the whole point — any plan/executor MUST diff the produced string (e.g., assert `L2ProjectionKeys.Root("skp:", id)` equals the pre-refactor output), not merely the source text.
- Optional cheap guard worth considering: a unit test asserting the writer forwarder and reader forwarder return the same string for the same input — this would have caught the original drift risk at unit-test time rather than only at E2E time.

</specifics>

<deferred>
## Deferred Ideas

- **Centralize the `Redis:KeyPrefix` constant** (`"skp:"`) into a shared constant rather than per-host config — intentionally NOT done; keeps config ownership with each host and the leaf config-free. Revisit only if a third consumer appears.
- **`IExecutionCorrelated` execution-id vocabulary** (`ExecutionId/WorkflowId/StepId/ProcessorId/EntryId`) — Processor milestone (v3.5.x+), where those ids are real.
- HARDEN-01 (WR-01) and HARDEN-02 (WR-02) — already satisfied in Phase 18 (commits `d4c0af5`, `4e9e21a`); the audit mis-flagged them as open. NOT part of this phase.

</deferred>

---

*Phase: 21-v3.4.0-closeout-hygiene*
*Context gathered: 2026-05-31*
