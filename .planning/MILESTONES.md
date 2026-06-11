# Steps API — Milestones

## v4.0.0 Processor Pre/In/Post-Process + Keeper Recovery Redesign (Shipped: 2026-06-11)

**Phases completed:** 7 phases, 27 plans, 60 tasks

**Key accomplishments:**

- 1. [Rule 1 - Bug] IKeeperRecoverable inheriting ICorrelated hid CorrelationId from reflection
- Adapted the four execution-path consumer files to Plan-02's six-id no-H Guid-entryId contracts using the simplest straight-through behavior — made entryId a Guid end-to-end, seeded Guid.Empty at the entry-step fire, deleted the flag[H]/CAS dedup (RETIRE-01) + content-addressing + manifest fan-out (RETIRE-02) + the placeholder Redis result read, re-targeted ResultConsumer to IConsumer<StepCompleted>, and re-shaped EntryStepDispatchConsumer to emit one StepCompleted/StepFailed/StepCancelled per result — both target projects compile 0/0 against the new symbols.
- Kept the reactive Keeper recovery path DARK-but-compiling against Plan-02's reshaped contracts (D-14): rebound KeeperRecoveryHandler off the removed IExecutionCorrelated.H onto a local CompositeBackup 4-tuple key, retyped L2ProbeRecovery to the surviving ExecutionData(Guid) overload, and neutralized the now-unresolvable Fault<ExecutionResult> consumer by retargeting it onto StepCompleted + dropping its registration — while retaining the reactive recovery FEATURE registered on Fault<EntryStepDispatch>. Keeper builds clean 0/0; zero files deleted; the feature's real retirement stays RETIRE-03 (Phases 47/48).
- MTP ignores the VSTest `--filter` (warning MTP0001).
- Five additive author-contract source files (ProcessOutcome/ProcessItem/ProcessStatusException family, RetryLoop surface-not-throw helper, KeyAbsentException sentinel) plus the RESIL-01 RetryLoopFacts — the Wave-0 foundation Plan 02 composes from, with the old ProcessResult/ProcessAsync seam left untouched and the solution still GREEN.
- The load-bearing Phase-44 behavior change: a new `ProcessorPipeline` runner implements the explicit Pre→In→Post→`finally`-end-delete flow with the five terminals (REINJECT / UPDATE→write→CLEANUP / INJECT / DELETE + business StepFailed/StepCancelled/StepProcessing) proven hermetically, the `BaseProcessor` seam retyped to `ProcessAsync(validatedData, payload, ct) → List<ProcessItem>`, every L2 op and send wrapped in the Plan-01 `RetryLoop` (Retry:Limit), and the bus `UseMessageRetry` reconciled to the outer dead-letter latch — full hermetic suite 487 passed / 0 failed, `BaseProcessor.Core` Release 0-warning.
- Completes the v4.0.0 clean break (D-06/D-07): deletes the obsolete `ProcessResult.cs` (zero lingering references in `src/`/`tests/`), confirms `Processor.Sample` as the migrated worked example of the new `ProcessAsync → List<ProcessItem>` author contract (completed item with author-minted `ExecutionId` + a demonstrated `FailedException` status path now covered by a fact), and re-runs the phase-44 close gate — `SK_P.sln` Release 0-warning + the full hermetic suite 488 passed / 0 failed. Most of this plan's hands-on migration was already performed by the 44-02 executor's Rule-3 deviation; this plan landed the genuinely-remaining record deletion, reference hygiene, fail-path fact, and the green gate.
- Task 1 — Contracts (`cbbf904`):
- The Keeper proactive health engine: a swappable-TCS `IL2HealthGate` (starts CLOSED, cancel-aware wait), an edge-triggered `BitHealthLoop` BackgroundService that probes L2 each tick and Publishes `PauseAll`/`ResumeAll` once per transition, and a sentinel-parameterized `ProbeOnceAsync` extracted from `L2ProbeRecovery` — turning the Wave-0 Keeper RED stubs GREEN.
- The fleet-side enactment of the global broadcast: a thin scheduler-wide `PauseAllAsync` seam, a `PauseAllConsumer` (idempotent `PauseAll()`) + a `ResumeAllConsumer` (per-job resume over the L1 snapshot, NEVER native `ResumeAll()` — no catch-up herd), and both `ConsumerDefinition`s on a NEW per-replica fan-out endpoint `orchestrator-global-pauseresume` with single retry ownership — turning the Wave-0 Orchestrator RED stubs (incl. the load-bearing no-burst negative) GREEN.
- Relocated the shared RetryLoop into BaseConsole.Core, added Payload to KeeperReinject (contract + BuildReinject + golden test atomically), and scaffolded 11 RED Phase-46 test stubs so every Keeper/Orchestrator consumer in later waves has a discoverable automated target.
- Built the five sealed Keeper recovery-state consumers and their shared gate/retry base — the base awaits IL2HealthGate once at entry under a 300s-bounded linked CTS (throwing a transient marker on bound, Pattern A / D-03 LOCKED), and a Guard helper wraps every L2 op + Send in the relocated RetryLoop re-throwing on exhaustion (D-04). UPDATE writes the composite with the BackupOptions TTL; REINJECT re-injects an EntryStepDispatch carrying the D-01 Payload (or throws the data-gone terminal); INJECT does read→write(no TTL)→send→delete in strict order producing a StepCompleted indistinguishable from a direct completion; DELETE/CLEANUP delete the data/composite keys. Six Wave-0 Keeper-body facts are GREEN.
- Wired the five Keeper recovery consumers onto the shared queue:keeper-recovery endpoint with per-key ordering (KEEP-09): UpdateConsumerDefinition is the single endpoint-config owner — it registers UseMessageRetry plus five UsePartitioner<T> calls sharing one Partitioner(PartitionCount, Murmur3UnsafeHashGenerator) keyed on the IKeeperRecoverable 4-tuple (corr:wf:proc:exec, StepId excluded), while the other four definitions leave ConfigureConsumer an intentional no-op (Pitfalls 1 & 4). RecoveryOptions binds from the new "Recovery" appsettings section and all five consumers register additively alongside the surviving reactive FaultEntryStepDispatchConsumer. The partition-key fact and a data-gone dead-letter integration fact are GREEN.
- Replaced the single straight-through `ResultConsumer` with the `TypedResultConsumer<TMessage>` family — a generic advancement base (the verbatim `ResultConsumer.Consume` body with the hardcoded `StepOutcome.Completed` swapped for an abstract `Outcome` knob) plus four sealed one-line subclasses and four thin definitions on the shared `orchestrator-result` endpoint, with the ORCH-01 indistinguishability requirement proven green.
- Three hermetic Phase-47 facts proving the single-DLQ-consolidation (RESIL-02) and no-dedup (RESIL-03) structural invariants: a processor send-exhaustion -> skp-dlq-1 fact, a reflection no-MessageIdentity guard, and a directory-scoped no-keeper-dlq source-scan.
- Two duplicate-delivery no-collapse facts (StepCompleted via ONE RecordingDispatcher, KeeperReinject via ONE ReinjectConsumer + CapturingSendProvider) prove the v4 execution path reproduces effects on redelivery without dedup (RESIL-03), plus an additive Phase-47 trait makes the already-green data-gone proof (RESIL-02) discoverable for the audit.
- The phase's primary human-readable deliverable — `47-DLQ-AUDIT.md` mapping RESIL-02/RESIL-03 and roadmap SC-1/2/3 to 8 named GREEN proving tests — plus an additive A16 design-doc amendment elevating the at-least-once/no-dedup property to a test-cited guarantee and bundling the deferred Phase-46 KeeperReinject.Payload note. Doc-only: zero source change, build 0/0.
- Deleted the v3.x reactive Fault<T> Keeper recovery path (consumers, KeeperRecoveryHandler, the orphaned KeeperMetrics meter), reduced L2ProbeRecovery to the v4 BIT-probe helper, removed the keeper-dlq/keeper-fault-recovery queue consts + dead config/key remnants, and swept every dependent test — leaving the solution 0-warning green on the v4 recovery path alone.
- Authored the four-fact `ReactivePathRetiredFacts` class (anchored on the surviving `BitHealthLoop` assembly) that makes the RETIRE-03 teardown self-verifying and regression-proof, added the SC-2 RETIRE-02 remnant-verify (ExecutionData-Guid-only + no-Manifest), and widened the Phase-47 `keeper-dlq` scan to be unconditional now that `KeeperRecoveryHandler.cs` is deleted — Phase=48 trait run is 4/4 GREEN (non-empty) and the widened Phase-47 scan stays GREEN.
- Closed the v4.0.0 retirement story end-to-end: authored `48-TEARDOWN-AUDIT.md` (the RETIRE-01/02/03 + SC-1..SC-4 traceability ledger, every row citing a named green guard test/scan), flipped REQUIREMENTS.md RETIRE-01/02/03 to Satisfied, added the additive A17 design-doc amendment recording the reactive-path + keeper-dlq retirement, and passed the SC-4 hermetic close gate — the suite is GREEN ×3 consecutive (507/507, 0 failed) and both Release and Debug builds are 0-warning on the v4-only path.
- Authored SC1RoundTripE2ETests.cs — a Phase-49-tagged RealStack E2E that proves the full v4 Pre->In->Post round trip (dispatch consumed -> output written to skp:data:{entryId} -> orchestrator advances) with net-zero teardown; compiles 0-warning at Release+Debug and is excluded from the GREEN 507-fact hermetic suite.
- Authored `SC2RecoveryPathsE2ETests.cs` — a RealStack E2E that proves all four Keeper recovery states by direct-publishing the actual contracts to `queue:keeper-recovery` (const) and asserting each L2 / re-inject / orchestrator-advance / dead-letter effect, with every minted key (incl. the 2-day-TTL composite) registered to net-zero teardown.
- Authored SC3PauseResumeOutageE2ETests — a RealStack E2E (in its own non-parallel collection) proving the BIT-gate global pause-all/resume-all across a true transient L2 outage induced by `docker stop sk-redis` / `docker start sk-redis`, asserted via the orchestrator ES seam logs, healed in a finally; 0-warning Release+Debug, hermetic suite 507/507 GREEN.
- Authored `scripts/phase-49-close.ps1` (v4 triple-SHA N=3 GREEN net-zero close gate cloned from phase-39 with single `skp-dlq-1`, seed version `3.5.0`, no composite-TTL wait) and `49-HUMAN-UAT.md` (the operator runbook that gates the TEST-01/02/03 tick).
- Closed GAP-49-2: orchestrator Quartz scheduler no longer stays PAUSED for newly-scheduled workflows after a global pause-all/resume-all cycle — ResumeAllConsumer now clears Quartz's pausedTriggerGroups via a single scheduler-wide ResumeAll() placed AFTER the per-job fresh-from-now reschedule loop (no misfire herd).
- Chosen: shape (b) — publish-topology `BindQueue` + `DeployPublishTopology = true`.

