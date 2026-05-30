# Phase 17: Messaging.Contracts + Shared L2 Root Extract - Context

**Gathered:** 2026-05-30
**Status:** Ready for planning

<domain>
## Phase Boundary

Create one leaf class library, `Messaging.Contracts`, that both `BaseApi.Service` (publisher)
and the future `Orchestrator` (consumer) compile against — carrying the frozen message
vocabulary and the single-source-of-truth L2 root read-shape — plus pin MassTransit safely.

No host code, no bus wiring, no correlation filters, no behavior change beyond a
behavior-preserving using-swap. The correlation filters + AsyncLocal accessor are explicitly
**out of this phase** (they belong to Phase 18 / CORR-01, CORR-02).

</domain>

<decisions>
## Implementation Decisions

### Assembly composition (Fork 1 → Option A)
- **D-01:** `Messaging.Contracts` stays **pure-POCO** — honors `MSG-CONTRACTS-01` literally
  ("NO dependency on MassTransit or any host library"). The assembly references at most
  `Microsoft.Extensions.Logging.Abstractions`; it does **not** reference MassTransit.
- **D-02:** The two correlation filters (`IFilter<ConsumeContext>` / `IFilter<SendContext>` /
  `IFilter<PublishContext>`) and the AsyncLocal correlation accessor are **deferred to Phase 18**
  (`BaseConsole.Core`), which is where the formal requirements traceability already maps
  CORR-01/CORR-02. The ROADMAP milestone one-liner (line 27) and research SUMMARY that place
  the filters in Phase 17 are shorthand and are superseded by the Phase 17 success criteria +
  requirements, which never mention filters.
- **D-03:** The MassTransit CPM **pins are still added this phase** (INFRA-RMQ-01) — only the
  `PackageReference` to MassTransit waits for Phase 18. Phase 17 = version pins + blocking comment
  in `Directory.Packages.props`; no consumer of those packages yet.

### What moves out of BaseApi.Service (Fork 2 → Option A)
- **D-04:** Move `WorkflowRootProjection` **and** `LivenessProjection` to `Messaging.Contracts`.
  `LivenessProjection` is shared (also nested by `ProcessorProjection`), so moving it gives one
  liveness shape with no duplication.
- **D-05:** Both moved records flip from `internal sealed record` to **`public sealed record`**
  (forced by cross-assembly read — mechanical, not a design choice).
- **D-06:** Everything else **stays `internal` in `BaseApi.Service`**: `RedisProjectionWriter`,
  `IRedisProjectionWriter`, `StepProjection`, `ProcessorProjection`, `RedisProjectionKeys`,
  `RedisL2Cleanup`. `ProcessorProjection` (stays) will reference `LivenessProjection` from
  `Messaging.Contracts` via a using-swap. The Orchestrator reads only the **root** this milestone,
  so step/processor shapes do NOT move (Fork 2 Option C rejected as broader than needed).

### Naming & wire shape (Fork 3 → Option A)
- **D-07:** Keep the type name `WorkflowRootProjection` (do **not** rename to
  `WorkflowRootProjectionContract`). Only the namespace relocates to `Messaging.Contracts`.
  The C# identifier is not the wire contract; renaming buys nothing and adds churn/risk to the
  "v3.3.0 tests stay GREEN" criterion (would touch `RedisProjectionWriter` +
  `ProjectionRecordRoundTripTests`).
