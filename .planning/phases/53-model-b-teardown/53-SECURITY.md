---
phase: 53
slug: model-b-teardown
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-11
---

# Phase 53 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Phase 53 is a pure **teardown** of the legacy Model-B retry/`_error` transport wiring.
> No new endpoint, auth path, file access, or schema surface was introduced — every threat
> below concerns the *removal* (could it silently drop messages, leave a missed owner, or
> over-delete topology) rather than new attack surface.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| orchestrator consumer → RabbitMQ broker | A send that exhausts the in-code RetryLoop throws; with no bus retry and no error pipeline the broker nack-requeues (redelivery). Untrusted payloads already validated upstream (unchanged this phase). | MassTransit command messages (Start/Stop/StepCompleted/Pause/PauseAll/ResumeAll) |
| processor dispatch consumer → RabbitMQ broker | Send-exhaust throw → broker nack-requeue redelivery (no `_error`). | EntryStepDispatch messages |
| keeper recovery endpoint → skp-dlq-1 exchange | Dlq1-mode exhaust → `ConsolidatedErrorTransportFilter` sends to `exchange:skp-dlq-1`. After D-03, the keeper is the SOLE producer into skp-dlq-1. | ConsolidatedFault forensic payloads |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-53-01 | Tampering/Repudiation | Source-scan guard with a mis-resolved repo root (silent empty scan = false pass) | mitigate | Every source-scan fact asserts `Directory.Exists`/`File.Exists` before scanning. **Verified:** 6 existence guards in `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs`; verifier confirmed the RED facts named real offenders (had teeth). | closed |
| T-53-02 | Tampering | Source-scan false-positive on legitimate doc-comments masking a real regression | accept→mitigate | Guards match the CALL pattern (`endpointConfigurator.UseMessageRetry(`), not the bare word — excludes doc-comment survivors. **Verified:** FACT 6 GREEN in the live 4/4 Phase=53 run. | closed |
| T-53-Tamper | Tampering/Repudiation | Silent message loss if removal yields ack-discard instead of nack-requeue (DiscardFaultedMessages anti-pattern) | mitigate | No error pipeline added on any execution-path endpoint; the default with neither retry nor error filter is nack-requeue (redelivery). **Verified:** 0 `UseMessageRetry` CALLs in `src/Orchestrator/Consumers/` and `ProcessorStartupOrchestrator.cs`; global `ConfigureError` application callback = 0 in `MessagingServiceCollectionExtensions.cs`, relocated keeper-local at `RecoveryEndpointBinder.cs:108`; asserted by FACT 6 (GREEN). | closed |
| T-53-Regress | Tampering | A missed dual-owner retry (Stop left live) silently bounds redelivery | mitigate | The Wave-0 D-01 guard scans BOTH Start and Stop definitions. **Verified:** 0 `UseMessageRetry` CALLs across all 6 orchestrator consumer definitions incl. the dual-owner Start/Stop endpoint; FACT 6 GREEN. | closed |
| T-53-Topology | Denial of Service | Over-deleting the skp-dlq-1 topology with the global callback → keeper Dlq1 send hits a missing exchange (routing failure) | mitigate | The skp-dlq-1 topology declaration is explicitly KEPT (Pitfall 5); only the per-endpoint `ConfigureError` *application* moved keeper-local. **Verified:** `Publish<ConsolidatedFault>` survives (grep = 1) in `src/BaseConsole.Core/`. | closed |
| T-53-DoS | Denial of Service | Unbounded requeue spin on a permanently-failing poison send (orchestrator/processor unreachable on the bus — not an L2 outage, so the BIT gate does not pause it) | accept | DELIBERATELY accepted residual (D-04, A18 — mirrors the keeper SustainedOutage spin). The redelivery-count metric is the intended bound, deferred to observability work — NOT a code change this phase. Severity below `high` — does not block. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-53-1 | T-53-DoS | Unbounded nack-requeue spin on a permanently-failing poison send is the deliberately-accepted residual per design-doc A18 / decision D-04 (mirrors the keeper SustainedOutage policy). It is bounded by a redelivery-count metric tracked as deferred observability work, not by a code change in this teardown phase. Severity below `high`. | GSD secure-phase (Claude) | 2026-06-11 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-11 | 6 | 6 | 0 | GSD secure-phase (Claude, artifact-derived + first-hand grep verification) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-11
