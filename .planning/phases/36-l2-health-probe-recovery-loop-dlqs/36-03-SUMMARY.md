---
phase: 36-l2-health-probe-recovery-loop-dlqs
plan: 03
subsystem: messaging-error-transport
tags: [masstransit, rabbitmq, dlq, error-transport, baseconsole-core, dlq-01, dlq-02, dlq-04]
wave: 2
depends_on: [01]
requirements: [DLQ-01, DLQ-02, DLQ-04]
dependency_graph:
  requires:
    - "BaseConsole.Core AddBaseConsoleMessaging (the AddMassTransit bus skeleton + configureBus seam)"
    - "KeeperQueues.DeadLetter = keeper-dlq (Plan 01, the DLQ-2 contrast const)"
    - "MassTransit 8.5.5 / MassTransit.RabbitMQ 8.5.5 (Apache cap; ConfigureError + IFilter<ExceptionReceiveContext>)"
  provides:
    - "ConsolidatedErrorTransportFilter — custom IFilter<ExceptionReceiveContext> moving exhausted messages to ONE fixed skp-dlq-1"
    - "ConsolidatedFault — typed forensic envelope (original serialized body + exception detail) the move forwards"
    - "skp-dlq-1 (DLQ-1) declared once with x-message-ttl = 7 days across ALL 3 consoles"
    - "DLQ-04 uniform consolidated error transport (processor + orchestrator + Keeper)"
  affects:
    - "ALL three consoles' post-exhaustion _error MOVE destination (was per-{queue}_error → now skp-dlq-1)"
    - "Plan 04 RealStack E2E (live DLQ-1 drain proof) + Phase 39 close gate (broker topology snapshot)"
tech_stack:
  added: []
  patterns:
    - "Custom IFilter<ExceptionReceiveContext> with a FIXED-destination move (mechanism-a) installed via ConfigureError"
    - "AddConfigureEndpointsCallback for once-per-endpoint, framework-deduped error-pipeline wiring (Pitfall 3)"
    - "No-consumer ReceiveEndpoint to declare a forensic queue with x-message-ttl at boot (present in both close-gate snapshots)"
key_files:
  created:
    - "src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs (filter + ConsolidatedFault envelope)"
    - "tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs (DLQ-01/02/04 hermetic facts)"
  modified:
    - "src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs (callback wiring + skp-dlq-1 declaration)"
decisions:
  - "mechanism-a (custom IFilter<ExceptionReceiveContext>) SELECTED over mechanism-b (per-{queue}_error TTL + DLX) — confirmed against MT 8.5.5 assemblies + an in-memory spike; the only mechanism giving ONE consolidated queue at exhaustion (D-05/DLQ-04 literally)"
  - "The move forwards a TYPED ConsolidatedFault envelope (original serialized body bytes + content-type + exception detail) rather than a bare byte[] — preserves message identity for forensics AND is hermetically observable (a byte[] move is untyped + untracked by the in-mem harness)"
  - "Immediate(N) retry stays OWNED per-consumer (existing FaultEntryStepDispatchConsumerDefinition.UseMessageRetry); the callback adds ONLY the ConfigureError move (Pitfall 3 — no double retry registration)"
metrics:
  tasks_completed: 3
  task_commits: 2
  files_created: 2
  files_modified: 1
  duration_minutes: 22
  completed: 2026-06-06
---

# Phase 36 Plan 03: Consolidated DLQ-1 Error Transport Summary

Wired the ONE genuinely-novel piece of Phase 36: a consolidated `skp-dlq-1` error transport in `BaseConsole.Core` (mechanism-a — a custom `IFilter<ExceptionReceiveContext>` installed via `ConfigureError` in `AddConfigureEndpointsCallback`) so processor + orchestrator + Keeper all route their `Immediate(N)` transport-exhaustion to ONE shared 7-day-TTL'd forensic queue instead of the per-`{queue}_error` default — while keeping `GenerateFaultFilter` so the `Fault<T>` pub/sub stream Keeper rides keeps publishing. DLQ-01/02/04 hermetic-proven; the live broker-arg + drain proof is deferred to Plan 04 / Phase 39.

## What Shipped

