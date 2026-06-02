# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- ✅ **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-24 + 24.1 (shipped 2026-06-01) — see [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md)
- 🚧 **v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip** — Phases 25-28 (in progress)

## Phases (shipped milestones)

<details>
<summary>✅ v3.2.0 Steps API MVP (Phases 1-11) — SHIPPED 2026-05-28</summary>

11 phases / 41 plans / 142 integration facts GREEN × 3 consecutive runs. Full phase details, decisions, and execution narrative archived to [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md).

- [x] Phase 1: Repository Scaffold (3/3 plans) — 2026-05-26
- [x] Phase 2: Postgres + Docker Compose (2/2 plans) — 2026-05-26
- [x] Phase 3: EF Core Persistence Base (2/2 plans) — 2026-05-27
- [x] Phase 4: Cross-Cutting Middleware + Error Handling (2/2 plans) — 2026-05-27
- [x] Phase 5: Observability + Health Probes (2/2 plans) — 2026-05-27
- [x] Phase 6: Validation + Mapping Base (2/2 plans) — 2026-05-27
- [x] Phase 7: Generic HTTP Base + Composition Root (2/2 plans) — 2026-05-27
- [x] Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests (8/8 plans) — 2026-05-28
- [x] Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop (3/3 plans) — 2026-05-28
- [x] Phase 10: Remove SchemaId on AssignmentEntity, add ConfigSchemaId on ProcessorEntity (5/5 plans) — 2026-05-28
- [x] Phase 11: Migrate Prometheus + Elasticsearch from compose stack sk2_1 to sk_p (10/10 plans) — 2026-05-28

</details>

<details>
<summary>✅ v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline (Phases 12-16) — SHIPPED 2026-05-29</summary>

5 phases / 26 plans / 235 integration facts GREEN × 3 consecutive runs, dual-SHA (`psql \l` + `redis-cli --scan`) BEFORE=AFTER held. 64/64 requirements satisfied (audit PASSED). Full phase details, success criteria, and decisions archived to [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md).

- [x] Phase 12: Redis infra + composition + healthcheck + DI registration (8/8 plans) — 2026-05-29
- [x] Phase 13: OrchestrationService split + L3 fetch + L1 build (3/3 plans) — 2026-05-29
- [x] Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) (5/5 plans) — 2026-05-29
- [x] Phase 15: L2 Redis projection write + Stop existence check (5/5 plans) — 2026-05-29
- [x] Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout (5/5 plans) — 2026-05-29

</details>

<details>
<summary>✅ v3.4.0 BaseConsole + Orchestrator Messaging (Phases 17-24 + 24.1) — SHIPPED 2026-06-01</summary>

9 phases / 31 plans. A reusable `BaseConsole.Core` Generic-Host library + a runnable `Orchestrator` console connected to the WebApi over MassTransit/RabbitMQ, with body-carried CorrelationId proven end-to-end (HTTP → Redis L2 → fan-out → orchestrator log in Elasticsearch), the full orchestrator lifecycle (L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown), the processor→orchestrator result round-trip + L1-only step advancement, and a gating redesign (L2-existence dedup, boot-gate/plugin removal, atomic Stop). Final clean-build suite 335/335 GREEN (real-stack E2E live), Release 0 warnings. 70/70 requirements (ORCH-GATE-01 superseded by 24.1). Milestone audit PASSED. Full phase details archived to [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md).

- [x] Phase 17: Messaging.Contracts + Shared L2 Root Extract (2/2 plans) — 2026-05-30
- [x] Phase 18: BaseConsole.Core Library (4/4 plans) — 2026-05-30
- [x] Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier (4/4 plans) — 2026-05-30
- [x] Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout (4/4 plans) — 2026-05-31
- [x] Phase 21: v3.4.0 Closeout Hygiene — shared L2ProjectionKeys (1/1 plan) — 2026-05-31
- [x] Phase 22: L2 Root-Parent Restructure + Processor Self-Registration (5/5 plans) — 2026-05-31
- [x] Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown (5/5 plans) — 2026-05-31
- [x] Phase 24: Orchestrator Result-Consume & Step Advancement (5/5 plans) — 2026-06-01
- [x] Phase 24.1: Gating Redesign — L2-dedup + Gate Removal (gap-closure) (1/1 plan) — 2026-06-01

</details>

## 🚧 v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip (In Progress)

