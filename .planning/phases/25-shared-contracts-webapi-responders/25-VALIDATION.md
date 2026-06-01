---
phase: 25
slug: shared-contracts-webapi-responders
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-01
validated: 2026-06-01
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
| CONTRACT-01 | `ProcessorProjection` public in `Messaging.Contracts.Projections`; STJ round-trips with `inputDefinition`/`outputDefinition`/`liveness` preserved | unit | `dotnet test tests/BaseApi.Tests -- --filter-class "*ProjectionRecordRoundTripTests"` | ✅ `ProjectionRecordRoundTripTests.cs` | ✅ green |
| CONTRACT-01 | `BaseApi.Core` has no reference to `BaseApi.Service`/`BaseConsole.Core` (firewall) | architecture | `dotnet test tests/BaseApi.Tests -- --filter-class "*BaseApiCoreFirewallTests"` | ✅ `BaseApiCoreFirewallTests.cs` | ✅ green |
| CONTRACT-02 | `L2ProjectionKeys.ExecutionData(guid) == "skp:data:{guid:D}"` golden + distinct from root/step/processor | unit | `dotnet test tests/BaseApi.Tests -- --filter-class "*L2ProjectionKeysTests"` | ✅ `L2ProjectionKeysTests.cs:55,63` | ✅ green |
| CONTRACT-03 | `LivenessStatus.Healthy == "Healthy"` pin | unit | `dotnet test tests/BaseApi.Tests -- --filter-class "*LivenessStatusTests"` | ✅ `LivenessStatusTests.cs:15` | ✅ green |
| RPC-01 | `GetProcessorBySourceHash` found AND not-found round-trip via in-memory harness | integration | `dotnet test tests/BaseApi.Tests -- --filter-class "*ProcessorResponderTests"` | ✅ `ProcessorResponderTests.cs:94,117` | ✅ green |
| RPC-02 | `GetSchemaDefinition` found AND not-found round-trip via in-memory harness | integration | `dotnet test tests/BaseApi.Tests -- --filter-class "*SchemaResponderTests"` | ✅ `SchemaResponderTests.cs:79,99` | ✅ green |
| RPC-03 | Bus health stays capped at Degraded; `/health/ready` returns 200 broker-down | integration (regression guard) | `dotnet test tests/BaseApi.Tests -- --filter-class "*HealthEndpointsTests"` | ✅ `HealthEndpointsTests.cs:221,233` | ✅ green |
| RPC-03 | CRUD surface + v3.4.0 publish path unchanged | regression | `dotnet test` (full suite green) | ✅ existing (345/345 per SUMMARY) | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

All Wave 0 references were created/extended during execution and verified green (34/34 targeted tests on 2026-06-01).

- [x] `ProjectionRecordRoundTripTests.cs` — STJ serialize/deserialize pins `inputDefinition`/`outputDefinition`/`liveness` after the leaf move (CONTRACT-01). Pre-existing facts stayed green against the relocated public type.
- [x] Firewall assertion — `BaseApiCoreFirewallTests.cs` (reflection, no host boot): asserts `BaseApi.Core` references neither `BaseApi.Service` nor `BaseConsole.Core` (CONTRACT-01 / D-05). 3 facts.
- [x] `L2ProjectionKeysTests` — `ExecutionData` golden (`skp:data:{guid:D}`) + distinctness from Root/Processor (CONTRACT-02). Extended existing file.
- [x] `LivenessStatusTests.cs` — `Healthy == "Healthy"` pin (CONTRACT-03).
- [x] `ProcessorResponderTests.cs` + `SchemaResponderTests.cs` — in-memory `AddMassTransitTestHarness`/`UsingInMemory` harness, found AND not-found for each query (RPC-01/02) over a real service on a seeded EF-InMemory DbContext.
- [x] No framework install needed — xunit.v3 + `MassTransit.Testing` already referenced.

---

## Manual-Only Verifications

*All phase behaviors have automated verification.* Real RabbitMQ is intentionally NOT exercised this phase — the in-memory harness covers responder round-trips and the broker-down regression test points at a dead host by design. Full real-stack E2E is deferred to Phase 28 (TEST-01).

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-01 (retroactive audit — all 8 requirement rows COVERED + green)

---

## Validation Audit 2026-06-01

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |
| Requirements COVERED (green) | 8 / 8 |

**Method:** State-A retroactive audit. The VALIDATION.md draft was authored pre-execution (all rows `⬜ pending`) and never reconciled after the phase landed. Cross-referenced each requirement against on-disk tests, then ran the targeted classes via the native MTP `--filter-class` flag: `LivenessStatusTests`, `L2ProjectionKeysTests`, `ProjectionRecordRoundTripTests`, `BaseApiCoreFirewallTests`, `ProcessorResponderTests`, `SchemaResponderTests` → **23/23 green**; `HealthEndpointsTests` (incl. broker-dead RPC-03 guards) → **11/11 green**. No new tests generated — every behavior was already pinned by execution. No auditor spawn required.