- **D-08:** **Wire shape is byte-identical** — every `[property: JsonPropertyName(...)]` camelCase
  target is preserved verbatim (`entryStepIds`, `cron`, `jobId`, `liveness`, `correlationId`;
  liveness: `timestamp`, `interval`, `status`). This is load-bearing (SC#3 / RESEARCH Pitfall 1):
  on a positional record a bare attribute binds to the ctor parameter and STJ ignores it, so the
  `[property:]` prefix MUST stay.

### Frozen vocabulary
- **D-09:** `ICorrelated` declares the six `Guid` fields `{ CorrelationId, ExecutionId, WorkflowId,
  StepId, ProcessorId, EntryId }` (MSG-CONTRACTS-03) as **get-only** properties. This milestone has
  zero implementers (the control records deliberately do not implement it per MSG-CONTRACTS-02;
  it is the future `JobTrigger`/`ExecutionResult` vocabulary). Get-only is the safe minimal — a
  future phase revisits mutability when the outbound filter actually stamps it.
  > **[AMENDED 2026-05-30, Phase 19 D-01]** Superseded. `ICorrelated` is **slimmed to the single
  > field `{ Guid CorrelationId }`** and changed to **init-set** (so the publisher sets it at
  > construction). The five execution ids move to a derived `IExecutionCorrelated : ICorrelated`
  > defined in the future Processor milestone (interface segregation — avoids `Guid.Empty` slots on
  > operational messages). The control records (Start/Stop) DO now implement `ICorrelated`. Phase 19
  > reconciles the shipped Phase 17 code to this shape; see Phase 19 CONTEXT D-01.
- **D-10:** `StartOrchestration` / `StopOrchestration` are POCO records each carrying exactly
  `Guid[] WorkflowIds` and no correlation field (MSG-CONTRACTS-02). They do NOT implement
  `ICorrelated` this milestone.
  > **[AMENDED 2026-05-30, Phase 19 D-01]** Superseded. The control records **now implement
  > `ICorrelated`** (the slim `{ Guid CorrelationId }`), keeping `Guid[] WorkflowIds` as their
  > operational payload. Correlation rides the message body (not the MassTransit envelope). Phase 19
  > reconciles the shipped code; see Phase 19 CONTEXT D-01/D-02.
- **D-11:** The `"CorrelationId"` PascalCase log-scope key becomes a **shared constant** in
  `Messaging.Contracts` (the exact literal `CorrelationIdMiddleware` uses —
  `CorrelationIdMiddleware.cs:52` — and what OTel `IncludeScopes=true` serializes to Elasticsearch).
  Casing drift silently breaks the cross-service log join.

### MassTransit pin (INFRA-RMQ-01)
- **D-12:** Add `MassTransit` and `MassTransit.RabbitMQ` at `8.5.5` to `Directory.Packages.props`
  with a blocking comment that v9+ is commercial ($400/mo min; v8.x is Apache-2.0 through end-2026),
  mirroring the existing Npgsql cautionary comment block at `Directory.Packages.props:52`.

### Claude's Discretion
- Namespace granularity inside `Messaging.Contracts` (flat root vs `Messaging.Contracts.Projections`
  sub-namespace for the moved shapes) — planner/executor decides for consistency with existing
  conventions.
- Exact placement of the `"CorrelationId"` constant (dedicated static class name, e.g.
  `CorrelationKeys` / `LogScope`) — keep it discoverable; planner decides.
- New project's `.csproj` shape (TargetFramework/Nullable inheritance from `Directory.Build.props`,
  no redeclared common properties) follows the proven Phase 1 csproj-inheritance idiom.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements (authoritative)
- `.planning/ROADMAP.md` §"Phase 17" (lines 34-44) — goal, depends-on, 5 success criteria.
- `.planning/REQUIREMENTS.md` — MSG-CONTRACTS-01..04, INFRA-RMQ-01 (the 5 mapped requirements);
  Out-of-Scope table; note MSG-CONTRACTS-01 "NO dependency on MassTransit" is the authority behind D-01.
- `.planning/ROADMAP.md` lines 17-23 — cross-phase hard constraints (correlation key casing
  load-bearing; MassTransit pin; no global purge).

### Research (HIGH confidence — Phase 17 flagged "no phase research needed")
- `.planning/research/SUMMARY.md` — stack/pitfalls overview; NOTE its Phase 17 bullet (line 95)
  lists filters/accessor in Contracts — superseded by D-01/D-02 (filters → Phase 18).
- `.planning/research/STACK.md` — MassTransit 8.5.5 / v9-commercial pin rationale.
- `.planning/research/ARCHITECTURE.md` — Messaging.Contracts as leaf; BaseApi.Core/Service seam mirror.

