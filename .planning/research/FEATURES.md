# Feature Research — MassTransit/RabbitMQ Messaging (v3.4.0)

**Domain:** Message-driven console worker platform (.NET 8 modular monolith) — WebApi publisher → Orchestrator consumer over MassTransit/RabbitMQ
**Researched:** 2026-05-30
**Confidence:** HIGH (all core mechanisms verified against official MassTransit docs via Context7: masstransit.massient.com — Publish/Send topology, InstanceId fan-out, ack/redelivery, request/response, correlation conventions)

> **Scope discipline.** This file maps MassTransit behaviors to THIS milestone (v3.4.0) vs FUTURE (Processor milestone). The locked milestone decisions in `PROJECT.md` are treated as the requirement spine; research **confirms/refines** the idiomatic MassTransit way to honor them. Everything Quartz-, Processor-, or round-trip-related is explicitly an **ANTI-FEATURE for now** (= deliberately not built this milestone), not "bad."

---

## How the Four Mechanisms Actually Work (verified primer)

These four findings drive the categorization below. Confidence HIGH unless noted.

### 1. Fan-out (broadcast to all replicas) vs Load-balanced (competing consumers)

The distinction is **entirely a receive-endpoint topology choice**, not a publish-side choice. RabbitMQ semantics:

- **`Publish<T>(msg)`** → sends to the **exchange** for type `T`. Every queue bound to that exchange gets a copy. *Who receives* depends on how many distinct queues are bound.
- **`Send(addr, msg)`** → sends to **one specific queue**. If N consumers share that one queue, RabbitMQ round-robins (competing consumers = load-balancing). Verified: "RabbitMQ supports competing consumers... messages are dispatched to consumers, locked while being processed, and removed from the queue once successfully handled."

**Fan-out to all N replicas (THIS milestone, WebApi→Orchestrator):**
The idiomatic MassTransit mechanism is **`InstanceId`** on the receive endpoint. Verified quote from the configuration reference: *InstanceId* "If specified, should be unique for each bus instance — **to enable fan-out (instead of load balancing).**" Each replica sets a unique `InstanceId`, which the endpoint-name formatter appends to the queue name, so every replica binds its **own** distinct queue to the shared `StartOrchestration` exchange. `Publish` then reaches a copy on every replica. With 1 replica today this is a single instance-suffixed queue; scaling to N replicas needs zero code change — each new pod just self-assigns a unique InstanceId.

```csharp
// Orchestrator (consumer side) — fan-out: unique queue per replica
x.AddConsumer<StartOrchestrationConsumer>();
x.AddConsumer<StopOrchestrationConsumer>();
x.UsingRabbitMq((context, cfg) =>
{
    // InstanceId makes the queue name unique per replica → each binds its own queue → Publish fans out
    cfg.ConfigureEndpoints(context);
    // InstanceId supplied per-endpoint or via the formatter; e.g. pod name / Environment.MachineName / Guid
});
// WebApi (publisher side) — just Publish; topology decides fan-out
await publishEndpoint.Publish<StartOrchestration>(new { WorkflowIds = ids });
```

> **Refinement flagged for STACK/ARCHITECTURE:** the exact wiring of InstanceId (per-endpoint `.Endpoint(e => e.InstanceId = …)` vs a formatter that injects it vs `ConnectReceiveEndpoint(new TemporaryEndpointDefinition())`) is the one config detail to pin down in the phase plan. The endpoint must also be **non-durable / auto-delete (temporary)** so dead replicas don't leave orphan queues accumulating messages in RabbitMQ. MassTransit exposes a `Temporary`/auto-delete flag for exactly this fan-out case (`TemporaryEndpointDefinition` exists for the dynamic variant). MEDIUM confidence on the best of the 3 wiring variants — resolve in plan.

**Load-balanced send (FUTURE — Processor milestone):**
`Send` to `queue:processorId` and a shared results queue, with N processor replicas sharing one queue = competing consumers. Mechanism understood, **not built now.**

### 2. Request/Response (`IRequestClient<T>` / `GetResponse<TRes>`)

- Caller injects `IRequestClient<TReq>` and `await client.GetResponse<TRes>(req, ct, timeout)`. Returns `Response<TRes>`.
- **Default timeout is 30 seconds** (configurable via `RequestTimeout` per-call or on the client registration). On timeout → `RequestTimeoutException`.
- Responder side: a normal `IConsumer<TReq>` that calls `context.RespondAsync<TRes>(…)`. If the responder **throws**, MassTransit converts it into a `Fault<TReq>` and the awaiting `GetResponse` **throws `RequestFaultException`** rather than hanging.
- Uses a temporary response queue + the `RequestId`/`ResponseAddress` envelope headers under the hood; entirely automatic.