**Milestone Goal:** Stand up a reusable `BaseProcessor.Core` library + a first concrete `Processor.Sample` console (the processor-side mirror of `BaseConsole.Core`/`Orchestrator`) that self-identifies via an assembly-embedded SourceHash, self-registers its liveness into Redis L2 (only-when-Healthy, lock-free), and runs the full live orchestrator→processor→orchestrator execution round-trip — with the actual transform isolated to one minimal `abstract ProcessAsync` seam. The v3.4.0 `Orchestrator`, the `EntryStepDispatch`/`ExecutionResult` wire contracts, and `ProcessorLivenessValidator` are all reused **unchanged**.

**Build order (locked, mirrors the v3.4.0 leaf→base→wiring→concrete→proof cadence):**
25 (leaf shared contracts + WebApi responders) → 26 (`BaseProcessor.Core`: library + identity + two-loop startup + liveness worker) → 27 (execution round-trip) → 28 (SourceHash MSBuild identity + `Processor.Sample` + real-stack E2E closeout).

- [x] **Phase 25: Shared Contracts + WebApi Responders** — Leaf contract extracts (`ProcessorProjection` public, `ExecutionData` key, `"Healthy"` constant, 2 request/response pairs) + relax the WebApi publish-only firewall to host `GetProcessorBySourceHash` + `GetSchemaDefinition` responders. (completed 2026-06-01)
- [x] **Phase 26: BaseProcessor.Core — Library, Identity & Liveness** — Reusable Generic-Host scaffold on `BaseConsole.Core`; two-loop startup (identity-by-SourceHash + schema-definition resolution via `IRequestClient` with retry); only-when-Healthy liveness heartbeat worker into Redis L2. (completed 2026-06-01)
- [ ] **Phase 27: Execution Round-Trip** — Durable `queue:{processorId:D}` consumer bound at Healthy; L2 input resolution + input validation; the `abstract ProcessAsync` seam; per-result output validation + L2 data write + result minting + one-by-one `ExecutionResult` sends; ack-after-send / business-ack / infra-throw; inherited correlation.
- [ ] **Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout** — MSBuild SourceHash target (SHA-256, lowercase 64-hex, LF-normalized, folded over base+concrete `.cs`) + assembly-metadata embed; first concrete `Processor.Sample` (dummy `ProcessAsync` + multistage Dockerfile + compose tier); real-stack E2E round-trip proof + 3-GREEN/triple-SHA close gate.

## Phase Details

### Phase 25: Shared Contracts + WebApi Responders
**Goal**: The leaf shared-contract vocabulary both sides depend on exists in `Messaging.Contracts`, and the WebApi can answer identity + schema-definition bus requests — so the processor (built later) has something to query.
**Depends on**: Phase 24.1 (v3.4.0 close — `Messaging.Contracts`, `L2ProjectionKeys`, `ProcessorService.GetBySourceHashAsync`, and the publish-only `AddBaseApiMessaging` firewall all in place)
**Requirements**: CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03
**Success Criteria** (what must be TRUE):
  1. `ProcessorProjection` is public in `Messaging.Contracts.Projections` and is the single shared type used by both WebApi and (later) the processor — no duplicate definition.
  2. `L2ProjectionKeys.ExecutionData(Guid entryId)` returns `skp:data:{entryId:D}`, distinct from the existing `root`/`step`/`processor` key builders, with a golden test pinning the exact string.
  3. The liveness `status` value `"Healthy"` is a shared constant in `Messaging.Contracts` (single source of truth — writer and reader cannot desync), and the two `GetProcessorBySourceHash` / `GetSchemaDefinition` request/response record pairs are defined there.
  4. The WebApi bus join, extended from publish-only, answers a `GetProcessorBySourceHash` request with `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (or a not-found response) backed by `ProcessorService.GetBySourceHashAsync`, and a `GetSchemaDefinition(schemaId)` request with `{ Definition }` (or not-found) backed by the existing schema read.
  5. The CRUD surface is unaffected — existing WebApi HTTP behavior and the v3.4.0 publish path are unchanged (no regression in the existing suite).
**Plans**: 2 plans
- [x] 25-01-PLAN.md — Shared contract extracts in Messaging.Contracts (ProcessorProjection move, ExecutionData key, Healthy const, request/response record pairs + queue constants)
- [x] 25-02-PLAN.md — WebApi responder host (two-hook bus join extension + GetProcessorBySourceHash / GetSchemaDefinition dual-response consumers, firewall + Degraded-cap preserved)

### Phase 26: BaseProcessor.Core — Library, Identity & Liveness
**Goal**: A reusable `BaseProcessor.Core` library exists on which a concrete processor self-identifies via its embedded SourceHash, resolves its identity + schema definitions over the bus (retrying through boot-before-register), and self-registers liveness into Redis L2 — only while Healthy, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.
**Depends on**: Phase 25 (the request/response contracts + WebApi responders must exist before the processor can resolve identity/definitions)
**Requirements**: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01
**Success Criteria** (what must be TRUE):
  1. `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inheriting soft-dep Redis, embedded health probes, metrics-only OTel, MassTransit/RabbitMQ, inbound/outbound correlation filters), and `AddBaseProcessor` wires the startup orchestration so a concrete `Program.cs` stays minimal.
  2. At runtime the processor reads its SourceHash from assembly metadata via reflection, then resolves its identity (`Id` + three nullable schema Ids) by issuing a `GetProcessorBySourceHash` `IRequestClient` query, retrying on timeout/not-found until it succeeds (booting before the DB row exists is tolerated).
  3. For each non-null (input, output) schema Id the processor resolves the definition via a `GetSchemaDefinition` `IRequestClient` query (retry until resolved); null/optional schema Ids are skipped by design and never cause failure (the config schema is not resolved).
  4. A background heartbeat worker writes/refreshes `skp:{processorId:D}` every `Interval` seconds with `{ inputDefinition, outputDefinition, liveness{ timestamp, interval, status: "Healthy" } }`, re-applying the configured `Ttl` expiry each beat (sliding) — written **only once Healthy** (identity + all required definitions resolved); a starting/restarting/unhealthy replica does not write (orchestrator sees `absent`), and the written `interval` equals the configured delay so `timestamp + interval×2` staleness holds.
  5. The written L2 value shape exactly matches what the v3.4.0 `ProcessorLivenessValidator` reads (reused unchanged — presence+freshness ⟺ "≥1 replica healthy"), and multi-replica writes are lock-free: the shared liveness key is a blind whole-value `SET` of equivalent only-when-Healthy content (last-write-wins, no synchronization).
