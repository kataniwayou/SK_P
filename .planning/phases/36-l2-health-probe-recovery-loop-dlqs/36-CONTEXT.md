# Phase 36: L2 Health-Probe Recovery Loop & DLQs - Context

**Gathered:** 2026-06-05
**Status:** Ready for planning

<domain>
## Phase Boundary

Turn Keeper from the Phase-35 *observe-and-ack* skeleton into the actual recovery engine. On intake of a `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>`, Keeper runs a **bounded, crash-survivable L2 (Redis) read+write probe loop**; on the first successful probe it **re-injects the verbatim inner message to its origin endpoint by type** (spike-proven, Phase 33) and exits; when max-attempts exhaust it **parks the original `Fault<T>` in `keeper-dlq` (DLQ-2)** and exits. The fault is **acked only after the loop exits** (success or give-up). Plus the **two-DLQ topology** split by exhaustion mechanism (Immediate(N) transport exhaustion → consolidated TTL'd **DLQ-1**; probe give-up → **DLQ-2 `keeper-dlq`**) and the **shared `Immediate(N)` policy routed uniformly to DLQ-1** across all consoles.

**Scope anchor:** PROBE-01..05, DLQ-01..04 (and only these — see requirement map in REQUIREMENTS.md). PROBE-06 (no Keeper dedup) is spike-proven (Phase 33) and rides through unchanged.

**Explicitly OUT of this phase** (deferred — do NOT build here):
- `PauseWorkflow` / `ResumeWorkflow` contracts + the orchestrator pending-recovery set / cron pause-resume — **Phase 37** (PAUSE-01..05). Phase 36 re-injects only; it emits NO pause/resume signal (D-11).
- Keeper meter + `keeper_l2_probe_failed` / DLQ-depth counters/histograms — **Phase 38** (KMET-01..03).
- The Prometheus alert wiring on `keeper-dlq` depth, real-stack E2E close gate (3×GREEN triple-SHA) — **Phase 39** (TEST-01..03).

</domain>

<decisions>
## Implementation Decisions

### Probe loop + L2 client (Area 1 — PROBE-01/02)
- **D-01:** Keeper's `Program.cs` calls the **existing** `AddBaseConsoleRedis(cfg)` (singleton `IConnectionMultiplexer`, `abortConnect=false` so boot survives a dead Redis — the exact outage this console exists for). Add `ConnectionStrings:Redis` to Keeper `appsettings.json`; compose already injects `ConnectionStrings__Redis` (Phase 34 D-05). **No csproj firewall break** — it rides the existing `BaseConsole.Core` reference (no new `ProjectReference`; `StackExchange.Redis` flows transitively as it does for Orchestrator's `ResultConsumer`).
- **D-02 (probe op, PROBE-02):** each loop iteration performs BOTH a **read** — `db.StringGetAsync(L2ProjectionKeys.ExecutionData(inner.EntryId))` (tests read availability; the value need NOT exist) — AND a **write-then-delete** of a scratch key (tests write availability). A probe counts as **success only if both ops complete without a Redis exception** (`RedisConnectionException` / timeout = fault → keep looping). Not mere connectivity — real read + real write.
- **D-03 (scratch key):** new builder on `L2ProjectionKeys` → `KeeperProbe(string h)` = `skp:keeper:probe:{h}`. Written with a **short TTL (~30s) then `KeyDeleteAsync`**. The TTL is the crash-safety net: a kill between write and delete still self-cleans, so the close-gate net-zero triple-SHA (Phase 39) holds. Net-zero by construction.
- **D-04 (config + ack-timeout bound, PROBE-01):** new `"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }` appsettings section → a `ProbeOptions` options class (bound per process via `IOptions`). **`delay × attempts = 60s`, far under RabbitMQ's default 30-min `consumer_timeout`** — the loop is awaited *inside* `Consume`, holding the delivery un-acked for that window, so the bound is load-bearing. Document the constraint in the `ProbeOptions` doc-comment. Defaults locked: **5s delay × 12 attempts** (60s transient-blip recovery window).

