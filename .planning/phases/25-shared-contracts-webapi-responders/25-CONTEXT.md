# Phase 25: Shared Contracts + WebApi Responders - Context

**Gathered:** 2026-06-01
**Status:** Ready for planning

<domain>
## Phase Boundary

Land the leaf shared-contract vocabulary both sides depend on in `Messaging.Contracts`, and extend the WebApi's publish-only bus join into a request/response responder host that answers two bus queries — `GetProcessorBySourceHash` and `GetSchemaDefinition`. No processor is built this phase (Phase 26+); this delivers only the shared contracts plus the WebApi server side, so the processor has something to query later.

**Untouched (no regression):** the CRUD HTTP surface and the v3.4.0 publish-only Start/Stop path. `ProcessorLivenessValidator` and the existing L2 key writers/readers are reused as-is.

Covers: CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03.

</domain>

<decisions>
## Implementation Decisions

### Shared-contract extracts (`Messaging.Contracts`)

- **D-01 (CONTRACT-01):** Relocate `ProcessorProjection` from `BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` (currently `internal sealed record`) into `Messaging.Contracts.Projections` and make it `public`. Single shared type used by WebApi now and the processor later — no duplicate definition. Its only dependency, `LivenessProjection`, is **already** public in `Messaging.Contracts.Projections`, so this is a clean lift. The `[property: JsonPropertyName("inputDefinition"/"outputDefinition"/"liveness")]` targets are load-bearing and must be preserved verbatim (locked PROJECT.md field-name constraint; RESEARCH Pitfall 1). Update the writer reference in `BaseApi.Service` (its `using` already points at `Messaging.Contracts.Projections`).

- **D-02 (CONTRACT-02):** Add `L2ProjectionKeys.ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}"` producing `skp:data:{entryId:D}`. This is the **first** key with a `data:` discriminator segment — distinct from the flat bare-prefix `root`/`step`/`processor` builders. Pin the exact output string with a **golden test** (e.g. `ExecutionData(known-guid) == "skp:data:{guid:D}"`), matching the existing key-format test discipline.

- **D-03 (CONTRACT-03):** Define the liveness `status` value `"Healthy"` as a shared constant in a new tiny `LivenessStatus` static class in `Messaging.Contracts.Projections` — `public const string Healthy = "Healthy"` — mirroring the `L2ProjectionKeys` static-class single-source-of-truth shape. This is the single source the processor (writer, Phase 26) and any reader consume, so writer/reader cannot desync. (Chosen over hanging the const off the `LivenessProjection` record.)

### Bus request/response responder design (RPC)

- **D-04 (RPC-01/02 — not-found shape):** Use the idiomatic MassTransit **dual-response** pattern. Each query defines a distinct found record AND a distinct not-found record:
  - `GetProcessorBySourceHash` → success carries `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (maps directly from `ProcessorReadDto`, which already exposes exactly these fields); plus a separate not-found response record.
  - `GetSchemaDefinition` → success carries `{ Definition }` (string, read via the existing `SchemaService` / `SchemaReadDto.Definition`); plus a separate not-found response record.
  - Rationale: Phase 26's processor resolves identity/schemas via `IRequestClient.GetResponse<TFound, TNotFound>()` and retries on not-found until resolved — distinct response types let it pattern-match cleanly rather than null-check.

- **D-05 (RPC-03 — how the Core publish-only join hosts the responders):** Keep `AddBaseApiMessaging` in `BaseApi.Core` and extend it with an **optional consumer/endpoint hook** (e.g. `Action<IBusRegistrationConfigurator>? configureConsumers = null`, applied inside the existing `AddMassTransit`). `BaseApi.Service` owns the two consumer classes (they reference `ProcessorService` / `SchemaService`, which live in `BaseApi.Service`) and passes them in via the hook. Core continues to reference `Messaging.Contracts` + MassTransit only — **never** `BaseApi.Service` or `BaseConsole.Core` — so the Phase-19 dependency firewall holds. Default behavior with no hook stays publish-only; existing callers and the CRUD surface are byte-unaffected. The bus health check stays capped at `Degraded` (MSG-WEBAPI-04) — adding responders must not flip CRUD `/health/ready` to 503.

- **D-06 (RPC-03 — endpoint/queue naming):** Add shared queue-name constants to `Messaging.Contracts` mirroring the existing `OrchestratorQueues` static class (bare short-names, no `queue:` scheme prefix — the sender adds it), and bind explicit `ReceiveEndpoint`s for the two responders. Consistent with the established single-source-of-truth discipline; Phase 26's request client targets the same constants. (Chosen over convention-based `ConfigureEndpoints` auto-naming.)

### Claude's Discretion

- Correlation filters on the responder side: the publish-only join deliberately omits correlation consume/send/publish filters (those live in `BaseConsole.Core`, which Core must not reference). These query responders are stateless and outside the orchestration correlation chain — defaulting to **no** correlation filters keeps the firewall intact; planner/research may confirm.
- Exact consumer class structure, names, namespaces, and the DI mapping from `ProcessorReadDto`/`SchemaReadDto` into the response records.
- Whether the two response-record pairs share a file or are split per query; exact not-found record naming.
- Retry/timeout posture on the responder side (the *client*-side retry is Phase 26; the responder just answers or returns not-found).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` §"Phase 25: Shared Contracts + WebApi Responders" — goal, success criteria, `Depends on: Phase 24.1`.
- `.planning/REQUIREMENTS.md` — CONTRACT-01/02/03 (§Shared-Contract Extracts), RPC-01/02/03 (§Bus Request/Response), and the Out-of-Scope table (no Orchestrator/WebApi wire-contract changes; validator reused unchanged).

