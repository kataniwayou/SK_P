---
phase: 25
slug: shared-contracts-webapi-responders
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-01
---

# Phase 25 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| (none new in 25-01) | Plan 25-01 adds only leaf contract types + tests. No new code path consumes untrusted input; no new external surface. The relocated `ProcessorProjection` deserializes existing Redis L2 values (already-trusted boundary, unchanged by the move — field-name-keyed JSON byte-identical). | n/a (passive DTOs) |
| bus → WebApi responder | A `GetProcessorBySourceHash` / `GetSchemaDefinition` message crosses from the internal message bus into the WebApi process. **NO new HTTP surface** — responders are bus-only. | SourceHash string / SchemaId Guid (non-sensitive) |
| responder → existing read service | The consumers call the EXISTING `ProcessorService` / `SchemaService` read paths (unchanged, parameterized EF Core queries — single indexed read / PK lookup). | schema Ids / schema Definition JSON (non-secret contract data) |

The bus is an internal trust boundary (RabbitMQ inside the compose network), not an external surface. The v1 CRUD auth posture is untouched (no HTTP endpoint added this phase).

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-25-01-01 | Tampering | `ProcessorProjection` STJ field mapping (relocation could silently drop a `[property: JsonPropertyName]` target → mis-deserialize L2 values) | mitigate | Verbatim lift preserving all three `[property: JsonPropertyName]` targets byte-identical (`src/Messaging.Contracts/Projections/ProcessorProjection.cs:14-17`); round-trip pins `ProcessorProjection_Serializes_Null_InputDefinition_With_Exact_Field_Name` and `ProcessorProjection_RoundTrips_By_Value` (`tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs:107,164`) assert exact field names + by-value equality against the moved leaf type. **Auditor-verified.** | closed |
| T-25-01-02 | Information disclosure | Leaf contract records (`ProcessorIdentityFound` exposes schema Ids) | accept | Passive type definitions; no behavior, no surface in 25-01. Actual exposure classified under T-25-02-01. | closed |
| T-25-01-03 | Elevation of privilege | `internal`→`public` visibility widening of `ProcessorProjection` | accept | Intentional per CONTRACT-01 / D-01 (single shared type). Public visibility of a passive DTO carries no privilege; consumed only intra-solution. | closed |
| T-25-02-01 | Information disclosure | `ProcessorIdentityFound` (`Id`, `InputSchemaId?`, `OutputSchemaId?`, `ConfigSchemaId?`) + `SchemaDefinitionFound.Definition` returned over the bus | accept | Responses carry exactly what the Phase 26 processor needs to resolve identity/schemas and no more — a direct field projection of `ProcessorReadDto` / `SchemaReadDto.Definition` (no secrets, no PII; schema definitions are non-sensitive contract JSON). Internal bus boundary. | closed |
| T-25-02-02 | Information disclosure (enumeration) | not-found response leaking existence/enumeration info | accept | `ProcessorIdentityNotFound` / `SchemaDefinitionNotFound` echo only the requested key (SourceHash / SchemaId) the caller already holds — they reveal nothing beyond "no row for the key you sent." Intended D-04 dual-response signal for the Phase 26 retry loop; no broader enumeration than a single-key probe. | closed |
| T-25-02-03 | Denial of service | unbounded request handling on the two responder endpoints | accept | Each request is a single indexed read (`SourceHash` lookup / PK `GetByIdAsync`) over EF Core; no fan-out, no unbounded allocation. Bus is internal (no external client can flood it). Responder-side retry/throttle deliberately out of scope (client retry is Phase 26). | closed |
| T-25-02-04 | Tampering / Elevation of privilege (integrity) | the Core→Service/Console dependency firewall and the Degraded health cap are integrity invariants the responder-hook addition could breach | mitigate | (a) `src/BaseApi.Core/BaseApi.Core.csproj:112` references only `Messaging.Contracts` (no `BaseApi.Service` / `BaseConsole.Core`); hook signatures (`src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:63-66`) are MassTransit-interface-typed only; concrete consumers named solely in `src/BaseApi.Service/Composition/ResponderMessaging.cs:33-34`. (b) `MinimalFailureStatus = HealthStatus.Degraded` (`MessagingServiceCollectionExtensions.cs:80`) intact. (c) `tests/BaseApi.Tests/Messaging/BaseApiCoreFirewallTests.cs:49,56,63` enforces no-forbidden-reference via `GetReferencedAssemblies()` reflection. **Auditor-verified.** | closed |
| T-25-02-05 | Spoofing / Repudiation | bus-message origin authenticity | accept | Out of scope this phase — the existing RabbitMQ transport trust model is unchanged; no new auth surface. The CRUD auth surface is untouched. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-25-01 | T-25-01-02 | Leaf records are passive type definitions; no surface in 25-01 (exposure governed by T-25-02-01). | Phase plan (locked) | 2026-06-01 |
| AR-25-02 | T-25-01-03 | Intentional `public` widening of a passive DTO per CONTRACT-01 / D-01; no privilege conferred; intra-solution only. | Phase plan (locked) | 2026-06-01 |
| AR-25-03 | T-25-02-01 | Responses are minimal field projections (schema Ids + non-sensitive contract JSON) over an internal RabbitMQ boundary; no secrets/PII. | Phase plan (locked) | 2026-06-01 |
| AR-25-04 | T-25-02-02 | Not-found responses echo only the caller's own key; single-key probe, no broader enumeration. Intended D-04 retry signal. | Phase plan (locked) | 2026-06-01 |
| AR-25-05 | T-25-02-03 | Single indexed EF read per request; no fan-out/amplification; bus is internal. Client retry/throttle deferred to Phase 26. | Phase plan (locked) | 2026-06-01 |
| AR-25-06 | T-25-02-05 | Existing RabbitMQ transport trust model unchanged; no new auth surface added this phase. | Phase plan (locked) | 2026-06-01 |

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-01 | 8 | 8 | 0 | gsd-security-auditor (sonnet) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-01