### DLQ-1 consolidation (Area 2 — DLQ-02/DLQ-04; base-library change)
- **D-05 (shape):** **one shared `skp-dlq-1` queue**, declared with `x-message-ttl`, consolidating EVERY console's `Immediate(N)` transport exhaustion — replacing the current per-`{queue}_error` MassTransit default. This is the consolidated TTL'd forensic record (DLQ-03 secondary alert).
- **D-06 (where):** configured **once in `BaseConsole.Core`** (the shared error-transport pattern) so processor + orchestrator + Keeper all inherit uniformly (DLQ-04 "same design pattern across consoles"). BaseApi is a publisher only (no consuming endpoints) → out of scope.
- **D-07 (TTL):** **7 days.** Forensic retention that also holds already-recovered transient faults, so it must drain (not grow unbounded) yet survive triage.
- **⚠ TOP RESEARCH ITEM (see canonical_refs):** the exact MassTransit-on-RabbitMQ mechanism to route post-retry-exhaustion `_error` *moves* into one shared queue — custom error-queue-name override + `SetQueueArgument("x-message-ttl", ...)` vs a dead-letter-exchange (DLX) bind. NOTE: MassTransit's exhaustion path **republishes** to `{queue}_error` (it does not nack-to-DLX), so a naive DLX bind catches only rejected/expired messages, NOT exhaustion moves — the researcher must confirm the correct hook. ROADMAP's "mechanism confirmed in the Phase-33 spike" is INACCURATE: Phase 33 only *recorded* the D-10 decision; the transport wiring is unbuilt.

