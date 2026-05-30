---
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
plan: 03
subsystem: messaging-webapi
tags: [masstransit, rabbitmq, publish, correlation, orchestration, health, contracts]
requires:
  - phase: 19-01
    provides: "StartOrchestration/StopOrchestration : ICorrelated with a body-carried Guid CorrelationId"
  - phase: 19-02
    provides: "Orchestrator console consuming Start/Stop (the publish target on the other end of the fan-out)"
  - phase: 17
    provides: "Messaging.Contracts leaf library + MassTransit 8.5.5 CPM pin"
provides:
  - "AddBaseApiMessaging in BaseApi.Core — publish-only AddMassTransit + UsingRabbitMq (no consumers/endpoints/filters), bus health capped at Degraded"
  - "OrchestrationService publishes StartOrchestration{ids} after the L2-write loop and StopOrchestration{ids} after the Stop gate+cleanup, each with a freshly-minted NewId body CorrelationId"
  - "BaseApi.Service RabbitMq appsettings section (rabbitmq/guest/guest)"
  - "OrchestrationServicePublishTests — in-memory harness publish assertions for Start/Stop + the MSG-WEBAPI-03 service-boundary failure path"
  - "Dependency firewall held: BaseApi.* references Messaging.Contracts only, never BaseConsole.Core"
affects:
  - "Plan 19-04 (infra) adds the RabbitMQ compose tier + must bring the broker up for the orchestration HTTP integration facts (now broker-dependent)"
  - "Phase 20 — real-broker fan-out + correlation proof + the broker-down HTTP contract test (TEST-RMQ-03) replace the in-memory harness assertions"
tech-stack:
  added:
    - "MassTransit 8.5.5 + MassTransit.RabbitMQ PackageReferences on BaseApi.Core (CPM pins from Phase 17)"
    - "Microsoft.EntityFrameworkCore.InMemory 8.0.27 (test-only) for the publish-test existence-check seed"
  patterns:
    - "Publish-only bus join: AddMassTransit + UsingRabbitMq with NO ConfigureEndpoints / NO consumers / NO correlation filters (WebApi is a pure publisher)"
    - "Body-only correlation at the publish boundary: { CorrelationId = NewId.NextGuid() } on the message body; no explicit envelope stamp (single source of truth)"
    - "Bus health capped at MinimalFailureStatus = Degraded so a broker-down condition never flips CRUD /health/ready to 503; o.Tags left at defaults (Pitfall 7)"
    - "[InternalsVisibleTo(\"DynamicProxyGenAssembly2\")] to let NSubstitute/Castle proxy internal seam interfaces in unit tests"
key-files:
  created:
    - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs
  modified:
    - src/BaseApi.Core/BaseApi.Core.csproj
    - src/BaseApi.Service/Program.cs
    - src/BaseApi.Service/appsettings.json
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
    - src/BaseApi.Service/Properties/AssemblyInfo.cs
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
    - Directory.Packages.props
key-decisions:
  - "Drove the REAL OrchestrationService in the publish test (not a thin seam): EF-InMemory seeds the existence check, NSubstitute stubs the internal L2 seams, real concrete validators pass on an empty snapshot"
  - "Body-vs-envelope assertion corrected to envelope == body (MassTransit by-convention populates the envelope from the CorrelationId property — the 19-01 masking effect), not envelope == null"
  - "EF-InMemory chosen over a real Postgres for the publish test because it ignores the relational xmin/xid + snake_case annotations on BaseDbContext (model builds cleanly)"
patterns-established:
  - "Publish-only AddBaseApiMessaging mirrors AddBaseConsoleMessaging's config-read skeleton but DROPS every consume/endpoint/filter line"
  - "Mocking internal interfaces requires InternalsVisibleTo(DynamicProxyGenAssembly2) in the DEFINING assembly"
requirements-completed: [MSG-WEBAPI-01, MSG-WEBAPI-02, MSG-WEBAPI-03, MSG-WEBAPI-04]
duration: 23min
completed: 2026-05-30
---

