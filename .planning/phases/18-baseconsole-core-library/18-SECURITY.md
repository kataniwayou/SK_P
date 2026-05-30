---
phase: 18
slug: baseconsole-core-library
status: secured
threats_open: 0
threats_total: 11
threats_closed: 11
asvs_level: 1
created: 2026-05-30
---

# Phase 18 — BaseConsole.Core Library: Security Audit

**Audit date:** 2026-05-30
**ASVS Level:** L1
**Auditor:** gsd-secure-phase (automated, gsd-security-auditor)
**Outcome:** SECURED — all 11 threats closed (8 mitigate verified, 3 accept confirmed)

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| appsettings → composition root | Connection strings (Redis) and RabbitMQ host/credentials read from config; never logged. | Secrets (conn strings, credentials) |
| inbound message → console | MassTransit envelope `ConsumeContext.CorrelationId` is untrusted external text. | Correlation id (opaque string) |
| ambient correlation → outbound envelope | AsyncLocal id stamped onto outgoing `ICorrelated` envelopes; only Guid-parseable ids reach the wire. | Correlation id (Guid) |
| K8s/operator → embedded health port | Unauthenticated HTTP probes on `ConsoleHealth:Port` (default 8081). | Health status only |
| inner Kestrel DI ↔ outer host DI | Two DI containers; only the startup gate + a bus-health read cross the boundary. | Health state |

---

## Threat Register

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-18-01 | Information Disclosure | mitigate | CLOSED | `ConsoleRedisServiceCollectionExtensions.cs:31–32` — `RequireConnectionString("Redis")` reads the conn string into the multiplexer factory closure only. `RequiredConfig.cs:26–28` — the `InvalidOperationException` template embeds only the key name (`ConnectionStrings:{name}`), never the value. No log call present anywhere in the method. |
| T-18-02 | EoP/Tampering | mitigate | CLOSED | `BaseConsole.Core.csproj` — zero `<ProjectReference>` entries to `BaseApi.Core`; one `ProjectReference` to `Messaging.Contracts.csproj` only. No `EntityFrameworkCore`, `Npgsql`, `FluentValidation`, `Swashbuckle`, or `Asp.Versioning` `PackageReference`. The single grep hit for "Npgsql" in the csproj is inside an XML comment ("no Npgsql"), not a reference. |
| T-18-03 | DoS | accept | CLOSED | No `AddCheck`/`AddHealthCheck` call referencing `IConnectionMultiplexer` or Redis exists in any `.cs` file under `src/BaseConsole.Core/`. `ConsoleRedisServiceCollectionExtensions.cs` registers only `AddSingleton<IConnectionMultiplexer>` (lazy factory). Fixture (`ConsoleTestHostFixture.cs:50`) uses `127.0.0.1:6399,abortConnect=false` — host starts without throw. Acceptance rationale is uncontradicted by code. |
| T-18-04 | Tampering/Repudiation | mitigate | CLOSED | `InboundCorrelationConsumeFilter.cs:36` — inbound id placed as scope VALUE under `CorrelationKeys.LogScope` key via `Dictionary<string, object>`, never interpolated into a format string or log message. `InboundCorrelationConsumeFilter.cs:34` — `Guid.NewGuid().ToString()` fallback when `context.CorrelationId` is null. |
| T-18-05 | Information Disclosure | mitigate | CLOSED | `MessagingServiceCollectionExtensions.cs:29–31` — `cfg.Require("RabbitMq:Host/Username/Password")` reads each value into local variables passed to `c.Host(...)` only. `RequiredConfig.cs:21–23` — the exception template embeds only the key name (`Required configuration key '{key}'`), never the value. No log call present. |
| T-18-06 | Spoofing/EoP | accept | CLOSED | Accepted: Compose-internal dev only; Phase 19+ deploy adds real creds and network policy. No code contradicts this disposition — the credentials are read from config rather than hardcoded, and no network-policy enforcement is expected from this library layer. |
| T-18-07 | Information Disclosure | mitigate | CLOSED | `BaseConsoleObservabilityExtensions.cs` — `WithMetrics(...)` only; no `.WithTracing`, no `AddSource(...)`, no `TracerProvider` reference anywhere in `src/BaseConsole.Core/` (grep returns 0 matches). Asserted by `ConsoleObservabilityTests.No_TracerProvider_Resolvable` (line 29: `Assert.Null(_fixture.Host.Services.GetService<TracerProvider>())`). |
| T-18-08 | Information Disclosure | mitigate | CLOSED | `EmbeddedHealthEndpointService.cs:82–96` — all three `MapHealthChecks` calls use `ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse`. Check result messages in `StartupHealthCheck` and `BusReadyHealthCheck` contain only status text and bus description, no connection strings. Asserted by `ConsoleHealthLiveTests.Live_Body_Has_No_Secrets` (lines 44–48: `DoesNotContain("Password=")`, `DoesNotContain("abortConnect")`, `DoesNotContain("   at ")`). |
| T-18-09 | DoS | mitigate | CLOSED | `EmbeddedHealthEndpointService.cs:82–86` — `/health/live` `Predicate = c => c.Tags.Contains("live")`; the only check tagged `"live"` is the always-`Healthy` `"self"` inline check (`AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })`). No Redis or bus check carries the `"live"` tag. Asserted by `ConsoleHealthLiveTests.Live_Returns_200_When_Redis_And_RabbitMQ_Dead`. |
| T-18-10 | Spoofing/EoP | accept | CLOSED | Accepted: probes intentionally unauthenticated (ASVS V4 N/A for liveness/readiness). `EmbeddedHealthEndpointService.cs` registers no auth middleware; the embedded `WebApplication` has no `UseAuthentication`/`UseAuthorization`. Network exposure is a Phase 19+ concern. Acceptance rationale is uncontradicted. |
| T-18-11 | Tampering | mitigate | CLOSED | `BusReadyHealthCheck.cs:53–57` — if `_outer.GetService<IBusControl>()` returns null, returns `HealthCheckResult.Unhealthy("Bus not started")` immediately. Lines 59–65 — `BusHealthStatus.Degraded` and `BusHealthStatus.Unhealthy` both map to `HealthCheckResult.Unhealthy(...)`. NOTE: Plan said `IBusHealth.CheckHealth()` but MassTransit 8.5.5 has no public `IBusHealth`; implementation correctly uses `IBusControl.CheckHealth()` — this API-name correction is expected and acceptable per the threat register annotation. Asserted by `ConsoleBusReadyHealthCheckTests.BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` (lines 31–42). |

