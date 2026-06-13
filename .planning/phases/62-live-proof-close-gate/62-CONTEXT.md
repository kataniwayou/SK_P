# Phase 62: Live Proof & Close Gate - Context

**Gathered:** 2026-06-13
**Status:** Ready for planning

<domain>
## Phase Boundary

The **v7.0.0 milestone capstone** — a RealStack end-to-end proof that the *reshaped per-replica
liveness* built in Phases 59–61 works **live**, sealed behind the milestone close gate. This is the
live counterpart to the build shipped in 59–61, and a direct **adaptation of the proven Phase
49/55/58 close-gate + RealStack E2E harness** — NOT new infrastructure (v7 changed the liveness
keyspace/reader/probe, not the v5 slot-array/3-state recovery machinery, which stays unchanged and
is carried forward as full regression).

What must be proven live (the 3-req TEST set):

- **TEST-01 (per-instance keyspace live):** two replicas of ONE processor each write a distinct
  per-instance key `skp:proc:{processorId}:{instanceId}` and `SADD` themselves to the instance-index
  SET `skp:proc:{processorId}`; a starting/failed replica is observable as `unhealthy` (never
  absent); a dead replica's per-instance key TTL-expires and is lazily `SREM`'d from the index.
- **TEST-02 (gate + probe live):** orchestration start admits a workflow when **≥1** required-
  processor replica is healthy-and-fresh (even with an unhealthy/stale sibling) and is blocked
  **422 + RFC 7807** when none qualify; the self-watchdog probe returns `unhealthy` + the per-schema
  `summary` when the in-memory L1 record is stale beyond active-interval ×2 grace.
