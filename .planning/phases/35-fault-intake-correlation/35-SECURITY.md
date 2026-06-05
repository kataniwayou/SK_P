---
phase: 35
slug: fault-intake-correlation
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-05
---

# Phase 35 — fault-intake-correlation Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| (none new — Plan 01) | Pure internal refactor of a POCO helper and a consume-filter call site. No new trust boundary, no external input, no auth/PII surface. | n/a |
| RabbitMQ broker → Keeper consumer (Plans 02/03) | A `Fault<T>` envelope (inner message ids + `Exceptions[0]` exception text) crosses from the internal compose broker into Keeper's log-emitting consumer body. Internal broker network only (compose credentials, no external network input). | Structured ids (Guids), inner.H (string), ExceptionType + Message (strings) — no PII. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-35-01 | Tampering | `ExecutionLogScope.BuildState` scope values | mitigate | Ids placed as scope VALUES under fixed keys only — no string concatenation or interpolation into a message template (T-18-04 convention preserved byte-for-byte). `BuildState` body confirmed: 5 fixed keys, 4 `!= Guid.Empty` guards, 1 `!string.IsNullOrEmpty(ec.EntryId)` guard, no CorrelationId key. | closed |
| T-35-02 | Repudiation | base-library scope drift breaking correlation across all consoles | mitigate | Refactor is byte-identical; `InboundExecutionScopeConsumeFilter` delegates to `ExecutionLogScope.BuildState(ec)` (single call, inline build removed). Regression-guarded by 6 enumerated scope test classes run BEFORE and AFTER (all GREEN). | closed |
| T-35-03 | Information Disclosure | (N/A — Plan 01) | accept | No new data logged in Plan 01 — the helper only relocates the existing 5-id scope build. CorrelationId/exception surfacing is a Wave-2 concern addressed in T-35-04/T-35-05. Documented in Accepted Risks Log. | closed |
| T-35-04 | Tampering / Repudiation (log injection) | "keeper fault intake" log in both consumers | mitigate | All ids, `inner.H`, and `ex.Message` are passed as STRUCTURED PARAMS under fixed template holes (`{FaultType}`, `{H}`, `{ExceptionType}`, `{ExceptionMessage}`) — never string-concatenated or interpolated. Template string is a fixed literal in both consumers. | closed |
| T-35-05 | Information Disclosure / DoS (storage) | exception text in the structured log | mitigate | Only `ExceptionType` + `Message` from `Exceptions[0]` are surfaced. `StackTrace` is NOT logged: `grep -cE StackTrace src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` == 0; `FaultExecutionResultConsumer.cs` == 0. Doc-comment mentions "stack frames are NOT logged" — this is a description, not a log call. | closed |
| T-35-06 | Repudiation (wrong correlationId hides fault from operators) | fault log `attributes.CorrelationId` | mitigate | Manual `BeginScope([CorrelationKeys.LogScope] = inner.CorrelationId.ToString())` exists in BOTH consumers (grep == 1 each), overriding the bus-wide filter's fresh-Guid fallback for `Fault<T>` envelopes. Asserted GREEN by the hermetic SC2 scope test (KeeperFaultConsumerScopeTests 3/3). | closed |
| T-35-07 | Denial of Service (poison/malformed envelope NPE) | `context.Message.Message` double-unwrap + `Exceptions[0]` read | mitigate | `Exceptions` read is nullable-safe (`is { Length: > 0 } exs ? exs[0] : null`) in BOTH consumers (grep == 1 each). `inner` is the verbatim framework-deserialized instance (no re-deserialize). Pure observe-then-ack with no side effect — no ack-before-effect data-loss path. | closed |
| T-35-08 | Repudiation | SC3 correlation assertion | mitigate | `KeeperFaultIntakeE2ETests` asserts `attributes.CorrelationId == dCorr` (the ORIGINAL tripped correlationId) + `attributes.StepId == stepId` via `PollEsForLog` on `service.name = "keeper"`. CorrelationId references == 8; StepId references == 9; `PollEsForLog` calls == 3 in the test file. | closed |
| T-35-09 | DoS (test residue / non-net-zero state) | run-minted `skp:*` + poison keys | mitigate | Net-zero teardown registers every run-minted `data:`/`flag:` + poison key via `L2KeysToCleanup`/`ParentIndexMembersToSrem` (grep == 14) and POSTs `/orchestration/stop` (grep == 1). Triple-SHA BEFORE==AFTER preserved. | closed |
| T-35-10 | Tampering (scope creep into Phase 36 topology) | the E2E test body | mitigate | Test builds NO DLQ-1/TTL/`keeper-dlq` topology. Scope-creep acceptance grep `x-message-ttl|x-dead-letter-exchange|keeper-dlq` across `KeeperFaultIntakeE2ETests.cs` == 0. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-35-01 | T-35-03 | Plan 01 is a pure internal refactor (ExecutionLogScope.BuildState extraction). No new data is logged; CorrelationId and exception surfacing are addressed in Plans 02/03 via T-35-04 and T-35-05. Zero new information-disclosure surface introduced. | gsd-security-auditor (Phase 35) | 2026-06-05 |

---

## Unregistered Threat Flags

No threat flags appeared in the SUMMARY files that lack a mapping to a registered threat ID.

Notable deviations recorded in the SUMMARYs that were evaluated and confirmed non-security-impacting:

- **35-01-SUMMARY:** The `CorrelationId` grep count in `ExecutionLogScope.cs` is 2 (doc-comment lines only) rather than the plan's expected 0. Both occurrences are XML doc text (`"CorrelationId is deliberately NOT here"` and `"no CorrelationId key"`). No `state[...CorrelationId...]` assignment exists anywhere in the file. The substantive security property (BuildState adds NO CorrelationId key to the scope dict) is verified — CLOSED for T-35-01.
- **35-02-SUMMARY:** `StackTrace` appeared in a doc-comment (`"the exception's stack frames are NOT logged"`) rather than a log call. `grep -cE StackTrace` of actual consumer code (not doc-comments) == 0 in both files — CLOSED for T-35-05.
- **35-02-SUMMARY:** `context.Message.Message` doc-comment mention counted 2 in the plan's grep but only 1 is the functional double-unwrap. Both the real double-unwrap (line 33/34 in each file) and the nullable-safe `Exceptions` guard are confirmed present — CLOSED for T-35-07.
- **35-03-SUMMARY:** SC3 live run is OPERATOR-PENDING (not yet observed against the rebuilt Keeper container). This is a test-verification deferral per the Phase-31..34 operator-gate precedent, not a code gap. T-35-08 evidence is the authored test's assertion structure; the live GREEN flips the full SC3 proof.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-05 | 10 | 10 | 0 | gsd-security-auditor (claude-sonnet-4-6) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-05
