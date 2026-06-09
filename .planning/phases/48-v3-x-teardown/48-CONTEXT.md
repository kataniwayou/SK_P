# Phase 48: v3.x Teardown - Context

**Gathered:** 2026-06-09
**Status:** Ready for planning

<domain>
## Phase Boundary

Remove the v3.x **reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path** and the **`keeper-dlq`** queue (RETIRE-03), perform a **final remnant sweep** verifying RETIRE-01 (`H`/`flag[H]`/CAS dedup) and RETIRE-02 (content-addressing + result manifest + N×M fan-out) — both already landed in Phase 43 — left no dead surface, and leave the solution **buildable and green on the v4 path alone**.

**Pure deletion. No new capability.** The "what to remove" list is pre-enumerated in 43-CONTEXT D-14 (the reactive artifacts kept "dark-but-compiling" since Phase 43). This discussion locks the *HOW* decisions only.

**v4 keep-set (must survive untouched):** the 5-state `keeper-recovery` consumers (`UPDATE`/`REINJECT`/`INJECT`/`DELETE`/`CLEANUP`), the BIT health gate + `L2HealthGate`, global `PauseAll`/`ResumeAll`, the consolidated `skp-dlq-1` / `_DLQ1`, and the per-item `TypedResultConsumer<T>` orchestrator consume.

</domain>

<decisions>
## Implementation Decisions

### Test disposition (D-01)
- **D-01:** **Delete the orphaned reactive-only test classes, then add a Phase-48 negative-guard fact set proving the absence.** When the reactive consumers/handler are deleted, the reactive-only test classes stop compiling and must go: `KeeperFaultConsumerScopeTests`, `KeeperRecoverCapTests` (per-`H`/4-tuple attempt cap), `KeeperRoundRobinTests` (the `keeper-fault-recovery` durable round-robin), and the reactive recover-loop portions of `KeeperProbeLoopTests`. After deleting them, add a small `[Trait("Phase","48")]` guard fact set asserting the negatives:
  - Keeper registers **no** `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` consumer.
  - No `keeper-fault-recovery` endpoint is wired.
  - No `keeper-dlq` (`KeeperQueues.DeadLetter`) topology const is reachable on the execution path.
  - Mirror the Phase-47 `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` reflection/source-scan pattern so the teardown is **self-verifying and regression-proof** (a future edit reintroducing a `Fault<T>` bind fails the guard).
- **Rejected:** delete-only (nothing enforces the path stays gone); convert-in-place (mixes a heavy E2E rig — e.g. the 819-line spike — into a trivial absence assertion, awkward fit).

### Remnant-sweep scope (D-02)
- **D-02:** **Exhaustive orphan hunt.** After removing the named RETIRE-03 artifacts, hunt and remove every now-dead dependent so zero dead surface remains (SC-4 literal: "no dead `Ignore<>`/binding/key remnants"):
  - **Dead config knobs** — `RecoveryOptions` and any `ProbeOptions`/`BackupOptions` members read *only* by the reactive path (keep members the v4 BIT gate / 5-state recovery still read).
  - **Dead metrics** — `KeeperMetrics` fault-intake / recover-attempt counters tied to the reactive path.
  - **Retired queue consts** — `KeeperQueues.FaultRecovery` (`keeper-fault-recovery`) and `KeeperQueues.DeadLetter` (`keeper-dlq`); keep `KeeperQueues.Recovery` (`keeper-recovery`, v4).
  - **Dead `Ignore<>` / bus bindings** registered for the reactive consumers.
  - **Stale comments / docstrings** referencing `H` / manifest / `Fault<T>` / the reactive path.
  - Verify RETIRE-01/02 symbols (`MessageIdentity`, `flag[H]`, result manifest / N×M fan-out) are absent from the execution-path source.
- **Rejected:** named-artifacts-only (leaves dead `RecoveryOptions`/fault-counter surface SC-4 targets); sweep-but-keep-config/metrics (dead config nothing reads is exactly the "remnant" SC-4 targets).

