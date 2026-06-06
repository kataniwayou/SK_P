# Phase 38: Uniform `service_name` + Instance Labels - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-06
**Phase:** 38-metrics-service-instance-labels
**Areas discussed:** GA-1 transformation site, GA-2 processor bridge, GA-3 boot placeholder / shared path, GA-4 service_version label, GA-5 PromQL update style

**SPEC status:** `38-SPEC.md` loaded (5 requirements locked, ambiguity 0.11). Discussion focused on HOW only. **GA-3 amended the locked SPEC (MLBL-03) — patched in the same commit.**

---

## GA-1 — `{name}_{version}` transformation site

| Option | Description | Selected |
|--------|-------------|----------|
| SDK-side combine | Set resource `service.name = $"{name}_{version}"` in C#; collector untouched | ✓ |
| Collector-side transform | Keep bare attrs; add a collector `transform`/`metricstransform` processor to concatenate | |

**User's choice:** SDK-side.
**Notes:** Recommendation accepted as-is. Collector can't help the processor's dynamic DB name, so SDK-side keeps one combination rule in one place.

---

## GA-2 — Processor dynamic `service_name` bridge

| Option | Description | Selected |
|--------|-------------|----------|
| #1 MeterProvider swap | Build placeholder provider; dispose+rebuild with DB resource on identity resolve | ✓ |
| #2 Exporter/resource wrapper | Keep one provider; rewrite `service.name` at export time from the mutable context | |
| #3 Defer metrics until resolved | (rejected by SPEC — it requires placeholder emissions in the boot window) | |

**User's choice:** #1 (MeterProvider swap).
**Notes:** User asked for a concrete explanation of option #1, then a second explanation of what "rebuild" / "dispose provider #1" means mechanically (the MeterProvider as the runtime listener/aggregator/exporter holding the immutable Resource; dispose stops its timer + unsubscribes + tears down the exporter; rebuild constructs a new provider re-subscribing to the same meter names with a new Resource; the `Meter` objects are untouched). After the explanation, user confirmed #1. The exact swap seam (provider-holder owning build/dispose outside the host's managed singleton lifecycle) was explicitly deferred to `gsd-phase-researcher` at OTel .NET 1.15.3.

---

## GA-3 — Processor boot-window placeholder + shared observability path *(SPEC AMENDMENT)*

| Option | Description | Selected |
|--------|-------------|----------|
| SPEC original | Remove appsettings `Service:Name`/`Version`; emit `service_name="processor-pending"` until resolve | |
| GA-3 amendment | **Retain** appsettings keys; boot label = appsettings `{name}_{version}`, swaps to DB on resolve | ✓ |

**User's choice:** Retain appsettings name+version as the unresolved/boot label until the processor gets its DB identity.
**Notes:** This reverses locked MLBL-03 acceptance (iii) (no appsettings keys) and (iv) (`processor-pending` sentinel). Flagged explicitly as a SPEC amendment; user confirmed ("proceed") that `38-SPEC.md` should be patched to match. Side-benefit: the shared `AddBaseConsoleObservability` path (`cfg.Require`) stays unchanged, so GA-3's original "how do processor & singletons share the method" sub-fork dissolves. Core MLBL-03 intent (DB = single source of truth in steady state) is preserved — appsettings only ever shows during the boot window.

---

## GA-4 — `service_version` label on metrics

| Option | Description | Selected |
|--------|-------------|----------|
| Keep `service.version` attr | Leave standalone `service_version` Prom label; harmless, low-cardinality | ✓ |
| Drop it | Remove as redundant once folded into `service_name` | |

**User's choice:** "mentioned on GA-3" — folded into the GA-3 decision (combined value sourced appsettings→DB; standalone `service.version` attr stays as-is).
**Notes:** No behavior change; avoids touching the shared/logs path.

---

## GA-5 — PromQL consumer update style

| Option | Description | Selected |
|--------|-------------|----------|
| Exact literal | `service_name="sk-api_3.2.0"` — precise, couples to version string | ✓ |
| Anchored regex | `service_name=~"^sk-api_.*"` — robust to version bumps | |

**User's choice:** Exact literal.
**Notes:** Trade-off accepted — assertions need a touch on each version bump in exchange for exactness.

---

## Claude's Discretion

- None left fully open. GA-4 (keep `service.version`) is the lowest-stakes call and was folded into GA-3.

## Deferred Ideas

- GA-2 #2 (export-time resource-rewrite exporter wrapper) — viable gap-free fallback if the swap seam proves too invasive.
- Collector-side `{name}_{version}` transform — rejected; noted for a future multi-language fleet.
- New Keeper instruments + their labeling verification — Phase 39.
