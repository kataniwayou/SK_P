---
phase: 25
slug: shared-contracts-webapi-responders
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-01
---

# Phase 25 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 + xunit.v3.assert (VERIFIED Directory.Packages.props:121-123) |
| **Bus harness** | MassTransit.Testing `AddMassTransitTestHarness` + `UsingInMemory` (VERIFIED ResultConsumeTests.cs) |
| **Config file** | none — `[Collection]`/`[Trait]` attributes; xunit auto-discovery |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~<TestClass>"` |
| **Full suite command** | `dotnet test` (solution root) |
| **Estimated runtime** | ~60-120 seconds (in-memory harness; no real broker) |

---

## Sampling Rate

- **After every task commit:** Run the matching `--filter` quick run (e.g. `L2ProjectionKeysTests`, `ProcessorResponderTests`).
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests` (full project).
- **Before `/gsd-verify-work`:** Full solution suite green (335/335 v3.4.0 baseline + new responder/contract facts).
- **Max feedback latency:** ~120 seconds.

---

## Per-Task Verification Map

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| CONTRACT-01 | `ProcessorProjection` public in `Messaging.Contracts.Projections`; STJ round-trips with `inputDefinition`/`outputDefinition`/`liveness` preserved | unit | `dotnet test --filter "FullyQualifiedName~ProcessorProjection"` | ❌ W0 | ⬜ pending |
| CONTRACT-01 | `BaseApi.Core` has no ProjectReference to `BaseApi.Service`/`BaseConsole.Core` (firewall) | architecture | `dotnet test --filter "FullyQualifiedName~Firewall"` | ❌ W0 | ⬜ pending |
| CONTRACT-02 | `L2ProjectionKeys.ExecutionData(guid) == "skp:data:{guid:D}"` golden + distinct from root/step/processor | unit | `dotnet test --filter "FullyQualifiedName~L2ProjectionKeysTests"` | ✅ extend | ⬜ pending |
| CONTRACT-03 | `LivenessStatus.Healthy == "Healthy"` pin | unit | `dotnet test --filter "FullyQualifiedName~LivenessStatus"` | ❌ W0 | ⬜ pending |
| RPC-01 | `GetProcessorBySourceHash` found AND not-found round-trip via in-memory harness | integration | `dotnet test --filter "FullyQualifiedName~ProcessorResponder"` | ❌ W0 | ⬜ pending |
| RPC-02 | `GetSchemaDefinition` found AND not-found round-trip via in-memory harness | integration | `dotnet test --filter "FullyQualifiedName~SchemaResponder"` | ❌ W0 | ⬜ pending |
| RPC-03 | Bus health stays capped at Degraded; `/health/ready` returns 200 broker-down | integration (regression guard) | `dotnet test --filter "FullyQualifiedName~Health_Ready_Returns_200_When_Broker_Dead"` | ✅ exists | ⬜ pending |
| RPC-03 | CRUD surface + v3.4.0 publish path unchanged | regression | `dotnet test` (full suite green) | ✅ existing | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `ProcessorProjectionRoundTripTests.cs` (or extend an existing projection fact) — STJ serialize/deserialize pins `inputDefinition`/`outputDefinition`/`liveness` after the leaf move (CONTRACT-01).
- [ ] Firewall assertion — `BaseApi.Core.csproj` has no `ProjectReference` to `BaseApi.Service`/`BaseConsole.Core` (CONTRACT-01 / D-05). New `FirewallFacts` (parse csproj or assert no type leakage) unless an existing csproj-reference architecture test can be extended.
- [ ] `L2ProjectionKeysTests` — add `ExecutionData` golden + distinctness (CONTRACT-02). Extend the existing file.
- [ ] `LivenessStatusTests.cs` — `Healthy == "Healthy"` pin (CONTRACT-03).
- [ ] `ProcessorResponderTests.cs` + `SchemaResponderTests.cs` — in-memory `AddMassTransitTestHarness`/`UsingInMemory` harness, found AND not-found for each query (RPC-01/02). Mirror `ResultConsumeTests` scaffold.
- [ ] No framework install needed — xunit.v3 + `MassTransit.Testing` already referenced.

---

## Manual-Only Verifications

*All phase behaviors have automated verification.* Real RabbitMQ is intentionally NOT exercised this phase — the in-memory harness covers responder round-trips and the broker-down regression test points at a dead host by design. Full real-stack E2E is deferred to Phase 28 (TEST-01).

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