---

A historical log of shipped milestones. Each entry is a frozen snapshot; full archives live in `milestones/`.

> **Note:** v3.5.0 (Processor Console — phases 25-30) shipped 2026-06-02 but its formal milestone
> entry / ROADMAP archive / git tag were deferred (its requirements snapshot is archived at
> `milestones/v3.5.0-REQUIREMENTS.md`). The v3.6.0 close was deliberately scoped to v3.6.0 only.

---

## v3.7.0 — Keeper (L2-Outage Dead-Letter Recovery & Workflow Pause/Resume)

**Shipped:** 2026-06-07
**Tag:** `v3.7.0`
**Archives:** [milestones/v3.7.0-ROADMAP.md](milestones/v3.7.0-ROADMAP.md) · [milestones/v3.7.0-REQUIREMENTS.md](milestones/v3.7.0-REQUIREMENTS.md) · [milestones/v3.7.0-MILESTONE-AUDIT.md](milestones/v3.7.0-MILESTONE-AUDIT.md)

### Delivered

A dedicated multi-replica `Keeper` console that makes the autonomous execution loop (cron fire → dispatch → process → result → fan-out) **self-heal through transient L2 (Redis) outages without operator intervention.** Keeper reacts to the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` events the execution-path consumers publish on retry-budget exhaustion (pub/sub, bind-to-all — never per-`{procId}_error`), double-unwraps the inner message + 6-id correlation + `H`, runs a bounded crash-survivable L2 health-probe loop (read `skp:data:{entryId}` + write-then-delete a scratch key), pauses the affected workflow's cron via the single-replica orchestrator's Quartz scheduler (deterministic `TriggerKey` + `GetTriggerState` as the single source of truth — pause-state never in L2, because L2 is the resource being recovered), re-injects recovered work directly to its origin by type riding the receiver's surviving Phase-31 `flag[H]` idempotency (Keeper holds no dedup of its own), resumes on any successful recovery, and parks the genuinely unrecoverable in `keeper-dlq` for operator triage. A per-`H` attempt cap bounds the recover→reinject cycle so a persistent fault converges to a single park instead of flooding the stack. Transport-exhaustion across all consoles consolidates into one TTL'd `skp-dlq-1`, wired once in `BaseConsole.Core` (two DLQs, split by mechanism). Metrics gained a uniform `service_name = {name}_{version}` + non-empty `service_instance_id` across all four consoles (the processor's name/version DB-sourced via a MeterProvider swap), plus a full `Keeper` meter. Keeper is the automated operator for v3.6.0's accepted "an infra-faulting workflow keeps dead-lettering until an operator intervenes" gap.

The original 33-39 build was delivered + live-proven (Phase-39 close gate); a follow-up audit (2026-06-07) added three gap-closure phases — 40 (recovery hardening: attempt cap, deterministic drain, shared handler), 41 (pause/resume diagnostics: WR-01/WR-02), 42 (docs & traceability reconciliation).

### Stats

| Metric                    | Value                                              |
| ------------------------- | -------------------------------------------------- |
| Phases                    | 10 (33-42; incl. gap-closure 40/41/42)             |
| Plans                     | 32                                                 |
| Requirements              | 37 satisfied (34 core 33-39 + KHARD-01..03 Phase 40) |
| C# files                  | 409 (207 src / 202 tests)                          |
| Lines of code             | ~40,141 (12,424 src / 27,717 tests)                |
| Code changed              | 75 files, +6,287 / −48 (src + tests, vs v3.6.0 close) |
| Commits                   | 220 (`v3.6.0..HEAD`; 33 feat / 17 fix / 28 test / 137 docs / 3 refactor) |
| Timeline                  | 2026-06-05 → 2026-06-07 (~3 days)                  |
| Final suite               | Phase-39 close gate 3×500 GREEN RealStack + triple-SHA net-zero; hermetic 505/0 (Phase 41), Release+Debug 0-warning |
| Milestone audit           | tech_debt — 0 functional blockers (safe to ship)  |
| Known deferred at close   | 23 open artifacts (consolidated into the passed Phase-39 gate) — see STATE.md Deferred Items |

### Key accomplishments

1. New multi-replica `Keeper` console on `BaseConsole.Core` (structural twin of `Orchestrator`, leaner) recovering L2-outage dead-letters off the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` pub/sub stream — one durable competing-consumer queue, round-robin across replicas.
2. Bounded crash-survivable `L2ProbeRecovery` loop (read + write-then-delete scratch key, `catch RedisException` only, ack-after-loop) → re-inject the verbatim inner message to origin by type, riding the receiver's surviving Phase-31 `flag[H]` (Keeper carries no dedup of its own).
3. Two-DLQ topology split by mechanism — consolidated `skp-dlq-1` (Immediate(N) transport exhaustion across all consoles, 7d TTL, wired once in `BaseConsole.Core` keeping `GenerateFaultFilter`) + `keeper-dlq` (probe/cap give-up park, depth = primary Prometheus alert).
4. Orchestrator pause/resume coordination via a deterministic Quartz `TriggerKey` three-state model (`PauseJob`/`GetTriggerState` as the single source of truth, no L1 state field), `ConcurrentMessageLimit=1` idempotent consumers; Keeper Publishes Pause-at-intake + Resume-on-Recovered.
5. Recovery hardening (Phase 40) — per-`H` attempt cap (atomic INCR+PEXPIRE-NX Lua, parks `recover_cap` instead of flooding) + deterministic keeper-dlq drain + one shared `KeeperRecoveryHandler` both consumers delegate to.
6. Uniform metric labels (Phase 38) — `service_name = {name}_{version}` + non-empty `service_instance_id` across all four consoles (processor DB-sourced via MeterProviderHolder swap; logs `service.name` kept bare) + a full `Keeper` meter (6 counters + UpDownCounter + histogram), live-proven by Prometheus scrape.

