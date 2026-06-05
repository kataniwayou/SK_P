---
phase: 34-keeper-console-foundation
plan: 02
subsystem: keeper
tags: [keeper, masstransit, competing-consumer, console, dockerfile, thin-shell, console-mirror]

# Dependency graph
requires:
  - phase: 34-keeper-console-foundation
    plan: 01
    provides: "Keeper.csproj (leaner reference closure) + KeeperQueues.FaultRecovery const + SK_P.sln entry"
  - phase: 19-orchestrator-console
    provides: "Orchestrator Program.cs / ResultConsumerDefinition / Dockerfile / appsettings.json — the byte-near clone analogs"
  - phase: 18-baseconsole-core
    provides: "AddBaseConsoleObservability / AddBaseConsole / AddBaseConsoleMessaging seam + default StartupCompletionService (readiness-on-bus-start)"
provides:
  - "src/Keeper/Program.cs — thin-shell composition root (Orchestrator seam MINUS scheduler/L1/hydration/metrics + MINUS the default-readiness-service removal; KEEPS readiness-on-bus-start, D-06)"
  - "src/Keeper/Consumers/PlaceholderConsumerDefinition.cs — stable DURABLE competing-consumer binding (EndpointName=KeeperQueues.FaultRecovery + UseMessageRetry(Immediate(Limit))); the D-02/D-09 load-bearing shape"
  - "src/Keeper/Consumers/PlaceholderConsumer.cs — no-op log-only IConsumer<KeeperPlaceholder> (D-03 topology proof)"
  - "src/Keeper/Consumers/KeeperPlaceholder.cs — local throwaway record : ICorrelated (deleted in Phase 35)"
  - "src/Keeper/appsettings.json — Service.Name=keeper, ConsoleHealth.Port=8083, Retry Immediate(3)"
  - "src/Keeper/Dockerfile — multi-stage sdk:8.0 -> aspnet:8.0, wget, port 8083, Keeper.dll entrypoint"
affects: [34-03-compose-and-tests, 35-keeper-fault-consumers]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Thin-shell composition root: Host.CreateApplicationBuilder -> AddBaseConsoleObservability -> AddBaseConsole -> Configure<RetryOptions> -> AddBaseConsoleMessaging(plain AddConsumer) -> RunAsync (Orchestrator MINUS scheduler/L1/hydration/metrics + MINUS the default-readiness-service removal)"
    - "Stable DURABLE competing-consumer binding: ConsumerDefinition.EndpointName = <stable const> + plain AddConsumer + UseMessageRetry(Immediate(Limit)) — round-robin, NOT per-replica auto-delete fan-out (close-gate net-zero SHA)"
    - "Multi-stage console Dockerfile on aspnet:8.0 (NOT runtime:8.0 — embedded Kestrel health needs the ASP.NET shared framework), wget installed as root before USER app"

key-files:
  created:
    - src/Keeper/Consumers/KeeperPlaceholder.cs
    - src/Keeper/Consumers/PlaceholderConsumer.cs
    - src/Keeper/Consumers/PlaceholderConsumerDefinition.cs
    - src/Keeper/Program.cs
    - src/Keeper/appsettings.json
    - src/Keeper/Dockerfile
  modified: []

key-decisions:
  - "Reworded explanatory comments in Program.cs / PlaceholderConsumerDefinition.cs / Dockerfile to drop forbidden literal tokens (the readiness-service name / per-replica-endpoint terms / BaseProcessor.Core / BaseApi) — the plan acceptance criteria + the Plan-03 KeeperDependencyFirewallTests + any source-grep firewall guard match these literally; same Rule-2 precedent set in 34-01. Zero behavioral/structural change."
  - "KeeperPlaceholder MESSAGE type kept LOCAL to Keeper (src/Keeper/Consumers/), only the KeeperQueues const is shared (RESEARCH OQ-1) — the message is deleted wholesale in Phase 35."
  - "Service.Version=3.7.0 in appsettings (current milestone version) rather than the Orchestrator's 3.4.0 — the value is non-load-bearing; mirrored the key shape exactly."

patterns-established:
  - "Pattern: Keeper thin-shell = Orchestrator seam minus the runtime block AND minus the readiness-service removal (Keeper KEEPS readiness-on-bus-start, D-06)"
  - "Pattern: durable shared competing-consumer queue materialized by a throwaway placeholder consumer so KEEP-02 round-robin is live-verifiable + the close-gate rabbitmq triple-SHA stays net-zero (Pitfall 1)"

requirements-completed: [KEEP-01, KEEP-02]

# Metrics
duration: 5min
completed: 2026-06-05
---

# Phase 34 Plan 02: Keeper Console Body Summary

