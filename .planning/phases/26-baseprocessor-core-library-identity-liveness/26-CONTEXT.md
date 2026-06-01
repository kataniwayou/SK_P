# Phase 26: BaseProcessor.Core — Library, Identity & Liveness - Context

**Gathered:** 2026-06-01
**Status:** Ready for planning

<domain>
## Phase Boundary

Deliver a reusable `BaseProcessor.Core` Generic-Host library built on `BaseConsole.Core`, plus an `AddBaseProcessor` composition root, that:
1. reads its SourceHash from assembly metadata via reflection (IDENT-03),
2. resolves its identity (`Id` + three nullable schema Ids) and its input/output schema **definitions** over the bus via MassTransit `IRequestClient` (dual-response, retry-until-resolved, boot-before-register tolerated), and
3. self-registers liveness into Redis L2 (`skp:{processorId:D}`) **only while Healthy**, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.

Covers: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01.

**Not this phase (locked out):**
- The execution round-trip consumer (`queue:{processorId:D}` bind, L2 input read, output validation/write, `ExecutionResult` sends) — **Phase 27**. The `abstract ProcessAsync` seam is *declared* here but *invoked* there.
- The MSBuild SourceHash **embed** target, the concrete `Processor.Sample`, its Dockerfile/compose tier, and the real-stack E2E close gate — **Phase 28**.

**Untouched (no regression):** `ProcessorLivenessValidator` and the L2 reader half are reused **unchanged** — this phase is the external self-registrar they have always expected. The shared `Messaging.Contracts` vocabulary (Phase 25) is consumed, not modified.

</domain>

<decisions>
## Implementation Decisions

### Startup orchestration & health-gate semantics

- **D-01 (BPC-03 / IDENT-04 / SCHEMA-01):** Model the two-loop startup as a single `BackgroundService` (or equivalent hosted startup orchestrator) wired by `AddBaseProcessor`. **Loop A** resolves identity by SourceHash; **Loop B** resolves the input/output definitions for each non-null schema Id. Mirrors the `Orchestrator/Program.cs` shape — base library supplies all infra, concrete `Program.cs` stays minimal.

- **D-02 (BPC-03):** Tie `BaseConsole.Core`'s startup gate (`IStartupGate.MarkReady`) to **"Healthy reached"** — i.e. flip `/startup` (and therefore `/ready`) green only once identity + all required definitions resolve, exactly mirroring how `Orchestrator/Program.cs` **removes** the base `StartupCompletionService` (so `MarkReady` no longer fires at bare host-start) and drives `MarkReady` from completion instead. `/live` stays dependency-independent (untouched). Until Healthy, the processor is not ready and writes no liveness key.

### Identity / schema resolution & retry posture (RPC-04)

- **D-03 (RPC-04 / IDENT-04 / SCHEMA-01):** Both queries go through MassTransit `IRequestClient` using the Phase 25 dual-response contracts — `IRequestClient<GetProcessorBySourceHash>.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>()` and `IRequestClient<GetSchemaDefinition>.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>()`. Pattern-match on the typed not-found response rather than null-check. Request clients target the `ProcessorQueues.IdentityQuery` / `ProcessorQueues.SchemaQuery` endpoints (sender adds the `exchange:`/`queue:` scheme).

- **D-04 (IDENT-04 — retry posture):** **Unbounded** retry loop with a **bounded exponential backoff** (cap between attempts, recommend ~30s). Retry on BOTH the `IRequestClient` request timeout AND the typed not-found response (boot-before-register: the operator may not have registered the Processor DB row yet — the processor must keep trying, not crash). Per-request `IRequestClient` timeout kept short (recommend ~5–10s) so a slow/missing responder fails the attempt fast and re-loops. Backoff cap and per-request timeout are **appsettings-configurable** (sensible defaults baked in).

- **D-05 (SCHEMA-02):** Null/optional schema Ids are **skipped by design** — an absent definition is never a failure. The **config** schema Id is resolved by neither loop (only input + output definitions are fetched and written). A processor with all-null input/output schema Ids is still able to reach Healthy (it just has no definitions to fetch).

### Shared identity/context state