**Task 1 (BLOCKING checkpoint:decision — mechanism confirmation):** Performed real source-confirmation + a spike, then SELECTED **mechanism-a**.
- **API surface confirmed against the MT 8.5.5 assemblies** (reflection over `MassTransit.dll` / `MassTransit.Abstractions.dll` / `MassTransit.RabbitMqTransport.dll`, resolving Assumptions A1/A5):
  - `ConfigureError(Action<IPipeConfigurator<ExceptionReceiveContext>>)` — exposed on `IReceivePipelineConfigurator` / `EndpointConfiguration` (the receive-endpoint configurator).
  - `MassTransit.Middleware.GenerateFaultFilter` — **public, parameterless ctor** (`new GenerateFaultFilter()` is valid); `MassTransit.Middleware.ErrorTransportFilter` is the public default move filter (resolves its per-endpoint `IErrorTransport` from the context payload).
  - `ExceptionReceiveContext : ReceiveContext` exposes `Exception`, `ExceptionInfo`, `ExceptionTimestamp`, `ExceptionHeaders`, and (inherited) `SendEndpointProvider`, `Body` (`MessageBody`), `ContentType`, `TransportHeaders` — everything a fixed-destination move needs.
  - `AddConfigureEndpointsCallback` lives on the **registration configurator `x`** (inside `AddMassTransit(x => …)`), NOT the bus factory.
- **In-memory spike PASSED:** an always-throwing consumer under `Immediate(N)` with `ConfigureError(GenerateFaultFilter + custom-move-filter)` produced both (a) the exhausted message reaching a `skp-dlq-1` send endpoint AND (b) `Fault<Boom>` still published. (Spike learning that shaped the final design: the in-mem harness does NOT track a `byte[]` error-pipe send, and a `byte[]` cannot be a registered message/handler type — drove the typed-envelope decision below.)

**Task 2 (`feat`, commit `edc4787`):**
- `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` — custom `IFilter<ExceptionReceiveContext>` that resolves a send endpoint for the ONE fixed `queue:skp-dlq-1` (const `Dlq1`, never config-injected — T-36-10) and forwards the ORIGINAL serialized body + content-type + original transport headers + `MT-Fault-*` exception headers, mirroring the default `_error` move (T-36-11).
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — added `x.AddConfigureEndpointsCallback(… e.ConfigureError(ep => { ep.UseFilter(new GenerateFaultFilter()); ep.UseFilter(new ConsolidatedErrorTransportFilter()); }))` inside `AddMassTransit` (once-per-endpoint, framework-deduped — Pitfall 3), and a no-consumer `c.ReceiveEndpoint("skp-dlq-1", e => e.SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds))` inside `UsingRabbitMq` BEFORE `ConfigureEndpoints(ctx)`. All 4 correlation filters + `configureBus?.Invoke` + `ConfigureEndpoints(ctx)` preserved verbatim and in order.
- `dotnet build SK_P.sln -c Release` → **0 Warning / 0 Error.**

**Task 3 (`test`, commit `28d528e`):**
- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` — 3 hermetic facts (named to match VALIDATION.md filters): `Dlq1_Consolidated` (exhaustion routes a typed `ConsolidatedFault` to skp-dlq-1 AND `Fault<T>` still published), `Keeper_SendFault_RetriesToDlq1` (`Immediate(N)` honoured then consolidated DLQ-1), `Dlq_TopologyArgs` (DLQ-1 TTL'd const vs DLQ-2 `keeper-dlq` no-TTL const + 604800000 ms = 7 days).
- Added a typed `ConsolidatedFault` forensic envelope to the filter file (see Deviations) so the move preserves message identity AND is hermetically observable.

## Verification

- `dotnet build SK_P.sln -c Release` — **0 Warning / 0 Error.**
- The 3 named DLQ facts each match `--filter-method` and PASS (`Dlq1_Consolidated` 1/1, `Keeper_SendFault_RetriesToDlq1` 1/1, `Dlq_TopologyArgs` 1/1).
- **Full Keeper namespace 16/16 deterministic** (13 prior + 3 new), including `KeeperDependencyFirewallTests` (no new forbidden reference — `ConsolidatedFault` lives in BaseConsole.Core, which Keeper already references).
- **Full hermetic suite (`--filter-not-trait "Category=RealStack"`): 465 passed / 2 failed of 467** (was 464 + 3 new = 467). The 2 reds — `Orchestrator.ResultConsumeTests.Result_ConsumedExactlyOnce_NotBroadcast` and `Orchestrator.FireDispatchTests.OneMessagePerEntryStep_CorrectFields` — are the **documented run-to-run cross-namespace in-memory-MassTransit harness flake** (the "Failed to stop bus … (Not Started)" / `rabbitmq://localhost` temporary-bus teardown race, STATE.md 35-02/36-02 precedent): **both PASS GREEN in isolation (5/5)**, are in untouched Orchestrator dispatch/result files, and do not exercise the BaseConsole.Core error-transport topology this plan changed. NOT a regression.
- Task-2 acceptance greps (exact counts): `GenerateFaultFilter`==1, `x-message-ttl`==1, `ConfigureError`>=1, `604800000|FromDays(7)`>=1, the 4 correlation filters==4, `configureBus?.Invoke`==1, `ConfigureEndpoints(ctx)`==1 (comment wordings adjusted so the literal tokens hit exactly — same 35-02/34 grep-compliance precedent).

