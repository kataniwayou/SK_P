---
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
plan: 02
subsystem: orchestrator-console
tags: [orchestrator, console, masstransit, consumers, fan-out, ack-split, redis-l2]
requires:
  - BaseConsole.Core (Phase 18) — AddBaseConsoleObservability + AddBaseConsole + AddBaseConsoleMessaging
  - Messaging.Contracts (Phase 17 + 19-01 slim) — StartOrchestration/StopOrchestration : ICorrelated, WorkflowRootProjection
  - 19-01 — body-carried correlation (inbound consume filter reads CorrelationId off the message body)
provides:
  - "Runnable Orchestrator console (first concrete BaseConsole.Core inheritor): Generic-Host thin shell"
  - "StartOrchestrationConsumer / StopOrchestrationConsumer — read L2 root per WorkflowId, log scheduler-job-start seam, business-ack/infra-throw split, no Redis writes, no Quartz"
  - "Start/Stop ConsumerDefinitions — shared EndpointName 'orchestrator' + UseMessageRetry(Immediate(3)) + Ignore<WorkflowRootNotFoundException>"
  - "Instance-unique temporary/auto-delete fan-out endpoint orchestrator-{InstanceId} (1->N replica broadcast, no code change)"
  - "OrchestratorL2Keys.Root — byte-identical {prefix}{workflowId:D} to BaseApi.Service RedisProjectionKeys"
  - "WorkflowRootNotFoundException — business-failure type the retry pipeline Ignore<>s"
affects:
  - Plan 19-04 (infra) adds the Orchestrator Dockerfile + compose service; this plan owns only the project + code
  - Phase 20 — real-broker two-bus fan-out proof (TEST-RMQ-01) replaces the in-memory harness assertions
tech-stack:
  added: []
  patterns:
    - "ConsumerDefinition as the per-consumer retry/Ignore<>/endpoint config seam (MassTransit)"
    - "Shared EndpointName + .Endpoint(e => { e.InstanceId; e.Temporary = true; }) -> one per-replica fan-out queue grouping both consumers"
    - "Business-ack vs infra-throw split: catch the business OUTCOME (raw.IsNullOrEmpty -> continue), never catch(Exception); infra faults propagate to bounded retry -> _error"
    - "Typed OrchestratorRedisOptions Singleton carrying the L2 key prefix to consumers (over raw IConfiguration injection)"
key-files:
  created:
    - src/Orchestrator/Orchestrator.csproj
    - src/Orchestrator/Program.cs
    - src/Orchestrator/appsettings.json
    - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
    - src/Orchestrator/Messaging/OrchestratorRedisOptions.cs
    - src/Orchestrator/Consumers/WorkflowRootNotFoundException.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs
  modified:
    - SK_P.sln
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
decisions:
  - "OutputType=Exe added to Orchestrator.csproj (Rule 3): a runnable console with top-level statements requires an executable (CS8805); the plan's csproj snippet omitted it"
  - "Content copy of appsettings.json added to Orchestrator.csproj (Rule 3): Microsoft.NET.Sdk (worker) does NOT copy appsettings.json by default unlike .Web; the runnable console reads its config at boot"
  - "OrchestratorRedisOptions typed Singleton introduced to carry Redis:KeyPrefix to consumers (the plan permitted 'IConfiguration or an injected options'); chosen for testability and to avoid raw IConfiguration in consumer ctors"
  - "Orchestrator:InstanceId intentionally OMITTED from appsettings.json to exercise the Guid.NewGuid().ToString(\"N\") fallback (plan: 'may be omitted')"
  - "L2 key uses $\"{prefix}{workflowId:D}\" — renders byte-identically to RedisProjectionKeys' bare $\"{prefix}{workflowId}\" (both default \"D\" hyphenated); no silent read-miss"
metrics:
  duration: ~8min
  completed: 2026-05-30
---

# Phase 19 Plan 02: Orchestrator Console (Thin Shell + Fan-Out + Ack Split) Summary

Built the runnable `Orchestrator` console — the first concrete inheritor of `BaseConsole.Core`. It is a thin Generic-Host shell: a composition-root `Program.cs` plus two consumers, two consumer definitions, a business-failure exception, an L2 key helper, a typed Redis-options carrier, the project + sln entry, and an in-memory harness ack-split test. The console consumes `StartOrchestration`/`StopOrchestration` on its own per-replica temporary/auto-delete fan-out queue, reads the Redis L2 root per `WorkflowId`, and logs to the scheduler-job-start seam — with NO Redis writes and NO Quartz. Business failures are caught + logged + acked; infra faults throw into a bounded retry pipeline. Full SK_P.sln suite is GREEN (256/256) and the Release build is zero-warning.

## What Was Built