- **D-06 (BPC-02 / forward-compat with Phase 27):** Introduce a single mutable singleton context holder (e.g. `IProcessorContext` / `ProcessorIdentity`) as the source of truth for the resolved `Id`, the three schema Ids, the resolved input/output **definitions**, and a **Healthy** flag. The startup orchestrator populates it; the heartbeat worker (this phase) reads it to write liveness; the Phase 27 consumer will read it for its queue name (`queue:{processorId:D}`) and validation. This keeps the resolution result in ONE place rather than threaded through constructors.

### Liveness heartbeat worker (LIVE-01..06)

- **D-07 (LIVE-01 / LIVE-04):** A background heartbeat worker writes/refreshes `L2ProjectionKeys.Processor(processorId)` (= `skp:{processorId:D}`) every `Interval` seconds with a `ProcessorProjection { inputDefinition, outputDefinition, liveness{ timestamp, interval, status } }`. It writes **only when the context Healthy flag is set** (identity + all required non-null definitions resolved). A starting / restarting / unhealthy / not-yet-Healthy replica does **not** write — to the orchestrator it is `absent`. `status` is always `LivenessStatus.Healthy` ("Healthy") when written.

- **D-08 (LIVE-02 / LIVE-03):** Each beat refreshes the liveness `timestamp` (from `TimeProvider` — mirror `RedisProjectionWriter`/`ProcessorLivenessValidator` clock usage) and re-applies the configured `Ttl` key expiry on every write (**sliding** expiration via `SET ... EX`). The written `interval` value **equals the configured heartbeat delay in seconds**, so the reader's `timestamp + interval×2` staleness math holds. `Interval` and `Ttl` are two **independent** appsettings seconds-values (CONFIG-01).

- **D-09 (LIVE-05 — frozen shape):** The written value MUST serialize to **exactly** what `ProcessorLivenessValidator` deserializes. Reuse the shared `Messaging.Contracts.Projections.ProcessorProjection` + `LivenessProjection` records directly (do NOT define a parallel writer DTO) so the load-bearing `[property: JsonPropertyName(...)]` targets (`inputDefinition`/`outputDefinition`/`liveness`/`timestamp`/`interval`/`status`) cannot desync. Use `L2ProjectionKeys.Processor(id)` for the key and `LivenessStatus.Healthy` for the status — both shared single-source-of-truth.

- **D-10 (LIVE-06 — lock-free):** The shared liveness key is a blind whole-value `SET` written only-when-Healthy; concurrent writes from N replicas are equivalent (same definitions, `status:"Healthy"`, fresh timestamp), so last-write-wins requires **no** synchronization. No distributed lock, no read-modify-write.

- **D-11 (heartbeat resilience — Redis soft-dep):** A Redis write fault on a beat is **log-and-continue** — never crash the host. A few missed beats simply let the key slide to `stale` (orchestrator sees stale/absent, which is the correct admission signal). The worker keeps beating; the next successful write re-establishes freshness.

### Abstract seam surface

- **D-12 (BPC-02):** Declare the `abstract` base processor class + the single `abstract` execution method signature (the `ProcessAsync` seam) + its `ProcessResult` return type **now**, so the class shape and a test double are stable for this phase. The seam is **invoked** only in Phase 27 (the dispatch consumer). A concrete processor overrides exactly one method — no infra/id/L2/bus code in the concrete (BPC-02).

### Verification strategy (this phase, standalone)

- **D-13:** Prove `BaseProcessor.Core` standalone the way Phase 18 validated `BaseConsole.Core` (in-memory tests) — the SourceHash **embed** target and concrete `Processor.Sample` do not exist until Phase 28. Approach:
  - Read SourceHash behind a thin seam (e.g. `ISourceHashProvider`) whose **default** implementation is the reflection-over-`AssemblyMetadata` read (IDENT-03); tests stub it with a known hash.
  - Drive Loop A/B with the **MassTransit in-memory test harness** (no real broker), asserting retry-on-not-found then resolve-on-found.
  - Assert the exact L2 JSON string (against a real/fake Redis) byte-matches what `ProcessorLivenessValidator` reads — close the writer↔reader loop in-test.