- **TEST-03 (close gate):** triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`
  BEFORE==AFTER net-zero, DLQ depth 0, N=3 consecutive GREEN, at Release + Debug 0-warning.

**In scope:**
- Reshape the `processor-sample` compose tier to run TWO replicas (the live multi-replica subject).
- New xUnit RealStack E2E tests for the deterministic gate + keyspace assertions (reuse harness).
- A `62-HUMAN-UAT.md` operator runbook for the live multi-container lifecycle proofs + the N=3 run.
- `scripts/phase-62-close.ps1` cloned from `phase-58-close.ps1` with v7 keyspace + seed deltas.

**Explicitly NOT in this phase (out of scope):**
- Any change to the liveness writer/reader/probe SEMANTICS — those are built (59–61); this phase
  only PROVES them live and seals the milestone.
- K8s liveness/startupProbe wiring + pod-restart policy — explicitly milestone "Future" (the probe
  delivers the *semantics* only; this phase proves the probe's verdict + summary surface).
- Mid-life `healthy → unhealthy` health flip — frozen-healthy this milestone (the probe catches a
  *stale/crashed loop*, never a flip).
- Workflow-root liveness (`LivenessProjection`) — out of scope for the whole milestone.

</domain>

<decisions>
## Implementation Decisions

### Two-replica deployment (TEST-01)
- **D-01 — Reshape `processor-sample` → `deploy.replicas: 2`:** Drop `container_name`
  (`sk-processor-sample`) and the published port from the `processor-sample` compose tier, add
  `deploy.replicas: 2` — mirroring the existing scalable `keeper` tier (`compose.yaml:233-234`,
  no container_name / no ports / replicas:2). The DEFAULT `docker compose up` now runs **two**
  replicas — the honest representation of the v7 "processors are multi-replica" reality. Each
  replica gets a distinct instanceId/podId → a distinct per-instance key + its own `SADD` to the
  index SET. (This changes the default stack from 1→2 processor replicas; prior shipped phases'
  close gates are unaffected — they are sealed.)
- **D-02 — Per-replica probe access via `docker exec`:** With no published port, reach a specific
  replica's self-watchdog probe with
  `docker exec <replica-container> wget -qO- localhost:8082/health/live` — hits the in-process probe
  on the internal port, per-replica. No published port range needed (keeps the clean replica shape).

### Proof harness split (TEST-01 / TEST-02)
- **D-03 — Mix: xUnit for deterministic, runbook for lifecycle.** The harness model is fixed: xUnit
  RealStack tests run the **WebAPI in-process** (`RealStackWebAppFactory`) against the **host-mapped
  real stack** (Redis `localhost:6380`, RMQ `localhost:5673`, Postgres `localhost:5433`); the
  processor runs as a compose **container** (the test seeds the matching Processor row + observes via
  host Redis + the in-process gate; it does NOT drive processor lifecycle). Therefore:
  - **xUnit RealStack** (reuse `RealStackWebAppFactory` + host Redis): the **deterministic** gate +
    keyspace assertions (per-instance key written, `SADD` to index, ≥1-healthy admits, 422 when
    none).
  - **Operator runbook (`62-HUMAN-UAT.md`)**: the genuinely-multi-container **lifecycle** proofs
    (two REAL replicas self-register, real restart→unhealthy, real TTL-expiry+SREM, real probe via
    `docker exec` curl) + the N=3 GREEN close run.
  - Mirrors Phase 58 (new behavior as xUnit RealStack + operator-gated close), extended with the
    unavoidable docker-lifecycle steps.
- **D-04 — Deterministic gate verdicts via FABRICATED Redis keys:** Prove "≥1-healthy admits even
  with an unhealthy/stale sibling" and "422 when none qualify" by writing **crafted** per-instance
  keys directly into host Redis (`status=unhealthy`, or an old `timestamp` for stale) + `SADD`ing
  them to the index alongside the real healthy replica's key, then asserting the in-process gate
  verdict — the **Phase-61 WR-01/WR-02 craft-redis-state pinning style** (deterministic, repeatable,
  no timing flake). The REAL multi-replica self-registration is proven separately in the runbook
  (D-03), so the fabricated-key test is a clean regression-gated verdict proof, not a substitute for
  the live proof.

### Fault injection — live induction of transient states
- **D-05 — Restarting→`unhealthy` (never absent) via a DURABLY-BROKEN extra replica:** Bring up ONE
  additional **profile-gated** processor instance that reaches the startup loop (writes
  `status=unhealthy` each iteration per Phase-60 STATE-03 "unhealthy-is-written") but **never flips
  healthy** (can't pass a startup gate). Its per-instance key is **durably `unhealthy`** + present in
  the index — cleanly observable, no timing race. The exact never-healthy mechanism (missing-
  definition vs Gate-A-incompatible reuse) is research/discretion. **RESEARCH FLAG:** confirm the v7
  writer (Phase 60) actually **persists `unhealthy`** for a never-healthy processor — v6's heartbeat
  *no-op'd* when not healthy (Phase-58 D-05); v7's STATE-03 says "unhealthy-is-written." This is
  load-bearing for D-05.
- **D-06 — Stale-L1 probe trip: hermetic verdict + live-wiring proof.** Prove the
  stale→`Unhealthy` verdict math **hermetically** with a controllable clock/`TimeProvider` (the
  probe verdict is deterministic; likely already covered by a Phase-61 hermetic test — confirm) AND
  prove the probe is **wired live** by `docker exec <replica> wget /health/live` returning the
  expected verdict + per-schema `summary` on a real replica. **No production fault code is added** —
  consistent with the project's no-test-seams-in-prod culture (Phase 58 used a real broken
  processor, not injected faults). Rationale: L1 is **in-memory**, updated every loop iteration
  *independent of Redis*; it only goes stale if the loop thread stops while Kestrel keeps serving —
  `docker pause` freezes the whole process (probe unreachable too) and Redis faults don't stop the
  in-memory L1 update, so a live stale-L1 would otherwise require a fault seam we choose not to add.
- **D-07 — Dead-replica TTL-expiry + lazy-`SREM` via `docker stop`:** `docker stop` one replica,
  wait past the per-key TTL (`max(interval×2, Ttl)` ≈ 30s for a heartbeat entry —
  `ProcessorLivenessOptions.cs:36`) so its per-instance key TTL-expires (`GET`→null), then trigger an
  orchestration-start / validator read so the absent member is lazily `SREM`'d from the index.
  Assert via `redis-cli SMEMBERS` shrinking. (`docker kill` is equivalent — discretion.)

### Close-gate v7 deltas (TEST-03)
- **D-08 — Clone `scripts/phase-58-close.ps1` → `scripts/phase-62-close.ps1`, triple-SHA verbatim:**
  Keep the proven protocol identical — idempotent steady-state Processor-row seed, compose-health
  pre-flight, dual-config 0-warning build gate, **N=3** consecutive-GREEN identical-fact-count
  Smell-A cadence, `psql \l` SHA, `redis-cli --scan` SHA, `rabbitmqctl -q list_queues name` SHA, the
  additive `skp:msg:*` count==0 assertion (A19 active reclaim), the separate `skp-dlq-1` depth==0
  check, `_bus_` transient exclusions. Retitle to Phase 62.
- **D-09 — `redis --scan` SHA excludes the per-replica liveness namespace by PREFIX `skp:proc:*`:**
  The v7 successor to phase-58's single `skp:{procId:D}` liveness exclusion. The steady-state
  liveness keyspace is now the index SET `skp:proc:{procId}` + the per-instance keys
  `skp:proc:{procId}:{instanceId}` (2× per processor). instanceIds are **non-deterministic**
  (podId/hostname-based), so an exact-key exclusion won't work — exclude the whole `skp:proc:*`
  prefix from both BEFORE/AFTER snapshots.
- **D-10 — Full accumulated regression retag `[Trait("Phase","62")]` + badconfig profile up:**
  Milestone-close "seal everything": retag the new v7 TEST-01/02 gate + keyspace tests + carry
  forward SC1/2/3 (recovery), Gate-A CFG-08/09, and the Sample round-trip suite; bring up the
  `badconfig` profile (net-zero-harmless per Phase-58 D-05) so Gate A still composes. Mirrors Phase
  58 retagging the v5 SCs forward into its close run. All stay `[Trait("Category","RealStack")]`
  (excluded from the hermetic suite, included in the live gate).
- **D-11 — Close-gate N=3 SHA runs against the CLEAN steady state:** 2 healthy replicas (+ optional
  net-zero-harmless badconfig). ALL TEST-01 lifecycle manipulations (the durably-broken extra
  replica D-05, the `docker stop`/restart fault steps, the TTL-expiry+SREM D-07) are **SEPARATE
  operator-runbook steps OUTSIDE the close-gate window** so they never perturb BEFORE==AFTER.
- **D-12 — v7 seed deltas vs phase-58 (the only changes vs the clone):**
  - Two-replica bring-up of `processor-sample` (D-01); the full-regression badconfig profile (D-10).
  - Processor rows + config-Schema rows seeded **CREATE-IF-ABSENT** only (frozen-once-referenced;
    Phase-57 D-06 — an edit returns 409); carry the phase-58 two-schema/two-processor seed.
  - **Verify the current `Processor.Sample` version string** for the seed
    (`src/Processor.Sample/appsettings.json` reads `"3.5.0"` at discuss time — confirm whether v7
    bumps it rather than carrying it blindly; phase-58 D-09c precedent).

### Carried-forward locks (precedent / convention — captured, not re-decided)
- **D-13 — N=3 consecutive GREEN** with the identical-fact-count Smell-A guard (phase-39/49/55/58).
- **D-14 — Build gate FIRST (autonomously verifiable):** `dotnet build SK_P.sln -c Release` AND
  `-c Debug` both 0-warning, AND the new/adapted RealStack E2E tests **COMPILE** (excluded from the
  hermetic run by `Category=RealStack` but must build). `scripts/phase-62-close.ps1` exists and is
  AST-syntactically valid.
- **D-15 — Live N=3×GREEN run is operator-gated** via `62-HUMAN-UAT.md`, mirroring Phase 58/55/49/39.
  Requires the rebuilt v7 docker stack (2 replicas + badconfig profile). **TEST-01/02/03 stay
  unticked** until the operator's GREEN run.
- **D-16 — Embedded SourceHash must match host build** (rebuilt v7 images) or the liveness gate
  false-passes/times out. Stable Processor rows seeded idempotently (genuine embedded SourceHash via
  GET-or-create on `uq_processor_source_hash`).

### Claude's Discretion
- The exact never-healthy mechanism for the durably-broken replica (D-05): missing-definition vs
  Gate-A-incompatible reuse of `Processor.BadConfig`.
- The exact crafted-key shapes/timestamps for the fabricated unhealthy/stale siblings (D-04).
- xUnit collection/parallelization shaping for the new tests; reuse `RealStackWebAppFactory` +
  host-Redis poll + the `PollForHealthyLivenessAsync`/`SMEMBERS` precedent + a fabricated-key seeding
  helper.
- `docker stop` vs `docker kill` for the dead-replica step (D-07).
- The Compose profile name for the durably-broken fault instance.
- Whether the gate test reads per-instance values sequentially or pipelined (behavior-equivalent).
- The `data`-dictionary key names the probe carries the `summary` in (Phase-61 discretion).

### Research flags (for the phase researcher — confirm before planning)
- **RF-01 (load-bearing for D-05):** Confirm the v7 writer (Phase 60 LOOP/STATE) **persists
  `status=unhealthy` durably** for a never-healthy processor (v6 heartbeat no-op'd when not healthy;
  v7 STATE-03 = "unhealthy-is-written"). If it does NOT, the durably-broken-replica unhealthy proof
  needs a different mechanism (e.g. catch the transient startup window).
- **RF-02 (D-06 hermetic half):** Confirm Phase 61 already has a hermetic clock-driven
  probe-stale→Unhealthy verdict test; if not, this phase adds it.
- **RF-03 (D-07 wait budget):** Confirm the effective per-key TTL for a heartbeat-written entry
  (`max(interval×2, Ttl)` with interval=10, Ttl=30 ⇒ 30s) so the dead-replica wait is sized right.

### Folded Todos
None — no pending todos matched this phase.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements & roadmap (locked)
- `.planning/REQUIREMENTS.md` — **TEST-01** (per-instance keyspace live: 2 replicas →
  `skp:proc:{procId}:{instanceId}` + `SADD` index; restarting→unhealthy never absent; dead→TTL-
  expiry+lazy-SREM), **TEST-02** (gate ≥1-healthy admits / 422+RFC7807 when none; probe
  stale→unhealthy+summary), **TEST-03** (close gate net-zero N=3 GREEN). **MUST read.** The "Out of
  Scope" section (workflow-root liveness; Gate A/B logic unchanged; no K8s wiring) is binding.
- `.planning/ROADMAP.md` §"Phase 62: Live Proof & Close Gate" (lines ~79-86) — goal + 3 success
  criteria. Plus the v7.0.0 milestone header (build order 59→60→61→62; "mirroring shipped Phases
  49/55/58").
- `.planning/PROJECT.md` §"Current Milestone: v7.0.0" — per-replica liveness framing.

### Upstream contracts this phase PROVES (shipped in 59/60/61 — read first)
- `.planning/phases/61-1-healthy-orchestration-start-gate-self-watchdog-probe/61-CONTEXT.md` — the
  gate + probe being proven live: the `SMEMBERS`→`GET`-each ≥1-healthy verdict (D-06/07/08),
  lazy-`SREM` absent-only (D-09), 422-vs-500 split (D-10), the self-watchdog `/health/live` probe
  semantics + summary-in-body (D-01..D-04), boot-null⇒Unhealthy (D-02).
- `.planning/phases/61-.../61-VERIFICATION.md` — what the gate + probe were verified to do
  hermetically (the live proof's hermetic counterpart; D-06 RF-02 target).
- `src/Messaging.Contracts/Projections/ProcessorLivenessEntry.cs` — the value record the gate/probe
  read; nested `LivenessSummary {inputSchema, outputSchema, configSchema}`. `[JsonPropertyName]` is
  load-bearing for the fabricated-key crafting (D-04).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `PerInstance(Guid,string)` +
  `InstanceIndex(Guid)` builders (the `skp:proc:*` namespace the SHA excludes — D-09).
- `src/Messaging.Contracts/Projections/LivenessStatus.cs` (`Healthy`/`Unhealthy`) — the consts the
  fabricated-key states use (never literals).
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — `IntervalSeconds`(10) /
  `StartupIntervalSeconds`(30) / `TtlSeconds`(30, TTL = `max(interval×2, Ttl)`) — the D-07/RF-03 wait
  budget + the staleness grace the probe/gate use.
- `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` — the in-memory L1 holder the
  watchdog reads (the basis for the D-06 in-memory-stale reasoning).

### Reader swap target being exercised live
- `src/BaseApi.Service/Features/Orchestration/Validation/ProcessorLivenessValidator.cs` — the
  swapped ≥1-healthy gate (D-04 fabricated-key verdicts + D-07 lazy-SREM exercise this).

### Proven close-gate template (clone source) + harness
- `scripts/phase-58-close.ps1` — the v6.0.0 triple-SHA close gate to **clone**. Documents every
  steady-state exclusion + net-zero discipline (unfiltered scan, `skp:msg:*` count==0, procId/`_bus_`
  exclusions, separate DLQ depth check, N=3 cadence, two-schema/two-processor CREATE-IF-ABSENT seed).
- `scripts/phase-55-close.ps1` — the v5.0.0 ancestor (phase-58 cloned it).
- `.planning/phases/58-.../58-CONTEXT.md` — the decision record (D-04 profile-gating, D-05 net-zero-
  harmless broken processor, D-07 retag-for-regression, D-08/09 clone discipline) this phase inherits
  and adapts to the v7 keyspace.
- `.planning/phases/58-.../58-HUMAN-UAT.md` — the operator runbook structure to mirror for
  `62-HUMAN-UAT.md`.
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — `RealStackWebAppFactory` (in-
  process WebAPI → host-mapped real stack) + host-Redis override + net-zero teardown
  (`L2KeysToCleanup`) + `SeedProcessorAsync` + `PollForHealthyLivenessAsync`. The harness all new
  tests reuse.
- `tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs` — the Phase-58 RealStack pattern
  (host-Redis assertions + the live processor-sample container) the new gate/keyspace tests mirror.
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs`, `SC2RecoveryPathsE2ETests.cs`,
  `SC3PauseResumeOutageE2ETests.cs` — the recovery suite to **retag** `[Trait("Phase","62")]` (D-10).