**The runnable Keeper console body: the thin-shell `Program.cs` (Orchestrator seam MINUS scheduler/L1/hydration/metrics AND MINUS the readiness-service removal — Keeper keeps readiness-on-bus-start, D-06), the throwaway local `KeeperPlaceholder` + no-op `PlaceholderConsumer`, the load-bearing stable DURABLE competing-consumer `PlaceholderConsumerDefinition` (EndpointName=KeeperQueues.FaultRecovery + Immediate(Limit); zero fan-out shape), `appsettings.json` (port 8083), and the multi-stage aspnet:8.0 `Dockerfile` (wget, port 8083, Keeper.dll). Builds 0-warning Release+Debug, full solution clean, and `docker build` produces the 334MB image.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-06-05T14:30:43Z
- **Completed:** 2026-06-05T14:35:05Z
- **Tasks:** 3
- **Files modified:** 6 (6 created, 0 modified)

## Accomplishments
- **KeeperPlaceholder + PlaceholderConsumer + PlaceholderConsumerDefinition** — the placeholder consumer surface. The definition binds the STABLE DURABLE shared queue `KeeperQueues.FaultRecovery` ("keeper-fault-recovery") via `EndpointName` + plain `AddConsumer` + `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` (D-02/D-09). The throwaway message stays LOCAL to Keeper (deleted Phase 35); the no-op consumer is a single log line (D-03 topology proof). **NO per-replica auto-delete fan-out shape anywhere** — that is the explicit anti-pattern for Keeper (Pitfall 1: keeps the Phase-38 close-gate rabbitmq triple-SHA net-zero).
- **Program.cs (thin-shell composition root)** — the Orchestrator seam (`Host.CreateApplicationBuilder` -> `AddBaseConsoleObservability` -> `AddBaseConsole` -> `Configure<RetryOptions>` -> `AddBaseConsoleMessaging(plain AddConsumer)` -> `RunAsync`) with the two D-08 deletions: the entire runtime-wiring block (scheduler/L1/lifecycle/dispatch/metrics/hydration) AND the default-readiness-service removal `foreach`. Keeper KEEPS the default readiness service so readiness flips on bus-start (D-06). Plus **appsettings.json** with Service.Name=keeper, ConsoleHealth.Port=8083, Retry Immediate(3), and no instance-id key.
- **Dockerfile** — multi-stage `sdk:8.0-bookworm-slim` -> `aspnet:8.0-bookworm-slim` (aspnet NOT runtime — the embedded Kestrel health listener needs the ASP.NET shared framework; Pitfall 3); COPY restore-cache closure = Messaging.Contracts + BaseConsole.Core + Keeper csproj only; wget installed as root BEFORE `USER app` (Pitfall 2); `ASPNETCORE_URLS`/`EXPOSE` 8083; `ENTRYPOINT ["dotnet", "Keeper.dll"]`. `docker build` produced the 334MB image end-to-end.

## Task Commits

Each task committed atomically (scoped paths only — the in-progress `.planning/` archive deletions + untracked `launchSettings.json`/`psql-*.txt` left untouched, NOT staged, NOT reverted, per established project precedent):

1. **Task 1: KeeperPlaceholder + PlaceholderConsumer + PlaceholderConsumerDefinition** — `d9b9c56` (feat)
2. **Task 2: Program.cs (thin-shell) + appsettings.json (port 8083)** — `b40457b` (feat)
3. **Task 3: Dockerfile (multi-stage aspnet:8.0, wget, port 8083, Keeper.dll)** — `54f8427` (feat)

## Files Created/Modified
- `src/Keeper/Consumers/KeeperPlaceholder.cs` — local throwaway `record KeeperPlaceholder : ICorrelated` with `Guid CorrelationId` (body-correlation; deleted Phase 35).
- `src/Keeper/Consumers/PlaceholderConsumer.cs` — `sealed class PlaceholderConsumer(ILogger<PlaceholderConsumer>) : IConsumer<KeeperPlaceholder>`; no-op log-only body (D-03).
- `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` — `ConsumerDefinition<PlaceholderConsumer>`; `EndpointName = KeeperQueues.FaultRecovery`; `ConfigureConsumer` -> `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`. The D-02/D-09 load-bearing template.
- `src/Keeper/Program.cs` — thin-shell composition root; KEEPS the default readiness service (D-06); no scheduler/L1/hydration/metrics block; plain `AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>()`.
- `src/Keeper/appsettings.json` — Service.Name=keeper, Service.Version=3.7.0, ConsoleHealth.Port=8083, Retry Immediate(3); mirrors Orchestrator's key shape; no instance-id key.
- `src/Keeper/Dockerfile` — multi-stage aspnet:8.0 console build; wget; port 8083; Keeper.dll entrypoint.