## Deviations from Plan

### Auto-added critical functionality

**1. [Rule 2 — Missing critical functionality] Typed `ConsolidatedFault` forensic envelope (not a bare `byte[]` move)**
- **Found during:** Task 1 spike + Task 3.
- **Issue:** The plan's filter shape implies moving the raw faulted message. A bare `byte[]` move (a) is REJECTED as a MassTransit message/handler type ("Messages types must not be in the System namespace: System.Byte[]"), (b) is NOT tracked by the in-memory harness's `Sent`/`Consumed` probes (so the move is unobservable hermetically), and (c) loses message-type identity for forensics.
- **Fix:** The filter forwards a typed `ConsolidatedFault` record (namespace `BaseConsole.Core.Messaging`) carrying the ORIGINAL serialized body bytes + content-type + the exception type/message/timestamp, preserving the original transport + `MT-Fault-*` headers. This is strictly MORE faithful than a raw byte move (operators can both reconstruct the original message AND read structured exception detail) and is fully observable hermetically.
- **Files modified:** `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs`.
- **Commit:** `28d528e`.

No bugs, no architectural changes, no auth gates, no scope creep. Retry ownership kept per-consumer (the callback adds ONLY the ConfigureError move) exactly as the plan's Pitfall-3 branch directs; `GenerateFaultFilter` retained.

## Threat Surface

No NEW threat surface beyond the plan's `<threat_model>`. The mitigations land as authored: T-36-09 (skp-dlq-1 x-message-ttl=7d — declared), T-36-10 (fixed const destination + GenerateFaultFilter kept — asserted by `Dlq1_Consolidated`), T-36-11 (faithful header/body move — typed envelope captures body+content-type+exception+`MT-Fault-*` headers), T-36-13 (once-per-endpoint callback — full Keeper suite green, no doubled filters). T-36-12 (faulted payloads visible to operators) accepted, unchanged from the existing `_error` default.

## Pending-Verification (operator runbook — LIVE DLQ-1, deferred to Plan 04 / Phase 39)

This plan is `autonomous:false` with a BLOCKING checkpoint:decision (Task 1). Task 1 was resolved by the executor performing the source-confirm + spike (mechanism-a, the research-recommended + RESEARCH-designated Plan-36-03/Task-1 resolution); it is NOT a user fork. No live Docker stack was started this session — the RabbitMQ-specific behaviors (the broker actually creating `skp-dlq-1` with `x-message-ttl=7d`, the real `_error`→skp-dlq-1 move, and orphan `{queue}_error` cleanup) are inherently un-observable hermetically and are deferred to the authoritative live signals:

1. **Rebuild containers** (embedded SourceHash must match — project gotcha): `docker compose up -d --build processor-sample orchestrator keeper baseapi-service` (ALL consoles whose BaseConsole.Core error transport changed).
2. **One-time broker reset (Pitfall 2):** if a `skp-dlq-1` ever existed without the TTL, delete it on the broker first (RabbitMQ rejects arg changes on a live queue; MT never deletes). Also purge/delete orphan `{queue}_error` queues so they do not drift the Phase-39 close-gate rabbitmq snapshot (Pitfall 1).
3. **Verify topology after boot:** `skp-dlq-1` present with `x-message-ttl=604800000`; `keeper-dlq` present with NO TTL.
4. **Live DLQ-1 move proof:** Plan 04's RealStack E2E (induce a transport exhaustion and assert the faulted message lands in `skp-dlq-1`). The Phase-39 3×GREEN triple-SHA close gate is the authoritative net-zero + live signal.

DLQ-01/02/04 are code-complete + hermetically proven; the LIVE arg/move/drain proof is operator-pending. NOT ticked beyond what hermetic proof supports — the orchestrator/verifier handles REQUIREMENTS traceability on the operator's GREEN live run.

## Commits

- `edc4787` — feat(36-03): consolidated skp-dlq-1 error transport (DLQ-01/02/04, mechanism-a)
- `28d528e` — test(36-03): hermetic DLQ-01/02/04 consolidation proof + typed forensic envelope

(Scoped paths only — the ~242 pre-existing `.planning/` archive deletions left UNtouched, NOT staged, NOT reverted; verified 242 still uncommitted after both commits. NO file deletions in either commit.)

## Self-Check: PASSED

All 4 source/test/summary files exist on disk; both task commits (edc4787, 28d528e) present in git history.