**This is FUTURE** (processor→WebApi `GetProcessorBySourceHash`). Mechanism captured; **not built now.**

### 3. Ack / redelivery / retry semantics (the crash-vs-business-failure distinction)

Verified delivery model (Context7, `concepts/transports` + `concepts/outbox`):

> "The broker locks a message, making it invisible to other consumers. The message remains locked until it is explicitly acknowledged by the consumer or negatively-acknowledged due to a service or network failure. **If a process crashes or a network split occurs, the broker will redeliver the message.**"

So:
- **Consume completes normally → MassTransit acks** (RabbitMQ `basic.ack`); message removed from queue. This is the "ack on success."
- **Consumer throws →** retry policy (if any) runs; once exhausted, the message is **moved to the `_error` queue** and the original is acked-away (it is *not* left on the source queue forever). A `Fault<T>` is published. So a thrown exception is **not** "leave on queue" — it is "dead-letter after retries."
- **Process crash / connection drop mid-Consume → no ack → RabbitMQ redelivers** to the same or another bound queue. This is the genuine "left on queue if the consumer crashed" guarantee.

**Critical design consequence for THIS milestone** (the requirement says "business failures still ack but crashes don't"):

| Outcome | Consumer should… | Result |
|---|---|---|
| Business failure (workflow not in L2, validation fail, pass/fail result) | **Log it and return normally (complete Consume)** — do NOT throw | Message **acked**, not redelivered, not dead-lettered |
| Infrastructure crash (Redis unreachable transiently, process killed, broker drop) | **Let it throw / let the process die** | No ack → broker **redelivers** |

This means the consumer must **catch its own business/domain exceptions, log them at the correlated scope, and complete** — reserving thrown exceptions (and the resulting nack/redelivery) for genuine infrastructure faults. That mapping is a **table-stakes** design requirement, not an optional refinement, because the default ("throw on anything bad") would dead-letter business failures, which the milestone explicitly does not want.

### 4. Correlation: native CorrelationId/ConversationId/InitiatorId vs the explicit domain Guid

Verified (`concepts/messages`, `configuration/topology`):
- Every message rides in an envelope with headers: `MessageId`, `CorrelationId`, `ConversationId`, `InitiatorId`, `RequestId`, plus addresses.
- **Convention:** MassTransit auto-populates the envelope `CorrelationId` header if the message implements `CorrelatedBy<Guid>` **or** has a `Guid`/`Guid?` property literally named `CorrelationId`, `CommandId`, or `EventId`.
- On a consumed→published chain, `ConversationId` is inherited and the inbound `CorrelationId` is copied to the new message's `InitiatorId` — automatic conversation threading.

**Reconciliation with this codebase (the important part):** there are **two different correlation worlds and the milestone deliberately does NOT unify them** (locked decision in PROJECT.md):
1. **HTTP edge:** `X-Correlation-Id` string, `Guid.NewGuid().ToString("N")` format, lives in `CorrelationIdMiddleware` and inside the Redis L2 root value (`WorkflowRootProjection.CorrelationId`, a `string`). This is the value the Orchestrator extracts and logs against this milestone.
2. **Bus world:** MassTransit's envelope `CorrelationId` (`Guid`) and the frozen `ICorrelated` domain vocabulary `{ CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId }` — all `Guid`. The bus `Guid CorrelationId` is **minted by the Quartz scheduler per trigger, which is FUTURE.**

The two are **linked via logs, not merged.** For THIS milestone:
- The `Start/Stop` **control contracts carry NO correlationId on the wire** (locked) — they are bare `WorkflowIds[]`. The Orchestrator obtains the correlation **string** from Redis L2, not from the message envelope.
- The reusable inbound consume filter in `BaseConsole.Core` pushes that string into an AsyncLocal accessor + a MEL log scope under the **literal key `"CorrelationId"`** — deliberately the SAME key `CorrelationIdMiddleware` uses and the same key OTel `IncludeScopes=true` surfaces, so HTTP-side and bus-side log lines correlate in Elasticsearch by identical attribute name. This mirroring is the whole point of the milestone ("CorrelationId propagation proven end-to-end").
- The outbound send/publish filter that stamps the ambient correlationId onto `ICorrelated` messages is built but exercised only by a **synthetic test-harness downstream send** this milestone (no real downstream yet).