# Phase 19 Plan 03: WebApi Publish-Only Bus Join + Start/Stop Publish Summary

**The WebApi joins the MassTransit/RabbitMQ bus as a publish-only participant (`AddBaseApiMessaging` — no consumers, no endpoints, no filters, bus health capped at Degraded) and `OrchestrationService` broadcasts `StartOrchestration`/`StopOrchestration` after their L2 stage with a freshly-minted `NewId` body CorrelationId.**

## Performance

- **Duration:** 23 min
- **Started:** 2026-05-30T13:15:35Z
- **Completed:** 2026-05-30T13:39:12Z
- **Tasks:** 3
- **Files modified:** 9 (2 created, 7 modified)

## Accomplishments

- `AddBaseApiMessaging(cfg)` in `BaseApi.Core` — `AddMassTransit` + `UsingRabbitMq` with NO `ConfigureEndpoints`, NO `AddConsumer`, NO correlation filters (publish-only, D-02), and `MinimalFailureStatus = HealthStatus.Degraded` so a broker-down condition keeps CRUD `/health/ready` at 200 (MSG-WEBAPI-04). `BaseApi.Core` references `Messaging.Contracts` only — NEVER `BaseConsole.Core` (dependency firewall, MSG-WEBAPI-01).
- `Program.cs` chains `AddBaseApiMessaging(builder.Configuration)` after `AddBaseApi<AppDbContext>` and before `Build()`.
- `OrchestrationService` publishes `StartOrchestration(workflowIds.ToArray()) { CorrelationId = NewId.NextGuid() }` after the per-workflow L2-write loop, and `StopOrchestration{...}` after the Stop gate+cleanup — body-only correlation, envelope id never explicitly stamped (T-19-envelope-leak). The HTTP-stage `correlationId` string is left untouched (per-stage handoff, D-01).
- 4 in-memory harness tests GREEN (no real broker): Start/Stop publish observed with the input WorkflowIds + a non-empty `NewId` body CorrelationId; envelope id equals body id (single source of truth); a faulting `IPublishEndpoint` propagates out of `StartAsync` (broker hard dep, MSG-WEBAPI-03); `FallbackExceptionHandler` maps an unhandled exception to 500 + ProblemDetails.

## Task Commits

1. **Task 1: AddBaseApiMessaging + Program chain + RabbitMq config** — `8be31be` (feat)
2. **Task 2: Publish Start/Stop from OrchestrationService (body CorrelationId, NewId)** — `f221b11` (feat)
3. **Task 3: In-memory harness publish tests** — `18d0fb4` (test) — _test-after harness; production code (Tasks 1-2) precedes the test in this plan's task ordering, mirroring 19-02._

**Plan metadata:** _(final docs commit — this SUMMARY + STATE + ROADMAP + REQUIREMENTS)_

## Files Created/Modified