### Known deferred items at close

No functional blockers (audit `tech_debt`). 23 open artifacts acknowledged and deferred (recorded in STATE.md Deferred Items): per-phase live HUMAN-UAT smokes (33/34/35/36/37/40) and `human_needed` VERIFICATION statuses — **by design** consolidated into the single authoritative Phase-39 real-stack close gate (passed: 3×500 GREEN, triple-SHA net-zero), not re-run per phase; plus stale older items (Ph 08/09 v3.2.0, 32/32.1 v3.6.0) and a leftover Phase-29 debug session. KHARD-02's live 3× close-gate is Manual-Only (drain fix source-resolved; live re-run operator-pending). Nyquist VALIDATION partial/missing on 8 phases (advisory). FUTURE-KEEPER-SWEEP (auto-resume into recovered L2) deferred to a future milestone. **v3.5.0 formal ROADMAP/MILESTONES/tag archival remains deferred** (prior-milestone housekeeping; snapshot at `milestones/v3.5.0-REQUIREMENTS.md`).

---

## v3.6.0 — Idempotent Execution (Exactly-Once-Effect Round-Trip)

**Shipped:** 2026-06-05
**Tag:** `v3.6.0`
**Archives:** [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md) · [milestones/v3.6.0-REQUIREMENTS.md](milestones/v3.6.0-REQUIREMENTS.md) · [milestones/v3.6.0-MILESTONE-AUDIT.md](milestones/v3.6.0-MILESTONE-AUDIT.md)

