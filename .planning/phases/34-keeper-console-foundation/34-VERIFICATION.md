---
phase: 34-keeper-console-foundation
verified: 2026-06-05T15:10:00Z
status: human_needed
score: 4/4 must-haves verified (hermetic); live stack halves require operator confirmation
overrides_applied: 0
human_verification:
  - test: "Live multi-replica round-robin smoke (KEEP-02 live half)"
    expected: "docker compose up -d --build keeper brings up 2 healthy replicas (sk_p-keeper-1 and sk_p-keeper-2 both 'healthy'); publishing N messages to keeper-fault-recovery shows the 'Keeper placeholder consumed' log split across both replicas (not duplicated to both per message)"
    why_human: "True cross-replica distribution requires the live Docker compose stack with rebuilt Keeper containers. The hermetic KeeperRoundRobinTests proves the binding SHAPE (count==1) in-process; real cross-replica round-robin is observable only against the running stack per RESEARCH A1/A2 and VALIDATION Manual-Only."
  - test: "Live compose-health-ready (KEEP-03 live half)"
    expected: "docker compose up -d shows keeper replicas 'healthy' alongside orchestrator and processor-sample; rabbitmqctl list_queues shows exactly one durable 'keeper-fault-recovery' queue (NOT GUID-suffixed or auto-delete)"
    why_human: "Docker health probe readiness requires live containers with the embedded Kestrel health listener responding to wget --spider. Not executable in the non-interactive verification environment. The authoritative live gate is Phase 38 close gate."
---

# Phase 34: Keeper Console Foundation — Verification Report

**Phase Goal:** Stand up a runnable, multi-replica `Keeper` console on `BaseConsole.Core` (mirroring `Orchestrator`) that builds, containerizes, and joins the compose stack as a healthy tier with work load-balanced across replicas via a shared competing-consumer endpoint. HOST SHELL ONLY — no fault-recovery logic (Phases 35-38).

**Verified:** 2026-06-05T15:10:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A Keeper console exists on BaseConsole.Core with minimal Program.cs mirroring Orchestrator (Generic-Host, metrics-only OTel, soft-dep Redis, embedded health, MassTransit/RabbitMQ, inherited correlation filters) | ✓ VERIFIED | Program.cs lines 1-31: 5-call seam confirmed; KeeperDependencyFirewallTests anchors on Keeper assembly, ForbiddenPrefixes includes BaseApi.Core/EF/Npgsql/Quartz/Cronos; Keeper.csproj reference closure = BaseConsole.Core + Messaging.Contracts only |
| 2 | Keeper placeholder binds a stable DURABLE shared queue (EndpointName=KeeperQueues.FaultRecovery, plain AddConsumer, NO InstanceId/Temporary) so RabbitMQ round-robins across replicas | ✓ VERIFIED (hermetic) | PlaceholderConsumerDefinition.cs line 22: `EndpointName = KeeperQueues.FaultRecovery`; grep for InstanceId/Temporary in all 3 consumer files = 0 matches; KeeperRoundRobinTests asserts count==1; compose tier has deploy.replicas:2 and no container_name |
| 3 | Keeper builds clean (Release+Debug, 0 warnings) and containerizes via multi-stage Dockerfile | ✓ VERIFIED | Dockerfile: sdk:8.0-bookworm-slim -> aspnet:8.0-bookworm-slim, wget before USER app, EXPOSE 8083, ENTRYPOINT dotnet Keeper.dll; SUMMARY 34-02 records dotnet build Release+Debug 0/0 and docker build succeeded (334MB image); SK_P.sln lists Keeper (GUID x6) |
| 4 | Keeper joins the compose stack as a new tier alongside orchestrator/processor-sample (health probes report ready live) | ✓ VERIFIED (hermetic shape) / ? HUMAN (live probes) | compose.yaml lines 229-252: keeper tier with dockerfile src/Keeper/Dockerfile, deploy.replicas:2, no container_name, no baseapi-service dep, no Orchestrator__InstanceId, no ports:, healthcheck on http://localhost:8083/health/ready; 4 block-scoped ComposeYamlFacts pass (18 total ComposeYamlFacts green per SUMMARY); live health-ready requires operator confirmation |

**Score:** 4/4 truths verified (hermetic codebase proofs complete; live stack halves classified as human_verification per project precedent)

---

## Deferred Items

None. All phase 34 scope is either hermetially verified or classified as human_verification (live operator items). Nothing is addressed in a later phase as a deferred gap.

---

## Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/KeeperQueues.cs` | Stable durable shared queue-name const | ✓ VERIFIED | `public const string FaultRecovery = "keeper-fault-recovery";` — file-scoped namespace Messaging.Contracts, static class only, no message-type declarations |
| `src/Keeper/Keeper.csproj` | OutputType=Exe, leaner reference closure (no BaseApi/Quartz/Cronos/Version=) | ✓ VERIFIED | OutputType=Exe, RootNamespace/AssemblyName=Keeper, NoWarn CS1591, MassTransit+MassTransit.RabbitMQ only, two ProjectReferences; grep for Quartz/Cronos/BaseApi/BaseProcessor/Version=/TargetFramework/Nullable/TreatWarningsAsErrors = 0 matches |
| `SK_P.sln` | Keeper registered in 3 blocks (GUID consistent x6) | ✓ VERIFIED | GUID {D4E5F6A7-8B9C-4D0E-A1F2-3456789ABCDE} appears in Project decl (line 23) + 4 ProjectConfigurationPlatforms (lines 63-66) + NestedProjects under {EAC83310-2BA8-4E7E-9A90-5C6BD471C231} (line 77) = 6 occurrences |
| `src/Keeper/Program.cs` | Thin-shell composition root (5-call seam, no scheduler/L1/metrics, KEEPS SCS) | ✓ VERIFIED | Contains: Host.CreateApplicationBuilder, AddBaseConsoleObservability, AddBaseConsole, Configure<RetryOptions>, AddBaseConsoleMessaging, AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>, await host.RunAsync(); absent: AddQuartz, StartupCompletionService, InstanceId, Temporary, HydrationBackgroundService, IWorkflowL1Store, ConfigureOpenTelemetryMeterProvider |
| `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs` | Stable competing-consumer binding (EndpointName=KeeperQueues.FaultRecovery, Immediate(Limit)) | ✓ VERIFIED | Line 22: `EndpointName = KeeperQueues.FaultRecovery`; line 32: `UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))`; no InstanceId/Temporary |
| `src/Keeper/Consumers/PlaceholderConsumer.cs` | Throwaway no-op IConsumer<KeeperPlaceholder> | ✓ VERIFIED | `sealed class PlaceholderConsumer(ILogger<PlaceholderConsumer> logger) : IConsumer<KeeperPlaceholder>`; log-only body (no Redis/L1/dispatch logic) |
| `src/Keeper/Consumers/KeeperPlaceholder.cs` | Local throwaway message record : ICorrelated | ✓ VERIFIED | `public sealed record KeeperPlaceholder : ICorrelated` with `Guid CorrelationId { get; init; }` |
| `src/Keeper/appsettings.json` | Service.Name=keeper, Port=8083, Retry Limit=3, no InstanceId | ✓ VERIFIED | "Name": "keeper", "Port": 8083, "Retry": {"Limit": 3, "Strategy": "Immediate"}, no InstanceId key |
| `src/Keeper/Dockerfile` | Multi-stage sdk:8.0->aspnet:8.0, wget before USER app, port 8083, Keeper.dll | ✓ VERIFIED | sdk:8.0-bookworm-slim AS build, aspnet:8.0-bookworm-slim AS runtime, apt-get install wget on line 29 before USER app on line 31, ENV ASPNETCORE_URLS=http://+:8083, EXPOSE 8083, ENTRYPOINT dotnet Keeper.dll; 0 matches for 8081/Orchestrator.dll |
| `compose.yaml keeper tier` | replicas:2, no container_name, no baseapi-service dep, 8083 health, src/Keeper/Dockerfile | ✓ VERIFIED | Lines 229-252: dockerfile src/Keeper/Dockerfile, deploy.replicas:2, depends_on rabbitmq+redis only, env block without Orchestrator__InstanceId, healthcheck on 8083, no container_name, no ports: |
| `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` | KEEP-02 round-robin proof count==1 | ✓ VERIFIED | `var consumedCount = consumer.Consumed.Select<KeeperPlaceholder>(ct).Count(); Assert.Equal(1, consumedCount);` — int-local pattern avoids xUnit2013 analyzer; plain AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition> |
| `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` | KEEP-01 boot fixture (subclasses ConsoleTestHostFixture) | ✓ VERIFIED | Subclasses ConsoleTestHostFixture, overrides ConfigureBuilder with the 4-call Keeper seam + RetryOptions binding |
| `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` | KEEP-01 IBusControl resolvable | ✓ VERIFIED | `IClassFixture<KeeperHostBootFixture>`, asserts Host non-null and IBusControl non-null |
| `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | KEEP-01 reference closure guard (incl. Quartz/Cronos) | ✓ VERIFIED | Anchors on `typeof(global::Keeper.Consumers.PlaceholderConsumer).Assembly`; ForbiddenPrefixes = [BaseApi.Core, Microsoft.EntityFrameworkCore, Npgsql, Quartz, Cronos] |
| `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` | 4 keeper facts, block-scoped | ✓ VERIFIED | 4 facts added: Has_Keeper_Service_Block, Keeper_Declares_Two_Replicas, Keeper_Has_No_ContainerName, Keeper_Has_No_BaseApi_Dependency; negatives use tempered-greedy window `(?ms)^  keeper:(?:(?!^  \S).)*?<key>` to prevent cross-tier false-pass |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | ProjectReference to src/Keeper/Keeper.csproj | ✓ VERIFIED | Line 132: `<ProjectReference Include="..\..\src\Keeper\Keeper.csproj" />` |

---

## Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| src/Keeper/Program.cs | PlaceholderConsumer + PlaceholderConsumerDefinition | AddBaseConsoleMessaging plain AddConsumer | ✓ WIRED | Line 28: `x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>()` — no .Endpoint(InstanceId/Temporary) |
| src/Keeper/Consumers/PlaceholderConsumerDefinition.cs | KeeperQueues.FaultRecovery | EndpointName const binding in ctor | ✓ WIRED | Line 22: `EndpointName = KeeperQueues.FaultRecovery` |
| src/Keeper/Consumers/PlaceholderConsumerDefinition.cs | RetryOptions | UseMessageRetry(Immediate(Limit)) | ✓ WIRED | Line 32: `endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` |
| compose.yaml keeper tier | src/Keeper/Dockerfile | build.dockerfile | ✓ WIRED | Line 232: `dockerfile: src/Keeper/Dockerfile` |
| tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs | PlaceholderConsumer + PlaceholderConsumerDefinition + KeeperPlaceholder | AddMassTransitTestHarness plain AddConsumer + Publish | ✓ WIRED | Line 44: `x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>()`; Publish KeeperPlaceholder; GetConsumerHarness<PlaceholderConsumer> |
| tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs | Keeper assembly | typeof(Keeper.Consumers.PlaceholderConsumer).Assembly | ✓ WIRED | Line 18: `typeof(global::Keeper.Consumers.PlaceholderConsumer).Assembly` |
| SK_P.sln | src\Keeper\Keeper.csproj | Project declaration + ProjectConfigurationPlatforms + NestedProjects | ✓ WIRED | GUID {D4E5F6A7-...} appears x6 (1 decl + 4 config + 1 nested under src folder GUID) |

---

## Data-Flow Trace (Level 4)

Not applicable. This phase ships console infrastructure (Generic-Host composition root, queue binding, compose tier) — no components that render dynamic data from a data source. The consumer is intentionally a no-op (topology proof only, D-03); data flow is deferred to Phase 35.

---

## Behavioral Spot-Checks

Step 7b: SKIPPED — the phase produces a runnable container but no entry points testable without the full live compose stack (Docker not available in non-interactive verification environment). The hermetic test suite (454 passed per SUMMARY 34-03) covers all in-process behaviors. Live checks are classified as human_verification items above.

---

## Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KEEP-01 | 34-01, 34-02, 34-03 | Keeper console on BaseConsole.Core with minimal Program.cs mirroring Orchestrator | ✓ SATISFIED | Program.cs 5-call seam verified; KeeperDependencyFirewallTests guards reference closure; KeeperHostBootTests proves bus resolvable; appsettings/Dockerfile correct |
| KEEP-02 | 34-02, 34-03 | Keeper runs multi-replica with shared competing-consumer endpoint (not fan-out) | ✓ SATISFIED (hermetic) + ? HUMAN (live) | PlaceholderConsumerDefinition binds stable EndpointName, no InstanceId/Temporary; compose replicas:2; KeeperRoundRobinTests count==1; live cross-replica distribution = human verification |
| KEEP-03 | 34-03 | Keeper builds, containerizes, joins compose stack as healthy tier | ✓ SATISFIED (hermetic shape) + ? HUMAN (live health) | Dockerfile multi-stage aspnet:8.0 builds (SUMMARY evidence); SK_P.sln registered; ComposeYamlFacts 4 block-scoped assertions green; live docker health-ready = human verification |
| DLQ-04 | NOT claimed by phase 34 | Shared Immediate(N) policy across all consumers | Pattern established (RetryOptions binding + UseMessageRetry in definition) but DLQ-04 checkbox NOT ticked; traceability maps DLQ-04 to Phase 36 | Confirmed: REQUIREMENTS.md traceability table row for DLQ-04 = Phase 36; phase 34 intentionally establishes the pattern without claiming the requirement |

**Note on REQUIREMENTS.md traceability table:** The table row at line 92 shows KEEP-03 as "Not started" — this is a stale entry. The KEEP-03 checkbox at line 19 is correctly marked `[x]`, the compose tier and ComposeYamlFacts exist in the codebase, and the SUMMARY documents completion. The traceability table inconsistency is a documentation-only issue, not a code gap.

---

## Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/Keeper/Consumers/PlaceholderConsumer.cs` | 18 | `return Task.CompletedTask` (no-op body) | ℹ️ Info | By design (D-03) — throwaway topology proof; replaced wholesale in Phase 35. Not a stub in the harmful sense: the queue binding is real, the consumer shape is correct, and the return value is expected behavior for a log-only handler. |