- `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — **created**: publish-only `AddBaseApiMessaging`, bus health Degraded.
- `src/BaseApi.Core/BaseApi.Core.csproj` — MassTransit + MassTransit.RabbitMQ pkg refs + Messaging.Contracts proj ref (no BaseConsole.Core).
- `src/BaseApi.Service/Program.cs` — chained `AddBaseApiMessaging` after `AddBaseApi<AppDbContext>`.
- `src/BaseApi.Service/appsettings.json` — `RabbitMq` section (rabbitmq/guest/guest).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — `IPublishEndpoint` field/ctor-guard + the two `Publish` call sites.
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` — DI factory passes `IPublishEndpoint`.
- `src/BaseApi.Service/Properties/AssemblyInfo.cs` — `InternalsVisibleTo("DynamicProxyGenAssembly2")` (test-mocking of internal seams).
- `tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs` — **created**: 4 harness publish facts.
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` + `Directory.Packages.props` — EF-InMemory test-only provider pin.

## Decisions Made

- **Drive the real `OrchestrationService`** in the publish test (the plan's preferred option over a thin seam): seed the existence check with EF-InMemory, stub the internal L2 seams (`IWorkflowGraphLoader`/`IRedisProjectionWriter`/`IRedisL2Cleanup`) via NSubstitute, and use the real concrete validators (which pass on the empty snapshot the stub loader returns). This proves the ACTUAL publish call site.
- **Envelope assertion = body (not null).** MassTransit auto-populates the envelope `CorrelationId` BY CONVENTION from the message's `CorrelationId` property — the same masking effect documented in 19-01. Our code sets only the body; the truthful harness assertion is that the envelope id, when present, EQUALS the body id (single source of truth, no divergent stamp), not that it is unset.
- **EF-InMemory over real Postgres** for the publish test: `BaseDbContext` carries a Postgres-only `xmin`/`xid` shadow concurrency token + snake_case naming; EF-InMemory ignores those relational annotations so the model builds cleanly without a broker or a real DB.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` to BaseApi.Service**
- **Found during:** Task 3 (harness test could not stub the internal seam interfaces).
- **Issue:** NSubstitute uses Castle DynamicProxy, which emits proxies into the dynamic assembly `DynamicProxyGenAssembly2`. Proxying the INTERNAL `IWorkflowGraphLoader`/`IRedisProjectionWriter`/`IRedisL2Cleanup` failed with `ArgumentException: Can not create proxy ... it is not accessible` until the defining assembly exposed its internals to that proxy assembly.
- **Fix:** Added the IVT attribute alongside the existing `InternalsVisibleTo("BaseApi.Tests")` in `AssemblyInfo.cs`. This is the canonical mechanism for mocking internal types; test-infra only.
- **Files modified:** src/BaseApi.Service/Properties/AssemblyInfo.cs
- **Verification:** Publish tests 4/4 GREEN after the change.
- **Committed in:** `18d0fb4` (Task 3 commit)

**2. [Rule 3 - Blocking] Added the EF Core InMemory provider (test-only CPM pin + reference)**
- **Found during:** Task 3 (the real `OrchestrationService.StartAsync` existence check queries `_db.Set<WorkflowEntity>()`).
- **Issue:** Driving the real existence check needs a `BaseDbContext` seeded with the workflow rows; a real Postgres is too heavy for a harness unit test and a plain `List.AsQueryable()` cannot satisfy EF's async `ToListAsync`.
- **Fix:** Pinned `Microsoft.EntityFrameworkCore.InMemory 8.0.27` in `Directory.Packages.props` and referenced it from the test csproj. The provider ignores relational annotations (xmin/xid, snake_case) so the `BaseDbContext` model builds. Test-only — no production surface.
- **Files modified:** Directory.Packages.props, tests/BaseApi.Tests/BaseApi.Tests.csproj
- **Verification:** Publish tests 4/4 GREEN; full Release build 0 warnings.
- **Committed in:** `18d0fb4` (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking, both test-infra only).
**Impact on plan:** No production-surface change beyond the planned files; both fixes were required to drive the real service in the harness as the plan preferred. No scope creep.

## Issues Encountered

**`dotnet test --filter` runs the WHOLE suite under Microsoft.Testing.Platform.** MTP emits `MTP0001: VSTest-specific properties ... will be ignored` — the `--filter` (VSTestTestCaseFilter) is dropped, so the first full run executed all 260 tests (240 passed, 20 failed) and took 11 min. The plan's verification command (`dotnet test ... --filter "FullyQualifiedName~OrchestrationServicePublish"`) therefore does not scope as written; the working MTP equivalent is `dotnet run --no-build -- --filter-class "*OrchestrationServicePublishTests*"`, which runs exactly the 4 new tests (4/4 GREEN, Debug and Release).