### Delivered

The orchestrator↔processor execution round-trip became **exactly-once-effect**. Every duplicate inbound message — from configurable `Immediate(N)` retry, broker redelivery, publish-confirm ambiguity, the orchestrator's own re-dispatch, or a fan-in/merge re-dispatch — reproduces the same deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded) and is collapsed at the receiving node via an **effect-first** `flag[H]` `Pending → Ack` CAS at both hops, so each step's downstream effect happens exactly once with no lost branch and `ProcessAsync` needs no idempotency logic. L2 data is content-addressed at two levels (result blobs + manifest); merge steps are per-edge (distinguished by input `EntryId`, identical-input collapses), and the orchestrator fans the manifest out N×M. The hard-coded `Immediate(3)` became a configurable retry budget; content-addressed `data`/`flag` keys are `prefix + 64-hex`. A late course-correction (Phase 32 → 32.1) **reverted** the planned cancelled circuit-breaker — after building it, the two-level breaker was judged not worth its surface area and net-deleted in favor of plain dead-lettering to `_error` on exhaustion; the Phase-31 idempotency gates survived the revert intact.

### Stats

| Metric                    | Value                                              |
| ------------------------- | -------------------------------------------------- |
| Phases                    | 4 (31, 31.1, 32→32.1)                              |
| Plans                     | 9 active (31: 6 · 31.1: 1 · 32: 5 superseded · 32.1: 2) |
| Requirements              | 14 active satisfied (Phase-31 ×8 + Phase-32.1 ×6); Phase-32 ×8 retired |
| C# files                  | 423 (233 src / 190 tests)                          |
| Lines of code             | ~34,714 (11,779 src / 22,935 tests)                |
| Code changed              | 56 files, +2,710 / −255 (src + tests, vs v3.5.0 close) |
| Commits                   | 93 (first phase-31 spec → HEAD)                    |
| Timeline                  | 2026-06-04 → 2026-06-05 (~2 days)                  |
| Final suite               | 3×GREEN = 452 facts (32.1 close gate), Release+Debug 0 warnings |
| Milestone audit           | tech_debt — 0 functional blockers (safe to ship)  |