**Plans**: 3 plans
  - [x] 26-01-PLAN.md — Project skeleton + identity/seam/options contracts + Wave 0 test scaffold + exchange: request-client confirmation (BPC-01, BPC-02, IDENT-03, CONFIG-01) — completed 2026-06-01
  - [x] 26-02-PLAN.md — AddBaseProcessor composition root + two-loop startup orchestrator (BPC-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02) — completed 2026-06-01
  - [x] 26-03-PLAN.md — Only-when-Healthy liveness heartbeat worker + closed reader round-trip (LIVE-01..06) — completed 2026-06-01

### Phase 27: Execution Round-Trip
**Goal**: A Healthy processor consumes a real `EntryStepDispatch`, resolves + validates its input from L2, runs the `abstract ProcessAsync` transform, validates + writes each output to L2, mints results, and sends `ExecutionResult`s back to the orchestrator one-by-one — with the framework owning all id-minting, validation, L2 I/O, and sending so a concrete overrides only `ProcessAsync`.
**Depends on**: Phase 26 (the processor must resolve identity + definitions and be Healthy before it can bind the dispatch queue and validate input/output)
**Requirements**: EXEC-01, EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02
**Success Criteria** (what must be TRUE):
  1. The processor consumes `EntryStepDispatch` on a **durable** `queue:{processorId:D}` competing-consumer endpoint that is bound only once Healthy (and `Healthy` is written to L2 only after the bind), so the orchestrator never sends to a non-existent queue; a restarting/unhealthy processor leaves dispatches queued (not lost, not processed) until it recovers.
  2. Input data is read only from `L2[data(entryId)]` (existence-checked; `Payload` is config, never input) and validated against `inputDefinition` when present — an empty definition skips validation; a non-empty definition with missing/empty `entryId` yields a `Failed` result with an error message.
  3. The sole transform seam is `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` (`config` = dispatch `Payload`); for each result the framework validates output vs `outputDefinition` (empty = valid), mints a new `entryId` + writes the output to `L2[data(newEntryId)]` with the configured execution-data TTL on success (nothing written on output-validation failure → `Failed`), and mints a per-result `executionId` + stamps the shared Ids.
  4. Results are sent to `queue:orchestrator-result` one-by-one (never a batched list); an empty result list with no exception/cancellation acks only (no message), while `Failed` (incl. caught exceptions, with an error message) and `Cancelled` are always sent.
  5. The dispatch is acked only after all sends complete; infra faults throw and retry (`Immediate(3)`) (business-ack / infra-throw, mirroring the orchestrator), and the body `CorrelationId` is inherited from `BaseConsole.Core` — flowing from the dispatch into the log scope and onto every published `ExecutionResult`.