No blockers. No warnings beyond the intentional placeholder design documented in D-03.

Negative grep results confirming firewall integrity:
- Program.cs: 0 matches for AddQuartz / StartupCompletionService / InstanceId / Temporary / HydrationBackgroundService / IWorkflowL1Store / WorkflowScheduler / ConfigureOpenTelemetryMeterProvider / using Quartz
- PlaceholderConsumerDefinition.cs: 0 matches for InstanceId / Temporary
- Keeper.csproj: 0 matches for Quartz / Cronos / BaseApi / BaseProcessor / Version= / TargetFramework / Nullable / TreatWarningsAsErrors
- Dockerfile: 0 matches for 8081 / Orchestrator.dll

---

## Human Verification Required

### 1. Live Multi-Replica Round-Robin Smoke (KEEP-02 live half)

**Test:** Run `docker compose build keeper` then `docker compose up -d --build keeper`. If only one replica appears, fallback to `docker compose up -d --scale keeper=2 keeper`. Then publish 6 `KeeperPlaceholder` messages to the `keeper-fault-recovery` queue (via RabbitMQ management UI or a small publisher) and run `docker compose logs keeper`.

**Expected:** Two replicas appear as healthy (`sk_p-keeper-1` and `sk_p-keeper-2` or `keeper-1`/`keeper-2`, both status `healthy`). The "Keeper placeholder consumed (topology proof only)" log line is split across both replicas (approximately 3 each for N=6 messages), NOT duplicated to both per message (which would indicate fan-out regression).