### Key accomplishments

1. Deterministic message identity `H` via a single canonical `MessageIdentity` hash class (executionId excluded) — a retry/redelivery/re-dispatch of the same logical message yields the same `H`.
2. Effect-first `flag[H]` CAS dedup at BOTH hops (processor inbound dispatch + orchestrator inbound result) — exactly-once-EFFECT with a collapsed-duplicate residual, never a lost branch.
3. Content-addressed two-level L2 data (result blobs at `data[hash(blob)]` + manifest at `data[hash(manifest)]`) with N×M orchestrator manifest fan-out; empty result = terminal branch.
4. Per-edge merge correctness — content-collapse on identical input, distinct `H` (no override) on divergent input; the `StepB4`-×2 duplicate proven gone live in Elasticsearch.
5. Configurable retry budget (`Immediate(N)` default) bound from appsettings at all 4 endpoints, replacing the hard-coded `Immediate(3)`.
6. Cancelled circuit-breaker built then **consciously reverted** (Phase 32 → 32.1) to plain dead-lettering on exhaustion — a net-deletion that preserved the Phase-31 idempotency layer (integration re-confirmed 5/5).

### Known deferred items at close

Artifact/doc hygiene only (no functional blockers): Phase 32's VERIFICATION/VALIDATION still read pending against the reverted breaker (moot — superseded); Phase 31.1 has no VERIFICATION/VALIDATION/SECURITY (gap-closure, script-only); Phase 32.1 VALIDATION `nyquist_compliant: false` by design (real-stack behaviors are manual-only operator-gate, live-proven by the close gate + smoke tests). Accepted runtime consequence: a persistently infra-faulting workflow is not auto-stopped — each cron fire dead-letters one message to `_error` until an operator intervenes. v3.5.0's formal archival (ROADMAP/MILESTONES/tag) remains deferred.

---

## v3.4.0 — BaseConsole + Orchestrator Messaging

**Shipped:** 2026-06-01
**Tag:** `v3.4.0`
**Archives:** [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md) · [milestones/v3.4.0-REQUIREMENTS.md](milestones/v3.4.0-REQUIREMENTS.md) · [milestones/v3.4.0-MILESTONE-AUDIT.md](milestones/v3.4.0-MILESTONE-AUDIT.md)

### Delivered

A reusable `BaseConsole.Core` Generic-Host library (the console-side mirror of `BaseApi.Core`) and a runnable `Orchestrator` console that inherits it, connected to the WebApi over MassTransit/RabbitMQ. Body-carried CorrelationId is proven end-to-end (HTTP `X-Correlation-Id` → publish-boundary `NewId` mint → fan-out message body → orchestrator log scope, surfaced in Elasticsearch). The full orchestrator lifecycle landed: startup L1 hydration from the `skp:` parent-index, per-workflow Quartz scheduling, entry-step dispatch to `queue:{processorId}`, and stop teardown. The processor→orchestrator result round-trip closes the loop — `ExecutionResult`/`StepOutcome` consumed on a shared competing-consumer queue, L1-only edge traversal + continuation dispatch. A late gating redesign (Phase 24.1, gap-closure) replaced the boot gate + scheduled redelivery + the `rabbitmq_delayed_message_exchange` plugin with an L2-existence dedup + atomic discover-then-delete Stop + L1-only graceful result path, and added a terminal-step guard.

### Stats

| Metric                    | Value                                              |
| ------------------------- | -------------------------------------------------- |
| Phases                    | 9 (17-24 + 24.1)                                   |
| Plans                     | 31                                                 |
| Requirements              | 70/70 (ORCH-GATE-01 superseded by 24.1)            |
| C# files                  | 336 (194 src / 142 tests)                          |
| Lines of code             | ~25,396 (9,772 src / 15,624 tests)                 |
| Code changed              | 127 files, +7,887 / −795 (vs v3.3.0 close)         |
| Commits                   | 273                                                |
| Timeline                  | 2026-05-30 → 2026-06-01 (~3 days)                  |
| Final suite               | 335/335 GREEN (real-stack E2E live), Release 0 warnings |
| Milestone audit           | PASSED                                             |

### Key accomplishments

1. `Messaging.Contracts` leaf assembly — frozen `ICorrelated` vocabulary + shared `L2ProjectionKeys` single-source-of-truth (writer↔reader desync structurally impossible).
2. `BaseConsole.Core` reusable Generic-Host library — metrics-only OTel (no traces), soft-dep Redis, embedded minimal-Kestrel health probes, MassTransit bus + correlation filters.
3. Two-process WebApi↔Orchestrator messaging over RabbitMQ with body-carried CorrelationId proven end-to-end in Elasticsearch.
4. L2 root-parent restructure + processor self-registration boundary + liveness-gated Start (422 + RFC 7807).
5. Full orchestrator lifecycle — L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown (no L2 mutation by the orchestrator).
6. Processor→orchestrator result round-trip + L1-only step advancement, then a gating redesign (Phase 24.1) removing the boot gate/redelivery/plugin in favor of L2-existence dedup + atomic Stop + L1-only graceful result.

