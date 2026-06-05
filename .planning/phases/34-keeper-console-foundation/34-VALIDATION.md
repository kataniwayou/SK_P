---
phase: 34
slug: keeper-console-foundation
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-05
---

# Phase 34 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution. Derived from 34-RESEARCH.md §"Validation Architecture". The sole test project is `tests/BaseApi.Tests` (xUnit v3 + MassTransit.Testing harness); console/messaging tests live under `tests/BaseApi.Tests/Console/`, `.../Orchestrator/`, and (new this phase) `.../Keeper/`.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (`TestContext.Current.CancellationToken` idiom) + MassTransit.Testing in-memory harness |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (the only test project — no separate Keeper.Tests) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"` |
| **Full suite command** | `dotnet test SK_P.sln` |
| **Estimated runtime** | ~3–5 s (Keeper hermetic subset); full hermetic suite ~minutes |

---

## Sampling Rate

- **After every task commit:** `dotnet build src/Keeper -c Debug` + `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Keeper"`
- **After every plan wave:** `dotnet test SK_P.sln` (full hermetic, `--filter-not-trait "Category=RealStack"`)
- **Before `/gsd-verify-work`:** Full suite green + `dotnet build SK_P.sln -c Release` 0-warning + `dotnet build -c Debug` 0-warning + `docker compose build keeper` + live `docker compose up` health-ready
- **Max feedback latency:** ~5 s (Keeper hermetic subset)

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command | File Exists |
|--------|----------|-----------|-------------------|-------------|
| KEEP-01 | Keeper console exists on `BaseConsole.Core`, mirrors Orchestrator; metrics-only OTel, no BaseApi/EF/Quartz refs | build + reflection fact | `dotnet build src/Keeper -c Release` (+ `-c Debug`); ref-firewall reflection test | ❌ W0 |
| KEEP-01 | `Program.cs` boots three-call seam; bus resolvable; `/health/ready` flips on bus-start (StartupCompletionService KEPT) | hermetic host-boot | Keeper variant of `ConsoleHostBootTests` | ❌ W0 |
| KEEP-02 | Shared competing-consumer endpoint (NOT fan-out) — one publish → one consume | hermetic harness | round-robin test (inverse of `FanOutBroadcastTests`) | ❌ W0 |
| KEEP-02 | Live multi-replica round-robin across 2 replicas | manual live-stack smoke | `docker compose up -d keeper; docker compose ps` + publish + log split | ❌ manual |
| KEEP-03 | Builds + containerizes (multi-stage Dockerfile) | build + docker build | `docker compose build keeper` | ❌ W0 |
| KEEP-03 | Joins compose stack as healthy tier | compose-shape fact + live health | `ComposeYamlFacts` assertion + `docker compose up`/`ps` health | ❌ W0 |
| DLQ-04* | Placeholder binds `Immediate(Limit)` from shared `RetryOptions` | definition-read / harness pattern assertion | (pattern only; full DLQ routing is Phase 36 — do NOT check DLQ-04 box this phase) | ❌ W0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs` — KEEP-02 hermetic round-robin (inverse of `FanOutBroadcastTests`: ONE shared endpoint + ONE consumer type → total consumed == 1)
- [ ] `tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs` (+ a Keeper-specific `ConsoleTestHostFixture` variant registering the placeholder consumer) — KEEP-01 boot/readiness
- [ ] `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` — mirror `ConsoleDependencyFirewallTests` anchored on a Keeper type; assert no BaseApi.*/EF/Npgsql/Quartz/Cronos refs (KEEP-01 reference closure)
- [ ] `ComposeYamlFacts` new assertions: keeper tier present, `deploy.replicas: 2`, NO `container_name` for keeper, `dockerfile: src/Keeper/Dockerfile`, 8083 health (KEEP-03)
- [ ] No framework install needed — xUnit v3 + MassTransit.Testing already present in `BaseApi.Tests`

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live multi-replica round-robin (2 replicas, RabbitMQ load-balances fault events) | KEEP-02 | Needs the full live compose stack with 2 Keeper replicas | `docker compose up -d --build keeper` → `docker compose ps` (2 healthy keeper replicas) → publish a placeholder message → observe one-replica-per-message log split |
| Keeper joins compose as healthy tier | KEEP-03 | Needs live Docker health probes | `docker compose up -d` → `docker compose ps` shows keeper replicas `healthy` alongside orchestrator/processor-sample |
| Close-gate net-zero (triple-SHA) with keeper joined | KEEP-03 | Operator gate, full stack | Confirm the stable durable placeholder queue does NOT churn the `rabbitmqctl list_queues` SHA (Phase 38 close gate) |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references (4 new test files / assertions above)
- [ ] No watch-mode flags
- [ ] Feedback latency < 5s (Keeper hermetic subset)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
