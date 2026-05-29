# Pitfalls Research — Milestone v3.4.0 (BaseConsole + Orchestrator Messaging)

**Domain:** Adding MassTransit/RabbitMQ + a Generic-Host console base (`BaseConsole.Core` / `Orchestrator`) + shared `Messaging.Contracts` to an existing .NET 8 modular monolith with OTel (logs→ES, metrics→Prom, **no traces backend**), Redis soft-dep, three K8s probes with strict tag discipline, and a 3-GREEN + zero-leak (SHA-256 BEFORE=AFTER) test gate.
**Researched:** 2026-05-30
**Confidence:** HIGH on MassTransit/RabbitMQ mechanics (Context7 lookup + official observability docs + MassTransit discussion threads); HIGH on how they collide with THIS system's locked decisions (read from PROJECT.md + `ObservabilityServiceCollectionExtensions.cs` + `StartupHealthCheck.cs`).

> **Two highest-risk items, read these first:**
> 1. **Fan-out topology trap** (Pitfall 1) — accidentally configuring a *shared* receive endpoint so Start/Stop load-balances across replicas instead of broadcasting. The single most likely topology bug, and it is **invisible at 1 replica** (today's deployment).
> 2. **No-traces-backend OTel collision** (Pitfall 3) — MassTransit 8 ships a built-in `ActivitySource` named `"MassTransit"`. Copying any tutorial's `.AddSource("MassTransit")` / `.WithTracing(...)` resurrects the traces pipeline this platform deliberately stripped in Phase 11 (D-03), exporting spans to a Collector with no traces pipeline.

---

## Critical Pitfalls

### Pitfall 1: Fan-out configured as a SHARED receive endpoint (competing consumers) instead of per-instance queues

**What goes wrong:**
The milestone requires Start/Stop to reach **every** Orchestrator replica (broadcast). In RabbitMQ/MassTransit, *broadcast-to-all* requires **one queue per instance**, all bound to the published exchange. If instead every replica binds the **same named** receive endpoint (e.g. `ReceiveEndpoint("orchestrator-control", ...)`), RabbitMQ load-balances (round-robins) the message — only **one** replica gets each Start. Even with `Publish`, a *shared* queue makes the consumers *compete*. The symptom is invisible at 1 replica (today) and only surfaces when a second Orchestrator is added — exactly when broadcast matters.

**Why it happens:**
- MassTransit's most-documented pattern is the shared, durable, named receive endpoint (correct for *work distribution*, wrong for *broadcast*). Developers copy it.
- At 1 replica, shared-queue and per-instance-queue behave identically, so tests pass and the bug ships latent.
- Confusion between `Send` (queue-targeted, always 1 consumer) and `Publish` (exchange fan-out) — `Publish` through a *shared queue* still competes; the fan-out only materializes if each consumer owns a distinct queue.

**How to avoid:**
- Use an **instance-unique / temporary endpoint**: configure the receive endpoint as a **non-durable, `AutoDelete` queue whose name includes a per-instance token** (e.g. `$"orchestrator-{Guid.NewGuid():N}"`, a `TemporaryEndpointDefinition`, or the bus endpoint). PROJECT.md's locked decision already names this: "each replica binds an **instance-unique queue**."
- WebApi side uses **`Publish<StartOrchestration>`** (exchange fan-out), never `Send` to a named queue.
- Encode the rule in a test **even at 1 replica**: spin up **two** in-process bus instances (two distinct instance-unique queues) in a single harness and assert **both** consume the one published message. This is the only way to catch the latent bug before a second replica exists in prod.

**Warning signs:**
- A `ReceiveEndpoint("<static-name>", ...)` string literal for the control queue.
- The queue is declared `durable: true` / no `AutoDelete` for what should be an ephemeral per-instance consumer.
- Adding a second replica → "only one orchestrator logged the Start," non-deterministically.
- `rabbitmqctl list_queues` shows ONE control queue with N consumers instead of N queues with 1 consumer each.

**Phase to address:** **Messaging topology / WebApi-publish phase** (wires `AddBaseConsoleMessaging` + WebApi `Publish`). Verification: 2-bus broadcast test asserting both consume.

---

### Pitfall 2: ACK semantics — confusing transport-ack with business-result, causing swallowed crashes OR infinite redelivery

**What goes wrong:**
The locked intent is **"ack on success regardless of the pass/fail business result, but do NOT ack on a crash."** Two opposite failure modes:
- **Swallowing exceptions (auto-ack hides crashes):** the consumer catches everything and returns normally → message acked. A genuine infrastructure crash (Redis unreachable, deserialize failure, NRE) is silently lost — no error queue, no redelivery, no alert. Looks healthy, isn't.
- **Throwing on business failure (infinite redelivery):** the consumer throws when a workflow is *legitimately* not-found/invalid (a business outcome, not a crash). MassTransit treats the throw as a fault → retry policy → eventually `_error` queue; with no retry limit or an aggressive one you get a **redelivery storm** hammering Redis/logs.

**Why it happens:**
- "Ack" conflates two concepts: *I processed the message* (transport) vs *the work succeeded* (business). MassTransit acks when the consumer method **returns without throwing**; it faults/nacks when it **throws**.
- Developers reach for try/catch-all (mode 1) to "be safe," or treat every unhappy path as an exception (mode 2).

**How to avoid:**
- **Define the contract explicitly:** a *business* failure (workflow not found, validation-equivalent, Stop-on-missing) → **log + return normally → ack**. A *crash* (broker/Redis/deserialize/unexpected) → **let it throw** so MassTransit faults it.
- Catch **only** the known business outcomes; never `catch (Exception)` at the consumer boundary.
- Bound the crash path: configure **finite retry** (`UseMessageRetry(r => r.Immediate(3))` or short interval) + rely on the MassTransit **`_error` queue** as the poison destination. Never unbounded retry.
- This mirrors the existing API discipline: business failures are RFC-7807 422s (caught, mapped, returned), infrastructure failures bubble — port that mental model to the consumer.

**Warning signs:**
- `catch (Exception)` / `catch { }` anywhere in a consumer.
- No `UseMessageRetry` / `UseDelayedRedelivery` configured.
- Logs show the same `CorrelationId`/message redelivered repeatedly.
- `_error` / `_skipped` queues growing unboundedly, or conversely *never* receiving anything despite known-bad inputs.

**Phase to address:** **Orchestrator consumer phase.** Verification: one test for a business-fail input (assert acked, no redelivery, log emitted) and one for an injected crash (assert fault → bounded retry → `_error`).

---

### Pitfall 3: MassTransit's ActivitySource resurrecting traces on a no-traces-backend platform

**What goes wrong:**
This platform deliberately **stripped `.WithTracing(...)`** in Phase 11 (D-03) — there is no traces backend and the Collector has no traces pipeline. MassTransit 8 has **built-in OTel** and publishes an `ActivitySource` named **`"MassTransit"`** (`DiagnosticHeaders.DefaultListenerName`). Two ways this bites:
- A tutorial-copied `.AddSource("MassTransit")` (or any `.WithTracing(...)`) re-enables a `TracerProvider` in the new `BaseConsole.Core`, exporting spans via OTLP to a Collector with no traces pipeline → spans dropped, Collector logs errors, and you've re-introduced the exact thing v1 removed.
- Even without `.WithTracing`, MassTransit still **creates** `Activity` objects (cheap, no listener = no export); but if anything registers a global `ActivityListener` you pay cost with nowhere to send it.

**Why it happens:**
- Every MassTransit-OTel tutorial shows the **tracing** setup (`AddSource("MassTransit")`) because traces are the headline feature. The `BaseConsole.Core` author copies it.
- The metrics-only posture (logs+metrics, no traces) is the *unusual* one; the ecosystem default assumes traces exist.

**How to avoid:**
- In `BaseConsole.Core`, register **metrics only**: `.WithMetrics(m => m.AddMeter(InstrumentationOptions.MeterName) ...)` — MassTransit's **Meter** name is `InstrumentationOptions.MeterName`. **Do NOT** add a `.WithTracing` block and **do NOT** `.AddSource("MassTransit")`.
- Mirror `BaseApi.Core`'s `AddBaseApiObservability` exactly: logs via MEL bridge (`builder.Logging.AddOpenTelemetry`), metrics via `AddOpenTelemetry().WithMetrics(...)`, **no tracer provider**. The console flavor swaps AspNetCore instrumentation for `AddRuntimeInstrumentation()` + the MassTransit meter (PROJECT.md: "MEL-bridge logs + runtime + MassTransit instrumentation, no AspNetCore instrumentation").
- Add a guard test: assert no `TracerProvider` is registered in the console's `IServiceProvider` (or assert the Collector receives zero traces) — the console analog of the deleted `TraceExportTests`.

**Warning signs:**
- `.AddSource("MassTransit")` or any `.WithTracing(` in the console composition root.
- Collector logs `"data point dropped"` / `"traces pipeline not configured"` errors.
- A `TracerProvider` singleton resolvable from the console container.

**Phase to address:** **`BaseConsole.Core` observability phase.** Verification: container-introspection test (no `TracerProvider`) + metrics round-trip test polling Prometheus for `messaging_masstransit_consume` (mirrors `SchemasMetricsE2ETests`).

---

### Pitfall 4: RabbitMQ as a hard dependency — boot/crash behavior and readiness flipping at the wrong time

**What goes wrong:**
RabbitMQ is a **hard** dependency for the Start/Stop path (PROJECT.md), unlike Redis which is soft. Failure modes:
- **WebApi CRUD must stay up with RabbitMQ down** (PROJECT.md: "CRUD surface unaffected"). If the bus is wired so that `IBusControl.StartAsync` failing crashes the host, a RabbitMQ outage now also takes down CRUD — explicitly forbidden by the milestone.
- **Orchestrator readiness flips too early/late:** `/health/ready` must flip when the bus has actually **started and bound its queue** — not on host-start, not on liveness. If readiness ties to host-start, K8s marks ready before the consumer can receive, and early Start messages are missed (a non-durable instance queue doesn't exist until the bus binds it — published Starts in that window are dropped).
- **Boot storm:** the bus connects on host start with built-in retry; a slow broker blocks/retries the hosted-service start, and combined with K8s `restartPolicy` you get crash-loop churn.

**How to avoid:**
- **WebApi:** treat the bus as additive — bus-start failure must **not** fail the API host. `IBusControl` reconnects automatically; let `Publish` fail at call-time (return a clear 503 on the Start endpoint) rather than at boot. Do **not** add RabbitMQ to the WebApi's `/health/ready` (keep it soft, like Redis).
- **Orchestrator:** flip `/health/ready` **on bus-started** (use MassTransit's RabbitMQ bus health check tagged `"ready"`). The bus *is* the orchestrator's reason to live, so for the Orchestrator RabbitMQ **is** a readiness dependency — but `/health/live` must **never** touch it (Pitfall 5).
- Bound startup with a sane connection-retry; do not let the host hang indefinitely. Use the MassTransit-provided RabbitMQ health check, not a hand-rolled connection ping.

**Warning signs:**
- WebApi fails to boot when the `rabbitmq` container is down.
- Orchestrator `/health/ready` returns 200 before the queue exists.
- Crash-loop / repeated "Connection refused" at boot with no backoff.

**Phase to address:** **WebApi-messaging phase** (soft boundary) + **`BaseConsole.Core` health phase** (Orchestrator readiness-on-bus-start). Verification: `HealthDeadRabbitFixture` for WebApi (CRUD 200, Start 503 with broker down) — directly mirrors the existing `HealthDeadRedisFixture`.

---

### Pitfall 5: The Generic-Host "live" probe accidentally depending on RabbitMQ/Redis (tag-discipline violation)

**What goes wrong:**
The platform has a **locked invariant** (`StartupHealthCheck` doc + Phase 5 Pitfall 15): `/health/live` is tagged `"live"` and maps **only** to the always-Healthy `"self"` check — **liveness never touches a dependency.** When porting probes into the embedded Kestrel of `BaseConsole.Core`, the easy mistake is to register the MassTransit/RabbitMQ bus health check (or the Redis check) with the `"live"` tag, or to use a predicate that includes all checks. Then a transient RabbitMQ blip flips `/health/live` → 503 → **K8s kills the pod** (liveness failure = restart), turning a recoverable broker hiccup into a restart storm.

**Why it happens:**
- `AddMassTransit` auto-registers a bus health check; without constrained tags it lands in the default set, and a `MapHealthChecks("/health/live")` with no predicate picks it up.
- Copy-pasting probe wiring without re-applying the strict tag predicates `BaseApi.Core` uses.

**How to avoid:**
- Replicate the **exact tag discipline** from `BaseApi.Core`: `live` → predicate `c => c.Tags.Contains("live")` mapping only to the `"self"` Healthy check; `ready`/`startup` get the dependency checks (bus, optionally Redis).
- Explicitly **set the bus health check tags to `"ready"`** (MassTransit lets you configure health-check tags) — never `"live"`.
- Embedded Kestrel: bind health endpoints on a separate port/path and ensure the probe host starts independently of the bus so `/health/live` answers while the bus is still connecting.

**Warning signs:**
- `MapHealthChecks("/health/live")` without a `Predicate` constraining to `"live"`.
- Bus or Redis check resolvable under the live predicate.
- Pods restart (not just go un-ready) during a RabbitMQ blip.

**Phase to address:** **`BaseConsole.Core` health-probe phase.** Verification: port the `StartupHealthCheck`/tag test — assert `/health/live` stays 200 with both RabbitMQ and Redis ports dead.

---

### Pitfall 6: Correlation lost across the async boundary — outbound filter omitted, AsyncLocal not flowed, header-vs-field drift, log-scope key mismatch

**What goes wrong:**
The headline deliverable is **end-to-end correlation** (HTTP → Redis L2 → fan-out message → orchestrator correlated log in ES). Ways it breaks:
- **Outbound filter forgotten:** an inbound consume filter sets the AsyncLocal accessor, but no **outbound Send/Publish filter** stamps the ambient id back onto downstream `ICorrelated` messages → the chain dies at the first hop the orchestrator publishes. PROJECT.md explicitly requires *both* filters; the outbound one is the easy omission (exercised this milestone via a synthetic downstream send).
- **Filter ordering:** the correlation-extracting consume filter must run **before** the log-scope opens and before user consumer code; register it early. Wrong order = scope opened with no id.
- **AsyncLocal doesn't flow:** if the accessor is read after an `await` crossing a context where the value wasn't captured (set inside the filter but scope disposed before the consumer awaits), the id is null in logs. Set it *around* the consume, not fire-and-forget.
- **Header-vs-message-field drift:** the id can live as a MassTransit **header** (`CorrelationId`/`MessageId`) AND as an `ICorrelated.CorrelationId` field; if the filter reads the header but the consumer reads the field (or they disagree), correlation appears present but is wrong/empty.
- **Log-scope key mismatch with OTel `IncludeScopes`:** `BaseApi.Core` sets `o.IncludeScopes = true` and the HTTP side uses the scope key `"CorrelationId"`. If the console's MEL scope uses a different key (`"correlation_id"`, `"CorrId"`, …), the id won't land in the same ES field and the cross-service join silently breaks.

**Why it happens:**
- Inbound is intuitive; outbound is the asymmetric, easily-missed half.
- The two correlation worlds are deliberately distinct (HTTP `X-Correlation-Id` string on the edge/Redis vs the future bus-world `Guid CorrelationId` minted by Quartz). PROJECT.md: they **do not unify, they are linked via logs**. Trying to unify them is a modeling error.

**How to avoid:**
- Implement **both** filters in `BaseConsole.Core`: inbound (header/field → AsyncLocal accessor + `"CorrelationId"` MEL scope) and outbound (ambient AsyncLocal → `ICorrelated` field + header).
- **Reuse the exact scope key `"CorrelationId"`** matching the API + `IncludeScopes` — one constant, but it's the difference between a working cross-service ES join and silent drift. Make it a shared constant (in `Messaging.Contracts` or `BaseConsole.Core`).
- Single source of truth for the id: decide header vs field, then bridge them consistently in the filter. This milestone the orchestrator extracts the stored `X-Correlation-Id` from the Redis L2 root and establishes the scope from that — keep that path explicit.
- Honor the locked decision: do **not** reconcile the HTTP string id with the future bus Guid; link via logs only.

**Warning signs:**
- Only the inbound filter registered.
- Orchestrator logs in ES with empty/missing correlation, or with a *different* field name than the API's logs.
- The same logical request shows two different correlation values across services.

**Phase to address:** **Correlation-propagation phase** in `BaseConsole.Core`. Verification: end-to-end test asserting the orchestrator's ES log carries the same `CorrelationId` value (same field key) the HTTP request emitted; outbound filter exercised via the synthetic send.

---

### Pitfall 7: Leaked RabbitMQ queues/exchanges across test runs — breaking the zero-leak gate

**What goes wrong:**
The phase-close gate is **SHA-256 BEFORE=AFTER** proving zero leaked resources (Postgres `psql \l`, Redis `redis-cli --scan`). RabbitMQ adds a **third leakable resource class**: queues, exchanges, bindings. Tests that create durable named endpoints, or use non-unique queue names, leave queues behind → BEFORE≠AFTER, gate fails. Worse: durable queues from a failed test get **re-consumed** by the next run → flaky cross-test contamination (the hazard the discussion threads warn about for shared queues).

**Why it happens:**
- Default MassTransit receive endpoints are **durable** and named — they persist on the broker after the process exits.
- Parallel test classes (the existing per-class throwaway model) all binding similarly-named queues collide.

**How to avoid:**
- In tests, use **non-durable, auto-delete, instance-unique** queues (same `AutoDeleteOnIdle` / temporary-endpoint pattern as production fan-out). Auto-delete queues vanish when the connection closes → naturally zero-leak.
- **Per-test-class isolation analogous to Redis `KeyPrefix`:** unique queue/exchange prefix per class (e.g. `test-cls-<guid>-...`), the broker-world equivalent of the locked `RedisFixture` `KeyPrefix` discipline.
- **Extend the phase-close SHA gate** to a *third* snapshot: `rabbitmqctl list_queues name` (and `list_exchanges`) SHA-256 BEFORE=AFTER, alongside `psql \l` and `redis-cli --scan`. Forbid any global `rabbitmqctl purge`/vhost-reset in teardown (it would mask leaks across parallel classes, exactly as `FLUSHDB` is forbidden for Redis).
- Use **real RabbitMQ in Docker Compose** for the leak-discipline integration tests, not the in-memory harness (the leak gate is about real broker resources — Pitfall 8).

**Warning signs:**
- `rabbitmqctl list_queues` shows leftover `test-*` queues after a run.
- Flaky tests where a message from a prior run gets consumed.
- BEFORE≠AFTER on the new RabbitMQ SHA snapshot.

**Phase to address:** **Test-infrastructure phase** (RabbitMQ → Compose + harness). Verification: the new `list_queues` SHA-256 held BEFORE=AFTER across the 3-consecutive-GREEN cadence.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Shared named receive endpoint for the control queue | Simplest config; passes at 1 replica | Latent broadcast bug; breaks the moment a 2nd Orchestrator deploys (Pitfall 1) | **Never** — fan-out is the locked topology |
| In-memory MassTransit test harness only | Fast, no broker | Misses real topology, durability, prefetch, ack; can't feed the zero-leak gate | Acceptable for *consumer unit logic*; leak/topology/3-GREEN gate must use **real** RabbitMQ |
| `catch (Exception)` in the consumer "to be safe" | No redelivery storms during dev | Swallows real crashes; silent message loss; no `_error` queue (Pitfall 2) | **Never** at the consumer boundary |
| Adding RabbitMQ to WebApi `/health/ready` | "Consistent" probe wiring | Breaks soft/hard boundary — RabbitMQ outage takes down CRUD (Pitfall 4) | **Never** for WebApi (correct for Orchestrator readiness only) |
| Copy the tutorial `.AddSource("MassTransit")` block | Works in tutorials | Resurrects the removed traces pipeline; Collector errors (Pitfall 3) | **Never** — metrics-only via `AddMeter(InstrumentationOptions.MeterName)` |
| Unbounded / default redelivery | Messages "eventually" process | Storm against Redis/logs/broker on a poison message | Never; bound retry + rely on `_error` queue |
| Durable named test queues | Easy to inspect | Leaked queues fail the SHA gate; cross-run contamination | Never; use auto-delete instance-unique queues |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| RabbitMQ (broadcast) | `Send` to a queue, or `Publish` through a shared durable queue | `Publish` + per-instance auto-delete queue per replica |
| RabbitMQ (WebApi) | Bus-start failure crashes the host | Additive bus; CRUD unaffected; Start endpoint 503 if `Publish` fails |
| OTel (no traces) | `.WithTracing` / `.AddSource("MassTransit")` | Metrics-only: `.WithMetrics(m => m.AddMeter(InstrumentationOptions.MeterName))`; logs via MEL bridge; **no TracerProvider** |
| OTel metrics | Double-instrumenting (registering the MassTransit meter twice / mixing the legacy `OpenTelemetry.Instrumentation.MassTransit` package with built-in v8) | Built-in only (v8 needs no extra package); register the meter once; watch label cardinality on `messaging.masstransit.*` |
| MEL log scope | Console uses a different scope key than `"CorrelationId"` | Reuse the exact `"CorrelationId"` key (matches API + `IncludeScopes`); shared constant |
| Generic Host startup order | Hosted services start before the bus, or readiness flips on host-start | Flip `/health/ready` on bus-started; ensure consumer queue is bound before ready |
| Embedded Kestrel probes | Probe host coupled to bus start → `/health/live` blocked while bus connects | Independent probe host/port; live answers always |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Default prefetch count wrong | Memory spikes (too high) or idle consumer (too low) | Set `PrefetchCount` explicitly per endpoint (control queue is low-volume → small prefetch) | High-throughput later milestones |
| Redelivery storm on poison message | CPU/log/Redis hammering, same `CorrelationId` repeating | Bounded `UseMessageRetry` + `_error` queue (Pitfall 2) | First poison message in prod |
| Metric cardinality from message-type labels | Prometheus series explosion | Few types now (Start/Stop) → low risk; watch when `queue:processorId` per-processor labels arrive in the Processor milestone | Many processor types (future) |
| AutoDeleteOnIdle never firing (queue stays bound) | Leaked queues if a replica dies without clean disconnect | Auto-delete + connection-scoped cleanup; `list_queues` SHA gate catches leaks | Replica crash without graceful shutdown |

## Security Mistakes

| Mistake | Risk | Prevention |
|---------|------|------------|
| RabbitMQ default `guest/guest` over non-localhost binding | Broker takeover | Compose-internal only for dev; real creds + network policy before any non-local deploy |
| Trusting message payloads as already-validated | Malformed/oversized messages crash the consumer | Validate `ICorrelated` mandatory fields on consume; treat absent fields as a business-fail (ack + log), not a crash loop |
| Logging full message bodies | Sensitive workflow data in ES | Log `CorrelationId` + ids, not full payloads |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Start endpoint returns 204/202 even when RabbitMQ is down | Caller thinks orchestration started; nothing happened | Clear 503 on the Start path when the bus can't publish; CRUD stays 200 |
| No way to tell which replica handled a Start | Fan-out debugging is opaque | Each replica logs the Start with its instance id + the shared `CorrelationId` |

## "Looks Done But Isn't" Checklist

- [ ] **Fan-out:** Tested with **two** bus instances (not one) — BOTH consume one published Start.
- [ ] **Outbound correlation:** The Send/Publish filter exists and is exercised (synthetic downstream send), not just the inbound consume filter.
- [ ] **Log-scope key:** Console uses the literal `"CorrelationId"` key matching the API + OTel `IncludeScopes`; ES field name matches across services.
- [ ] **No traces:** No `TracerProvider` in the console container; Collector receives zero traces; metrics-only via `AddMeter(InstrumentationOptions.MeterName)`.
- [ ] **Liveness:** `/health/live` stays 200 with BOTH RabbitMQ and Redis ports dead.
- [ ] **WebApi soft boundary:** CRUD returns 200 with RabbitMQ down; only Start/Stop fails.
- [ ] **Readiness timing:** Orchestrator `/health/ready` flips on bus-started (queue bound), not host-start.
- [ ] **Ack semantics:** business-fail → acked, no redelivery, log emitted; injected crash → bounded retry → `_error`.
- [ ] **Queue leak gate:** `rabbitmqctl list_queues` SHA-256 BEFORE=AFTER held across 3 consecutive GREEN; no global purge in teardown.
- [ ] **Test isolation:** per-class unique queue/exchange prefix (broker analog of Redis `KeyPrefix`).

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Shared-queue fan-out shipped (1) | MEDIUM | Switch to per-instance auto-delete endpoint; redeploy; old shared queue auto-cleans or `delete_queue` once |
| Traces resurrected (3) | LOW | Remove `.AddSource`/`.WithTracing`; no data migration; Collector errors stop |
| Liveness depends on broker (5) | LOW | Re-apply tag predicate; redeploy — but K8s may have restart-looped pods until then |
| Leaked test queues (7) | LOW | `delete_queue` leftover `test-*`; fix teardown to auto-delete; re-run gate |
| Swallowed-exception message loss (2) | HIGH | Already-acked messages are unrecoverable; remove catch-all, add `_error` queue, re-drive from source if a replay path exists |
| Correlation drift (6) | MEDIUM | Fix scope key/outbound filter; past ES logs stay un-joinable, future ones correlate |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| 1. Fan-out as shared endpoint | Messaging topology / WebApi-publish phase | 2-bus broadcast test: both consume one `Publish` |
| 2. Ack semantics | Orchestrator consumer phase | business-fail acked + no redelivery; crash → bounded retry → `_error` |
| 3. Traces resurrection (no-traces OTel) | `BaseConsole.Core` observability phase | No `TracerProvider` in container; Prom shows `messaging_masstransit_consume` |
| 4. RabbitMQ hard-dep / readiness timing | WebApi-messaging + console health phase | `HealthDeadRabbitFixture`: CRUD 200 / Start 503 with broker down; ready flips on bus-started |
| 5. Live probe touches broker/Redis | `BaseConsole.Core` health-probe phase | `/health/live` 200 with RabbitMQ + Redis dead |
| 6. Correlation propagation | Correlation-propagation phase (`BaseConsole.Core`) | E2E: orchestrator ES log carries same `CorrelationId` key+value as the HTTP edge; outbound filter exercised |
| 7. Leaked queues / test isolation | Test-infrastructure phase (RabbitMQ → Compose) | `list_queues` SHA-256 BEFORE=AFTER across 3 GREEN; per-class prefix; no global purge |

## Sources

- MassTransit official observability docs — exact identifiers: `ActivitySource` = `DiagnosticHeaders.DefaultListenerName` ("MassTransit"), `Meter` = `InstrumentationOptions.MeterName`; metric names `messaging.masstransit.{receive,consume,send,delivery.durations,*.errors,*.active}`: https://masstransit.massient.com/configuration/observability — HIGH
- MassTransit fan-out vs competing-consumers mechanics (per-instance queue for broadcast; shared queue load-balances; multi-consumer-on-one-endpoint pitfalls): https://groups.google.com/g/masstransit-discuss/c/d_-JCmHC798 ; https://github.com/MassTransit/MassTransit/discussions/3482 ; https://github.com/MassTransit/MassTransit/discussions/3003 — HIGH (multiple sources agree)
- MassTransit `Send` vs `Publish` semantics: https://www.maldworth.com/2015/10/27/masstransit-send-vs-publish/ — MEDIUM
- MassTransit 8 built-in OTel (legacy `OpenTelemetry.Instrumentation.MassTransit` package no longer needed): https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/326 ; https://oneuptime.com/blog/post/2026-02-06-instrument-masstransit-message-bus-opentelemetry-dotnet/view — HIGH
- Temporary/auto-delete bus endpoints + `AutoDeleteOnIdle` (basis for instance-unique + zero-leak queues): https://masstransit.io/documentation/configuration/transports/rabbitmq ; https://github.com/MassTransit/MassTransit/discussions/3126 — HIGH
- Context7 library resolution `/masstransit/masstransit` (confirms current MassTransit framework surface) — HIGH
- THIS system's locked posture: `.planning/PROJECT.md` (no-traces D-03; Redis soft-dep decision; 3-GREEN + dual-SHA leak gate; fan-out instance-unique-queue locked decision; correlation HTTP-string-vs-bus-Guid "link via logs"); `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (metrics-only OTel shape to mirror — `IncludeScopes`, MEL bridge, no `.WithTracing`); `src/BaseApi.Core/Health/StartupHealthCheck.cs` (`"live"` tag → only `"self"` discipline to preserve) — HIGH

---
*Pitfalls research for: adding MassTransit/RabbitMQ + Generic-Host console base to a .NET 8 modular monolith (sk_p v3.4.0)*
*Researched: 2026-05-30*
</content>