### Known deferred items at close

Two non-blocking dead-code cleanups (recorded in the audit for a future hygiene pass): `RedisProjectionOptions.ProcessorKeyTtlDays` (dead config field post-Phase-22) and `WorkflowRootNotFoundException` (never thrown post-24.1; harmless `Ignore<>` no-ops). Nyquist VALIDATION.md missing for Phases 21 and 24.1 (discovery-only flag).

---

## v3.3.0 — Orchestration L3 → L1 → L2 Build Pipeline

**Shipped:** 2026-05-29
**Tag:** `v3.3.0`
**Archives:** [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md) · [milestones/v3.3.0-REQUIREMENTS.md](milestones/v3.3.0-REQUIREMENTS.md) · [milestones/v3.3.0-MILESTONE-AUDIT.md](milestones/v3.3.0-MILESTONE-AUDIT.md)

### Delivered

The orchestration Start/Stop build pipeline: `POST /api/v1/orchestration/start` fetches each requested workflow graph from Postgres (L3), builds a transient in-memory `WorkflowGraphSnapshot` (L1), validates it (existence → cycle → schema-edge → payload-config-schema, 422 + RFC 7807 at first failed gate), and projects it into 3 Redis (L2) keyspaces that external consumers read. Stop is a Redis existence gate with GET-and-follow cleanup. Redis landed as a soft compose-stack dependency (Phase 5 HEALTH contracts untouched). Closes v2-deferred VALID-21 at orchestration-start scope. No new SQL entities or columns — L3 schema is the v3.2.0 `InitialCreate` unchanged.

### Stats

| Metric                    | Value                                          |
| ------------------------- | ---------------------------------------------- |
| Phases                    | 5 (12-16)                                       |
| Plans                     | 26                                             |
| C# files                  | 237 (127 src / 110 tests)                       |
| Lines of code             | ~18,344 (7,366 src / 10,978 tests)             |
| Code changed              | 70 files, +6,855 / −149 (vs v3.2.0)            |
| Git commits               | 158 (`v3.2.0..HEAD`)                            |
| Timeline                  | 2026-05-29 (single intensive day)               |
| Integration facts (final) | 235/235 GREEN × 3 consecutive runs             |

### Key Accomplishments

1. **Redis as a soft infrastructure tier** — `redis:7.4.x-alpine` in compose (RSALv2/SSPLv1, not AGPLv3 8.0+); `AddBaseApiRedis` registers `IConnectionMultiplexer` Singleton + `RedisProjectionOptions`; `/health/ready` stays 200 with Redis down (only Start/Stop fail with 500 + RFC 7807); `RedisFixture` per-class `KeyPrefix` isolation with SCAN-assert-zero teardown (FLUSHDB forbidden).
2. **OrchestrationService split + transient L1 build** — thin orchestrator delegating to `IWorkflowGraphLoader` + 3 validators + `IRedisProjectionWriter`; `WorkflowGraphSnapshot` (flat `Dictionary<Guid, EntityDto>` per entity) loaded via batch `AsNoTracking` reads, disposed via `using` on success and failure paths; multi-child fan-out traversal.
3. **Validation gates in mandatory order (closes VALID-21)** — existence → cycle (ITERATIVE DFS, no recursion) → schema-edge (strict `Guid` equality) → Payload↔ConfigSchema (JsonSchema.Net draft 2020-12, SSRF-locked, per-Start parse cache); single `OrchestrationValidationException` → 422 with offending entity ids at the first failed gate; L1 cleanup in `finally`.
4. **L2 Redis materialized projection + Stop** — 3 keyspaces (root/step/processor) written in one `CreateBatch()` pipeline via System.Text.Json (camelCase-pinned records, processor-only TTL, `TimeProvider` liveness); Stop is a collect-all-missing existence gate (422 with full missing list, no deletion) else GET-and-follow cleanup (deletes root + per-step, never processor keys — `IServer.Keys()`/`KEYS` forbidden) → 204; non-idempotent by design.
5. **Idempotency + concurrency + 3-GREEN closeout** — re-Start reflects the second write (jobId changed) with orphan GC; concurrent Starts both 204 (last-write-wins, no Redis lock); gate failures write zero L2 keys (SCAN-verified); close gate ran 3× consecutive GREEN at 235 passed with byte-identical `psql \l` + `redis-cli --scan` SHA-256 BEFORE=AFTER.

### Decisions Set in Stone (v3.3.0)

| Decision                                                                          | Outcome |
| --------------------------------------------------------------------------------- | ------- |
| Redis is a SOFT dependency (`/health/ready` never probes it; CRUD serves 200 down) | ✓ Good  |
| Iterative DFS for cycle detection (no recursion — `StackOverflowException` uncatchable) | ✓ Good  |
| Strict `Schema.Id` equality for schema-edge compatibility (null-side passes)       | ✓ Good  |
| System.Text.Json (not Mapperly) for L2 DTO → JSON serialization                    | ✓ Good  |
| `IServer.Keys()`/`KEYS` forbidden in production; targeted GET-and-follow traversal | ✓ Good  |
| Stop scope: existence-gate + root/step deletion (never processor keys), non-idempotent | ✓ Good  |
| Per-processor keys TTL'd (`ProcessorKeyTtlDays` default 100); root/step no TTL     | ✓ Good  |

