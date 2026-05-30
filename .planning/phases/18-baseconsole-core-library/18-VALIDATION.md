---
phase: 18
slug: baseconsole-core-library
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-05-30
updated: 2026-05-30
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

> Maps the D-02 proof points to requirements and automated commands. All rows green post-execution; full suite 249/249 GREEN ×3 (245 runtime + 4 dependency-firewall).

| Requirement | Behavior | Test Type | Mechanism | Test File / Method | Status |
|-------------|----------|-----------|-----------|--------------------|--------|
| CONSOLE-01, CONSOLE-04 | Host boots through full `AddBaseConsole*` chain | integration | `ConsoleTestHostFixture` builds `Host.CreateApplicationBuilder` + all three Add calls + `Build()`; assert no throw and `IBus` resolvable | `ConsoleHostBootTests.Host_Boots_With_Dead_Deps_And_Bus_Is_Resolvable` | ✅ |
| CONSOLE-HEALTH-01, CONSOLE-HEALTH-02 | `/health/live` = 200 with Redis + RabbitMQ ports dead | integration | Fixture with dead Redis/RabbitMQ conn strings; HTTP GET inner listener `/health/live`; assert 200 + `"status":"Healthy"` | `ConsoleHealthLiveTests.Live_Returns_200_When_Redis_And_RabbitMQ_Dead` | ✅ |
| CONSOLE-HEALTH-02 (no-secrets body) | `/health/live` body leaks no secrets/stack traces | integration | HTTP GET body; assert no `Password=`/`abortConnect`/stack-trace markers | `ConsoleHealthLiveTests.Live_Body_Has_No_Secrets` | ✅ |
| CONSOLE-HEALTH-03 | `/health/ready` Unhealthy while broker unreachable (positively proven) | unit | `BusReadyHealthCheck` over an empty provider (null `IBusControl`) → assert `HealthStatus.Unhealthy` | `ConsoleBusReadyHealthCheckTests.BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` | ✅ |
| CONSOLE-HEALTH-04 | `/health/startup` flips Healthy after host init | integration | Boot fixture; `/health/startup` = 200 after `StartAsync`; negative variant removing `StartupCompletionService` → 503 | `ConsoleStartupGateTests.Startup_Returns_200_After_Host_Init` + `Startup_Returns_503_When_CompletionService_Removed` | ✅ |
| CONSOLE-02 | No `TracerProvider` resolvable from console container | unit/container | `provider.GetService<TracerProvider>()` is null | `ConsoleObservabilityTests.No_TracerProvider_Resolvable` | ✅ |
| CONSOLE-02 | MassTransit meter registered; AspNetCore/HttpClient instrumentation absent | unit/container | Assert `MeterProvider` resolvable; assert no AspNetCore/HttpClient instrumentation registered | `ConsoleObservabilityTests.MeterProvider_Resolvable` + `No_AspNetCore_Or_HttpClient_Instrumentation` | ✅ |
| CORR-01, CORR-02 | Both correlation filters registered + behavior exercised | unit (harness) | `AddMassTransitTestHarness` + probe consumer; ambient accessor set → assert inbound scope/accessor populated and outbound envelope `CorrelationId` stamped | `ConsoleCorrelationFilterTests.Inbound_Filter_Populates_Accessor_And_Outbound_Stamps_Envelope` | ✅ |
| CONSOLE-03 | Singleton soft-dep Redis client (`abortConnect=false`) registered, no startup probe | unit/container | Boot succeeds with dead Redis port (no startup probe forces Redis) | `ConsoleHostBootTests.Host_Boots_With_Dead_Deps_And_Bus_Is_Resolvable` | ✅ |
| CONSOLE-05 | `FrameworkReference Microsoft.AspNetCore.App`, library not Web SDK; no `BaseConsole.Core → BaseApi.Core` dep | build/static + reflection | csproj uses `Sdk=Microsoft.NET.Sdk` + `<FrameworkReference>`; **reflection test asserts compiled `BaseConsole.Core` references neither `BaseApi.Core`, `Microsoft.EntityFrameworkCore*`, nor `Npgsql*`** (regression guard added by validate-phase) | `ConsoleDependencyFirewallTests.*` (4 methods) | ✅ |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs` — the in-memory Generic-Host fixture (the D-02 validation vehicle)
- [x] `tests/BaseApi.Tests/BaseApi.Tests.csproj` — added `<ProjectReference Include="..\..\src\BaseConsole.Core\BaseConsole.Core.csproj" />` (Plan 18-04)
- [x] Dead-dependency fixture variants (dead Redis port; dead/absent RabbitMQ) — `ConsoleHostBootTests` + `ConsoleHealthLiveTests`
- [x] Harness-based correlation test using `AddMassTransitTestHarness` — `ConsoleCorrelationFilterTests`
- [x] CONSOLE-05 dependency-firewall regression test — `ConsoleDependencyFirewallTests` (added by validate-phase gap fill, commit `50cec8e`)
- No new test-framework install needed (xunit.v3 + harness already available)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | All Phase 18 behaviors have automated verification via the in-memory `ConsoleTestHostFixture` + `AddMassTransitTestHarness` (D-02). No real broker / concrete host ships this phase. |

*All phase behaviors have automated verification.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 30s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated — all 11 requirements have automated verification (full suite 249/249 GREEN ×3).

---

## Validation Audit 2026-05-30
| Metric | Count |
|--------|-------|
| Gaps found | 1 |
| Resolved | 1 |
| Escalated | 0 |

CONSOLE-05 had no automated regression guard for the D-08 dependency firewall (verified only by build + one-time grep). Filled via `ConsoleDependencyFirewallTests.cs` (4 reflection methods asserting compiled `BaseConsole.Core` references neither `BaseApi.Core`, `Microsoft.EntityFrameworkCore*`, nor `Npgsql*`), commit `50cec8e`. Suite now 249/249 GREEN. Phase is **nyquist-compliant**.
