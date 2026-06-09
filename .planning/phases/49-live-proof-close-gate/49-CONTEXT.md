# Phase 49: Live Proof & Close Gate - Context

**Gathered:** 2026-06-09
**Status:** Ready for planning

<domain>
## Phase Boundary

The final phase of v4.0.0. Author the real-stack E2E proofs + the close-gate script that **prove the v4 Pre/In/Post + Keeper-5-state-recovery rebuild live** and lock net-zero. All behavior already shipped (Phases 43–48); this phase proves it against the real stack and seals the milestone.

Fixed success criteria (from ROADMAP §"Phase 49"):
- **SC1** — RealStack E2E proves the full Pre → In → Post round trip (dispatch consumed → output written to `L2[entryId]` → orchestrator advances on the per-item `ExecutionResult`).
- **SC2** — RealStack E2E proves each recovery path: `REINJECT` data-present (re-injected to `queue:{ProcessorId}`), `REINJECT` data-gone → `skp-dlq-1`, `INJECT` (reconstructed `Completed` → orchestrator), and `DELETE`.
- **SC3** — RealStack E2E proves the BIT-gate global pause-all/resume-all across a transient L2 outage (outage → pause all → L2 recovers → resume all), pause/resume idempotent per job.
- **SC4** — close gate: N-consecutive-GREEN + triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE==AFTER net-zero, including the composite backup key (proven cleaned by `CLEANUP`/`INJECT`, not lingering on its 2-day TTL), the GUID data keys, and `skp-dlq-1`, at Release + Debug 0-warning.

**Scope clarification:** This is the live-proof + close-gate phase, NOT a behavior phase. No production code change unless a live E2E reveals a genuine defect (then a minimal, explicitly-surfaced gap-fix — same posture as Phase 47).

</domain>

<decisions>
## Implementation Decisions

### L2 outage simulation — SC3 (D-01, D-02)
- **D-01:** The SC3 E2E induces + heals a **true transient outage via `docker stop sk-redis` … `docker start sk-redis`** (shelled out from the test). `docker stop` makes the `BitHealthLoop` write-then-delete probe throw `RedisException` → `IL2HealthGate` closes → Keeper `Publish(PauseAll)` → orchestrators `PauseAllAsync()`; `docker start` makes the probe succeed → gate opens → `Publish(ResumeAll)` → per-job resume (Quartz `TriggerState == Paused` guard). A real down→up is the truest representation of the recovery model's core assumption (recovery from a **transient** L2 outage). Rejected: `docker pause/unpause` (timeouts, not clean `RedisException`, murky against the probe cadence); in-redis `CLIENT KILL`/config (doesn't reproduce a true tier-down).
- **D-02:** The outage E2E is **isolated in its own non-parallel xUnit `[Collection]`** (test parallelization disabled for it) so stopping `sk-redis` cannot destabilize sibling RealStack tests (or the soft-dep consoles) during the 3×GREEN suite. It **blocks before returning** on: `docker start sk-redis` → redis healthy → steady-state re-established (the `skp:{procId:D}` liveness heartbeat re-written AND the health gate re-opened). It stays in the one suite the close gate runs — just serialized.

### Live-run definition-of-done (D-03)
- **D-03:** Phase 49 is **complete when the proofs + close machinery are authored and hermetically green**: the 3 sibling E2E files + the close-gate script build 0-warning (Release + Debug), the non-RealStack suite is GREEN, and the close script + operator runbook are committed. The **actual live N×GREEN close run is operator-gated** — it requires the rebuilt v4 stack up (v4 is a breaking contract change: `baseapi-service`, `orchestrator`, `processor-sample`, `keeper` must all be rebuilt before a live run is valid). **TEST-01/02/03 stay unticked** until the operator's GREEN live run, tracked in `49-HUMAN-UAT.md`. This matches every prior milestone close (Phase 39/35/36/33 all deferred the live run to an operator gate).

### E2E structure & recovery driving — SC1/SC2 (D-04, D-05)
- **D-04:** **Three separate sibling RealStack E2E files, one concern each** — round-trip (SC1), recovery-paths (SC2: the 4 states), pause-resume-outage (SC3). Mirrors the project's one-concern-per-file pattern (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`). Each file registers every key it mints into its teardown cleanup (net-zero discipline). The SC3 file is the non-parallel one (D-02).
- **D-05:** SC2 exercises each Keeper recovery state by **publishing the actual state contracts directly to the gate-open `queue:keeper-recovery`** (`UPDATE`/`REINJECT`/`INJECT`/`DELETE`, plus the data-gone REINJECT → `skp-dlq-1`) and asserting each state's L2 / re-inject / orchestrator-advance / dead-letter effect. Deterministic per-state coverage; requires the gate open (healthy L2). SC1 drives the organic round trip through the real processor pipeline.