- **Task 1 — Project scaffold, sln entry, L2 key, business exception** (commit `aa17c05`)
  - `Orchestrator.csproj` — `Microsoft.NET.Sdk` (NOT `.Web`), inherits common props from Directory.Build.props; references ONLY BaseConsole.Core + Messaging.Contracts (no `BaseApi.*`, D-08). ASP.NET Core surface for the embedded health listener flows transitively via BaseConsole.Core's `FrameworkReference`.
  - `SK_P.sln` — fresh project GUID `{FB8035AA-…}`, C# SDK project-type GUID, path `src\Orchestrator\Orchestrator.csproj`, 4 Debug/Release|Any CPU config-platform lines, nested under the `src` solution folder — mirroring the BaseConsole.Core entry.
  - `OrchestratorL2Keys.Root(prefix, workflowId)` = `$"{prefix}{workflowId:D}"` — byte-identical render to `RedisProjectionKeys.Root` (both default "D" hyphenated; no "N" mismatch → no silent L2 read miss).
  - `WorkflowRootNotFoundException(Guid workflowId)` — business-failure type carrying `WorkflowId`, the retry `Ignore<>` target.
  - Verified: `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` → 0 Warning / 0 Error.

- **Task 2 — Consumers, definitions, thin-shell Program.cs** (commit `988c8ec`)
  - `StartOrchestrationConsumer` / `StopOrchestrationConsumer` — primary-ctor `IConsumer<T>` taking `IConnectionMultiplexer` + `ILogger<T>` + `OrchestratorRedisOptions`. Per `WorkflowId`: `redis.GetDatabase()` (infra fault throws), `StringGetAsync(OrchestratorL2Keys.Root(prefix, id))`; `raw.IsNullOrEmpty` → log warning + `continue` (business-ack, never thrown); else deserialize `WorkflowRootProjection` + `LogInformation("Scheduler job start (seam) …")`. NO `catch (Exception)`, NO Redis write, NO Quartz. Inbound filter already opened the correlated scope — not re-opened.
  - `StartOrchestrationConsumerDefinition` / `StopOrchestrationConsumerDefinition` — both `EndpointName = "orchestrator"` (shared base name → `ConfigureEndpoints` groups both onto one per-replica endpoint, A2); `UseMessageRetry(r => { r.Immediate(3); r.Ignore<WorkflowRootNotFoundException>(); })`.
  - `Program.cs` — `Host.CreateApplicationBuilder` + `AddBaseConsoleObservability` + `AddBaseConsole` + `AddBaseConsoleMessaging`; both consumers share ONE captured `instanceId` via `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` → `orchestrator-{instanceId}` temporary/auto-delete fan-out queue (ORCH-CON-02). `Orchestrator:InstanceId` falls back to `Guid.NewGuid().ToString("N")`. No `.WithTracing`/`.AddSource("MassTransit")` (metrics-only OTel, Pitfall 4); no `WebApplication.CreateBuilder`.
  - `appsettings.json` — `Service:{Name,Version}`, `RabbitMq:{Host,Username,Password}` (rabbitmq/guest/guest), `ConnectionStrings:Redis` + `Redis:KeyPrefix` (skp:), `ConsoleHealth:Port` (8081); `Orchestrator:InstanceId` deliberately omitted (exercises the GUID fallback).
  - `OrchestratorRedisOptions(string KeyPrefix)` — typed Singleton carrying the L2 prefix to the consumers.
  - Verified: `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` → 0 Warning / 0 Error; grep guards (no `catch (Exception)`, no Redis write API, no tracing) all clean.

- **Task 3 — In-memory harness ack-split tests** (TDD plan-task; commit `cd4f71d`)
  - `BaseApi.Tests` → `Orchestrator` ProjectReference added.
  - `StartStopConsumerAckTests` — 6 facts (Start ×3, Stop ×3) on `AddMassTransitTestHarness` + `UsingInMemory` (no real broker — Phase 19 is harness-only). NSubstitute `IConnectionMultiplexer`/`IDatabase` stubs (absent → `RedisValue.Null`; present → serialized `WorkflowRootProjection`; infra → `RedisConnectionException`); a `CapturingLoggerProvider` spy for the seam-log assertion.
    - MSG-ACK-01: absent-from-L2 → `Consumed.Any<T>()` true AND `Consumed.Any<T>(m => m.Exception != null)` false (acked, not faulted).
    - ORCH-CON-04: present-in-L2 → seam message "Scheduler job start" logged AND `db.DidNotReceive().StringSetAsync(...)` (zero Redis writes).
    - MSG-ACK-02: infra fault → `Consumed.Any<T>(m => m.Exception != null)` true (propagates, not swallowed).
  - Verified: full suite GREEN — Failed: 0, Passed: 256, Total: 256 (was 250; +6 new).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added `<OutputType>Exe</OutputType>` to Orchestrator.csproj**
