# Phase 38: Uniform `service_name` + Instance Labels Across All Metrics - Context

**Gathered:** 2026-06-06
**Status:** Ready for planning

<domain>
## Phase Boundary

Every Prometheus metric series (runtime, HTTP, and business instruments) for all four consoles
(sk-api, orchestrator, keeper, processor) carries a human-distinguishable
`service_name = {name}_{version}` label plus a non-empty `service_instance_id` label — where the
processor's `{name}_{version}` is sourced from the **database** (single source of truth) in steady
state. Logs' `service.name` stays the bare identity (metrics-only change). All in-repo PromQL
consumers are reconciled to the combined label.

This discussion settled **HOW** to implement; **WHAT** is locked by `38-SPEC.md` (5 requirements).

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**5 requirements are locked.** See `38-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `38-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**⚠ SPEC AMENDED during this discussion (GA-3):** MLBL-03 was changed from
"remove appsettings `Service:Name`/`Version` + `processor-pending` sentinel" to
"**retain** appsettings keys as the boot-window placeholder; DB value applied via MeterProvider swap
on resolve." The `38-SPEC.md` MLBL-03 target/acceptance (iii)/(iv), Boundaries, Constraints, and the
Acceptance Criteria checklist were patched in the same commit. CONTEXT.md and SPEC.md now agree.

**In scope (from SPEC.md):**
- Combined `service_name={name}_{version}` on every metric series (runtime/HTTP/business) for webapi, orchestrator, keeper, processor.
- Verifying a non-empty `service_instance_id` on all three instrument families.
- Extending `ProcessorIdentityFound` with `Name`+`Version`; updating the responder, `IProcessorContext`/`ProcessorContext`.
- Sourcing the processor's steady-state metric `service_name` from the DB; **retaining** appsettings keys as the boot placeholder; MeterProvider swap on resolve.
- Updating/verifying all in-repo Prometheus query consumers (incl. the Phase-11 `service_name` assertions).
- Keeping logs' `service.name` as the bare identity (no version suffix).

**Out of scope (from SPEC.md):**
- Renaming the singletons' appsettings values (`sk-api`, `orchestrator`, `keeper` stay as-is) — only the version is appended in the metric label.
- A DB `CHECK`/`NOT NULL` constraint on `Name`/`Version` — FluentValidation already guarantees non-empty on the write path.
- Adding the version suffix to logs' `service.name` (protects the Phase-35 ES queries).
- New business metrics or Keeper instruments — that is Phase 39.
- Collector relabeling to spell the label key literally `service` — the key stays `service_name`.
- Any high-cardinality labels (`workflowId`, per-request, per-message).

</spec_lock>

<decisions>
## Implementation Decisions

### `{name}_{version}` transformation site (GA-1)
- **D-01:** Combine **SDK-side**, in C# — set the metrics resource `service.name = $"{name}_{version}"`
  directly. Applies uniformly to all four services. The OTel Collector stays the dumb
  resource→label promoter it is today (`resource_to_telemetry_conversion: true`); **no collector
  `transform`/`metricstransform` processor is added.** Rationale: the collector can't help the
  processor's *dynamic* DB name anyway (it can only combine what the SDK sends), so collector-side
  would split the rule across two places for no gain.

