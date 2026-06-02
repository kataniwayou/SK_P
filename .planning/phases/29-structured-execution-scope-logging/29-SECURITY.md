---
phase: 29
slug: structured-execution-scope-logging
status: verified
threats_open: 0
asvs_level: standard
created: 2026-06-02
---

# Phase 29 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| bus → consumer (consume pipeline) | The inbound message body crosses into the console's logging scope; `InboundExecutionScopeConsumeFilter` reads the `IExecutionCorrelated` ids off the body into a MEL scope. | Server-minted `Guid` ids (WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) |
| processor identity (singleton) → log telemetry | `IProcessorContext.Id` (server-resolved `Guid?`, null pre-identity) crosses into every processor LogRecord via `ProcessorIdLogEnricher`. | Server-resolved `Guid` (ProcessorId), null-guarded |
| consume body (minted ids) → log telemetry | `EntryStepDispatchConsumer`'s minted `ExecutionId`/`EntryId` (`NewId.NextGuid()`) cross into the nested scope on the Completed-path log line. | Server-minted `Guid` ids |
| Quartz job body → log telemetry | `WorkflowFireJob`'s per-fire `correlationId` (server-minted) and `workflowId` (`Guid.TryParse`-validated from job-data) cross into the explicit scope. | Server-minted + type-validated `Guid` ids |
| test → Elasticsearch (read) | The real-stack E2E adds one read-only ES query for the scope-sourced WorkflowId. No new write boundary. | GUID read-back (self-seeded) |
| close-gate → live infra (read snapshots) | `phase-29-close.ps1` reads `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` snapshots; it does not mutate steady state (no FLUSHDB). | Infra snapshot hashes (read-only) |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-29-01 | Information Disclosure | ExecutionLogScope key constants | accept | Compile-time identifier strings only (param names), zero usings, no runtime data/PII. `ExecutionLogScope.cs` has no trust boundary. | closed |
| T-29-02 | Tampering | Messaging.Contracts dependency surface | accept | Pure POCO leaf, zero usings, no MassTransit/transitive dependency added — no new attack surface. | closed |
| T-29-03 | Tampering / Repudiation (log injection) | InboundExecutionScopeConsumeFilter scope values | accept | Typed `Guid` ids from `IExecutionCorrelated`, placed only as `.ToString()` values under fixed `ExecutionLogScope.*` keys (T-18-04), never interpolated into a template. No user-controlled string reaches the scope. | closed |
| T-29-04 | Information Disclosure | scope attributes serialized to ES | accept | Id-set is GUID-only by contract; `Guid.Empty` skipped (`!= Guid.Empty`). No PII/secret newly placed into attributes. | closed |
| T-29-05 | Information Disclosure | ProcessorId / ExecutionId / EntryId attributes | accept | All server-minted/resolved GUIDs; `ProcessorId` null-safe (nothing before identity, never `Guid.Empty`). No sensitive data. | closed |
| T-29-06 | Tampering / Repudiation (log injection) | nested-scope values + enricher value | accept | Typed `Guid` placed as scope/attribute values under fixed keys (T-18-04), never interpolated; not user-controlled. | closed |
| T-29-07 | Denial of Service | ProcessorIdLogEnricher hot logging path | mitigate | **Verified:** null-guard `if (context.Id is not { } id) return;` (OnEnd returns immediately on null, cannot throw); non-blocking, allocation-light, no I/O/locks. Null-case test `ProcessorIdEnricherTests.Case_B_Id_Null_Appends_Nothing_No_Exception_No_GuidEmpty`. | closed |
| T-29-08 | Tampering (log injection via job-data workflowId) | WorkflowFireJob scope WorkflowId value | accept | `Guid.TryParse`-validated with early return on failure BEFORE the scope opens; only a valid `Guid` reaches the scope as a value under a fixed key (T-18-04). | closed |
| T-29-09 | Information Disclosure | CorrelationId / WorkflowId attributes | accept | Both GUIDs (server-minted / type-validated); no PII/secret. | closed |
| T-29-10 | Information Disclosure | ES scope-proof query | accept | Reads back a GUID `WorkflowId` the test itself seeded; read-only; ES is internal telemetry. No data exposure. | closed |
| T-29-11 | Denial of Service / state leak | close-gate triple-SHA (E2E added ES read) | mitigate | **Verified:** E2E adds ONLY a `PollEsForLog` read; teardown lists (`L2KeysToCleanup` / `ParentIndexMembersToSrem`) unchanged; ES docs are append-only telemetry, not in the triple-SHA. `phase-29-close.ps1` captures + asserts all three SHAs BEFORE==AFTER, no `FLUSHDB`. Empirically: close gate `GATE_EXIT=0`, triple-SHA HELD. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-29-01 | T-29-01 | Constants leaf — static identifier strings, no runtime trust boundary, no data/PII. | gsd-security-auditor | 2026-06-02 |
| AR-29-02 | T-29-02 | Pure POCO leaf, no dependency/attack surface added. | gsd-security-auditor | 2026-06-02 |
| AR-29-03 | T-29-03 | Typed-`Guid` scope values under fixed keys (T-18-04); no user-controlled string; log-forging surface nil. | gsd-security-auditor | 2026-06-02 |
| AR-29-04 | T-29-04 | GUID-only id-set, `Guid.Empty` skipped; no PII reaches ES. | gsd-security-auditor | 2026-06-02 |
| AR-29-05 | T-29-05 | Server-minted/resolved GUIDs; null-safe ProcessorId. | gsd-security-auditor | 2026-06-02 |
| AR-29-06 | T-29-06 | Typed-`Guid` values under fixed keys (T-18-04), never interpolated. | gsd-security-auditor | 2026-06-02 |
| AR-29-08 | T-29-08 | `Guid.TryParse`-validated upstream of the scope; unparseable input rejected at early return. | gsd-security-auditor | 2026-06-02 |
| AR-29-09 | T-29-09 | CorrelationId/WorkflowId are GUIDs; no PII/secret. | gsd-security-auditor | 2026-06-02 |
| AR-29-10 | T-29-10 | Read-only ES query of a self-seeded GUID; internal telemetry. | gsd-security-auditor | 2026-06-02 |

*Accepted risks do not resurface in future audit runs. T-29-07 and T-29-11 are `mitigate` (control verified in code), not accepted risks.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-02 | 11 | 11 | 0 | gsd-security-auditor (Phase 29 secure-phase) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-02