- `tests/BaseApi.Tests/Orchestrator/RealStackNetZeroSweepFixture.cs` — the net-zero sweep fixture
  (docker-exec DLQ purge) reused by the close run.

### Compose tiers (the code this phase CHANGES)
- `compose.yaml` — the `keeper` tier (`:225-256`, `deploy.replicas:2`, no container_name, no ports)
  is the **template** for the D-01 `processor-sample` reshape (`:265-291`, currently
  `container_name: sk-processor-sample`). The `processor-badconfig` profile-gated tier (`:292+`) is
  the template for any D-05 durably-broken fault instance.
- `src/Processor.Sample/appsettings.json` — the version string for the seed (D-12; `"3.5.0"` at
  discuss time — confirm for v7).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RealStackWebAppFactory` (in-process WebAPI → host-mapped real stack Redis:6380/RMQ:5673/PG:5433)
  + net-zero teardown + `SeedProcessorAsync` + `PollForHealthyLivenessAsync` — the harness all new
  xUnit RealStack tests reuse; the fabricated-key seeding (D-04) writes directly to host Redis.
- `scripts/phase-58-close.ps1` — verbatim triple-SHA protocol; only the `skp:proc:*` prefix
  exclusion (D-09), the two-replica bring-up, and the version-string verify change (D-12).
- The `keeper` compose tier (`deploy.replicas:2`, no container_name, no ports) — the proven scalable
  shape for the D-01 `processor-sample` reshape.
- The `processor-badconfig` profile-gated tier — the template for the D-05 durably-broken fault
  instance, and itself carried up as part of the D-10 full regression.
- `RealStackNetZeroSweepFixture` — docker-exec DLQ purge + net-zero discipline for the close run.

### Established Patterns
- **Harness model:** xUnit RealStack = in-process WebAPI + host-mapped real stack; the processor is
  a compose container the test does NOT lifecycle. → deterministic gate/keyspace assertions live in
  xUnit (observe host Redis + the in-process gate); container lifecycle proofs live in the operator
  runbook (D-03).
- **Craft-redis-state verdict pinning (Phase-61 WR-01/WR-02):** fabricate per-instance keys + index
  members directly in Redis to drive the gate verdict deterministically (D-04).
- **Net-zero-harmless broken processor (Phase-58 D-05):** a processor that `MarkReady()`s (no crash-
  loop, `/ready` green) but never `MarkHealthy()`s contributes nothing destabilizing — the basis for
  the D-05/D-11 fault-isolation posture. (RF-01: confirm v7 now writes durable `unhealthy` for it.)
- **Frozen-once-referenced schema (Phase-57 D-06):** referenced schema `Definition` locked; edit →
  409 → the close-gate seed is CREATE-IF-ABSENT only (D-12).
- Triple-SHA BEFORE==AFTER (`psql \l` / `redis --scan` / `rabbitmq list_queues name`); additive
  `skp:msg:*` count==0 + `skp-dlq-1` depth==0; steady-state exclusions; N=3 identical-fact-count.
- xUnit v3 on Microsoft.Testing.Platform: `dotnet test --filter` ignored (MTP0001); filtered runs
  use the compiled `BaseApi.Tests.exe` with `--filter-not-trait Category=RealStack` (hermetic).
- `[Trait("Category","RealStack")]` excludes E2E from the hermetic suite; `[Trait("Phase","62")]`
  includes them in the close-gate live run (D-10).

### Integration Points
- The live stack must run **rebuilt v7 images**: 2 `processor-sample` replicas (D-01) + the
  `badconfig` profile (D-10) — `docker compose --profile badconfig up -d --build` then the replica
  reshape brings up 2; each embedded SourceHash must match its host build (D-16) or the liveness gate
  false-passes/times out.
- The per-replica probe is reached via `docker exec <replica> wget localhost:8082/health/live`
  (D-02) — no published port on the scaled tier.
- The ≥1-healthy gate + lazy-SREM are wired in `ProcessorLivenessValidator` (Phase 61); this phase
  proves them — it does not change them.
- `skp-dlq-1` is the single surviving DLQ (keeper-local since Phase 53); the gate's DLQ loop stays
  the single-element `@('skp-dlq-1')`.
- The per-replica liveness namespace `skp:proc:*` (index SET + per-instance keys) is the new
  steady-state to exclude from the redis-scan SHA (D-09) — the v7 successor to the single
  `skp:{procId:D}` exclusion.

</code_context>

<specifics>
## Specific Ideas

- This phase explicitly **mirrors shipped Phases 49/55/58** (the milestone-close live-proof pattern).
  The default posture is "clone the proven harness/protocol; change only what the v7 keyspace forces."
- The hardest real decision was the **stale-L1 probe proof** (D-06): L1 is in-memory and only goes
  stale if the loop stops while Kestrel stays up — `docker pause` kills the probe too, Redis faults
  don't stop L1 — so the verdict is proven hermetically (clock) + the probe is proven live-wired via
  `docker exec` curl, rather than adding a production fault seam.

</specifics>

<deferred>
## Deferred Ideas

- **K8s liveness/startupProbe wiring + pod-restart on a failed self-watchdog** — milestone "Future
  Requirements"; this phase proves the probe's verdict + summary surface only.
- **A minimal in-process fault-injection seam** (e.g. `FreezeLoopAfter=N`) for a fully-live stale-L1
  probe trip — rejected for this phase (no test-seams-in-prod; D-06 covers the verdict hermetically).
  Revisit only if a future requirement needs a live stale-loop reproduction.

### Reviewed Todos (not folded)
None — discussion stayed within phase scope.

</deferred>

---

*Phase: 62-live-proof-close-gate*
*Context gathered: 2026-06-13*