> **Subtle naming collision to flag:** the domain `ICorrelated.CorrelationId` is a `Guid`; if a real `ICorrelated` message is ever published, MassTransit's convention will auto-promote that `Guid` to the envelope `CorrelationId` header. That is fine and desirable — but note it is a **different value** from the HTTP `X-Correlation-Id` string. Do not write code that assumes the envelope header equals the L2 root's correlation string. They correlate **only through co-located log lines.**

---

## Feature Landscape

### Table Stakes (must-have for THIS milestone)

| Feature | Why Expected (milestone need) | Complexity | Dependencies |
|---|---|---|---|
| **MassTransit bus skeleton in `BaseConsole.Core`** (`AddBaseConsoleMessaging`) + WebApi joins the bus | Nothing moves without a configured bus on both ends; RabbitMQ in compose | MEDIUM | New RabbitMQ compose tier; reuses existing DI/Generic-Host patterns |
| **`Publish` of `StartOrchestration`/`StopOrchestration` from WebApi** | Locked: WebApi fans Start/Stop to all Orchestrator replicas | LOW | Bus skeleton; `Messaging.Contracts` shared assembly |
| **Per-replica fan-out receive endpoint via `InstanceId`** (unique, temporary/auto-delete queue per replica) | Locked: every replica must receive a copy; topology supports N with 1 today | MEDIUM | `ConfigureEndpoints` + InstanceId wiring; **the one config detail to pin in the plan** |
| **`Messaging.Contracts` assembly** (`StartOrchestration{WorkflowIds[]}`, `StopOrchestration{WorkflowIds[]}` — NO correlationId on wire; `ICorrelated` vocabulary; shared L2 root shape) | Locked vocabulary; one source of truth for the L2 root WebApi writes + Orchestrator reads | LOW–MEDIUM | Extract `WorkflowRootProjection` shape out of `BaseApi.Service` into shared assembly |
| **Orchestrator consumes Start/Stop → reads Redis L2 root per WorkflowId → extracts stored `X-CorrelationId`** | Locked milestone deliverable | MEDIUM | Existing Redis L2 root (`WorkflowRootProjection.CorrelationId`); StackExchange.Redis lifted into `BaseConsole.Core` |
| **Correlated log scope keyed `"CorrelationId"`** (inbound consume filter → AsyncLocal + MEL scope), mirroring `CorrelationIdMiddleware` | Locked: "correlation proven end-to-end (HTTP → L2 → message → orchestrator log in ES)" | MEDIUM | Existing OTel `IncludeScopes=true`; reuse the literal key from `CorrelationIdMiddleware` |
| **Ack-on-success / catch-business-failures-and-complete; throw only on infra fault** | Locked: "ack on success"; business failures must ack, crashes must redeliver | MEDIUM | Correct understanding of MassTransit ack/redelivery (see primer §3); explicit try/catch boundary in the consumer |
| **MassTransit OTel instrumentation in `BaseConsole.Core`** (MEL-bridge logs + runtime + MassTransit instrumentation, no AspNetCore instrumentation) | Console-flavored mirror of `BaseApi.Core` observability; needed to see the correlated logs in ES | MEDIUM | Existing OTel Collector; OpenTelemetry .NET SDK 1.15.x |
| **Embedded minimal-Kestrel health probes; `/ready` flips when the bus has started** | Console worker still needs K8s-style probes; bus-started readiness is the meaningful signal | MEDIUM | MassTransit `IBusHealth` / bus-started signal; mirrors existing health-probe discipline |
| **Outbound send/publish correlation filter** (stamps ambient correlationId onto `ICorrelated`) | Locked: outbound side must exist; exercised via synthetic harness send this milestone | LOW–MEDIUM | AsyncLocal accessor from the inbound filter |

### Differentiators (idiomatic resilience worth a small, bounded amount now)