### Claude's Discretion
- Exact namespaces, class names, and file layout under `src/BaseProcessor.Core/` (mirror `BaseConsole.Core` folder conventions: `DependencyInjection`, `Configuration`, plus a startup/identity + heartbeat area).
- Exact `IProcessorContext` member shape and whether Healthy is a flag, an enum, or a `TaskCompletionSource`-style signal (planner/research to confirm against the Phase 27 consumer's needs).
- Whether the two resolution loops live in one hosted service or two; whether the heartbeat worker is a separate `BackgroundService` or folded in (D-01 prefers one orchestrator + a separate heartbeat worker, but planner may consolidate).
- Exact backoff curve and the appsettings key names/defaults for retry timeout + backoff cap (D-04) and the heartbeat `Interval`/`Ttl` (CONFIG-01).
- Whether `AddBaseProcessor` composes `AddBaseConsole` + `AddBaseConsoleMessaging` internally or expects the concrete `Program.cs` to call them (mirror whichever keeps the concrete `Program.cs` smallest — BPC-03).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` §"Phase 26: BaseProcessor.Core — Library, Identity & Liveness" — goal, 5 success criteria, `Depends on: Phase 25`.
- `.planning/REQUIREMENTS.md` — BPC-01/02/03, IDENT-03/04, RPC-04, SCHEMA-01/02, LIVE-01..06, CONFIG-01 (exact wording locks the contracts).
- `.planning/PROJECT.md` §"Current Milestone: v3.5.0" — the two-loop startup narrative + "Key context / locked decisions" + "Out of scope (deferred)".

### Base library to build ON (mirror these patterns)
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` — `AddBaseConsole` (Redis soft-dep + embedded health) — `AddBaseProcessor` composes over this.
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — `AddBaseConsoleMessaging(cfg, configureConsumers, configureBus?)` — the bus join + 3 correlation filters; the processor adds its `IRequestClient`s here.
- `src/BaseConsole.Core/Health/IStartupGate.cs` + `StartupCompletionService.cs` + `StartupHealthCheck.cs` — the startup gate to **re-point** at Healthy (D-02).
- `src/Orchestrator/Program.cs` — **the reference composition root to mirror**: thin Generic-Host shell, removes the base `StartupCompletionService` to drive `MarkReady` from completion instead of bare host-start (D-02 pattern), `TryAddSingleton(TimeProvider.System)`.
- `src/Orchestrator/appsettings.json` — config shape (Redis/OTel/RabbitMq/ConsoleHealth) the processor's appsettings mirrors + the new `Interval`/`Ttl`/retry knobs.

### Frozen L2 shape & shared vocabulary (Phase 25 — consume unchanged)
- `src/Messaging.Contracts/Projections/ProcessorProjection.cs` — the **exact** record the writer must produce (reuse, don't re-define; JSON property names load-bearing).
- `src/Messaging.Contracts/Projections/LivenessProjection.cs` — `{ timestamp, interval(seconds), status }` nested sub-document.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `Processor(Guid)` = `skp:{processorId:D}` (use this builder for the key).
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` — `LivenessStatus.Healthy = "Healthy"` (write this const).
- `src/Messaging.Contracts/ProcessorQueries.cs` — `GetProcessorBySourceHash`/`ProcessorIdentityFound`/`ProcessorIdentityNotFound` + `GetSchemaDefinition`/`SchemaDefinitionFound`/`SchemaDefinitionNotFound` (the dual-response contracts the client queries).
- `src/Messaging.Contracts/ProcessorQueues.cs` — `IdentityQuery` / `SchemaQuery` endpoint-name constants the request clients target.

### The reader this phase must satisfy (reused unchanged — DO NOT modify)
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — absent/stale (`timestamp + interval*2 <= now`)/malformed → 422; `interval` interpreted in SECONDS, sourced from the entry. The writer's output must round-trip through this validator's `JsonSerializer.Deserialize<ProcessorProjection>`.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` §"6b. Processor-liveness gate" — where the gate is called at orchestration-Start (the admission signal this phase feeds).

### Prior context
- `.planning/phases/25-shared-contracts-webapi-responders/25-CONTEXT.md` — the contracts produced upstream (D-04 dual-response rationale, D-06 queue-name SoT) that this phase consumes.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `BaseConsole.Core` — entire infra base (soft-dep Redis `IConnectionMultiplexer`, embedded minimal-Kestrel health probes, metrics-only OTel, MassTransit/RabbitMQ + 3 correlation filters, `IStartupGate`). `AddBaseProcessor` is a composition over `AddBaseConsole` + `AddBaseConsoleMessaging`.
- `Orchestrator/Program.cs` — exact precedent for: thin Generic-Host shell, the `StartupCompletionService`-removal → re-pointed `MarkReady` pattern (D-02), and a hosted `BackgroundService` driving startup completion.
- Shared `Messaging.Contracts` types — `ProcessorProjection`, `LivenessProjection`, `L2ProjectionKeys.Processor`, `LivenessStatus.Healthy`, the 6 dual-response records, `ProcessorQueues` constants — all already public in the leaf (Phase 25). The processor consumes; nothing new is added to the leaf this phase.

### Established Patterns
- **`TimeProvider` for liveness time** — `RedisProjectionWriter` and `ProcessorLivenessValidator` both read `TimeProvider.GetUtcNow().UtcDateTime`; the heartbeat writer must use the same clock source so timestamps and the `interval*2` staleness math align.
- **Startup gate re-pointing** — `Orchestrator/Program.cs` removes the base `StartupCompletionService` so `/startup` flips on a real completion event, not bare host-start. Re-used verbatim for "flip on Healthy" (D-02).
- **Single-source-of-truth statics** — `L2ProjectionKeys`, `LivenessStatus`, `ProcessorQueues`: use the builders/consts, never re-spell the strings.
- **`cfg.Require` fail-fast config** — `AddBaseConsoleMessaging` reads RabbitMq keys via `cfg.Require` (names the missing key, never the value). New `Interval`/`Ttl`/retry knobs should follow the same fail-fast posture where required.

### Integration Points
- New project `src/BaseProcessor.Core/` referencing `BaseConsole.Core` + `Messaging.Contracts` (NOT `BaseApi.Service` — firewall: the processor talks to the WebApi only over the bus).
- The `IRequestClient`s register inside the `AddBaseConsoleMessaging` `configureConsumers` lambda (or alongside it) — first request/response usage on the console side (RPC-04).
- The heartbeat worker writes via the soft-dep `IConnectionMultiplexer` from `AddBaseConsole`.
- Phase 27 will add the dispatch consumer reading `IProcessorContext` (D-06) for its `queue:{processorId:D}` bind — the context holder is the seam between this phase and the next.

</code_context>

<specifics>
## Specific Ideas

- The processor is the **external self-registrar** the v3.4.0 reader has always expected — the success test is a closed loop: this phase's writer output deserializes cleanly through the unchanged `ProcessorLivenessValidator` and reads as "live."
- "Healthy" has a precise meaning here: **identity resolved AND all required (non-null input/output) definitions resolved.** Null optional schema Ids are by design, not unhealthy (LIVE-04 / SCHEMA-02). Config schema is never resolved.
- Boot-before-register is a first-class tolerated state: the processor can start before its Postgres `Processor` row exists; it retries identity until the row is registered (the operator registers the row manually using the hash read off the built binary — but that hash-embed mechanism is Phase 28; this phase reads whatever the assembly metadata provides, behind a stubbable seam).
- Mirror Phase 18's standalone-validation discipline: `BaseProcessor.Core` ships proven by in-memory tests, with the concrete `Processor.Sample` deferred to Phase 28.

</specifics>

<deferred>
## Deferred Ideas

- **Execution round-trip** — `queue:{processorId:D}` consumer, L2 input resolution + input validation, the `abstract ProcessAsync` **invocation**, per-result output validation + L2 data write + `ExecutionResult` sends, ack-after-send/business-ack/infra-throw — **Phase 27** (the seam is *declared* here, *wired* there).
- **SourceHash embed mechanism** — the MSBuild `BeforeTargets=CoreCompile` SHA-256 target + `[assembly: AssemblyMetadata("SourceHash", …)]` emit — **Phase 28**. This phase only *reads* assembly metadata (behind a seam).
- **Concrete `Processor.Sample`** — dummy `ProcessAsync`, multistage Dockerfile, compose tier, real-stack E2E + 3-GREEN/triple-SHA close gate — **Phase 28**.
- **Config re-validation in the processor; cleanup-on-read of execution-data keys; step-to-step output-data forwarding on the wire; real (non-dummy) transform logic** — out of scope this milestone (PROJECT.md "Out of scope").

</deferred>

---

*Phase: 26-baseprocessor-core-library-identity-liveness*
*Context gathered: 2026-06-01*