### Closed Requirements (per phase)

- **Phase 12 (Redis infra + composition + healthcheck + DI)** — INFRA-REDIS-01..06, INFRA-COMP-01..04, TEST-REDIS-01..05
- **Phase 13 (OrchestrationService split + L3 fetch + L1 build)** — ORCH-SPLIT-01..04, L1-BUILD-01..05
- **Phase 14 (Validation gates)** — L1-VALIDATE-01..10 (closes deferred VALID-21 at orchestration-start scope)
- **Phase 15 (L2 Redis projection write + Stop existence check)** — L2-PROJECT-01..07, ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-01..03
- **Phase 16 (Idempotency + concurrency + L1 cleanup + 3-GREEN closeout)** — TEST-REDIS-06..09

**Total: 64/64 requirements satisfied** (milestone audit PASSED — integration fully wired, all E2E flows complete).

### Notes

- **OBSERV-REDIS-04 deferred** — Redis-side profiling metrics → Prometheus; future milestone (FUTURE-OTEL-REDIS).
- **Deferred future candidates** — FUTURE-STOP-EVICTION (full eviction + processor-key ref-counting), FUTURE-SCHEDULER (real JobId/Liveness writers), FUTURE-GENERATION-ID (stale-projection detection), FUTURE-VALID-21-HTTP-WRITE (VALID-21 at Assignment write time).
- **Stop scope evolved twice** — original mirror-of-Start eviction → existence-check-only (2026-05-28) → root+step deletion (never processor keys) + collect-all-missing gate (Phase 15 D-04/D-06).
- **Known deferred items at close** — Nyquist VALIDATION.md for phases 12/14/16 left in `draft` (all passed VERIFICATION + 3×235 GREEN close gate); close-gate script maintenance smells (`-1` fact-count fallback, compose `ps --format json` assumption, PS1/SH divergence). Both tech-debt only, do not affect correctness. Phase 15 `15-HUMAN-UAT.md` (status passed, 0 pending) flagged by audit-open as a benign false positive — acknowledged at close.

### Code Review / Verification Posture at Close

All 5 phases passed independent VERIFICATION (12: 5/5, 13: 9/9, 14: 5/5 verified, 15: 5/5 human-verified, 16: 12/12). Cross-phase integration checker confirmed all 5 seams wired with no gaps and all E2E flows complete. Phase 16 close gate: 3×235 GREEN, dual-SHA BEFORE=AFTER held, EF-migration + HEALTH-01..05 byte-immutable guards clean.

---

## v3.2.0 — Steps API MVP

**Shipped:** 2026-05-28
**Tag:** `v3.2.0`
**Archives:** [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md) · [milestones/v3.2.0-REQUIREMENTS.md](milestones/v3.2.0-REQUIREMENTS.md)

### Delivered

A .NET 8 Web API platform exposing CRUD over the 5-entity workflow-engine data model (Schema, Processor, Step, Assignment, Workflow) on top of a reusable `BaseApi.Core` library — full Postgres persistence, OpenTelemetry observability (logs to Elasticsearch, metrics to Prometheus), RFC 7807 error responses, three K8s-style health probes, and a 142-fact integration test suite running 3-consecutive-GREEN against real backends.

### Stats

| Metric                    | Value                                          |
| ------------------------- | ---------------------------------------------- |
| Phases                    | 11                                             |
| Plans                     | 41                                             |
| C# files                  | 190 (105 src / 85 tests)                       |
| Lines of code             | 12,321 (5,924 src / 6,397 tests)               |
| Git commits               | 334 (76 feat / 59 fix / docs/test/refactor)    |
| Git range                 | `9a606ed` → `cbdc342`                          |
| Timeline                  | 2026-05-26 → 2026-05-28 (3 days)               |
| Integration facts (final) | 142/142 GREEN × 3 consecutive runs             |

### Key Accomplishments

