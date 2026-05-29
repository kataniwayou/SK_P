---
phase: 14
slug: validation-gates-dfs-schema-edge-payload-config-schema
status: verified
threats_open: 0
asvs_level: 1
created: 2026-05-29
---

# Phase 14 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| HTTP client → orchestration/start | Untrusted WorkflowIds + (downstream) untrusted Payload/Definition cross here | WorkflowId Guids, Assignment payloads |
| Stored Schema.Definition → JsonSchema.Net evaluation | A persisted Definition with an external `$ref` could trigger an outbound fetch | JSON Schema documents (potential SSRF `$ref`) |
| Untrusted workflow graph → validation gates | Crafted cyclic/deep graphs (stack exhaustion), incompatible schema edges, pathological payloads | In-memory L1 `WorkflowGraphSnapshot` |
| Domain exception → IExceptionHandler chain → HTTP 422 body | Error body must not leak internals | Problem Details body (gate + offending) |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-14-01 | Info Disclosure / Tampering (SSRF) | JsonSchemaConfig / SchemaDtoValidator | mitigate | `SchemaRegistry.Global.Fetch = (_,_) => null` in `JsonSchemaConfig` static ctor (`JsonSchemaConfig.cs:27`); both Schema validators pass `JsonSchemaConfig.DefaultOptions` (`SchemaDtoValidator.cs:48,98`) firing the cctor before evaluation. Guard: `ErrorMappingFacts.cs:203-204` `<500ms` assertion. | closed |
| T-14-02 | Info Disclosure | OrchestrationValidationExceptionHandler 422 body | mitigate | Handler sets only Status/Title/Detail/errors (`OrchestrationValidationExceptionHandler.cs:38-47`); no `correlationId`/`instance` assignment; offending records carry only Guids + flattened schema messages. | closed |
| T-14-03 | Spoofing (wrong-handler-claims) | IExceptionHandler chain ordering | mitigate | Fast-bail `is not OrchestrationValidationException → return false` (`OrchestrationValidationExceptionHandler.cs:34`); `AddBaseApiFallbackHandler()` registered after `AddAppFeatures()` (`Program.cs:9`, `ErrorHandlingServiceCollectionExtensions.cs:52-56`) keeps FallbackExceptionHandler last-walked. | closed |
| T-14-04 | DoS (StackOverflow) | CycleDetector traversal | mitigate | Explicit `Stack<>` iterative DFS, no recursion (`CycleDetector.cs:77-138`); `RunDfs`/`Push` are non-self-calling static helpers. | closed |
| T-14-05 | DoS (false-reject) / correctness | two-set DFS | mitigate | Two-set discriminator `onStack` + `fullyVisited` (`CycleDetector.cs:47,78`) prevents diamond/fan-in false-positives; guard `DiamondDag_Passes_NoFalsePositiveCycle` (`CycleDetectionFacts.cs:146`). | closed |
| T-14-06 | Info Disclosure | cycle/missingStep 422 offending | accept | offending carries only stepId Guids (`CycleOffending`, `MissingStepOffending`) — consistent with NotFoundException id-only disclosure policy. | closed |
| T-14-07 | Tampering (incompatible-pipeline) | SchemaEdgeValidator | mitigate | Strict Guid equality over EVERY `NextStepIds` entry (`SchemaEdgeValidator.cs:33-59`); mismatch → 422 before L2 projection. | closed |
| T-14-08 | Info Disclosure | schemaEdge 422 offending | accept | `SchemaEdgeOffending(parentStepId, childStepId)` carries only two step Guids — no schema bodies/internals. | closed |
| T-14-09 | Info Disclosure / Tampering (SSRF) | PayloadConfigSchemaValidator | mitigate | Evaluates via `JsonSchemaConfig.DefaultOptions` (`PayloadConfigSchemaValidator.cs:84`) — no outbound `$ref` fetch (same SSRF lockdown as Schema validators). | closed |
| T-14-10 | DoS (unbounded re-parse) | per-Start parse cache | mitigate | Per-Start LOCAL `Dictionary<Guid,JsonSchema>` keyed by Schema.Id (`PayloadConfigSchemaValidator.cs:32`) parses each schema at most once; local-only (zero `private` Dictionary fields confirmed). | closed |
| T-14-11 | Info Disclosure | payloadConfigSchema 422 offending | accept | `PayloadConfigSchemaOffending(assignmentId, errors)` carries assignment Guid + flattened JsonSchema.Net conformance messages, not raw payload/internals. | closed |
| T-14-12 | Tampering (gate bypass) | gate ordering in StartAsync | mitigate | Locked pipeline order existence → snapshot → cycle → schema-edge → payload → writer (`OrchestrationService.cs:77-83`); `ValidationOrderFacts.cs:210,251` short-circuit assertions fail the build on reordering. | closed |
| T-14-13 | DoS (resource leak) | L1 snapshot on failure path | mitigate | `using var snapshot = await _loader.LoadL1Async(...)` (`OrchestrationService.cs:78`) disposes on all paths; `L1Cleanup_RunsOnValidationFailurePath` (`ValidationOrderFacts.cs:296,336`) asserts disposal on the 422 path. | closed |
| T-14-14 | Info Disclosure | regression of error-body shape | accept | `ErrorMappingFacts` + `StartOrchestrationFacts` lock body shapes; re-ran GREEN in the 194/194 full-suite sweep (14-05-SUMMARY.md). | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-14-01 | T-14-06 | cycle/missingStep 422 offending carries only stepId Guids — consistent with existing NotFoundException id-only disclosure policy; low risk. | User (secure-phase) | 2026-05-29 |
| AR-14-02 | T-14-08 | schemaEdge 422 offending carries only `(parentStepId, childStepId)` Guids — no schema bodies/internals; low risk. | User (secure-phase) | 2026-05-29 |
| AR-14-03 | T-14-11 | payloadConfigSchema 422 offending carries `assignmentId` + flattened JsonSchema.Net conformance text (instance-location + keyword), not raw payload/internals; low risk. | User (secure-phase) | 2026-05-29 |
| AR-14-04 | T-14-14 | error-body-shape regression guarded by existing ErrorMappingFacts + StartOrchestrationFacts re-running GREEN; acceptable. | User (secure-phase) | 2026-05-29 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-05-29 | 14 | 14 | 0 | gsd-security-auditor (sonnet) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-05-29