### keeper-dlq (DLQ-2) park (Area 3 — PROBE-04/DLQ-02/DLQ-03)
- **D-08:** new const `KeeperQueues.DeadLetter = "keeper-dlq"` — a **plain durable queue, NO `x-message-ttl`** (terminal "L2 recovery gave up — needs an operator" alert; its depth is the **primary** Prometheus alert, so it MUST persist until an operator drains it — contrast DLQ-1's TTL).
- **D-09 (give-up path, PROBE-04/05):** on max-attempts exhaustion, `GetSendEndpoint("queue:keeper-dlq")` → `Send(...)` → then return (ack). A fault in the Send itself is a Keeper infra fault → falls to the endpoint's `Immediate(N)` → DLQ-1 (DLQ-01) — consistent with every other consumer.
- **D-10 (what to park):** the **original `Fault<T>` envelope** (`context.Message`, i.e. `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>`), NOT the bare inner message — the envelope carries the originating `Exceptions[]` the operator needs for triage. `keeper-dlq` is operator-drained (not auto-consumed), so envelope-type re-consumption is a non-issue.

### Re-inject ↔ Phase 37 boundary (Area 4 — PROBE-03/05, scope guard)
- **D-11 (scope line — LOCK):** Phase 36 **re-injects ONLY** (the spike-proven `GetSendEndpoint` + `Send` of the verbatim inner message by type — `queue:{processorId:D}` for dispatch, `queue:orchestrator-result` for result). It emits **NO** `PauseWorkflow` / `ResumeWorkflow` — those contracts are born in Phase 37. The SC clause "on give-up the workflow stays paused (no auto-resume)" is **structurally vacuous in Phase 36** (no pause mechanism exists yet); Phase 37 wires the per-workflow pending-recovery set that enforces it. **Do NOT pull Phase-37 contracts into the planner's reach.**
- **D-12 (crash-survivability, PROBE-05):** ack-after-loop is automatic — the loop is awaited inside `Consume`, so the broker delivery stays un-acked until the loop exits (success → re-inject; give-up → keeper-dlq Send), then the method returns → ack. Proof split: **hermetic** tests for loop logic (probe-fails-then-succeeds → re-inject; probe-fails-to-max → keeper-dlq park; assert NO premature ack) + a **RealStack** sibling of the Phase-33 `FaultRecoverySpikeE2ETests` family for live recover-both-paths + give-up. **Kill-mid-loop → redeliver → loop restarts** stays an operator runbook (Phase 39 close gate is the authoritative live signal — consistent with the 33–35 auto-approve-human-verify precedent).
- **D-13 (concurrency):** keep default prefetch/concurrency; the short 60s hold (D-04) bounds head-of-line risk. **Claude's Discretion** — flag during research if a fault-flood-during-outage surfaces a real consumer-starvation concern.

### Claude's Discretion (summary)
- Exact `ProbeOptions` placement (Keeper-local vs `Messaging.Contracts.Configuration`) and the read/write StackExchange.Redis call shapes (mirror `ResultConsumer`).
- The shared probe-loop helper vs inline-per-consumer (both fault consumers run the identical loop — a shared helper is the natural DRY move; planner's call).
- Settle-window durations, `PollEsForLog` query shapes, and the exact RealStack test vehicle (extend spike-family vs new Keeper-specific test) — within the established rig (D-12).
- Prefetch/concurrency limit (D-13).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents (researcher, planner, executor) MUST read these before planning or implementing.**

### Phase scope & requirements
- `.planning/ROADMAP.md` §"Phase 36: L2 Health-Probe Recovery Loop & DLQs" — goal + 5 success criteria + requirement map. NOTE the DLQ-02 "mechanism confirmed in Phase-33 spike" claim is inaccurate (see D-07 research item).
- `.planning/REQUIREMENTS.md` — **PROBE-01..05** (lines ~31–35: bounded loop, read+write probe, re-inject on success, park on give-up, ack-after-loop), **PROBE-06** (line ~36: no Keeper dedup — rides receiver `flag[H]`, spike-proven), **DLQ-01..04** (lines ~40–43: Keeper's own faults → DLQ-1; two DLQs split by mechanism; keeper-dlq = primary alert; shared `Immediate(N)` → DLQ-1). Downstream-only (read for the boundary, do NOT build): PAUSE-01..05 (Phase 37), KMET-01..03 / TEST-01..03 (Phase 38/39).

### Prior Keeper-phase context (the foundation this phase extends)
- `.planning/phases/35-fault-intake-correlation/35-CONTEXT.md` — the two `Fault<T>` consumers + manual CorrelationId/execution scope this phase extends; D-06 "observe-and-ack" left the recovery slot open between extract and ack (where the probe loop + re-inject now go).
- `.planning/phases/33-fault-recovery-spike-de-risk/33-CONTEXT.md` — §D-05..D-10: the spike-proven re-inject-by-type + `flag[H]`-collapse + WRONGTYPE live-trip recipe; D-10 the `_error`/DLQ-1 retention decision (mechanism, not origin component).
- `.planning/phases/34-keeper-console-foundation/34-CONTEXT.md` — D-05 the compose `keeper:` tier (already injects `ConnectionStrings__Redis` + `OTEL_EXPORTER_OTLP_ENDPOINT`); D-09 the shared `Immediate(N)` pattern.

### The consumers to extend (Phase 35 output — recovery slots in between extract and ack)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `FaultExecutionResultConsumer.cs` — observe-and-ack bodies; insert the probe loop → re-inject-or-park between the unwrap and the `return`.
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs` + `FaultExecutionResultConsumerDefinition.cs` — the single endpoint-retry owner (Immediate(N)); routes Keeper's own infra faults to DLQ-1 (DLQ-01).
- `src/Keeper/Program.cs` — composition root; add `AddBaseConsoleRedis(cfg)` (D-01) + register `ProbeOptions` (D-04).
- `src/Keeper/appsettings.json` — add `ConnectionStrings:Redis` (D-01) + the `"Probe"` section (D-04).
- `src/Keeper/Keeper.csproj` — confirm the firewall stays intact (no new ProjectReference; Redis rides BaseConsole.Core — D-01).

### L2 client + key builders (probe target + scratch key)
- `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` — `AddBaseConsoleRedis` (the `IConnectionMultiplexer` singleton Keeper consumes — D-01).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData(string entryId)` (probe read target — D-02); add the new `KeeperProbe(h)` builder (D-03).
- `src/Orchestrator/Consumers/ResultConsumer.cs` — the `IConnectionMultiplexer` ctor-inject + `db.StringGetAsync`/`StringSetAsync(... when: When.Exists, keepTtl: true)` read/write shape to mirror; INFRA-fault-propagates-to-retry convention.

### DLQ-1 shared error-transport (base-library change — DLQ-05/06 research)
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — `AddBaseConsoleMessaging`; the `configureBus` bus-factory seam (line ~55) + `c.ConfigureEndpoints(ctx)` (line ~59) are where the shared error-transport / consolidated DLQ-1 wiring lands (D-06). Touches ALL three consoles via this base.
- `src/Messaging.Contracts/Configuration/RetryOptions.cs` — the shared `Immediate(N)` budget (DLQ-04) every consumer already binds; the exhaustion that feeds DLQ-1.
- `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` + `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — the other consoles' consumers whose `_error` exhaustions must re-route to the consolidated DLQ-1 (DLQ-04 uniform pattern).

### Re-inject endpoints + dedup (spike-proven, PROBE-03/06)
- `src/Messaging.Contracts/OrchestratorQueues.cs` (`orchestrator-result`) + `src/Messaging.Contracts/ProcessorQueues.cs` (`queue:{processorId:D}`) — re-inject-by-type origin endpoints.
- `src/Messaging.Contracts/KeeperQueues.cs` — `FaultRecovery` const; add `DeadLetter = "keeper-dlq"` (D-08).
- `src/Messaging.Contracts/IExecutionCorrelated.cs` — the inner tuple (`EntryId` for the probe read target, `H` for the scratch key + dedup-collapse).

### RealStack proof rig (PROBE-05, D-12)
- `tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs` (Phase 33) + `tests/BaseApi.Tests/Keeper/` Phase-35 KeeperFaultIntakeE2ETests — the standing RealStack rigs (WRONGTYPE live-trip, `PollEsForLog`, embedded SourceHash, net-zero `skp:*` teardown) to extend for live recover-both-paths + give-up.
- OTLP: `OTEL_EXPORTER_OTLP_ENDPOINT` env var is the live knob; ES message body is `body.text` (project OTLP/E2E gotchas). Rebuild the keeper container before any live proof (embedded SourceHash must match).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`AddBaseConsoleRedis`** (BaseConsole.Core) — drop-in `IConnectionMultiplexer` singleton; Keeper gets its probe client with one Program.cs line + a connection-string key, no firewall change (D-01).
- **`ResultConsumer` read/write shape** — `redis.GetDatabase()` + `StringGetAsync`/`StringSetAsync`, with INFRA-faults-propagate-to-retry; the exact template for the probe ops (D-02) and the re-inject-then-ack ordering.
- **`L2ProjectionKeys`** — `ExecutionData(entryId)` is the probe read target; the file is the single home for the new `KeeperProbe(h)` scratch builder (D-02/D-03).
- **Phase-35 Keeper consumers** — already double-unwrap, scope, and ack; the recovery loop slots cleanly into the open slot between unwrap and `return` (Phase 35 D-06 designed for this).
- **Phase-33 spike re-inject + `flag[H]`-collapse** — `GetSendEndpoint` + `Send` verbatim-inner-by-type, proven LIVE (GATE_EXIT=0); Keeper adds no dedup (PROBE-06).

### Established Patterns
- INFRA fault on a Redis/Send op = NO catch → propagates to the definition's `Immediate(N)` → `_error` (→ consolidated DLQ-1 once D-05/06 land). Business outcomes = clean return (ack).
- Competing-consumer round-robin on the single durable `keeper-fault-recovery` queue; the endpoint-retry is owned by ONE definition (the sibling's ConfigureConsumer is an intentional no-op — Phase 35 D-04).
- Shared `Immediate(N)` from `RetryOptions."Retry"` across ALL consoles (DLQ-04); `Immediate` is the only wired strategy this milestone.
- Per-consumer `{queue}_error` is the MassTransit default TODAY; the consolidated TTL'd DLQ-1 is NEW infra this phase (D-05/06).

### Integration Points
- `src/Keeper/Program.cs` + `appsettings.json` — Redis + ProbeOptions wiring (D-01/D-04).
- `src/Keeper/Consumers/Fault*Consumer.cs` — probe loop + re-inject/park bodies (shared helper, Claude's discretion).
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — shared error-transport / consolidated DLQ-1 (D-06) — base library used by ALL consoles, so existing endpoint/error behavior must stay green across processor + orchestrator + Keeper.
- `src/Messaging.Contracts/KeeperQueues.cs` + `L2ProjectionKeys.cs` — new `DeadLetter` const + `KeeperProbe` builder.
- `tests/BaseApi.Tests/` — hermetic loop-logic tests + a RealStack recover/give-up E2E (D-12).

</code_context>

<specifics>
## Specific Ideas

- The probe is **read + write**, not connectivity — D-02 mandates a real `StringGetAsync` AND a write-then-delete, because a half-open Redis (reads OK, writes failing) must still count as "down" for recovery purposes.
- The scratch key's **TTL is a net-zero safety net**, not a functional requirement — it exists so a Keeper crash mid-probe cannot leave a dangling `skp:keeper:probe:*` key that drifts the Phase-39 close-gate triple-SHA (D-03).
- The DLQ split is by **MECHANISM** (DLQ-1 = transport-exhaustion, TTL'd forensic; DLQ-2 `keeper-dlq` = probe give-up, persistent operator alert), never by origin component — carried from Phase 33 D-10.
- "delay × attempts bounded under the RabbitMQ delivery-ack timeout" is the load-bearing constraint that makes the in-Consume blocking loop legal — it MUST be documented, and the defaults (60s) leave a 30× margin under the 30-min default (D-04).
- The "stays paused / no auto-resume" success clause is **forward-declared, not built** in Phase 36 — there is nothing to keep paused until Phase 37 introduces pause (D-11). Don't let SC-4's wording pull Phase-37 contracts forward.

</specifics>

<deferred>
## Deferred Ideas

- `PauseWorkflow` / `ResumeWorkflow` contracts + the orchestrator per-workflow pending-recovery set (cron pause on intake, resume when no recoveries remain, stays-paused on give-up) — **Phase 37** (PAUSE-01..05). Phase 36's re-inject is the recovery half; this is the cron-scheduling half.
- Keeper meter + `keeper_l2_probe_failed` / DLQ-depth counters/histograms — **Phase 38** (KMET-01..03).
- `keeper-dlq`-depth Prometheus **alert** wiring + the real-stack E2E close gate (3×GREEN triple-SHA, both DLQs + scratch-key scan-clean) — **Phase 39** (TEST-01..03). The kill-mid-loop live proof (D-12) lands as the operator runbook gated here.

*No reviewed-but-deferred todos (none matched this phase).*

</deferred>

---

*Phase: 36-l2-health-probe-recovery-loop-dlqs*
*Context gathered: 2026-06-05*