1. **Reusable `BaseApi.Core` composition root** — `AddBaseApi`/`UseBaseApi` wires generic `BaseController` / `BaseService` / `Repository<T>` through abstract DTO / Validator / Mapper seams; `Program.cs` capped at ≤10 non-trivial body lines; observability split to `IHostApplicationBuilder.AddBaseApiObservability` for MEL bridge.
2. **5 v1 entity feature folders + 3 junction tables + InitialCreate migration + multistage Dockerfile** — Schema / Processor / Step / Assignment / Workflow CRUD over real Postgres 17 with audit-symmetric Mapperly mappings, FluentValidation per-entity rules, and explicit FK constraint names matching the Phase 4 `PostgresExceptionMapper` regex.
3. **Cross-cutting middleware + RFC 7807 Problem Details** — `X-Correlation-Id` middleware (read-or-generate, echoed on every response), 4-handler `IExceptionHandler` chain (NotFound → Validation → DbUpdate → Fallback), Postgres SQLSTATE → HTTP mapping (`23503`→422, `23505`→409, both with offending field name).
4. **OpenTelemetry backend cutover (Phase 11)** — logs ship to Elasticsearch (`logs-generic.otel-default`, OTLP-normalized field shape), metrics ship to Prometheus (`up{job="otel-collector"}=1`, `service_name="sk-api"` label preserved via `resource_to_telemetry_conversion`), traces dropped; Collector v0.152.0 wired in compose stack alongside ES 8.15.5 + Prom v3.11.3; Phase 5 file-exporter test infrastructure fully retired.
5. **Three K8s-style health probes with strict tag discipline** — `/health/live` (process only, never DB per Pitfall 15), `/health/ready` (Postgres + StartupCompletionService), `/health/startup` (migration-gated via `MigrateAsync` in `StartupCompletionService.ExecuteAsync` try/catch/LogCritical/no-rethrow contract).
6. **Test suite discipline** — 142/142 integration facts × 3 consecutive GREEN runs (163s / 161s / 162s on final Phase 11 close), zero flakes; byte-identical `psql \l` SHA-256 snapshot `0d98b0de…0aac127` BEFORE = AFTER (4 baseline DBs, zero leaked `stepsdb_test_*` databases — Phase 3 D-15 invariant preserved across all 11 phases).

### Decisions Set in Stone (v3.2.0)

| Decision                                                                  | Outcome  |
| ------------------------------------------------------------------------- | -------- |
| Single-API (Option B) instead of 6 separate webapis                       | ✓ Good   |
| `BaseApi.Core` shared class library + abstract generic controllers        | ✓ Good   |
| `BaseEntity` abstract, no table; 5 concrete entities → 5 tables           | ✓ Good   |
| Single Postgres DB + single `AppDbContext`                                | ✓ Good   |
| Mapperly source-generators (RMG007/RMG012/RMG020/RMG089 → solution error) | ✓ Good   |
| FluentValidation with inheritable `BaseDtoValidator<T>`                   | ✓ Good   |
| OTel Collector via OTLP — logs to ES, metrics to Prom, no traces          | ✓ Good   |
| `MigrateAsync` at startup inside `StartupCompletionService` (PERSIST-10)  | ✓ Good   |
| `Processor.ConfigSchemaId` (Phase 10) — symmetric Input/Output/Config FKs | ✓ Good   |
| `Assignment` surface collapsed to `(StepId, Payload)` only (Phase 10)     | ✓ Good   |

### Closed Requirements (per phase)

- **Phase 1 (Repository Scaffold)** — INFRA-01, INFRA-02, INFRA-03, INFRA-04
- **Phase 2 (Postgres + Docker Compose)** — INFRA-06, INFRA-07
- **Phase 3 (EF Core Persistence Base)** — ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15, PERSIST-16
- **Phase 4 (Cross-Cutting Middleware + Error Handling)** — OBSERV-09..11, ERROR-01..11
- **Phase 5 (Observability + Health Probes)** — OBSERV-01..08, OBSERV-12 (later superseded by Phase 11 D-03), HEALTH-01..05
- **Phase 6 (Validation + Mapping Base)** — VALID-01..07, HTTP-10
- **Phase 7 (Generic HTTP Base + Composition Root)** — HTTP-01..03, HTTP-08, HTTP-09, HTTP-13..16
- **Phase 8 (Entity Build-Out + Migrations + Docker + Tests)** — 41 REQ-IDs: PERSIST-01/08/09/10/12/13/14, ENTITY-03..10, HTTP-04..07/11/12, VALID-08..20, INFRA-05, TEST-01/02/05/06
- **Phase 9 (Processor.GetBySourceHash + Orchestration Start/Stop)** — 6 SPEC-local REQ-IDs (REQ-1..REQ-6)
- **Phase 10 (Remove SchemaId on Assignment; add ConfigSchemaId on Processor)** — ENTITY-04, ENTITY-07, VALID-11, VALID-15 amended
- **Phase 11 (Migrate Prometheus + Elasticsearch into compose stack)** — OBSERV-13, OBSERV-14, INFRA-08, TEST-07; OBSERV-12 supersession finalized; INFRA-06 amendment locked

### Notes

- **TEST-03 / TEST-04 deferred to v2** (Phase 8 D-05/D-06) — concurrency & xmin token integration tests deferred; current unit-level coverage (Phase 3 `XminConcurrencyTokenTests`) judged sufficient for v1 ship.
- **`WorkflowScheduleEntity` dropped** — replaced by `Workflow.CronExpression` nullability for gating.
- **Authentication/Authorization out of scope for v1** — service is open; `CreatedBy`/`UpdatedBy` populate only when `HttpContext.User.Identity.Name` is available.
- **`(Name, Version)` is not unique** — user-specified; `Version` is metadata.

### Code Review Posture at Close

Across Phase 11 close: 0 critical / 5 warning / 8 info — all warnings test-hygiene only (cancellation token threading, env var restoration, TOCTOU windows). None blocked phase completion. Aggregated across all 11 phases: 0 critical findings reached production paths; all warnings advisory or test-hygiene.

---

*Next milestone planning begins with `/gsd-new-milestone`.*