| Feature | Value Proposition | Complexity | Dependencies / Note |
|---|---|---|---|
| **Bounded message-retry on infra faults** (`UseMessageRetry(r => r.Intervals(…))` or `r.Immediate(n)`, `Ignore<TBusinessException>`) | Transient Redis blip → a few short retries before redelivery/dead-letter, instead of immediate fault; classic MassTransit idiom | LOW | Must `Ignore` the business-failure path so business failures still ack (ties to table-stakes ack rule). Keep intervals tiny. |
| **Honoring the `_error` queue as the dead-letter outcome** (don't suppress it) | Free operational visibility: genuine infra faults land in `orchestrator_error` for inspection, not lost | LOW | Default MassTransit behavior — just don't disable it |
| **`ConcurrentMessageLimit` / `PrefetchCount` tuning on the consumer** | Prevents a single replica grabbing more in-flight Start/Stop than it can correlate-and-log | LOW | Endpoint/consumer definition; sensible defaults are fine for 1 replica |
| **Consumer definition class per consumer** (`StartOrchestrationConsumerDefinition : ConsumerDefinition<…>`) | The clean seam for retry/InstanceId/concurrency config; matches the project's "base + per-entity definition" idiom | LOW | Pure organization; cheap and future-proofs the Processor milestone |

### Anti-Features (deliberately NOT built this milestone)

| Feature | Why It's Tempting | Why Defer / Problem Now | What To Do Instead |
|---|---|---|---|
| **Quartz scheduler + minting the bus `Guid CorrelationId`** | "Correlation should be a Guid on the wire end-to-end" | Locked FUTURE; the bus-world Guid correlation is the scheduler's job and unifying it with the HTTP string is explicitly NOT a goal | Carry NO correlationId on the Start/Stop wire; pull the string from L2; link worlds via logs only |
| **`Send` to `queue:processorId` (load-balanced competing consumers)** | The mechanism is right there and understood | FUTURE Processor milestone; no Processor console exists; no downstream send this milestone | Document the mechanism (done, §1); build only fan-out now |
| **Request/Response `GetProcessorBySourceHash` + WebApi responder** | WebApi already has `GetBySourceHash`; wiring `IRequestClient` looks small | FUTURE; processor→WebApi query has no caller yet; adds a temporary-response-queue surface with nothing using it | Leave `GetBySourceHash` as the existing HTTP endpoint; add the bus responder in the Processor milestone |
| **Concrete `JobTrigger` / `ExecutionResult` records + real downstream/result SEND** | Completes the "round-trip" mental model | FUTURE; the milestone deliverable is *logs up to the scheduler-job-start seam*, no downstream send | Stop the consumer at the "scheduler job start" log seam; exercise outbound filter with a synthetic harness send only |
| **Throwing on business failure (workflow missing in L2, pass/fail result)** | Throwing is the default "something's wrong" reflex | Throwing dead-letters the message to `_error` after retries — the opposite of the locked "business failures still ack" rule | Catch domain exceptions, log at correlated scope, **complete** the Consume (ack). Reserve throw for infra faults only |
| **Persisting Redis writes / new keyspaces from the Orchestrator** | "While we're here, write status back" | Locked: "No Redis writes" this milestone — logs are the deliverable | Read-only L2 access from the Orchestrator |
| **Transactional Outbox (`AddEntityFrameworkOutbox` / in-memory outbox)** | Exactly-once / dedup is a known MassTransit feature | Overkill: WebApi only *publishes* control messages, no DB-transaction-coupled publish; no downstream publish from the Orchestrator yet | Skip the outbox until the Processor round-trip introduces transaction-coupled publishing |
| **Sagas / state machines for orchestration state** | "Orchestration" sounds like a saga | The Orchestrator is a stateless log-to-seam consumer this milestone; saga = large complexity with no state to hold | Plain `IConsumer<T>`; revisit sagas only if/when long-running execution state appears |
| **OTel traces / spans across the bus hop** | Tracing the HTTP→bus→log flow would be elegant | Project has NO traces backend (locked, Phase 11 D-03); logs + the shared `"CorrelationId"` key are the correlation substrate | Correlate via the MEL log scope key in Elasticsearch; traces stay a future-milestone candidate |
| **Durable per-replica fan-out queues** | Durability sounds safer | Durable instance queues orphan and accumulate when replicas die/rescale → unbounded broker growth | Make fan-out endpoints temporary/auto-delete (the InstanceId fan-out idiom) |

---

## Feature Dependencies

```
RabbitMQ compose tier
    └──requires──> Bus skeleton in BaseConsole.Core (AddBaseConsoleMessaging) + WebApi bus join
                       ├──requires──> Messaging.Contracts (StartOrchestration / StopOrchestration / ICorrelated / shared L2 root shape)
                       │                  └──requires──> extract WorkflowRootProjection shape out of BaseApi.Service
                       │
                       ├──enables──> WebApi Publish<Start/Stop>  ──(topology)──> InstanceId fan-out receive endpoint (Orchestrator)
                       │                                                              └──requires──> Redis client lifted into BaseConsole.Core
                       │                                                                                 └──requires──> existing Redis L2 root (correlationId string)
                       │
                       ├──enables──> Inbound consume filter (CorrelationId → AsyncLocal + "CorrelationId" MEL scope)
                       │                  └──enhances──> MassTransit OTel instrumentation ──> correlated logs in Elasticsearch
                       │                  └──enables──> Outbound send/publish filter (stamp ICorrelated) ──exercised by──> synthetic harness send
                       │
                       └──enables──> Health probe /ready flips on bus-started

Ack-on-success rule ──governs──> Orchestrator consumer body (catch business → log → complete; throw only infra)
        └──interacts-with──> bounded UseMessageRetry (must Ignore<TBusinessException>)

[FUTURE] Quartz scheduler ──mints──> bus Guid CorrelationId ──would-flow-through──> ICorrelated wire fields
[FUTURE] Send to queue:processorId (load-balanced) ──requires──> Processor console
[FUTURE] IRequestClient<GetProcessorBySourceHash> ──requires──> WebApi bus responder
```

### Dependency Notes
- **Fan-out endpoint requires the Redis client + L2 root:** the consumer's whole job is read-L2-then-log; without the lifted Redis client and the shared root shape it can't extract the correlation string.
- **Ack rule governs the consumer body and constrains retry config:** `UseMessageRetry` must `Ignore` the business-failure exception type (or business failures must never be expressed as throws) so they don't get retried/dead-lettered.
- **Inbound filter must use the literal `"CorrelationId"` key:** any drift from `CorrelationIdMiddleware`'s key breaks the cross-source correlation in Elasticsearch — this is a hard coupling, not a convention.
- **Contracts assembly is a prerequisite for both publisher and consumer compilation** — earliest phase item.

---

## MVP Definition

### Launch With (THIS milestone, v3.4.0)
- [ ] RabbitMQ compose tier + bus skeleton both ends — nothing works without it
- [ ] `Messaging.Contracts` (control contracts, `ICorrelated` vocab, shared L2 root) — compile prerequisite
- [ ] WebApi `Publish` Start/Stop — the publisher half of fan-out
- [ ] InstanceId fan-out receive endpoint (temporary/auto-delete) — the broadcast topology, N-ready
- [ ] Orchestrator consumer: read L2 → extract corr string → correlated log scope → log to "scheduler job start" seam → ack-on-success
- [ ] Inbound consume filter (`"CorrelationId"` MEL scope) + MassTransit OTel instrumentation — proves the end-to-end correlation in ES
- [ ] Ack semantics: catch business failures + complete; throw only on infra
- [ ] Health probe `/ready` flips on bus-started
- [ ] Outbound correlation filter exercised by a synthetic harness send

### Add After Validation (bounded, optional within milestone)
- [ ] Bounded `UseMessageRetry` on infra faults (with `Ignore<business>`) — add once happy-path correlation is proven green
- [ ] `ConcurrentMessageLimit`/`PrefetchCount` tuning — add if 1-replica behavior shows in-flight pressure
- [ ] Per-consumer `ConsumerDefinition` classes — adopt as the config seam (cheap, future-proofs Processor milestone)

### Future Consideration (Processor milestone, v3.5.x+)
- [ ] Quartz scheduler + bus `Guid CorrelationId` minting — the bus-world correlation source
- [ ] `Send` to `queue:processorId` (load-balanced competing consumers) — orchestrator→processor
- [ ] Processor→orchestrator result `Send` (load-balanced shared results queue)
- [ ] `IRequestClient<GetProcessorBySourceHash>` + WebApi responder — request/response query
- [ ] Concrete `JobTrigger` / `ExecutionResult` records + the live round-trip
- [ ] Transactional outbox — once DB-transaction-coupled publishing appears
- [ ] OTel traces across the bus hop — when request-flow debugging becomes painful (Phase 11 D-03 future candidate)

---

## Feature Prioritization Matrix

| Feature | User/Platform Value | Implementation Cost | Priority |
|---|---|---|---|
| Bus skeleton + RabbitMQ tier | HIGH | MEDIUM | P1 |
| `Messaging.Contracts` (incl. shared L2 root extract) | HIGH | LOW–MEDIUM | P1 |
| WebApi `Publish` Start/Stop | HIGH | LOW | P1 |
| InstanceId fan-out endpoint (temporary) | HIGH | MEDIUM | P1 |
| Orchestrator consumer (read-L2 → correlated log → ack) | HIGH | MEDIUM | P1 |
| Inbound `"CorrelationId"` consume filter + MassTransit OTel | HIGH | MEDIUM | P1 |
| Ack-on-success / business-vs-infra rule | HIGH | MEDIUM | P1 |
| Health `/ready` flips on bus-started | MEDIUM | MEDIUM | P1 |
| Outbound correlation filter (synthetic harness) | MEDIUM | LOW–MEDIUM | P1 |
| Bounded `UseMessageRetry` (Ignore business) | MEDIUM | LOW | P2 |
| `ConsumerDefinition` classes | LOW–MEDIUM | LOW | P2 |
| Concurrency/prefetch tuning | LOW | LOW | P2 |
| Quartz / Send-load-balanced / Request-Response / round-trip | (future value) | HIGH | P3 (FUTURE) |
| Outbox, sagas, traces | LOW (now) | HIGH | P3 (FUTURE) |

**Priority key:** P1 = required to ship v3.4.0 · P2 = bounded refinement within milestone · P3 = FUTURE (Processor milestone or later)

---

## Mechanism Reference (MassTransit term ↔ milestone use)

| MassTransit term | What it does | THIS milestone | FUTURE |
|---|---|---|---|
| `Publish<T>` | exchange-routed; all bound queues get a copy | WebApi Start/Stop fan-out | downstream events |
| `Send(addr,T)` | one queue; N consumers compete (load-balance) | — (anti-feature) | orchestrator→processor `queue:processorId`, results queue |
| `InstanceId` (endpoint) | unique per-replica queue bound to shared exchange → **fan-out instead of load-balance** | the broadcast topology (1 replica, N-ready) | — |
| Temporary/auto-delete endpoint | queue removed when bus stops; no orphan accumulation | fan-out queues | — |
| `IConsumer<T>` Consume completes | broker `basic.ack`; message removed | ack-on-success | — |
| Throw in Consume | retry → `_error` dead-letter + `Fault<T>` | **only** for infra faults | — |
| Process crash / drop mid-Consume | no ack → broker redelivers | crash safety guarantee | — |
| `UseMessageRetry` | bounded retry before fault | optional, Ignore business | — |
| `IRequestClient<T>` / `GetResponse<TRes>` | temp-response-queue req/resp; default 30s timeout; fault→`RequestFaultException` | — (anti-feature) | `GetProcessorBySourceHash` |
| `RespondAsync<TRes>` | responder side | — | WebApi responder |
| Envelope `CorrelationId`/`ConversationId`/`InitiatorId` | conversation threading via headers | NOT used to carry the HTTP corr; logs link the two worlds | scheduler-minted Guid flows here |
| `CorrelatedBy<Guid>` / property named `CorrelationId`/`CommandId`/`EventId` | auto-sets envelope `CorrelationId` header | `ICorrelated.CorrelationId` Guid will auto-promote if ever published | wire-level correlation |

---

## Sources

- MassTransit official docs (via Context7 `/websites/masstransit_massient`, 2026-05-30): `concepts/transports`, `concepts/outbox`, `concepts/messages`, `concepts/exceptions`, `concepts/requests`, `configuration`, `configuration/topology`, `configuration/middleware/retry`, `reference/ipublishendpoint`, `reference/irequestclient` — **HIGH** confidence on Publish/Send topology, InstanceId fan-out, ack/redelivery + `_error` dead-letter, request/response timeout (30s default), correlation conventions.
- `masstransit.massient.com/documentation/configuration` (WebFetch, 2026-05-30) — InstanceId fan-out quote: "unique for each bus instance — to enable fan-out (instead of load balancing)." **HIGH.**
- Project context: `.planning/PROJECT.md` (v3.4.0 Current Milestone, locked decisions), `src/BaseApi.Service/Features/Orchestration/Projection/WorkflowRootProjection.cs` (L2 root correlation string), `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` (the `"CorrelationId"` log-scope key to mirror).
- **MEDIUM** confidence flag: the precise InstanceId wiring variant (per-endpoint `.Endpoint(e => e.InstanceId)` vs formatter vs `ConnectReceiveEndpoint(TemporaryEndpointDefinition)`) — resolve in the phase plan; all three achieve fan-out, the choice is ergonomics + temporary-queue cleanliness.

---
*Feature research for: MassTransit/RabbitMQ messaging — WebApi publisher → Orchestrator consumer (v3.4.0)*
*Researched: 2026-05-30*
