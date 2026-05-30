---
phase: 18
slug: baseconsole-core-library
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-05-30
---

# Phase 18 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 (`Directory.Packages.props:110`), NSubstitute 5.3.0 |
| **Config file** | none — xunit.v3 auto-discovers; `[Collection]` attributes serialize shared-resource tests |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter FullyQualifiedName~Console` |
| **Full suite command** | `dotnet test` (solution root) |
| **Estimated runtime** | ~30s quick subset; full suite per existing baseline |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter FullyQualifiedName~Console` (fast subset, < 30s)
- **After every plan wave:** Run `dotnet test` (full suite)
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** ~30 seconds (quick subset)
- **Phase close gate:** Full suite GREEN 3-consecutive + **dual-SHA** (`psql \l` + `redis-cli --scan`) BEFORE=AFTER (D-03 — NOT triple-SHA; no real broker this phase)

---

## Per-Task Verification Map

> Plan/wave/task IDs are finalized by the planner; this maps the six D-02 proof points to requirements and automated commands. Each row's test file is a Wave 0 stub (❌) until created.

| Requirement | Behavior | Test Type | Mechanism | Test File | File Exists |
|-------------|----------|-----------|-----------|-----------|-------------|
| CONSOLE-01, CONSOLE-04 | Host boots through full `AddBaseConsole*` chain | integration | `ConsoleTestHostFixture` builds `Host.CreateApplicationBuilder` + all three Add calls + `Build()`; assert no throw and `IBus` resolvable | `ConsoleTestHostFixture.cs` | ❌ W0 |
| CONSOLE-HEALTH-01, CONSOLE-HEALTH-02 | `/health/live` = 200 with Redis + RabbitMQ ports dead | integration | Fixture with dead Redis/RabbitMQ conn strings; HTTP GET inner listener `/health/live`; assert 200 + `"status":"Healthy"` (mirror `HealthEndpointsTests` dead-port pattern) | `ConsoleHealthLiveTests.cs` | ❌ W0 |
| CONSOLE-HEALTH-03 | `/health/ready` Unhealthy while broker unreachable (POSITIVELY proven, not optional) | unit | `BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy`: construct `BusReadyHealthCheck` with an outer provider whose `IBusHealth` is null (or an NSubstitute returning a non-Healthy `BusHealthResult`); `CheckHealthAsync` → assert `HealthStatus.Unhealthy`. No real broker needed | `ConsoleBusReadyHealthCheckTests.cs` | ❌ W0 |
| CONSOLE-HEALTH-04 | `/health/startup` flips Healthy after host init | integration | Boot fixture; assert `/health/startup` = 200 after `StartAsync`; negative variant removing `StartupCompletionService` → 503 (mirror `HealthNoStartupCompletionFixture`) | `ConsoleStartupGateTests.cs` | ❌ W0 |
| CONSOLE-02 | No `TracerProvider` resolvable from console container | unit/container | `provider.GetService<TracerProvider>()` is null in the console container (console analog of deleted `TraceExportTests`) | `ConsoleObservabilityTests.cs` | ❌ W0 |
| CONSOLE-02 | MassTransit meter registered; AspNetCore/HttpClient instrumentation absent | unit/container | Assert `MeterProvider` resolvable + `InstrumentationOptions.MeterName` meter present; assert no AspNetCore/HttpClient instrumentation services registered | `ConsoleObservabilityTests.cs` | ❌ W0 |
| CORR-01, CORR-02 | Both correlation filters registered + behavior exercised | unit (harness) | `AddMassTransitTestHarness` with the outbound send/publish filters + a probe consumer; publish an `ICorrelated` test message with ambient accessor set → assert inbound scope/accessor populated and outbound `SendContext.CorrelationId` stamped | `ConsoleCorrelationFilterTests.cs` | ❌ W0 |
| CONSOLE-03 | Singleton soft-dep Redis client (`abortConnect=false`) registered, no startup probe | unit/container | Assert single `IConnectionMultiplexer` registration; boot succeeds with dead Redis port | covered by `ConsoleTestHostFixture.cs` | ❌ W0 |
| CONSOLE-05 | `FrameworkReference Microsoft.AspNetCore.App`, library not Web SDK; no `BaseConsole.Core → BaseApi.Core` dep | build/static | `BaseConsole.Core.csproj` uses `Sdk=Microsoft.NET.Sdk` + `<FrameworkReference>`; grep asserts no `BaseApi.Core` ProjectReference; `dotnet build` succeeds | build assertion | ❌ W0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/.../ConsoleTestHostFixture.cs` — the in-memory Generic-Host fixture (the D-02 validation vehicle)
- [ ] `tests/BaseApi.Tests/BaseApi.Tests.csproj` — add `<ProjectReference Include="..\..\src\BaseConsole.Core\BaseConsole.Core.csproj" />`
- [ ] Dead-dependency fixture variants (dead Redis port; dead/absent RabbitMQ) — mirror `HealthDeadRedisFixture` / dead-port patterns from `HealthEndpointsTests.cs`
- [ ] Harness-based correlation test using `AddMassTransitTestHarness` (no separate NuGet — ships in core MassTransit 8.5.5)
- No new test-framework install needed (xunit.v3 + harness already available)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All Phase 18 behaviors have automated verification via the in-memory `ConsoleTestHostFixture` + `AddMassTransitTestHarness` (D-02). No real broker / concrete host ships this phase. |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