### Existing code to move / swap (the "extract" half)
- `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` — the root
  read-shape to MOVE (keep name, flip to public, preserve JsonPropertyName).
- `src/BaseApi.Service/Features/Orchestration/Projection/LivenessProjection.cs` — MOVE with the root.
- `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` — STAYS; using-swap
  to reference `LivenessProjection` from Contracts.
- `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` — STAYS internal.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` — STAYS; serializes
  the root (using-swap to the new namespace; behavior preserved).
- `tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs` — wire-shape
  regression guard; must stay GREEN through the move.

### Correlation key constant source
- `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs:52` — `const string ItemKey = "CorrelationId"`;
  the literal Phase 17 hoists into a shared constant.

### Pin pattern + solution layout
- `Directory.Packages.props:52` — the Npgsql cautionary comment block to mirror for the MassTransit pin.
- `SK_P.sln` + `src/` (currently `BaseApi.Core`, `BaseApi.Service`) — where the new
  `Messaging.Contracts` project is added.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `WorkflowRootProjection` / `LivenessProjection` records already carry the exact load-bearing
  `[property: JsonPropertyName]` camelCase shape — relocate verbatim, do not rewrite.
- Npgsql cautionary pin block (`Directory.Packages.props:52`) is the template for the MassTransit
  blocking comment — same authorial voice/format.
- Phase 1 csproj-inheritance idiom (common props live only in `Directory.Build.props`; no `Version=`
  on `PackageReference` thanks to CPM) — the new project follows it by absence.

### Established Patterns
- Records are positional + `internal sealed` in `BaseApi.Service`; cross-assembly read forces the two
  moved records to `public sealed` (D-05) while the rest stay internal (D-06).
- Wire shapes pinned by `[property: JsonPropertyName]`, default System.Text.Json (no source-gen mapper) —
  preserve exactly (D-08).
- PascalCase `"CorrelationId"` log-scope key is the single cross-service join key (CorrelationIdMiddleware
  + OTel `IncludeScopes`) — becomes a shared constant (D-11).

### Integration Points
- `RedisProjectionWriter.UpsertAsync` constructs `WorkflowRootProjection` (line 66) and `LivenessProjection`
  (line 60) — these call sites get a namespace using-swap, no logic change.
- `ProcessorProjection` nests `LivenessProjection` — using-swap to the Contracts namespace.
- `SK_P.sln` gains a third project; `BaseApi.Service` adds a `ProjectReference` to `Messaging.Contracts`.

</code_context>

<specifics>
## Specific Ideas

- The build order is leaf-first by design: `Messaging.Contracts` must compile before either host can
  reference it. This phase intentionally has no runtime behavior change — the only observable effects are
  (1) the new assembly exists and is referenced, (2) the CPM pins appear, (3) the v3.3.0 suite stays GREEN
  proving the using-swap is behavior-preserving.
- The contradiction between the roadmap one-liner (filters in 17) and the requirements (filters in 18)
  was surfaced and resolved in favor of the requirements (D-01/D-02). Downstream agents should NOT
  re-introduce the filters into Phase 17.

</specifics>

<deferred>
## Deferred Ideas

- **Correlation filters + AsyncLocal accessor** → Phase 18 (`BaseConsole.Core`, CORR-01/CORR-02).
  Defined and wired there, not here.
- **`ICorrelated` mutability** (settable/init properties for outbound stamping) → revisit in the phase
  that introduces the outbound filter and its first `ICorrelated` implementer (Processor milestone /
  Phase 18-20 filter work). Get-only for now (D-09).
- **Concrete `ICorrelated` implementers** (`JobTrigger`, `ExecutionResult`) → Processor milestone
  (v3.5.x+, FUT-CONTRACTS-01) — out of v3.4.0 scope.
- **Step/Processor projection shapes moving to Contracts** → only if/when a consumer reads them;
  the Orchestrator reads the root only this milestone (Fork 2 Option C deferred).

None of these are losses — all are tracked above and/or in REQUIREMENTS.md Future/Out-of-Scope.

</deferred>

---

*Phase: 17-messaging-contracts-shared-l2-root-extract*
*Context gathered: 2026-05-30*