---

## Accepted Risks Log

| Threat ID | Risk | Rationale | Condition for Re-evaluation |
|-----------|------|-----------|------------------------------|
| T-18-03 | Redis DoS at boot | `abortConnect=false` in caller's conn string; lazy connect; no startup probe. Dead Redis non-fatal. | If a Redis health check or startup probe is ever added to BaseConsole.Core, re-evaluate. |
| T-18-06 | RabbitMQ guest/guest | Compose-internal dev-only; real creds + network isolation are Phase 19+ deploy concerns. | Phase 19 deploy configuration. |
| T-18-10 | Unauthenticated health probes | Probes expose status only (no diagnostics). ASVS V4 N/A for liveness/readiness. Network-level exposure is Phase 19+ concern. | If probe bodies expose sensitive data, re-evaluate. |

---

## Unregistered Threat Flags

None. No threat flags in any SUMMARY.md `## Threat Flags` section were unregistered.

---

## Audit Notes

- **T-18-11 API correction:** `BusReadyHealthCheck` uses `IBusControl.CheckHealth()` (not the plan-text `IBusHealth.CheckHealth()`) because MassTransit 8.5.5 exposes no public `IBusHealth` interface. This is documented in the threat register annotation ("NOTE: plan text says IBusHealth...implementation correctly uses IBusControl.CheckHealth()") and confirmed by the 18-03-SUMMARY. The corrected implementation provides identical semantics and the test proves the unhealthy path.
- **Inbound filter open-generic fix:** `InboundCorrelationConsumeFilter` was changed from non-generic to `InboundCorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>>` during Plan 04 (commit `af953ea`) because MassTransit 8.5.5 requires a generic type definition for scoped bus-wide registration. This does not affect T-18-04: the scope key is still `CorrelationKeys.LogScope` and the value is still treated as opaque text.

---

## Audit Trail

### Security Audit 2026-05-30
| Metric | Count |
|--------|-------|
| Threats found | 11 |
| Closed | 11 |
| Open | 0 |

Verified via gsd-security-auditor (ASVS L1, block_on=high). 8 mitigate threats verified against implemented code + tests; 3 accept threats confirmed uncontradicted by code. Outcome: **SECURED**.
