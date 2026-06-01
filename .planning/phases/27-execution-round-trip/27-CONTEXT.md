# Phase 27: Execution Round-Trip - Context

**Gathered:** 2026-06-01
**Status:** Ready for planning

<domain>
## Phase Boundary

A **Healthy** `BaseProcessor.Core` processor runs the full live execution round-trip with the framework owning all id-minting, validation, L2 I/O, and sending — so a concrete overrides **only** `ProcessAsync`:

1. Consumes a real `EntryStepDispatch` off a **durable** `queue:{processorId:D}` competing-consumer endpoint, bound only once Healthy (and `"Healthy"` written to L2 only after the bind).
2. Reads + existence-checks input from `L2[data(entryId)]`, validates it against `inputDefinition` (empty skips).
3. Invokes the `abstract ProcessAsync(string inputData, string config, CancellationToken ct)` seam (`config` = dispatch `Payload`).
4. Per result: output-validates vs `outputDefinition`, mints a new `entryId` + writes the output to `L2[data(newEntryId)]` (TTL'd), mints a per-result `executionId`, builds one `ExecutionResult`.
5. Sends `ExecutionResult`s to `queue:orchestrator-result` one-by-one; acks the dispatch only after all sends; business-ack / infra-throw (`Immediate(3)`); inherited body `CorrelationId`.

Covers: EXEC-01..EXEC-10, CONFIG-02.

**Not this phase (locked out):**
- The MSBuild SourceHash **embed** target, the concrete `Processor.Sample`, its Dockerfile/compose tier, and the real-stack E2E close gate — **Phase 28**.

**Untouched (no regression / reused unchanged):**
- The v3.4.0 `Orchestrator` (`ResultConsumer` / `StepDispatcher` / `StepAdvancement`), the `EntryStepDispatch` / `ExecutionResult` wire contracts, `StepOutcome`, and `ProcessorLivenessValidator` — all reused as-is. This phase is the processor half that completes the loop.
- The P26 startup orchestrator + `IProcessorContext` + heartbeat are **extended** (a bind step is inserted before `MarkHealthy`), not rewritten.

</domain>

<decisions>
## Implementation Decisions

### Queue bind + Healthy/liveness sequencing (EXEC-01)

- **D-01:** The `queue:{Id:D}` endpoint name is only known after Loop A resolves identity, so it is **NOT** a static startup endpoint. The dispatch consumer is registered **without** a static auto-bound receive endpoint (so MassTransit does not declare a wrong-named queue at bus start — known gotcha: suppress default endpoint auto-config for this consumer).
- **D-02:** In the **startup orchestrator**, after Loop A+B resolve, dynamically bind the runtime endpoint via MassTransit `IBus.ConnectReceiveEndpoint("queue:{Id:D}", …)` — **durable**, plain-named (→ competing-consumer across replicas), `UseMessageRetry(r => r.Immediate(3))`, dispatch consumer attached. `await handle.Ready` before proceeding.
- **D-03 (sequencing — the load-bearing order):** `ConnectReceiveEndpoint` + `await Ready` **THEN** `context.MarkHealthy()`. Because the heartbeat writes L2 only when `IsHealthy`, the `"Healthy"` liveness key necessarily appears **after** the bind completes — satisfying EXEC-01 "Healthy written to L2 only after the bind." The orchestrator (admits only Healthy) therefore never sends to a non-existent queue.
- **D-04 (restart safety, falls out of D-03):** A restarting/unhealthy replica is not-Healthy → does not bind → does not consume → dispatches sit in the **durable** queue (not lost, not processed) until it recovers and re-binds.

### Schema validation reuse + "empty" semantics (EXEC-02/03/05)

- **D-05 (firewall-preserving port):** `Json.Schema` (JsonSchema.Net) + the SSRF-locked `JsonSchemaConfig` live in `BaseApi.Service`, which the processor is **firewalled** from (processor talks to the WebApi only over the bus). **Port a minimal SSRF-locked validator into `BaseProcessor.Core`** — add the `Json.Schema` package ref; mirror `JsonSchemaConfig.DefaultOptions` (pinned dialect + global no-op `$ref` fetcher + `OutputFormat.List`) + the flat-error-string flattening + the parse-guard from `PayloadConfigSchemaValidator`. Rationale: keeps the firewall intact; the surface is small enough that duplication < a new shared-lib coupling. *(Shared `Schema.Validation` lib extraction = deferred refactor — see Deferred.)*
- **D-06 ("empty definition" semantics):** A definition that is **null OR whitespace-only** → **skip validation**. A non-empty-but-unparseable definition (should not normally occur — definitions came validated from the WebApi at create-time) → translate to a `Failed` result via the parse-guard, never a host crash.
- **D-07 (input read — EXEC-02/03):** Input data is read from `L2[data(entryId)]` via the same soft-dep `IConnectionMultiplexer` the heartbeat uses, **existence-checked first**. The dispatch `Payload` is treated as **config, never input**. Non-empty `inputDefinition` + missing/empty `entryId` (no L2 data) → `Failed` result with an error message, **before** `ProcessAsync` is invoked.

### ProcessResult shape + outcome ownership (EXEC-04/05/06/08)

- **D-08 (shape):** `ProcessResult` carries **only an output-data string** — `public sealed record ProcessResult(string OutputData)`. The concrete's transform produces outputs; it does **not** carry/own an outcome.
- **D-09 (framework owns ALL outcomes):**
  - Each returned result → output-validate vs `outputDefinition` (empty = valid) → **pass:** mint `entryId`, write `L2[data(newEntryId)]` with the execution-data TTL, mint `executionId`, build a **`Completed`** `ExecutionResult`; **fail:** **`Failed`** + error message, **nothing written** (so a multi-item batch can yield mixed `Completed`/`Failed`).
  - Empty result list, no exception/cancellation → **ack only**, no message (EXEC-08).
  - `OperationCanceledException` from `ProcessAsync` (token tripped) → one **`Cancelled`** `ExecutionResult`.
  - Any other exception from `ProcessAsync` → one **`Failed`** `ExecutionResult` carrying the exception message (EXEC-08 "Failed including a caught exception").
- **D-10 (concrete cannot emit business outcomes):** A concrete **cannot** emit business-`Failed`/`Cancelled` directly — keeps the seam minimal (BPC-02). Per-item business-failure within a batch (item 3 of 5 bad without throwing the whole batch) is **deferred** (POC scope; throwing fails the whole dispatch as one `Failed`). See Deferred.

### Id minting + round-trip chaining (EXEC-05/06/10)

- **D-11 (`ExecutionResult` field mapping, per result):**
  - `WorkflowId / StepId / ProcessorId` — **inherited from the dispatch**.
  - `CorrelationId` — explicitly copied from the dispatch **body** `CorrelationId` (EXEC-10; mirrors `StepDispatcher` threading it through onto the outbound message).
  - `ExecutionId` — **minted per-result** via `NewId.NextGuid()` (mirrors the dispatch's stated `NewId.NextGuid()` minting; sequential GUIDs).
  - `EntryId` — on success, the **newly-minted output entryId** (the same id as the `L2[data(newEntryId)]` write key).
- **D-12 (the chain linkage — confirmed intended):** The minted output `entryId` flows onto `ExecutionResult.EntryId` so the orchestrator's `ResultConsumer` → `StepDispatcher` dispatches the **next** step pointing at *this* output as *its* input (`L2[data(entryId)]`). This closes the step-to-step round-trip **through L2** (output data is never on the wire — `ExecutionResult` has no output field).
- **D-13 (Failed/Cancelled EntryId):** `Failed`/`Cancelled` results mint **no output** → `EntryId = Guid.Empty` (a per-result `ExecutionId` is still minted). Echoing the inbound input entryId on failure is **not** done (not spec-required).

### Ack / retry discipline (EXEC-07/08/09)

- **D-14 (one-by-one send — EXEC-07):** Results are sent to `queue:orchestrator-result` (`OrchestratorQueues.Result`) **one-by-one** in a loop (individual messages, never a batched list), via `Send` to the named queue.
- **D-15 (ack-after-send + business-ack / infra-throw — EXEC-09):** The dispatch is acked only **after all result sends complete**. Mirror `ResultConsumer`/`ResultConsumerDefinition`: business outcomes (input-missing, output-validation-fail, empty-list, caught transform exception) are handled in-consumer and **acked** (the `Failed`/`Cancelled`/no message is the business signal); only genuine **infra** faults (e.g. a `Send` failure, an L2 write fault that must not be silently dropped) **throw** → bounded `Immediate(3)` retry → `_error`. The exact business-vs-infra line for the L2 output write is a planner/research call (mirror the orchestrator's split).

### Correlation (EXEC-10)

- **D-16:** Body `CorrelationId` is inherited from `BaseConsole.Core`'s inbound/outbound correlation filters — it flows from the dispatch into the log scope and onto every published `ExecutionResult` envelope; the body field is additionally set explicitly per D-11 so the orchestrator's body-correlation reads consistently.

### Configuration (CONFIG-02)

- **D-17:** Execution-data L2 keys have their **own** configurable TTL (seconds), **distinct** from the liveness `Ttl`. Added to the `"Processor"` options section. Exact key name / options class = Claude's discretion (mirror the existing `IntervalSeconds`/`TtlSeconds` `cfg.Require` fail-fast pattern), but locked as a separate value applied on every `L2[data(newEntryId)]` write.

### Claude's Discretion
- Exact file/class layout under `src/BaseProcessor.Core/` for the dispatch consumer, the ported schema validator, and the result-builder/sender (mirror existing folder conventions: `Processing`, `Liveness`, `Startup`, `Configuration`).
- How the dispatch consumer is registered to avoid static auto-binding (e.g. `AddConsumer` with endpoint config suppressed, vs a manual consumer factory in `ConnectReceiveEndpoint`) — planner to confirm against MassTransit semantics.
- The precise business-vs-infra classification of an L2 output-write fault (D-15) — mirror `WorkflowLifecycle.IsBusiness`.
- The CONFIG-02 TTL key name, default value, and whether it lives on `ProcessorLivenessOptions` or a sibling options class.
- Test strategy: drive the consumer with the MassTransit in-memory test harness against a real/fake Redis (mirror P26's standalone-validation discipline; concrete `Processor.Sample` + real-stack E2E are Phase 28).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` §"Phase 27: Execution Round-Trip" — goal, 5 success criteria, `Depends on: Phase 26`.
- `.planning/REQUIREMENTS.md` — EXEC-01..EXEC-10, CONFIG-02 (exact wording locks the contracts).
- `.planning/PROJECT.md` §"Current Milestone: v3.5.0" — round-trip narrative + "Key context / locked decisions" + "Out of scope (deferred)".

### The orchestrator mirror (business-ack / infra-throw / one-by-one Send / Immediate(3) — mirror these patterns)
- `src/Orchestrator/Consumers/ResultConsumer.cs` — the business-ack vs infra-throw split to mirror (D-15); L1-only graceful-ack discipline.
- `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` — `EndpointName` + `UseMessageRetry(Immediate(3))` (the retry posture to mirror on the dispatch endpoint, D-02).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` — `Send` (NOT `Publish`) to `queue:{id:D}`, id/correlation threading (D-11/D-14 mirror).
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` §`IsBusiness` — the business-vs-infra classifier to mirror (D-15).

### Frozen wire contracts & L2 keys (Phase 25 — consume unchanged)
- `src/Messaging.Contracts/EntryStepDispatch.cs` — inbound message; `Payload` = config; `CorrelationId`/`ExecutionId`(empty)/`EntryId` fields.
- `src/Messaging.Contracts/ExecutionResult.cs` — outbound message; **no output field** (round-trip is through L2, D-12); `Outcome` + `ErrorMessage`/`CancellationMessage` + correlation/execution/entry ids.
- `src/Messaging.Contracts/StepOutcome.cs` — `Completed=1 / Failed=2 / Cancelled=3` (the outcomes D-09 sets).
- `src/Messaging.Contracts/OrchestratorQueues.cs` — `Result` = `"orchestrator-result"` (the send target, D-14).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData(Guid entryId)` = `skp:data:{entryId:D}` (input read + output write key).

### Schema validation to port (firewalled — copy the pattern, not the reference)
- `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs` — the SSRF-locked `DefaultOptions` (pinned dialect + no-op `$ref` fetcher + List output) to mirror into the processor (D-05).
- `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` — the parse-guard + `JsonSchema.FromText` + flat-error-string flattening pattern to mirror (D-05/D-06).

### P26 seams this phase extends (read to extend, not rewrite)
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` — `WhenHealthy` latch + `IsHealthy` + `Id`/definitions; the bind-after-Healthy await seam (D-02/D-03); WR-03 memory-visibility invariant.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — where the `ConnectReceiveEndpoint` + bind-then-`MarkHealthy` step is inserted (D-02/D-03).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — `AddBaseProcessor` composition root; where the dispatch consumer (no static endpoint) + the CONFIG-02 TTL knob are wired (D-01/D-17).
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` + `ProcessResult.cs` — the seam to invoke + the `ProcessResult` record to firm up to `(string OutputData)` (D-08).
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — the `IConnectionMultiplexer` write pattern the L2 input-read/output-write mirror (D-07/D-09).

### Prior context
- `.planning/phases/26-baseprocessor-core-library-identity-liveness/26-CONTEXT.md` — D-06 (context holder forward-fit for this phase), D-12 (seam declared there, invoked here).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`IProcessorContext` (`WhenHealthy` + `IsHealthy` + `Id`/definitions)** — explicitly forward-fit in P26 for this phase's bind-after-Healthy await. No new context plumbing needed.
- **`ProcessorStartupOrchestrator`** — already runs Loop A+B then `MarkHealthy`; this phase inserts the `ConnectReceiveEndpoint` bind between Loop B and `MarkHealthy`.
- **`ProcessorLivenessHeartbeat`** — the exact `IConnectionMultiplexer.GetDatabase().StringSet/StringGet` + `L2ProjectionKeys` pattern to mirror for L2 input-read / output-write.
- **Orchestrator `ResultConsumer` / `StepDispatcher` / `ResultConsumerDefinition`** — the verbatim business-ack / infra-throw + `Immediate(3)` + `Send`-to-named-queue precedent; this phase is the symmetric processor half.
- **`PayloadConfigSchemaValidator` + `JsonSchemaConfig`** — the SSRF-locked `Json.Schema` validation pattern to port (cannot reference directly — firewall).

### Established Patterns
- **Send (NOT Publish) to `queue:{id:D}` / named queue** — `StepDispatcher` precedent; results go to `OrchestratorQueues.Result` the same way.
- **`NewId.NextGuid()` minting** — the dispatch's correlation id is minted this way; per-result `executionId`/output `entryId` mirror it.
- **`cfg.Require` fail-fast config** — the new execution-data TTL knob follows the existing `IntervalSeconds`/`TtlSeconds` posture.
- **Business-ack vs infra-throw** — `WorkflowLifecycle.IsBusiness` is the canonical classifier; the dispatch consumer mirrors it.
- **Soft-dep Redis** — Redis faults are log-and-continue for liveness, but an L2 **output write** fault on the execution path is load-bearing (an un-written output breaks the chain) → planner classifies it (D-15).

### Integration Points
- The dispatch consumer (new) lives in `BaseProcessor.Core`, registered via `AddBaseProcessor` WITHOUT a static endpoint; bound at runtime by the startup orchestrator (D-01/D-02).
- L2 input-read and output-write go through the soft-dep `IConnectionMultiplexer` from `AddBaseConsole`.
- Result sends go through `ISendEndpointProvider` to `queue:orchestrator-result` (mirrors `StepDispatcher`).
- The ported schema validator is a new `BaseProcessor.Core` type + a new `Json.Schema` package ref on the project.

</code_context>

<specifics>
## Specific Ideas

- The round-trip closes **through L2, never on the wire**: this processor writes output to `L2[data(newEntryId)]` and puts `newEntryId` on `ExecutionResult.EntryId`; the orchestrator forwards that entryId to the next step's dispatch, whose processor reads `L2[data(entryId)]`. `ExecutionResult` deliberately has no output field (P25).
- "Healthy written to L2 only after the bind" is achieved structurally, not by a flag: bind → `await Ready` → `MarkHealthy()` → heartbeat's gate (`IsHealthy`) now passes → first liveness write. The ordering is the guarantee.
- The framework owns every outcome; the concrete's `ProcessAsync` only produces `(string OutputData)` results. Mixed batches (some output-valid, some not) yield mixed `Completed`/`Failed` `ExecutionResult`s — all framework-determined.
- Mirror P26/P18 standalone-validation discipline: prove the round-trip with the MassTransit in-memory harness + a real/fake Redis; the concrete `Processor.Sample` and real-stack E2E are Phase 28.

</specifics>

<deferred>
## Deferred Ideas

- **Per-item business-failure within a batch** — letting a concrete signal that item 3 of 5 is a business `Failed` without throwing the whole batch. Deferred (POC scope; D-10). A throwing `ProcessAsync` currently fails the whole dispatch as one `Failed`.
- **Shared `Schema.Validation` library** — extracting the `Json.Schema` SSRF-locked validator into a lib shared by `BaseApi.Service` + `BaseProcessor.Core` instead of porting (D-05). Deferred refactor — avoids new-shared-project churn this milestone.
- **Echoing the inbound input entryId onto Failed/Cancelled results** (instead of `Guid.Empty`, D-13) — not spec-required; revisit only if a failure-branch needs to re-read the original input.
- **SourceHash embed target, concrete `Processor.Sample`, Dockerfile/compose tier, real-stack E2E + 3-GREEN/triple-SHA close gate** — **Phase 28**.
- **Config re-validation in the processor; cleanup-on-read of execution-data keys; step-to-step output-data forwarding on the wire; real (non-dummy) transform logic** — out of scope this milestone (PROJECT.md "Out of scope").

</deferred>

---

*Phase: 27-execution-round-trip*
*Context gathered: 2026-06-01*
