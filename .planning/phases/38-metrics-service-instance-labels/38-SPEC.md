# Phase 38: Uniform `service_name` + Instance Labels Across All Metrics — Specification

**Created:** 2026-06-05
**Ambiguity score:** 0.11 (gate: ≤ 0.20)
**Requirements:** 5 locked

## Goal

Every Prometheus metric series (runtime, HTTP, and business instruments) for all four consoles carries a human-distinguishable `service_name = {name}_{version}` label plus a non-empty `service_instance_id` label — where the processor's `{name}_{version}` is sourced from the **database** (the single source of truth), not appsettings.

## Background

Metrics are exported OTLP-only (`AddOtlpExporter` → OTel Collector); there is **no `AddPrometheusExporter` in the .NET code**. The Collector's prometheus exporter has `resource_to_telemetry_conversion: enabled: true` (`compose/otel-collector-config.yaml`), which already promotes every OTel **resource attribute** onto every metric series as a Prom label. Phase 30 (`METRIC-01`) attaches `service.name` and `service.instance.id` to the metrics resource in both base libraries (`BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` for the webapi; `BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` for the consoles). So today every series already carries `service_name` and `service_instance_id` — but:

- `service_name` is the **bare** name (`sk-api`, `orchestrator`, `keeper`, `processor-sample`), with `service_version` as a separate label — there is no single `{name}_{version}` human label.
- The processor's name/version come from **appsettings** (`src/Processor.Sample/appsettings.json` → `Service:Name="processor-sample"`, `Service:Version="3.5.0"`), read by `cfg.Require("Service:Name")` at host-build time. This is a hand-maintained string that can drift from the DB's authoritative identity.
- The DB **is** the source of truth: `ProcessorEntity : BaseEntity`, and `BaseEntity` has non-empty-validated `Name` + `Version` (FluentValidation `BaseDtoValidator<T>`: `Name` NotEmpty/MaxLength(200) VALID-05, `Version` NotEmpty/strict-SemVer VALID-06, on both create and update). But the identity round-trip drops them: `ProcessorIdentityFound(Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId)` carries only Id + 3 schema Ids, and `GetProcessorBySourceHashConsumer` responds `new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId)` — Name/Version are never returned, stored, or used.

The known in-repo PromQL consumer is the Phase-11 D-17 round-trip test assertion `{service_name="sk-api"}`; `prometheus.yml` defines no recording/alert rules and no committed Grafana dashboards were found.

## Requirements