### Close-gate parameters — SC4 (D-06, D-07)
- **D-06:** **N = 3 consecutive GREEN** (carry forward the established cadence: Phase 39/28/32.1/16 all 3×, with identical fact count across the runs as a Smell-A guard). The close script clones the proven `phase-39-close.ps1` triple-SHA protocol (idempotent steady-state Processor-row seed, pre-flight compose-health check, BOTH-config 0-warning build gate, settle-drain of transient TTL'd namespaces before the AFTER snapshot).
- **D-07:** The **2-day-TTL composite backup key is proven net-zero by active cleanup, not by waiting out the TTL.** Keep the proven **unfiltered `redis-cli --scan` SHA BEFORE==AFTER** (unfiltered already captures the composite `corr:wf:proc:exec` namespace), and rely on the **E2E tests registering every composite key into teardown** so `CLEANUP`/`INJECT` (or an explicit teardown delete) removes them BEFORE the AFTER snapshot — a lingering composite then surfaces as a redis SHA mismatch (exit 1). **No TTL settle-wait** for the composite namespace (its 2-day TTL can't be waited out the way Phase 39 drained its short-TTL keys). The standard separate **`skp-dlq-1` depth==0** assertion is retained; the full triple-SHA (psql/redis/rabbitmq) net-zero + Release/Debug 0-warning gate stands.

### Claude's Discretion
- Exact E2E file names/namespaces (follow `*RoundTripE2ETests` convention) and the non-parallel collection name.
- The precise `docker stop/start` wait/poll timings and how redis-healthy + gate-re-open + liveness-re-written are detected before the outage test returns.
- Whether the new close script is a fresh `phase-49-close.ps1` clone of `phase-39-close.ps1` or a parameterized reuse — keep the triple-SHA protocol identical; update the canonical service list / namespaces for v4 (no `keeper-dlq`, single `skp-dlq-1`, composite backup namespace, GUID data keys).
- The processor-row seed version string in the close script (Phase 39 used `'3.7.0'`; v4 should reflect the current processor version — verify against the live `Processor.Sample` version).
- Whether SC2's direct-publish helper reuses an existing test kit or a new RealStack helper.
- Exact contents/format of `49-HUMAN-UAT.md` (operator runbook: stack rebuild set, gate invocation, the three SHA values + Passed count + DLQ depth to record).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements & design (source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — the LOCKED v4 design (recovery model, the 5 Keeper states, composite backup key `corr:wf:proc:exec` TTL 2 days, REINJECT data-present vs data-gone, global pause-all/resume-all, single `skp-dlq-1`). Amendments A16 (at-least-once/no-dedup) + A17 (reactive-path retired) are current.
- `.planning/REQUIREMENTS.md` §"Live Proof & Close Gate (TEST)" — **TEST-01** (line 74: full round trip + each recovery path), **TEST-02** (line 75: BIT-gate pause/resume across transient outage), **TEST-03** (line 76: close gate N-GREEN + triple-SHA net-zero).
- `.planning/ROADMAP.md` §"Phase 49: Live Proof & Close Gate" — the 4 success criteria are the verification target.

### Close-gate protocol to clone (read before authoring the gate)
- `scripts/phase-39-close.ps1` — the proven triple-SHA close-gate template: idempotent steady-state Processor-row seed (genuine embedded SourceHash via reflection), compose-health pre-flight, BOTH-config 0-warning build gate, 3-GREEN identical-fact-count cadence, settle-drain before AFTER, triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues name`) invariants, separate DLQ depth==0 assertion. **D-06/D-07 update for v4:** drop `keeper-dlq` (retired), single `skp-dlq-1`, capture composite `corr:wf:proc:exec` + GUID data keys via the unfiltered scan, no composite TTL settle-wait, v4 service list.

### v4 production code the E2E exercises (read before authoring tests)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the Pre→In→Post pipeline (SC1 round-trip), `BuildReinject` stamp, end-delete `finally`.
- `src/Keeper/Recovery/*` — the gate-open 5-state recovery consumer on `queue:keeper-recovery` (`RecoveryConsumerBase` + UPDATE/REINJECT/INJECT/DELETE/CLEANUP), partitioned by the `IKeeperRecoverable` 4-tuple; the `RecoveryDataGoneException` → `skp-dlq-1` route (SC2 direct-publish target).
- `src/Keeper/Health/*` (BIT health gate) — `IL2HealthGate`/`L2HealthGate` + `BitHealthLoop` write-then-delete probe on `Probe:DelaySeconds`; `Publish(PauseAll)`/`Publish(ResumeAll)` on each health edge (SC3 driver).
- `src/Orchestrator/Consumers/PauseAllConsumer.cs` + `ResumeAllConsumer.cs` — idempotent `PauseAllAsync()` + per-job resume with `TriggerState == Paused` guard, on `orchestrator-global-pauseresume` (SC3 assert target; `Native_ResumeAll_Is_Never_Called` is the load-bearing negative).
- `src/Orchestrator/Consumers/TypedResultConsumer*.cs` — per-item `StepCompleted/Failed/Cancelled/Processing` consumers on `OrchestratorQueues.Result` (SC1/SC2 orchestrator-advance assert target).
- `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs` — `const Dlq1 = "skp-dlq-1"` (reference the const, never the literal, in assertions).
- `src/Messaging.Contracts/L2ProjectionKeys.cs` — GUID data key (no TTL) + composite backup key `corr:wf:proc:exec` + `BackupOptions` 2-day TTL (the net-zero namespaces SC4 must capture).
- `src/Messaging.Contracts/KeeperQueues.cs` — `Recovery` is the sole surviving Keeper queue (`DeadLetter`/`FaultRecovery` retired in Phase 48).

### Test templates to reuse/extend
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — the live round-trip harness template for SC1.
- `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — RealStack-trait E2E with backend polling + net-zero teardown pattern.
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` — pause/resume consumer assertion idiom for SC3.
- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` + `RecoveryDeadLetterFacts.cs` — REINJECT-data-gone → `skp-dlq-1` assertion idiom for SC2.
- `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs` — the v4 compose service inventory (canonical service list for the close-gate pre-flight).

### Test runner note
- `tests/BaseApi.Tests/` uses the Microsoft.Testing.Platform runner — `dotnet test --filter` is ignored; scope via `dotnet run --project tests/BaseApi.Tests -- --filter-trait`/`--filter-method`. Tag new Phase-49 facts `[Trait("Phase","49")]`; RealStack facts carry `[Trait("Category","RealStack")]`.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`phase-39-close.ps1`** — clone for `phase-49-close.ps1`: the entire triple-SHA protocol (seed, pre-flight, build gate, 3-GREEN cadence, settle-drain, SHA invariants, DLQ depth) transfers; only the v4 namespaces / service list / single-DLQ change.
- **`SampleRoundTripE2ETests` / `MetricsRoundTripE2ETests`** — RealStack E2E harness (live HTTP drive, backend poll, `L2KeysToCleanup`-style net-zero teardown) to clone for the 3 new sibling files.
- **`ResumeAllConsumerTests`** — the Quartz pause/resume + `TriggerState == Paused` assertion idiom for SC3.
- **`RecoveryDeadLetterFacts` / `ConsolidatedErrorTransportFilter.Dlq1`** — REINJECT-data-gone → `skp-dlq-1` (reference the const) for SC2.

### Established Patterns
- **Triple-SHA net-zero close gate** — unfiltered `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues name` SHA BEFORE==AFTER + separate DLQ-depth==0; idempotent steady-state Processor-row seed keeps the procId (hence its liveness key + dispatch queue) stable across the run.
- **One-concern-per-file RealStack E2E** with self-registering net-zero teardown.
- **Authored-hermetic + operator-gated-live** — the project's recurring close pattern; live N×GREEN run deferred to an operator, requirements stay unticked until the GREEN run (`*-HUMAN-UAT.md`).

### Integration Points
- The close script drives the live stack over the WebApi host port (`http://localhost:8080`) for the idempotent seed; `docker exec`/`docker compose exec` for the SHA snapshots; the v4 compose service set (no `keeper-dlq`, single `skp-dlq-1`).
- New tests live in `tests/BaseApi.Tests/`, `[Trait("Phase","49")]` + `[Trait("Category","RealStack")]`; the SC3 outage test in its own non-parallel collection.

</code_context>

<specifics>
## Specific Ideas

- "True transient outage" — `docker stop/start sk-redis`, not pause or in-redis tricks; it must be a real down→up the BIT probe genuinely observes, because the v4 recovery model is explicitly built for a transient L2 outage.
- The composite backup's **2-day TTL can't be waited out** — net-zero must be proven by active `CLEANUP`/`INJECT` (E2E teardown registration), surfacing a leak as a redis SHA mismatch rather than a silent TTL pass.
- Reference `ConsolidatedErrorTransportFilter.Dlq1` (not `"skp-dlq-1"`) in assertions so a future rename can't silently desync the tests.
- Verify the live `Processor.Sample` version string before seeding the close-gate Processor row (Phase 39's `'3.7.0'` is stale for v4).

</specifics>

<deferred>
## Deferred Ideas

- **Actual live N×GREEN close run** — operator-gated (D-03), tracked in `49-HUMAN-UAT.md`; TEST-01/02/03 tick only on the operator's GREEN run against the rebuilt v4 stack.
- **Literal queue rename `skp-dlq-1` → `_DLQ1`** — out of scope (carried from Phase 47); `skp-dlq-1` IS the canonical single DLQ, `_DLQ1` is roadmap shorthand.
- **v4.0.0 milestone audit / archival** — a post-49 milestone-completion step, not this phase.

</deferred>

---

*Phase: 49-live-proof-close-gate*
*Context gathered: 2026-06-09*
