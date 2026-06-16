---
phase: 71-orchestrator-recovery-pipeline
audited: 2026-06-16
asvs_level: 1
block_on: high
threats_total: 12
threats_closed: 12
threats_open: 0
status: SECURED
---

# Phase 71 — Orchestrator Recovery Pipeline: Security Audit

**Phase:** 71 — Orchestrator Recovery Pipeline
**Audited:** 2026-06-16
**ASVS Level:** 1
**Threats Closed:** 12/12

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-71-01 | Tampering | accept | CLOSED | Accepted risk (see Accepted Risks log below): operational mitigation via drained-queue deploy per close-gate net-zero protocol; no code surface. |
| T-71-02 | (none) | accept | CLOSED | Accepted risk: type-rename only, no new external surface, no new key or deletion path. |
| T-71-03 | Information Disclosure | accept | CLOSED | Accepted risk: OrchestratorReinject.ErrorMessage/CancellationMessage on internal broker only; same posture as existing StepFailed/StepCancelled on orchestrator-result. |
| T-71-04 | (none) | accept | CLOSED | Accepted risk: OrchestratorInject/OrchestratorReinject are inert DTOs; no delete/write capability added. |
| T-71-05 | Tampering | mitigate | CLOSED | `OrchestratorForwardWrite` is a `private const string` (line 70); KEYS[1..3]/ARGV[1..4] are parameterized; no orchestrator or user data is concatenated into the script text. `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:70-75` |
| T-71-06 | DoS (own state) | mitigate | CLOSED | `TryParseTuple` returns `false` on null/non-JSON/JsonException (lines 316-338); line 214 guards `!TryParseTuple(...) \|\| tuple.NewEntryId == Guid.Empty` and skips the slot — a bad or retired slot cannot abort the RECOVERY pass. `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:214,316-338` |
| T-71-07 | Availability (resource leak) | mitigate | CLOSED | (1) Lua line 74: `redis.call('SET', KEYS[2], v, 'PX', ARGV[4])` — the copy leg carries the data TTL inline via ARGV[4] (computed in C# as `executionDataTtl`). (2) WR-02 fix applied: `OrchestratorInjectConsumer.HandleAsync` line 42 calls `StringSetAsync(..., _executionDataTtl)` with the bounded TTL from `RecoveryOptions.ExecutionDataTtlSeconds`. No immortal-key path exists. `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:74`; `src/Keeper/Recovery/OrchestratorInjectConsumer.cs:30,42` |
| T-71-08 | Elevation/Repudiation | mitigate | CLOSED | `KeyDeleteAsync` appears exactly once in the pipeline file, at line 264 inside `DeleteTerminalAsync`. The FORWARD escalation leg (`SendKeeper(BuildInject(...))`) and RECOVERY escalation leg (`SendKeeper(BuildReinject(...))`) contain zero `KeyDeleteAsync` calls (grep-confirmed). `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs:264` (sole occurrence) |
| T-71-09 | Elevation (over-deletion) | mitigate | CLOSED | (1) Grep confirms zero `KeyDeleteAsync` in both `OrchestratorInjectConsumer.cs` and `OrchestratorReinjectConsumer.cs`. (2) `KeeperDeleteInvariantFacts.cs` lines 171-173 (`OrchestratorInjectConsumer_never_deletes`) and lines 206-208 (`OrchestratorReinjectConsumer_never_deletes`) assert `DidNotReceive()` on BOTH the `RedisKey` and `RedisKey[]` overloads, each with a positive co-assertion (single `EntryStepDispatch` or `StepCompleted` send) so a silent no-op cannot satisfy the guard. `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs:136-208` |
| T-71-10 | Tampering/Spoofing | mitigate | CLOSED | `OrchestratorReinjectConsumer.HandleAsync` line 31: the only status branch is `m.Outcome switch` covering all four `StepOutcome` values plus an exhaustive default `_ => new StepFailed(...)`. No reflection, no polymorphic deserialization; an unknown outcome degrades to safe `StepFailed`. `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs:31-61` |
| T-71-11 | DoS | mitigate | CLOSED | `Keeper/Program.cs` lines 72-73: `OrchestratorReinjectConsumer` and `OrchestratorInjectConsumer` are both registered with `.ExcludeFromConfigureEndpoints()`. `RecoveryEndpointBinder.cs` lines 65-66 bind them inside the existing `ConnectReceiveEndpoint(KeeperQueues.Recovery, ...)` callback — same endpoint, no new queue. `src/Keeper/Program.cs:72-73`; `src/Keeper/Recovery/RecoveryEndpointBinder.cs:65-66` |
| T-71-12 | Information Disclosure | accept | CLOSED | Accepted risk: same as T-71-03; OrchestratorReinject diagnostic strings are on an internal broker only; no new exposure. |

---

## Accepted Risks Log

| Threat ID | Category | Component | Rationale |
|-----------|----------|-----------|-----------|
| T-71-01 | Tampering | MassTransit URN on keeper-recovery at deploy | In-flight old-type messages (KeeperInject/KeeperReinject) would not bind to the renamed consumers at deploy time. Mitigation is operational: deploy on a DRAINED keeper-recovery queue per the project close-gate net-zero protocol. No code change possible; same-deploy contract+consumer rename has no intra-deploy version skew. Accepted. |
| T-71-02 | (none) | Contract type rename only | No new external surface, no new key, no new deletion path introduced. KeeperDelete/DeleteConsumer and the delete invariant are unchanged. Not a threat. Accepted. |
| T-71-03 | Information Disclosure | OrchestratorReinject.ErrorMessage / CancellationMessage on the broker | Diagnostic strings flow on an internal broker only. Identical posture to the pre-existing StepFailed.ErrorMessage / StepCancelled.CancellationMessage already on orchestrator-result. No external consumer. No new exposure. Accepted. |
| T-71-04 | (none) | OrchestratorInject / OrchestratorReinject are inert DTOs | The two new contract records add no key-deletion or key-write capability. Deletion authority lives only in KeeperDelete/the pipeline cleanup tail (asserted in T-71-09). Not a threat. Accepted. |
| T-71-12 | Information Disclosure | ErrorMessage / CancellationMessage on OrchestratorReinject | Duplicate of T-71-03 (same strings, same broker, same posture). Accepted. |

---

## Unregistered Threat Flags

None. The 71-03-SUMMARY.md `## Threat Flags` section explicitly states: "None — the pipeline's atomic Lua is a compile-time `const` with parameterized KEYS/ARGV (T-71-05 mitigated: no orchestrator data concatenated into the script); the copy leg carries the data TTL inline (T-71-07); the only deleter is the gated cleanup tail (T-71-08); RECOVERY parses slots tolerantly (T-71-06). No new network/auth/file surface introduced."

---

## Notes

- WR-02 from the code review (immortal-key leak in `OrchestratorInjectConsumer`) was fixed before this audit in commit `4c64553`. The TTL is now sourced from `Keeper.RecoveryOptions.ExecutionDataTtlSeconds` (not `OrchestratorRecoveryOptions`, which would violate the Keeper dependency firewall T-34-01).
- WR-01 from the code review (`BuildReinject` EntryId reset) was applied in commit `33bbf6f`. The fix is visible at `OrchestratorResultPipeline.cs:364`: `EntryId = m is StepCompleted ? m.EntryId : Guid.Empty`.
- The 13 pre-existing parallel-run flaky orchestrator tests identified in 71-03-SUMMARY.md are a harness/Quartz timing issue, NOT a regression from Phase 71, and are out of scope for this audit.