1. **MLBL-01 — Combined `service_name={name}_{version}` on all metric series**: Every scraped metric series carries `service_name` valued `{name}_{version}`.
   - Current: Prom `service_name` = bare name (`keeper`, `orchestrator`, `sk-api`, plus the processor); `service_version` is a separate label.
   - Target: every series across runtime + HTTP + business instruments carries `service_name = {name}_{version}` (e.g. `keeper_3.7.0`, `orchestrator_3.4.0`, `sk-api_3.2.0`, and the processor's DB `{Name}_{Version}`).
   - Acceptance: Prom queries for a runtime metric, an HTTP metric (`http_server_request_duration_seconds_count`), and a business counter (e.g. `orchestrator_*`) each return series whose `service_name` matches `^{name}_{semver}$` for the emitting console; no series for these four services has a bare `service_name` lacking the `_{version}` suffix.

2. **MLBL-02 — `service_instance_id` present on all three instrument families**: Every metric series carries a non-empty per-replica instance label.
   - Current: `service.instance.id` is attached to the metrics resource (Phase 30) → `service_instance_id` label; presence was historically asserted mainly on business counters.
   - Target: a non-empty `service_instance_id` label is verified present on runtime, HTTP, and business series alike.
   - Acceptance: for each of the three families, at least one scraped series is asserted to contain a non-empty `service_instance_id`; a test fails if any sampled family is missing it.

3. **MLBL-03 — Processor name+version sourced from the DB (single source of truth)**: The processor's steady-state identity is read from its DB row; its appsettings name/version are **retained** only as the boot-window placeholder until the DB identity resolves. *(Amended in `/gsd-discuss-phase 38`, GA-3 — supersedes the original "appsettings removed / `processor-pending` sentinel" framing.)*
   - Current: `ProcessorIdentityFound` has no Name/Version; the responder drops `p.Name`/`p.Version`; `IProcessorContext` stores only `{ Id + 3 schema Ids }`; the processor's metric identity comes from appsettings `Service:Name`/`Service:Version`.
   - Target: `ProcessorIdentityFound` carries `Name` + `Version`; `GetProcessorBySourceHashConsumer` populates them from `p.Name`/`p.Version`; `IProcessorContext`/`ProcessorContext` store and expose them; the processor's steady-state metric `service_name` = `{db.Name}_{db.Version}`. The processor's appsettings `Service:Name`/`Service:Version` are **retained** and feed the shared observability bootstrap unchanged (`cfg.Require` keeps working); until the DB identity resolves the metric `service_name` = the appsettings `{name}_{version}` (e.g. `processor-sample_3.5.0`), then the MeterProvider is swapped to the DB-sourced resource (GA-2 #1).
   - Acceptance: (i) the contract has `Name`+`Version` and the responder populates them; (ii) a running processor's business metric carries `service_name = {seeded DB row Name}_{seeded DB row Version}` (not the appsettings `processor-sample_3.5.0`) once identity resolves; (iii) `src/Processor.Sample/appsettings.json` **retains** its `Service:Name` / `Service:Version` keys (the boot-window placeholder source); (iv) metrics emitted before identity resolves carry `service_name` = the appsettings `{name}_{version}` (e.g. `processor-sample_3.5.0`), which is swapped to the DB value on resolve.

4. **MLBL-04 — Logs `service.name` stays the bare identity (metrics-only change)**: The `{name}_{version}` combination applies to the **metrics** `service_name` label only; the logs' resource `service.name` is unchanged.
   - Current: logs' `service.name` = bare name; the Phase-35 SC3 E2E and other ES queries filter `service.name="keeper"`.
   - Target: logs' `service.name` remains the bare identity (appsettings name for the singletons; DB `Name` — no version — for the processor); no `_{version}` suffix is added to any log's `service.name`. Existing ES log queries keep resolving; any processor-name log query is reconciled to the DB `Name`.
   - Acceptance: the Phase-35 hermetic + SC3 ES assertions filtering `service.name="keeper"` still pass; a sampled ES log document's `resource.attributes.service.name` equals the bare name (no `_{semver}` suffix) for every console.

5. **MLBL-05 — Prometheus query consumers updated and verified**: Every in-repo PromQL consumer is reconciled to the combined label and passes.
   - Current: the primary in-repo consumer is the Phase-11 D-17 round-trip assertion `{service_name="sk-api"}`; no recording/alert rules or dashboards are committed.
   - Target: every committed Prometheus query site (test metric assertions, plus any rules/dashboards if introduced) that references `service_name` is updated to the `{name}_{version}` scheme and continues to resolve `service_instance_id`; the inventory of updated sites is documented, and the absence of dashboards/alert rules beyond test assertions is recorded.
   - Acceptance: a grep/inventory shows zero remaining references to a bare `service_name="<name>"` (without version) for these four services; the updated Phase-11 assertion (now `service_name="sk-api_3.2.0"` or regex-matched) passes; the list of updated query sites is written into the phase summary.

## Boundaries

**In scope:**
- Combined `service_name={name}_{version}` on every metric series (runtime/HTTP/business) for webapi, orchestrator, keeper, processor.
- Verifying a non-empty `service_instance_id` on all three instrument families.
- Extending `ProcessorIdentityFound` with `Name`+`Version`; updating the responder, `IProcessorContext`/`ProcessorContext`.
- Sourcing the processor's steady-state metric `service_name` from the DB; **retaining** the processor's appsettings `Service:Name`/`Service:Version` as the boot-window placeholder; swapping the MeterProvider to the DB-sourced resource once identity resolves (GA-3 amendment).
- Updating/verifying all in-repo Prometheus query consumers (incl. the Phase-11 `service_name` assertion).
- Keeping logs' `service.name` as the bare identity (no version suffix).

**Out of scope:**
- Renaming the singletons' appsettings values (`sk-api`, `orchestrator`, `keeper` stay as-is) — only the version is appended in the metric label.
- A DB `CHECK`/`NOT NULL` constraint on `Name`/`Version` — application-layer FluentValidation already guarantees non-empty on the write path; DB hardening is not needed here.
- Adding the version suffix to logs' `service.name` — explicitly excluded to protect the Phase-35 ES queries.
- New business metrics or Keeper instruments — that is Phase 39 (the next phase); this phase only labels what already exists, and Phase 39's new Keeper instruments inherit this labeling automatically (resource/meter-provider level) and are verified there.
- Collector relabeling to spell the label key literally `service` — the key stays `service_name` (the OTel→Prom convention), which already distinguishes by human name.
- Any high-cardinality labels (`workflowId`, per-request, per-message).

## Constraints

- **Startup ordering:** the OTel metrics resource is built synchronously at host startup, but the processor's DB identity resolves later (async, via the `GetProcessorBySourceHash` request/response in `ProcessorStartupOrchestrator`, after the MeterProvider is built). The processor's `service_name` mechanism must bridge this (placeholder → resolved) rather than rely on an immutable startup resource value.
- The processor **keeps** its appsettings keys, so the shared `AddBaseConsoleObservability` path (`cfg.Require("Service:Name")`) is used **unchanged** to build the boot-window placeholder resource; the DB-sourced value is applied later via a MeterProvider swap (GA-2 #1), not by altering the shared path. *(Supersedes the original constraint that removing the keys would break the shared path.)*
- Low cardinality preserved: `{name}_{version}` is a small fixed set (version is strict SemVer); `service_instance_id` remains the only per-replica dimension.
- The Phase-30 single-resolve invariant for `service.instance.id` (resolved once per process; logs and metrics carry the same value) must be preserved.
- Non-emptiness of processor `Name`/`Version` is an application-layer (FluentValidation) guarantee on the HTTP write path, not a DB constraint — assumes all processor rows are written through the validated API.
- No live operator verification required: provable hermetically + via a scrape assertion against the compose stack's Prometheus / collector `/metrics`.

## Acceptance Criteria

- [ ] Every scraped metric series for each console carries `service_name="{name}_{version}"`, verified on the runtime, HTTP, and business families.
- [ ] No metric series for these four services carries a bare `service_name` lacking the `_{version}` suffix.
- [ ] Each sampled metric family (runtime, HTTP, business) carries a non-empty `service_instance_id` label.
- [ ] `ProcessorIdentityFound` includes `Name` and `Version`; `GetProcessorBySourceHashConsumer` populates them from `p.Name`/`p.Version`.
- [ ] `IProcessorContext`/`ProcessorContext` store and expose the resolved `Name`+`Version`.
- [ ] A running processor's metric `service_name` equals `{seeded DB Name}_{seeded DB Version}` (not `processor-sample`).
- [ ] `src/Processor.Sample/appsettings.json` **retains** its `Service:Name` / `Service:Version` keys (boot-window placeholder source — GA-3 amendment).
- [ ] Metrics emitted before the processor's identity resolves carry `service_name` = the appsettings `{name}_{version}` (e.g. `processor-sample_3.5.0`); the MeterProvider swaps to the DB-sourced `service_name` once identity resolves.
- [ ] Logs' `service.name` for keeper/orchestrator/webapi is unchanged (bare name); the Phase-35 ES assertion `service.name="keeper"` still passes.
- [ ] All in-repo Prometheus query consumers (incl. the Phase-11 `service_name` round-trip assertion) are updated to the combined label and pass.
- [ ] Solution builds 0 warning / 0 error (Release + Debug); the hermetic suite is green.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Combined `{name}_{version}` + DB-sourced processor, measurable |
| Boundary Clarity   | 0.90  | 0.70 | ✓      | Explicit out-of-scope: logs unchanged, no DB constraint, singletons not renamed |
| Constraint Clarity | 0.82  | 0.65 | ✓      | Startup-ordering + Require()-coupling + app-layer non-empty captured |
| Acceptance Criteria| 0.88  | 0.70 | ✓      | 11 pass/fail checks                                           |
| **Ambiguity**      | 0.11  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                   | Decision locked                                                                 |
|-------|-----------------|----------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher      | How do service names reach Prometheus?             | appsettings `Service:Name` → `AddService` → resource attr → `resource_to_telemetry_conversion` → `service_name`; both `service_name` + `service_instance_id` already present (OTLP-only, no app Prom exporter) |
| 2     | Researcher      | Does the processor retrieve Name/Version from DB?  | No — `ProcessorIdentityFound` carries only Id + 3 schema Ids; the DB (`BaseEntity`) has Name+Version but they are dropped by the responder |
| 3     | Boundary Keeper | Label shape + source per console; scope of DB-source | `service_name={name}_{version}`; singletons (webapi/orch/keeper) from appsettings; processor from DB; processor appsettings name/version removed as redundant; DB-as-source scoped to processors only |
| 4     | Failure Analyst | Empty Name/Version? logs divergence? startup window? | FluentValidation guarantees non-empty Name + SemVer Version (create+update) — verified; logs `service.name` kept bare (protect ES queries); `processor-pending` placeholder until DB identity resolves |
| 4     | Seed Closer     | Final confirmation                                 | "confirm all" — 5 requirements locked, ambiguity 0.11 |

---

*Phase: 38-metrics-service-instance-labels*
*Spec created: 2026-06-05*
*Next step: /gsd-discuss-phase 38 — implementation decisions (resource-vs-label mechanism for the processor's DB-sourced `service_name`, the `processor-pending` bootstrap, the collector vs SDK transformation for `{name}_{version}`)*