### Close-gate depth (D-03)
- **D-03:** **Hermetic suite GREEN (×3 consecutive) + `dotnet build SK_P.sln` Release AND Debug at 0 warnings — exactly SC-4, nothing more.** This is a pure-deletion phase with no new behavior to live-prove; **Phase 49 owns the real-stack live proof + final close gate.** **No** triple-SHA `psql`/`redis-cli`/`rabbitmqctl` infra-parity gate in Phase 48 (the queue-topology change makes `rabbitmqctl list_queues` deliberately NOT net-zero this phase).
- **Rejected:** add-triple-SHA-now (the deliberate queue delta defeats a clean net-zero); record-expected-delta-in-48 (the user opted to keep 48 to SC-4 only — the topology delta is a *Phase-49* baseline concern, captured in Deferred Ideas, not a 48 deliverable).

### Audit / reconciliation (D-04)
- **D-04:** **Full reconciliation** — three artifacts close the v4.0.0 retirement story end-to-end (this is the last RETIRE phase):
  1. **`48-TEARDOWN-AUDIT.md`** in the phase directory — a traceability ledger, one row per criterion mapping **RETIRE-01 / RETIRE-02 / RETIRE-03** and roadmap **SC-1..SC-4** → its named proving guard test / source-scan (matching the Phase-47 `47-DLQ-AUDIT.md` pattern). The verifier checks every row resolves to a real, green test/scan.
  2. **REQUIREMENTS.md** — mark **RETIRE-01, RETIRE-02, RETIRE-03** satisfied (RETIRE-01/02 verified-via-remnant-sweep; RETIRE-03 newly removed here).
  3. **Design-doc amendment** to `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` recording the reactive-path + `keeper-dlq` retirement (the additive-amendment pattern used in Phases 46/47).
- **Rejected:** audit-ledger-only (skips the doc amendment — leaves the LOCKED source-of-truth doc silent on the retirement); verification-report-only (loses the traceability ledger the prior RETIRE/RESIL phases established as the pattern).

### Claude's Discretion
- Exact namespace/file name of the Phase-48 negative-guard fact set (keep parity with `AtLeastOnceStructuralFacts` / the firewall-test reflection style).
- The precise removal ordering of the deletion within the phase (any order is acceptable so long as every intermediate commit still builds — the build-before-teardown invariant from 43-CONTEXT D-01/D-03 holds).
- `48-TEARDOWN-AUDIT.md` table columns/layout (follow the `47-DLQ-AUDIT.md` / VALIDATION.md traceability-table style).
- The precise wording of the design-doc retirement amendment.
- Exactly which `ProbeOptions`/`BackupOptions` members (if any) are reactive-only vs. shared — to be confirmed during research/planning against the live keep-set (`BitHealthLoop`, 5-state recovery, `BackupOptions.TtlDays`).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### v4.0.0 source of truth
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — LOCKED 2026-06-08; the v4 target-state design. **D-04 requires an additive amendment here** recording the reactive-path + `keeper-dlq` retirement. Amendment pattern: see the Phase-46 `Payload`-on-`KeeperReinject` and Phase-47 at-least-once (A16) amendments.

### What RETIRE-03 covers + what was kept dark (the removal list)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` §D-02, §D-13, §D-14 — the authoritative enumeration of the reactive artifacts kept "dark-but-compiling" in Phase 43 and explicitly slated for removal in Phase 48: `KeeperRecoveryHandler` (`<T>` rebound off the 4-tuple), the retargeted `Fault<EntryStepDispatch>` consumer + its definition, the reactive members of `L2ProbeRecovery`, and the `KeeperQueues.FaultRecovery`/`KeeperQueues.DeadLetter` consts.

