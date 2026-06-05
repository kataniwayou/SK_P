---
phase: 31
slug: idempotent-execution-exactly-once-effect
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-05
---

# Phase 31 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| internal framework messaging (Plan 01) | `MessageIdentity` hashes server-minted Guids + server-produced output blobs into 64-hex content addresses. No external/auth/PII boundary introduced. | Server-derived ids only (non-sensitive). |
| client/process wire — existing (Plan 02) | `EntryStepDispatch` / `ExecutionResult` envelopes cross the MassTransit/RabbitMQ boundary between orchestrator and processor. The EntryId field type changed Guid→string; transport/lifecycle unchanged. No NEW boundary. | Envelope with server-derived EntryId (no secrets). |
| processor inbound dispatch — existing (Plan 03) | `EntryStepDispatch` arrives off `queue:{processorId}`; dedup reads/writes Redis `flag[H]` / `data[hash]` — internal L2. No external boundary. | Internal L2 keys/values (server-derived). |
| orchestrator inbound result — existing (Plan 04) | `ExecutionResult` arrives off `orchestrator-result`; dedup reads `flag[m.H]` + manifest `data[m.EntryId]` — internal L2. No external boundary. | Internal L2 keys/values (server-derived). |
| config → retry budget — existing (Plan 05) | The `Retry` appsettings section feeds `UseMessageRetry`. Operator-owned (not network-untrusted); additive only. No new boundary. | Operator config (intra-cluster). |
| live compose stack — existing (Plan 06) | E2E drives the real orchestrator↔processor round-trip over RabbitMQ/Redis/Postgres/OTLP. Test-only re-publish induces a duplicate inside the existing boundary; no new external surface. | Test-induced duplicate with identical server-derived identity. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-31-01 | Tampering | MessageIdentity canonical string (delimiter injection) | mitigate | Reserved U+001F unit-separator (`Sep`) that cannot appear in Guid "D" or lowercase-hex text; fixed field order in one helper. Verified: `src/Messaging.Contracts/Hashing/MessageIdentity.cs:20`. | closed |
| T-31-02 | Information disclosure | hash logging | mitigate | Helper does no logging (no log calls in the file); callers place H/EntryId only as scope VALUES under fixed keys (Plans 03/04). Verified: `MessageIdentity.cs` (grep-confirmed zero log calls). | closed |
| T-31-03 | Spoofing/Tampering | SHA-256 digest construction | accept | SHA-256 used for content-addressing/dedup identity, NOT security; BCL `SHA256` only, never hand-rolled. Verified: `MessageIdentity.cs:24`. See Accepted Risks (AR-31-01). | closed |
| T-31-04 | Tampering | EntryId as a Redis key segment | mitigate | EntryId is always server-derived 64-hex (or the reserved `""` sentinel, never used as a content key); `^skp:data:[a-f0-9]{64}$` / `^skp:flag:[a-f0-9]{64}$` shape pinned by golden tests. Verified: `src/.../L2ProjectionKeys.cs:41`, `tests/BaseApi.Tests/Contracts/HashHelperGoldenFacts.cs:103-104`. | closed |
| T-31-05 | Information disclosure | EntryId in the execution-scope filter | mitigate | EntryId placed only as a scope VALUE under the fixed `ExecutionLogScope.EntryId` key, never interpolated into a template (T-18-04 precedent preserved through the Guid→string change). Verified: `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:33`. | closed |
| T-31-06 | Tampering | mixed-version deployment (old Guid vs new string) | accept | Both processes reference the single `Messaging.Contracts` leaf and are rebuilt/redeployed together; no rolling upgrade across the contract change (SUMMARY-02: clean build + 426 tests green). See Accepted Risks (AR-31-02). | closed |
| T-31-07 | Tampering | manifest content-address (delimiter/forgery) | mitigate | Manifest is a server-serialized JSON array of server-derived 64-hex; `hash(manifest)` over the exact wire bytes via the one helper; never user input. Verified: `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:194-195`. | closed |
| T-31-08 | Information disclosure | H/EntryId in processor logs | mitigate | Ids emitted only as scope VALUES under fixed `ExecutionLogScope` keys, never in a message template. The input-absent business-Failed log was remediated this audit to drop `{EntryId}` from the template (EntryId remains a scope value via the inbound execution-scope filter). H is never in any log template in `src/`. Verified: `EntryStepDispatchConsumer.cs:99-104` (post-fix), `InboundExecutionScopeConsumeFilter.cs:33`. | closed |
| T-31-09 | Denial of service | flag[H] false-return treated as retryable error | mitigate | `When.Exists` false return is the DESIGNED residual (Pending lost); code does NOT throw on it; downstream H dedup absorbs the residual, no retry storm. Verified: `EntryStepDispatchConsumer.cs:218,223`. | closed |
| T-31-10 | Input validation | output-schema validation bypass | mitigate | Schema validation runs on each result DATA blob pre-write; the manifest is an internal pointer list, never schema-validated (D-09, V5). Verified: `EntryStepDispatchConsumer.cs:147-155` (validate) vs `:194-199` (manifest written unvalidated). | closed |
| T-31-11 | Tampering | manifest deserialize (untrusted shape) | mitigate | Manifest at `data[m.EntryId]` was server-written by the processor; deserialize as `string[]` with `?? Array.Empty` guard — a missing/garbled key degrades to zero fan-out, never a throw. Verified: `src/Orchestrator/Consumers/ResultConsumer.cs:89-93`. | closed |
| T-31-12 | Spoofing | child H forgery via executionId | mitigate | Child H excludes executionId; a redelivery regenerating executionId yields the SAME H → deduped; executionId cannot bypass dedup. Verified: `MessageIdentity.cs:36` (`ComputeH`, no executionId param), `HashHelperGoldenFacts.cs:80-96` (reflection proves structural exclusion). | closed |
| T-31-13 | Information disclosure | H/EntryId in orchestrator logs | mitigate | Scope-value-under-fixed-key convention preserved; no id interpolated into a template; the single log uses `{WorkflowId}`/`{StepId}` only. Verified: `ResultConsumer.cs:79`. | closed |
| T-31-14 | Denial of service | flag[H] false-return retry storm | mitigate | `When.Exists` false-return not treated as an error (D-07); graceful business-acks unchanged. Verified: `ResultConsumer.cs:112,115`. | closed |
| T-31-15 | Denial of service | unbounded/huge retry Limit from config | accept | Operator-owned config; default is the proven `Immediate(3)`; a misconfigured large Limit is an operational concern, not an attack surface (intra-cluster, no external config injection path). Verified: `src/Messaging.Contracts/Configuration/RetryOptions.cs:10`, `src/Orchestrator/Program.cs:29`. See Accepted Risks (AR-31-03). | closed |
| T-31-16 | Tampering | retry desync between UseMessageRetry and the Phase-32 final-attempt check | mitigate | Single shared `RetryOptions.Limit` is the ONE source all sites read (D-10); no hard-coded `Immediate(3)` survives in production. Verified: `ResultConsumerDefinition.cs:41`, `StartOrchestrationConsumerDefinition.cs:40`, `StopOrchestrationConsumerDefinition.cs:35`, `ProcessorStartupOrchestrator.cs:151,154`. | closed |
| T-31-17 | Tampering | induced-duplicate test re-publish | accept | Test-only mechanism inside the harness; re-publishes a message with the SAME server-derived identity — it exercises the dedup, it does not weaken it. Verified: `tests/BaseApi.Tests/Orchestrator/IdempotentExactlyOnceE2ETests.cs:154-177`. See Accepted Risks (AR-31-04). | closed |
| T-31-18 | Information disclosure | ES log assertions on CorrelationId/H | mitigate | The E2E queries ES on ids already emitted only as scope VALUES under fixed keys (T-18-04); the test reads them, it does not introduce new template interpolation. Verified: `IdempotentExactlyOnceE2ETests.cs:283-296`. | closed |
| T-31-19 | Denial of service | per-fire key accumulation | mitigate | L2 TTL outlives the slowest fire+redelivery; the close-gate net-zero teardown scan-cleans both `skp:data:*` and `skp:flag:*` (D-12) so keys do not accumulate across gate runs. Verified: `IdempotentExactlyOnceE2ETests.cs:213-218`, `scripts/phase-31-close.ps1` (unfiltered `redis-cli --scan`). | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-31-01 | T-31-03 | SHA-256 is used for content-addressing/dedup identity, not for authentication or integrity-against-an-adversary; collision-resistance is the only relied-on property. BCL `SHA256` only, never hand-rolled. | User (operator) | 2026-06-05 |
| AR-31-02 | T-31-06 | Both orchestrator and processor reference the single `Messaging.Contracts` leaf and are rebuilt/redeployed together; the milestone has no rolling-upgrade path across the Guid→string contract change. | User (operator) | 2026-06-05 |
| AR-31-03 | T-31-15 | The retry `Limit` is operator-owned intra-cluster config with a proven `Immediate(3)` default; there is no external config-injection path, so a misconfigured large value is an operational concern, not an attack surface. | User (operator) | 2026-06-05 |
| AR-31-04 | T-31-17 | The induced-duplicate re-publish is a test-only harness mechanism that re-sends a message with the SAME server-derived identity; it exercises the dedup gate and cannot weaken production behaviour. | User (operator) | 2026-06-05 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-05 | 19 | 19 | 0 | /gsd-secure-phase (State B from artifacts; gsd-security-auditor verified mitigations against implementation files; T-31-08 remediated in-audit — `{EntryId}` removed from the input-absent log template, BaseProcessor.Core rebuilt clean) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-05