**Why human:** True cross-replica distribution requires the live Docker compose stack with rebuilt containers. The hermetic KeeperRoundRobinTests proves the binding SHAPE (plain AddConsumer, stable EndpointName, count==1 in-process) but cannot observe actual cross-process message routing. The operator runbook is documented in 34-03-SUMMARY.md Pending-Verification section. Per the project's auto-approve-human-verify precedent (Phases 31/31.1/32.1/33), the authoritative live gate is Phase 38.

### 2. Live Compose-Health-Ready (KEEP-03 live half + durable queue confirmation)

**Test:** Run `docker compose up -d` (full stack) then `docker compose ps` and `docker compose exec rabbitmq rabbitmqctl list_queues name | grep keeper-fault-recovery`.

**Expected:** The keeper replicas show `healthy` status alongside orchestrator and processor-sample. The rabbitmqctl output shows exactly one queue named `keeper-fault-recovery` — NOT a GUID-suffixed or auto-delete name (confirming durable shared queue, net-zero SHA readiness for Phase 38).

**Why human:** Docker health probe readiness (wget --spider against the embedded Kestrel health listener) and RabbitMQ queue inspection require the running compose stack. The ComposeYamlFacts hermetic assertions prove the compose SHAPE; live container health is an operator observation. The compose config parses cleanly (`docker compose config --quiet` EXIT 0 per SUMMARY evidence), but container liveness requires running Docker.

---

## Gaps Summary

No gaps found. All must-haves are satisfied in the codebase:

- KEEP-01: Verified at all four levels (exists, substantive, wired, and hermetic test coverage).
- KEEP-02: Hermetic binding-shape proof verified (count==1, no fan-out shape, stable EndpointName, deploy.replicas:2). Live cross-replica distribution classified as human_verification per project precedent — not a gap.
- KEEP-03: Build artifacts verified (Dockerfile correct, SK_P.sln registered, ComposeYamlFacts passing). Live compose health classified as human_verification — not a gap.
- DLQ-04: Correctly NOT claimed by phase 34; pattern established (RetryOptions + UseMessageRetry) and maps to Phase 36.

The REQUIREMENTS.md traceability table row for KEEP-03 reads "Not started" while the checkbox is `[x]` — stale documentation inconsistency only, not a code gap.

---

_Verified: 2026-06-05T15:10:00Z_
_Verifier: Claude (gsd-verifier)_
