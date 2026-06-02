---
phase: 30
slug: runtime-business-metrics
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-03
---

# Phase 30 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| test process → Prometheus server (:9090) | `PrometheusTestClient` / `MetricsRoundTripE2ETests` construct a PromQL query string; the only interpolated value is a server-minted `ProcessorId` GUID (`:D`). | PromQL query text (no secrets); read-only `api/v1/query`. |
| env → OTel resource | `POD_NAME` / `HOSTNAME` are read once per process into the `service.instance.id` resource attribute (one bounded value per replica). | Replica identity string (non-sensitive). |
| orchestrator/processor code → metric instruments | Counter tags are built from `ProcessorId` (bounded GUID) and, on the processor, `outcome` (3 bounded enum values) — never per-execution ids. | Low-cardinality metric labels. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-30-01 | Tampering | PromQL query construction in `PrometheusTestClient` | mitigate | `Uri.EscapeDataString(promQL)` on the query param; query bodies are static C# templates with no untrusted input. Verified: `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs:83,210`. | closed |
| T-30-02 | Denial of Service | metric label cardinality (`service_instance_id`) | accept | `service_instance_id` is bounded by replica count (one value per process), not per-execution; NO `workflowId`. Locked by SPEC / D-03. See Accepted Risks. | closed |
| T-30-03 | Denial of Service | orchestrator counter label cardinality | mitigate | Counters tag only `ProcessorId` (bounded) + ambient `service_instance_id`; NO `workflowId` / per-execution id. Verified: `src/Orchestrator/Dispatch/StepDispatcher.cs:33`, `src/Orchestrator/Consumers/ResultConsumer.cs:52` (grep-confirmed no `workflowId` in any metric `.Add` site). | closed |
| T-30-04 | Denial of Service | processor counter label cardinality | mitigate | Counters tag only `ProcessorId` (bounded) + `outcome` ∈ {completed,failed,cancelled} (3 values) + ambient `service_instance_id`; NO `workflowId`. Verified: `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:61,223` (outcome via the pinned `OutcomeLabel` switch). | closed |
| T-30-05 | Tampering | `outcome` tag value | accept | `outcome` is derived from the framework-owned `StepOutcome` enum via a static literal switch (`OutcomeLabel`), never user input; build paths never emit `processing`. See Accepted Risks. | closed |
| T-30-06 | Tampering | PromQL query construction in the E2E | mitigate | Static C# string templates with only `procId:D` (a server-minted GUID) interpolated; the shared `PrometheusTestClient` applies `Uri.EscapeDataString` on the query param (T-30-01). Verified: `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` uses `PollPromForQuery`. | closed |
| T-30-07 | Denial of Service | dev-posture no-auth on Prometheus (:9090) | accept | Pre-existing documented dev-only stance (compose comments); not introduced or changed by this phase; prod/k8s hardening is out of the current horizon. See Accepted Risks. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-30-01 | T-30-02 | `service_instance_id` cardinality is bounded by replica count (one value per process), not per-execution; the metric exists specifically to enable per-replica analysis. No `workflowId` label anywhere. Locked by SPEC / D-03. | User (operator) | 2026-06-03 |
| AR-30-02 | T-30-05 | `outcome` is sourced from the framework-owned `StepOutcome` enum (3 bounded values), never from user/wire input; the `OutcomeLabel` switch pins the label vocabulary so an enum rename cannot widen it. | User (operator) | 2026-06-03 |
| AR-30-03 | T-30-07 | Prometheus runs without auth in the dev compose stack — a pre-existing documented dev-only posture unchanged by this phase. Production/k8s ingress hardening is a future-horizon concern outside this milestone. | User (operator) | 2026-06-03 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-03 | 7 | 7 | 0 | /gsd-secure-phase (State B from artifacts; mitigations corroborated by 30-REVIEW.md + 30-VERIFICATION.md, both of which read the implementation files directly) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-03