**Plans**: 3 plans
- [x] 27-01-PLAN.md — Foundation: firm ProcessResult + BaseProcessor internal invoker, port SSRF-locked Json.Schema validator, add CONFIG-02 ExecutionDataTtlSeconds (EXEC-03/04/05, CONFIG-02) — completed 2026-06-01
- [x] 27-02-PLAN.md — EntryStepDispatchConsumer: L2 input read/validate, ProcessAsync invoke, per-result output-validate/mint/write, one-by-one ExecutionResult send, business-ack/infra-throw (EXEC-02/04/05/06/07/08/09/10) — completed 2026-06-01
- [x] 27-03-PLAN.md — Wiring: register consumer (ExcludeFromConfigureEndpoints) + runtime ConnectReceiveEndpoint bind-then-MarkHealthy (EXEC-01) — completed 2026-06-02

### Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout
**Goal**: The deterministic build-time SourceHash identity is embedded into the assembly, the first concrete `Processor.Sample` exists and joins the compose stack, and a real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the liveness-gated Start — all behind the 3-GREEN / triple-SHA close gate.
**Depends on**: Phase 27 (the full framework round-trip must exist before a concrete + E2E can exercise it end-to-end)
**Requirements**: IDENT-01, IDENT-02, SAMPLE-01, SAMPLE-02, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):
  1. An MSBuild target (`BeforeTargets=CoreCompile`) computes the SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded deterministically (ordinal path sort) over `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`) — emits it as `[assembly: AssemblyMetadata("SourceHash", …)]`, and re-runs on implementation source change (no stale hash on incremental builds).
  2. `Processor.Sample` is the first concrete console (family convention `Processor.<Purpose>`), implementing `ProcessAsync` with a minimal POC dummy result list and carrying no infrastructure/id/L2/bus code.
  3. `Processor.Sample` ships a multistage Dockerfile and joins the compose stack (mirroring the Orchestrator tier), and its built binary's embedded SourceHash (lowercase 64-hex, satisfying the DB `^[a-f0-9]{64}$` validator) is the value registered as the Processor DB row via CRUD.
  4. A real-stack E2E proves the live round-trip — a dispatch is consumed, output is written to L2, and the orchestrator advances on the returned `ExecutionResult` — and proves the liveness-gated Start path (a live `Processor.Sample` heartbeat lets orchestration Start pass).
  5. The phase-close gate holds: 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER, with scan-clean teardown covering the new processor-liveness and execution-data keys.
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 25 → 26 → 27 → 28

| Phase | Milestone | Plans Complete | Status   | Completed  |
| ----- | --------- | -------------- | -------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete | 2026-05-28 |
| 12-16 | v3.3.0    | 26/26          | Complete | 2026-05-29 |
| 17    | v3.4.0    | 2/2            | Complete | 2026-05-30 |
| 18    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 19    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 20    | v3.4.0    | 4/4            | Complete | 2026-05-31 |
| 21    | v3.4.0    | 1/1            | Complete | 2026-05-31 |
| 22    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 23    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 24    | v3.4.0    | 5/5            | Complete | 2026-06-01 |
| 24.1  | v3.4.0    | 1/1            | Complete | 2026-06-01 |
| 25. Shared Contracts + WebApi Responders | v3.5.0 | 2/2 | Complete    | 2026-06-01 |
| 26. BaseProcessor.Core — Library, Identity & Liveness | v3.5.0 | 3/3 | Complete    | 2026-06-01 |
| 27. Execution Round-Trip | v3.5.0 | 3/3 | Complete | 2026-06-02 |
| 28. SourceHash Identity + Processor.Sample + E2E Closeout | v3.5.0 | 0/TBD | Not started | - |

---
*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 shipped 2026-06-01 (9 phases 17-24+24.1, BaseConsole + Orchestrator Messaging). v3.5.0 STARTED 2026-06-01 (4 phases 25-28, Processor Console — `BaseProcessor.Core` + `Processor.Sample`, assembly-embedded SourceHash, WebApi bus responders, L2 liveness self-registration, live execution round-trip; build order 25→26→27→28). 38/38 requirements mapped.*