### Prior teardown/verification precedent (patterns to mirror)
- `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-CONTEXT.md` §D-03 — the no-`keeper-dlq` source-scan that **deliberately excluded `src/Keeper/Recovery/KeeperRecoveryHandler.cs` "until Phase 48"** (now in scope); §D-01 — the `47-DLQ-AUDIT.md` ledger pattern D-04 mirrors.
- `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md` — the audit-ledger layout to follow.
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` — the reflection + source-scan negative-guard pattern the D-01 Phase-48 guard set mirrors.

### Requirements
- `.planning/REQUIREMENTS.md` — RETIRE-01 (L66), RETIRE-02 (L67), RETIRE-03 (L68); status table L117-119. D-04 marks all three satisfied.
- `.planning/ROADMAP.md` §"Phase 48: v3.x Teardown" — Goal + the four Success Criteria (SC-1 RETIRE-01 remnant-verify, SC-2 RETIRE-02 remnant-verify, SC-3 reactive path + `keeper-dlq` removed, SC-4 full suite green + clean build, no dead remnants).

</canonical_refs>

<code_context>
## Existing Code Insights

### To remove (reactive path — RETIRE-03)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `FaultEntryStepDispatchConsumerDefinition.cs`
- `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` + `FaultExecutionResultConsumerDefinition.cs`
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` (the dormant reactive handler — the 47 scan's deliberate exclusion)
- The reactive registrations/bindings in `src/Keeper/Program.cs` for the above + the `keeper-fault-recovery` endpoint.
- `KeeperQueues.FaultRecovery` and `KeeperQueues.DeadLetter` consts in `src/Messaging.Contracts/KeeperQueues.cs`.

### SHARED — do NOT delete wholesale (sweep reactive-only members only)
- **`src/Keeper/Recovery/L2ProbeRecovery.cs`** — the live v4 `src/Keeper/Health/BitHealthLoop.cs` calls **`probe.ProbeOnceAsync(...)`** (Phase 45). Keep `ProbeOnceAsync` (+ its sentinel plumbing); remove only the reactive recover-loop / per-`H`-attempt members if present.
- `src/Keeper/ProbeOptions.cs`, `src/Keeper/BackupOptions.cs` — keep members read by the v4 keep-set (`BackupOptions.TtlDays` for the composite backup; `Probe:DelaySeconds` for the BIT loop). `src/Keeper/RecoveryOptions.cs` — likely reactive-only orphan; confirm against the keep-set before removal.
- `src/Keeper/Observability/KeeperMetrics.cs` — remove only the reactive fault/recover-attempt instruments; keep the v4 meter.

### Tests to delete (orphaned by the removal) — per D-01
- `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs`
- `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs`
- `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs`
- `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` (reactive recover-loop portions; keep any `ProbeOnceAsync` coverage)
- (Researcher: enumerate exactly — also check for a `FaultRecoverySpikeE2ETests` / `KeeperRecoveryE2ETests` if still present.)

### Integration points
- `src/Keeper/Program.cs` — the bus-config / endpoint-registration site; the deletions converge here. Keep the `keeper-recovery` (v4) endpoint, the BIT loop hosted service, and the pause/resume publish wiring.

</code_context>

<specifics>
## Specific Ideas

- The Phase-48 negative-guard facts should be **structural** (reflection over the Keeper assembly for absent consumer types + a source-scan for the retired consts/endpoint), not live/E2E — consistent with this being a hermetic-only phase (D-03) and the Phase-47 `AtLeastOnceStructuralFacts` precedent.
- The `47` no-`keeper-dlq` source-scan should now be **widened to include `KeeperRecoveryHandler.cs`** (or that file deleted, which makes the exclusion moot) — verify the 47 guard still passes post-teardown with the exclusion removed.

</specifics>

<deferred>
## Deferred Ideas

- **Phase-49 net-zero baseline (forward note, NOT Phase-48 scope):** Phase 48 deliberately changes RabbitMQ topology (removes `keeper-dlq` + `keeper-fault-recovery`). Phase 49's recurring triple-SHA `rabbitmqctl list_queues` BEFORE==AFTER gate must therefore take its baseline from the **post-teardown** topology (those two queues already gone). The user chose to keep 48 to SC-4 only (D-03) — this note exists so Phase 49 planning starts from the correct topology, not so 48 records a delta artifact.

</deferred>

---

*Phase: 48-v3-x-teardown*
*Context gathered: 2026-06-09*