**The 20 full-suite failures are the INTENDED MSG-WEBAPI-03 behavior change, not a regression.** After Task 2, the orchestration HTTP happy-path integration facts publish to RabbitMQ; with no broker in this environment (RabbitMQ arrives in Plan 19-04, not this plan) the publish throws `BrokerUnreachable` → `FallbackExceptionHandler` → HTTP 500, so facts asserting a 204 now fail. This is exactly the contract this plan introduces (broker is a hard dep for Start/Stop). `HealthEndpointsTests` passes 9/9 with the broker down (bus health capped Degraded, MSG-WEBAPI-04 verified live). Bringing the broker up for the integration suite + updating those happy-path facts is **Plan 19-04 / Phase 20** work (TEST-RMQ-01..05; the real-broker-down HTTP test is TEST-RMQ-03). Logged in `deferred-items.md`.

## Threat Surface

Per the plan's threat register — all four `mitigate` dispositions honored:
- **T-19-dep-firewall** — `BaseApi.Core.csproj` references `Messaging.Contracts` only; grep confirms NO `BaseConsole.Core` ProjectReference (the only matches are explanatory comments).
- **T-19-broker-down** — `MinimalFailureStatus = HealthStatus.Degraded` caps the auto-registered bus check; `o.Tags` left at defaults (Pitfall 7). Verified: `HealthEndpointsTests` 9/9 GREEN with the broker unreachable. Start/Stop themselves surface 5xx + RFC 7807 via `FallbackExceptionHandler` (Task 3 failure-path test).
- **T-19-broker-creds** — dev-only `guest/guest` in appsettings; compose/k8s env override (`RabbitMq__*`) is the production path (accept disposition documented).
- **T-19-envelope-leak** — correlation set on the message body only (`{ CorrelationId = NewId.NextGuid() }`); no explicit envelope stamp; HTTP-stage id not carried onto the bus. The harness test asserts envelope id == body id (single source of truth).

No new threat surface beyond the plan's register.

## Verification Evidence

| Check | Result |
|-------|--------|
| `dotnet build src/BaseApi.Service ... -c Release --nologo` | 0 Warning / 0 Error |
| `dotnet build SK_P.sln -c Release --nologo` | 0 Warning / 0 Error |
| `OrchestrationServicePublishTests` (filter-class, Debug + Release) | 4/4 Passed |
| `HealthEndpointsTests` with broker down | 9/9 Passed (bus health Degraded → /health/ready 200) |
| Grep: BaseApi.Core.csproj BaseConsole.Core ProjectReference | none (comment-only) |
| Grep: MessagingExt ConfigureEndpoints/AddConsumer/UseConsumeFilter/o.Tags (code) | none (comment-only) |
| Grep: OrchestrationService `NewId.NextGuid` on Start + Stop; no `context.CorrelationId =`/`SetCorrelationId` | confirmed |
| File deletions in the 3 task commits | none |

## TDD Gate Compliance

Task 3 is marked `tdd="true"`, but the plan structures it as a **test-after harness** (the production publish call sites land in Task 2, the test in Task 3) — identical to 19-02's structure. There is therefore a single `test(19-03): …` commit (`18d0fb4`) with no preceding net-new-behavior RED commit; the 4 facts pass GREEN against the committed implementation. This is a structural consequence of the plan's task split, not a skipped gate.

## Next Phase Readiness

- **Publisher side of the fan-out complete.** The WebApi mints the bus-stage CorrelationId at the publish boundary and broadcasts Start/Stop; the Orchestrator console (19-02) consumes them.
- **Plan 19-04 must:** add the RabbitMQ compose tier (INFRA-RMQ-02/03) and bring the broker up for the orchestration HTTP integration facts (now broker-dependent — see `deferred-items.md`), or update those happy-path facts.
- **Phase 20 replaces** the in-memory harness assertions with the real-broker two-bus fan-out + correlation proof and the broker-down HTTP contract test (TEST-RMQ-01..05).

---
*Phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier*
*Completed: 2026-05-30*

## Self-Check: PASSED

All created/modified files present on disk; all 3 task commits (`8be31be`, `f221b11`, `18d0fb4`) present in git history.