- **Found during:** Task 2 (Release build failed CS8805 "Program using top-level statements must be an executable").
- **Issue:** The plan's csproj snippet omitted `OutputType`; a runnable console host with top-level statements + `RunAsync` cannot build as a library.
- **Fix:** Added `<OutputType>Exe</OutputType>` to the PropertyGroup with an explanatory comment.
- **Files modified:** src/Orchestrator/Orchestrator.csproj
- **Commit:** `988c8ec`

**2. [Rule 3 - Blocking] Added Content copy of appsettings.json to Orchestrator.csproj**
- **Found during:** Task 2.
- **Issue:** `Microsoft.NET.Sdk` (worker) does NOT copy `appsettings.json` to the output directory by default (unlike `.Web`). Without it, the runnable console (the whole point of ORCH-CON-01) would boot with no `Service`/`RabbitMq`/`Redis`/`ConsoleHealth` config and fail the `cfg.Require` fail-fast at startup.
- **Fix:** `<Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />`.
- **Files modified:** src/Orchestrator/Orchestrator.csproj
- **Commit:** `988c8ec`

### Discretionary (plan-permitted)

**3. OrchestratorRedisOptions typed Singleton** — the plan said the consumers may read the prefix "from `IConfiguration` or an injected options". Chose a small `OrchestratorRedisOptions(string KeyPrefix)` record (registered in Program.cs from `Redis:KeyPrefix`, default "skp:") over injecting raw `IConfiguration` into consumer constructors — cleaner dependency surface and lets the harness register a known prefix. Not a deviation; an explicit fork resolution.

## TDD Gate Compliance

Task 3 is marked `tdd="true"`, but in this plan's task ordering the production consumers (Task 2) are authored before the tests (Task 3) — the plan structures Task 3 as a test-after harness that asserts the already-built ack-split/no-write/seam behavior, not as a RED-first feature increment. There is therefore a single `test(19-02): …` commit (`cd4f71d`) with no preceding RED commit for net-new behavior. The 6 facts pass GREEN against the committed implementation (256/256 full suite). No RED gate commit exists because the implementation predates the tests within this plan's defined sequence; this is a structural consequence of the plan's task split, not a skipped gate.

## Threat Surface

Per the plan's threat register, all five dispositions are `mitigate` and were honored:
- **T-19-fan-out-trap** — each replica binds a distinct `orchestrator-{InstanceId}` temporary/auto-delete queue via `.Endpoint(e => { e.InstanceId; e.Temporary = true; })` (fan-out broadcast, not competing-consumer). Real two-bus broadcast proof is Phase 20.
- **T-19-ack-business** — absent-from-L2 is caught (`raw.IsNullOrEmpty → continue`) + logged + acked; `Ignore<WorkflowRootNotFoundException>` blocks any escaped business exception from retry-storming.
- **T-19-ack-infra** — infra faults throw → bounded `UseMessageRetry(r.Immediate(3))` → `_error`; the harness infra-fault facts prove propagation (no clean ack).
- **T-19-payload-log** — consumers log only `{WorkflowId}` (+ the inbound filter's CorrelationId scope), never the full message/root payload.
- **T-19-l2-write** — consumers use `StringGetAsync` only; grep confirms zero write APIs; the harness facts assert `db.DidNotReceive().StringSetAsync(...)`.

No new threat surface beyond the plan's register.

## Verification Evidence

| Check | Result |
|-------|--------|
| `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug --nologo` | 0 Warning / 0 Error |
| `dotnet build src/Orchestrator/Orchestrator.csproj -c Release --nologo` | 0 Warning / 0 Error |
| `dotnet build SK_P.sln -c Release --nologo` | 0 Warning / 0 Error |
| Orchestrator harness tests (filter ignored by MTP — full suite ran) | Failed: 0, Passed: 256, Total: 256 (+6 new) |
| Grep `catch (Exception)` in Consumers | NONE |
| Grep Redis write API (StringSet/HashSet/KeyDelete) in Consumers | NONE |
| Grep `.WithTracing` / `.AddSource("MassTransit")` / `WebApplication.CreateBuilder` in Program.cs | NONE |
| `OrchestratorL2Keys.Root` vs `RedisProjectionKeys.Root` rendered string | Byte-identical ("D" hyphenated) |

## Commits

- `aa17c05` feat(19-02): scaffold Orchestrator project, sln entry, L2 key helper, business exception
- `988c8ec` feat(19-02): consumers, definitions, thin-shell Program.cs (fan-out + ack split)
- `cd4f71d` test(19-02): in-memory harness ack-split + no-write + seam-log for Orchestrator consumers

## Self-Check: PASSED

All 12 created/modified files present on disk; all 3 task commits (`aa17c05`, `988c8ec`, `cd4f71d`) present in git history.