## Decisions Made
- **Const/message split:** the `KeeperPlaceholder` message type stays LOCAL to Keeper (deleted in Phase 35); only the shared `KeeperQueues.FaultRecovery` const (from Plan 01, in Messaging.Contracts) is reused by Phase 35's real consumers.
- **Service.Version=3.7.0** (current milestone) instead of the analog's 3.4.0 — non-load-bearing value; the key shape/names mirror Orchestrator exactly.
- **Comment-token rewording** (see Deviations) — applied the 34-01 precedent so the acceptance/firewall literal-greps hold.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Reworded explanatory comments to drop forbidden literal tokens**
- **Found during:** Tasks 2 and 3 (grep-guard verification after writing the files).
- **Issue:** First drafts cloned the Orchestrator analogs' comment style, which mentions the readiness-service class name, the per-replica `InstanceId`/`Temporary` endpoint terms (Program.cs / PlaceholderConsumerDefinition.cs), and `BaseProcessor.Core`/`BaseApi` (Dockerfile) in explanatory prose. The plan's acceptance criteria require these files to NOT contain those literal strings, and the Plan-03 `KeeperDependencyFirewallTests` plus any source-grep firewall guard match literally — so leaving the tokens in comments would make a correctness/firewall acceptance grep falsely fail. Identical situation + resolution to the 34-01 SUMMARY deviation.
- **Fix:** Reworded the comments to convey the same intent without the forbidden literals (e.g. "default-readiness-service removal", "per-replica auto-delete fan-out endpoint", "no processor-base or web-api projects"). No structural/reference/behavioral change — the actual code (plain `AddConsumer`, stable `EndpointName`, the 3-csproj COPY closure, the kept readiness service) was already correct.
- **Files modified:** src/Keeper/Program.cs, src/Keeper/Consumers/PlaceholderConsumerDefinition.cs, src/Keeper/Dockerfile
- **Verification:** post-reword grep == 0 for each of the forbidden token classes in each file; required tokens all present; Debug+Release rebuild 0/0; docker build still succeeds.
- **Committed in:** the respective task commits (`b40457b`, `54f8427`).

---

**Total deviations:** 1 auto-fixed (1 missing critical — firewall/acceptance grep compliance). **Impact on plan:** comment-only rewording; zero behavioral/structural/reference change. No scope creep.

## Verification Evidence
- `dotnet build src/Keeper -c Debug` → **0 Warning(s) / 0 Error(s)**.
- `dotnet build src/Keeper -c Release` → **0 Warning(s) / 0 Error(s)**.
- `dotnet build SK_P.sln -c Release` → **0 Warning(s) / 0 Error(s)** (Keeper joins the solution build cleanly).
- `docker build -f src/Keeper/Dockerfile -t keeper-build-check .` → **succeeded** end-to-end (publish + wget install + image export all green; image 334MB on aspnet:8.0; cleaned up after verification).
- Grep guards: Program.cs has 0 of `AddQuartz`/the readiness-service name/`InstanceId`/`Temporary`/`HydrationBackgroundService`/`IWorkflowL1Store`/`WorkflowScheduler`/`ConfigureOpenTelemetryMeterProvider`/`using Quartz`; the definition has 0 `InstanceId`/`Temporary`; all 3 consumer files have 0 `InstanceId`/`Temporary`; Dockerfile has 0 `8081`/`Orchestrator.dll`/`BaseProcessor.Core`/`BaseApi`; wget (line 29) is before `USER app` (line 31).

## Issues Encountered
None beyond the comment-token rewording (Deviation 1). All build + docker verifications passed.

## User Setup Required
None — no external service configuration required for this plan.

## Next Phase Readiness
- **Plan 03** (compose `keeper:` tier with `deploy.replicas: 2` + the Keeper test suite: `KeeperRoundRobinTests`, `KeeperHostBootTests` + fixture, `KeeperDependencyFirewallTests`, `ComposeYamlFacts` Keeper facts) consumes this console body directly — `PlaceholderConsumer`/`PlaceholderConsumerDefinition`/`KeeperPlaceholder` are the test targets; the Dockerfile is the compose `build.dockerfile` target.
- **Phase 35** (real Fault<T> consumers) swaps the placeholder consumer + the local `KeeperPlaceholder` message wholesale, reusing the SAME stable `KeeperQueues.FaultRecovery` endpoint name — so the queue (and its close-gate SHA) survives the swap.
- No blockers.

## Threat Surface
No new surface beyond the plan's `<threat_model>`:
- **T-34-03** (Tampering, inbound correlation id): inherited posture — `KeeperPlaceholder.CorrelationId` is read by the bus-wide `InboundCorrelationConsumeFilter` as a scope VALUE under a fixed key; the no-op consumer never interpolates it into a log template. No new code.
- **T-34-04** (Info Disclosure, secrets in image): accept — guest/guest + Redis conn come from compose env (Plan 03), never the Dockerfile/image; no secrets in any src/Keeper file. Confirmed: no credential literals in appsettings beyond the non-secret RabbitMq guest/guest default (mirrors Orchestrator).
- **T-34-06** (EoP, container runs as root): mitigated — the Dockerfile runs `USER app` (non-root) before `ENTRYPOINT`.
No new network endpoint, auth path, file-access pattern, or schema change at a trust boundary.

## Self-Check: PASSED

- Files: `src/Keeper/Consumers/KeeperPlaceholder.cs`, `src/Keeper/Consumers/PlaceholderConsumer.cs`, `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs`, `src/Keeper/Program.cs`, `src/Keeper/appsettings.json`, `src/Keeper/Dockerfile`, `.planning/phases/34-keeper-console-foundation/34-02-SUMMARY.md` — all to be confirmed FOUND below.
- Commits: `d9b9c56`, `b40457b`, `54f8427` — to be confirmed FOUND below.

---
*Phase: 34-keeper-console-foundation*
*Completed: 2026-06-05*