### Shared contracts leaf (`Messaging.Contracts`) — patterns to mirror
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — static-class SoT key builders; add `ExecutionData` here (CONTRACT-02).
- `src/Messaging.Contracts/Projections/LivenessProjection.cs` — already-public dependency of `ProcessorProjection`; `status` field that CONTRACT-03's constant pins.
- `src/Messaging.Contracts/OrchestratorQueues.cs` — shared queue-name-constant convention to mirror for the responder endpoints (D-06).

### Types being moved / consumed
- `src/BaseApi.Service/Features/Orchestration/Projection/ProcessorProjection.cs` — the `internal` record to relocate + make public (CONTRACT-01).
- `src/BaseApi.Service/Features/Processor/ProcessorDtos.cs` — `ProcessorReadDto` (`Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId?`), the source for the `GetProcessorBySourceHash` success response.
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — `GetBySourceHashAsync(string, ct)` backing RPC-01 (throws `NotFoundException` on miss — map to the not-found response).
- `src/BaseApi.Service/Features/Schema/SchemaService.cs` + `SchemaDtos.cs` — `SchemaReadDto.Definition` backing RPC-02 (read by Id; map miss to not-found response).

### Bus join to extend (firewall-bound)
- `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — the publish-only `AddBaseApiMessaging` to extend with the responder hook (D-05); documents the firewall + `Degraded`-cap constraints that must be preserved.
- `src/BaseApi.Service/Program.cs` — call site (`AddBaseApiMessaging`, line 8) where the Service passes its consumers.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `LivenessProjection` (public, `Messaging.Contracts.Projections`) — dependency of `ProcessorProjection` is already in the leaf; the CONTRACT-01 move is dependency-free.
- `ProcessorReadDto` already exposes `Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId?` — the RPC-01 success response is a direct field projection, no DTO reshaping.
- `ProcessorService.GetBySourceHashAsync` + `SchemaService` read paths exist — responders are thin adapters over them, not new query logic.
- `OrchestratorQueues` static class — exact template for the new shared responder queue-name constants (D-06).
- `L2ProjectionKeys` golden-test discipline — template for the CONTRACT-02 `ExecutionData` string-pin test.

### Established Patterns
- **Dependency firewall (Phase 19):** `BaseApi.Core` → `Messaging.Contracts` only, never `BaseConsole.Core`/`BaseApi.Service`. D-05's hook keeps this intact (Core invokes a callback typed in MassTransit/Contracts; Service supplies the consumers).
- **Publish-only posture:** current `AddBaseApiMessaging` has NO `ConfigureEndpoints`, NO consumers, NO correlation filters. Extension is additive and gated behind the optional hook so the default path is unchanged.
- **Bus health cap at `Degraded` (MSG-WEBAPI-04):** must survive the responder addition so broker-down never 503s CRUD `/health/ready`.
- **Single-source-of-truth static classes:** `L2ProjectionKeys`, `OrchestratorQueues` — CONTRACT-02/03 and D-06 all follow this shape.

### Integration Points
- `BaseApi.Service/Program.cs:8` — where `AddBaseApiMessaging` is wired; the consumer hook is supplied here (or via an `AddAppFeatures`-style Service extension).
- New responder consumers live in `BaseApi.Service` (need `ProcessorService`/`SchemaService` from the Service DI container).
- New request/response contracts + queue-name constants live in `Messaging.Contracts` (consumed by Phase 26's processor request client).

</code_context>

<specifics>
## Specific Ideas

- Mirror the Phase 17/21 extract discipline: lift the type into the leaf, make it public, keep JSON property-name targets verbatim, update the single writer reference.
- The `data:` segment in `skp:data:{entryId:D}` is intentional discrimination — the existing flat keys (`skp:{guid:D}`) carry no type discriminator, so the new execution-data key namespace must not collide with root/processor keys.
- Dual-response (`GetResponse<TFound, TNotFound>`) is chosen specifically because the Phase 26 processor retries on not-found until resolved — a typed not-found is cleaner to loop on than a nullable payload.

</specifics>

<deferred>
## Deferred Ideas

- Processor-side consumption of these contracts (`IRequestClient` usage, identity/schema resolution, retry loops) — Phase 26 (RPC-04, IDENT-03/04, SCHEMA-01/02).
- Actual writes under `skp:data:{entryId}` (execution round-trip read/write) — Phase 27 (EXEC-02/05).
- Writing the liveness key with `status: "Healthy"` (the heartbeat worker) — Phase 26 (LIVE-01/04).
- Config re-validation, eviction/cleanup of execution-data keys — out of scope this milestone (FUT-PROC-02).

</deferred>

---

*Phase: 25-shared-contracts-webapi-responders*
*Context gathered: 2026-06-01*