### Processor dynamic `service_name` bridge (GA-2)
- **D-02:** Use the **MeterProvider swap** strategy (GA-2 #1). The metrics Resource is immutable once
  `MeterProvider.Build()` runs at host startup, but the processor's DB identity resolves async later
  in `ProcessorStartupOrchestrator` Loop A (`context.SetIdentity(...)`). Bridge: build an initial
  MeterProvider with the placeholder resource; when identity resolves, **dispose that provider and
  build a new one** with the DB-sourced resource (`service.name = {db.Name}_{db.Version}`). The
  `Meter` objects themselves (`BaseProcessor`, MassTransit, runtime) are NOT recreated — only the
  provider (listener/aggregator/exporter) is swapped; provider #2 re-subscribes to the same meter
  names. The placeholder series and resolved series are genuinely distinct Prom series (different
  label value), which is expected — the old one goes stale.
- **D-03:** The **swap seam is deferred to research** (`gsd-phase-researcher`). The mechanics are
  non-trivial because the host normally owns the MeterProvider as a managed singleton; making it
  swappable means owning the build/dispose in a small holder (e.g. a singleton
  `MeterProviderHolder` with `_current.Dispose(); _current = BuildNew(resolvedResource)`), driven
  from Loop A after `SetIdentity`, **without** leaking the OTLP exporter, double-subscribing meters,
  or racing the heartbeat that fires at `MarkHealthy`. Research must pin the cleanest seam at
  OpenTelemetry .NET **1.15.3**. (Rejected alternative: GA-2 #2, the export-time resource-rewrite
  wrapper — viable and gap-free, but the user chose the explicit swap.)

### Processor boot-window placeholder + shared observability path (GA-3 — SPEC AMENDMENT)
- **D-04:** **Retain** the processor's appsettings `Service:Name` / `Service:Version` in
  `src/Processor.Sample/appsettings.json`. They are NOT removed (this reverses the original
  MLBL-03 (iii)). They serve as the **boot-window placeholder** identity.
- **D-05:** Before the DB identity resolves, the processor's metric `service_name` = the appsettings
  `{name}_{version}` (e.g. `processor-sample_3.5.0`) — **not** a `processor-pending` sentinel (this
  reverses the original MLBL-03 (iv)). Rationale: a meaningful real-ish identity during boot beats
  an opaque sentinel; the DB is still the single source of truth in steady state (appsettings only
  ever shows during the boot window, which a drifted value would only briefly expose before
  correcting).
- **D-06:** Because the keys are retained, the shared `AddBaseConsoleObservability` path
  (`cfg.Require("Service:Name")`) is used **unchanged** to build the initial placeholder resource —
  identical to the singletons. The processor-specific behavior is *only* the later MeterProvider
  swap (D-02). GA-3's original "how do processor & singletons share the method" fork **dissolves** —
  no overload, no separate method needed.

### `service_version` label on metrics (GA-4)
- **D-07:** **Keep** the standalone `service.version` resource attribute (→ `service_version` Prom
  label) on metrics. No behavior change, low-cardinality, harmless, avoids touching the shared/logs
  path. The combined `service_name` value is built from name+version sourced appsettings (boot) →
  DB (resolved); `service.version` stays as-is alongside it. (Folded into the GA-3 decision per the
  user: the combined value's version comes from the same source as the standalone attr.)

### PromQL consumer update style (GA-5)
- **D-08:** Update all in-repo PromQL consumers with the **exact literal** combined value — e.g.
  `service_name="sk-api_3.2.0"` — NOT a regex match. Sites are listed under code_context below.
  Trade-off accepted: exact literals couple the assertions to the version string and will need a
  touch on each version bump; the user chose exactness over churn-resistance.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (read first)
- `.planning/phases/38-metrics-service-instance-labels/38-SPEC.md` — 5 locked requirements
  (MLBL-01..05), boundaries, constraints, acceptance criteria. **MLBL-03 was amended during
  discussion (GA-3) — read the current file, not memory of the original.**

### Observability wiring (the SDK-side combine + swap live here)
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — shared
  console OTel wiring (`AddService(name, version)` + `service.instance.id`); the `cfg.Require`
  path used by orchestrator/keeper/processor for the placeholder resource. SDK-side `{name}_{version}`
  combine (D-01) goes here.
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — the webapi
  analog (sk-api); same `AddService(name, version)` pattern + `ResolveInstanceId` precedence.
- `src/Processor.Sample/Program.cs` — processor composition root; calls
  `AddBaseConsoleObservability` then `AddBaseProcessor`. The swap (D-02) must be wired without
  breaking this thin-shell order.

### Processor identity round-trip (DB-source plumbing — MLBL-03)
- `src/Messaging.Contracts/ProcessorQueries.cs` — `ProcessorIdentityFound` record (extend with
  `Name`+`Version`).
- `src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs` —
  responder that must populate `Name`/`Version` from `p.Name`/`p.Version`.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` + `ProcessorContext.cs` — the mutable
  singleton holder; `SetIdentity` must store + expose `Name`+`Version` (note the WR-03
  memory-visibility invariant — safe to read only after `IsHealthy`).
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — Loop A resolves identity;
  the swap trigger (D-02/D-03) fires here right after `context.SetIdentity(found.Message)`.

### Collector (NOT changed — confirms D-01 leaves it alone)
- `compose/otel-collector-config.yaml` — `prometheus` exporter with
  `resource_to_telemetry_conversion: enabled: true` promotes resource attrs to labels. No new
  processor added (D-01).

### PromQL consumers to update (MLBL-05 / D-08) — exact literal `service_name="sk-api_3.2.0"`
- `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — multiple `service_name="sk-api"` literals.
- `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` — `service_name="sk-api"` literals.
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — doc-comment reference (reconcile narrative).
- (Inventory note for MLBL-05: `prometheus.yml` defines no recording/alert rules; no committed
  Grafana dashboards — confirm during planning that test assertions are the only query sites.)

### Drift guard (touch in lock-step if `ResolveInstanceId` changes)
- `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs` — hermetic mirror of the
  `service.instance.id` precedence (IN-03 three-place drift guard). Likely untouched this phase
  (instance id is already done) but noted because MLBL-02 verifies its label.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`AddBaseConsoleObservability` / `ObservabilityServiceCollectionExtensions`**: existing
  `AddService(name, version)` resource build — the single edit point for SDK-side `{name}_{version}`
  combine (D-01). Both already attach `service.instance.id` (Phase 30), so MLBL-02 is largely
  already satisfied at the resource level — this phase mostly *verifies* it across all three families.
- **`ProcessorStartupOrchestrator` Loop A**: already has the `context.SetIdentity(...)` resolution
  point — the natural trigger for the MeterProvider swap (D-02).
- **`IProcessorContext`/`ProcessorContext`**: established mutable-singleton-with-`SetIdentity`
  pattern; extends cleanly with `Name`+`Version` (mirror the existing schema-Id properties).
- **`ProcessorIdentityFound`**: a record — adding `Name`+`Version` is a positional extension; the
  responder and `SetIdentity` are the two call sites.

### Established Patterns
- **OTLP-only export** (no `AddPrometheusExporter` in .NET); the collector's
  `resource_to_telemetry_conversion` does all resource→label promotion. D-01 keeps this intact.
- **Resource is immutable post-`Build()`** — the entire reason GA-2 needs a swap, not a mutate.
- **`service.instance.id` resolved ONCE per process** (Phase 30 D-10) and shared by logs+metrics —
  must be preserved across the swap (provider #2 must reuse the same instance id).
- **Snake_case instruments, no `_total` suffix** (collector appends suffixes) — affects nothing new
  here but governs any assertion strings.

### Integration Points
- The MeterProvider swap connects `ProcessorStartupOrchestrator` (identity) → the observability
  layer (resource). New seam: a swappable provider holder owned outside the host's managed
  singleton lifecycle (research to design).
- Logs path (`AddOpenTelemetry` MEL bridge) must stay on the **bare** name (MLBL-04) — the swap and
  the `{name}_{version}` combine touch the **metrics** resource only, never the logs resource.

</code_context>

<specifics>
## Specific Ideas

- Placeholder must be the appsettings `{name}_{version}` rendered the same way as the resolved value
  (e.g. `processor-sample_3.5.0`), so the only thing that changes at swap time is the name/version
  *source*, not the label *shape*.
- PromQL assertions use the concrete current versions (`sk-api_3.2.0`, and the orchestrator/keeper
  versions from their appsettings) — planner should read the live appsettings `Service:Version` of
  each console when writing the exact-literal assertions.

</specifics>

<deferred>
## Deferred Ideas

- **GA-2 #2 (export-time resource-rewrite exporter wrapper)** — not chosen, but a viable gap-free
  alternative if the swap seam proves too invasive during research/execution; recorded so it isn't
  re-discovered from scratch.
- **Collector-side `{name}_{version}` transform** — rejected (D-01); noted in case a future
  multi-language fleet ever needs centralized label shaping.
- New Keeper instruments + their labeling verification → **Phase 39** (inherit this labeling
  automatically at the resource/meter-provider level).

None of these are in Phase 38 scope.

</deferred>

---

*Phase: 38-metrics-service-instance-labels*
*Context gathered: 2026-06-06*
