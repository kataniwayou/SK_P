# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- ✅ **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-24 + 24.1 (shipped 2026-06-01) — see [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md)
- ✅ **v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip** — Phases 25-30 (shipped 2026-06-02)
- ✅ **v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip** — Phases 31-32.1 (shipped 2026-06-05) — see [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md)
- ✅ **v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume** — Phases 33-42 (shipped 2026-06-07) — see [milestones/v3.7.0-ROADMAP.md](milestones/v3.7.0-ROADMAP.md)
- ✅ **v4.0.0 Processor Pre/In/Post-Process + Keeper Recovery Redesign** — Phases 43-49 (shipped 2026-06-11) — see [milestones/v4.0.0-ROADMAP.md](milestones/v4.0.0-ROADMAP.md)
- ✅ **v5.0.0 Recovery Re-architecture — messageId slot-array + 3-state keeper** — Phases 50-55 (shipped 2026-06-12) — supersedes v4.0.0 Model-B recovery; source of truth [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) A18 + A19
- ✅ **v6.0.0 Config & Payload Validation Hardening** — Phases 56-58 (shipped 2026-06-13) — typed base-config seam on `BaseProcessor` + startup config-schema compatibility **Gate A** (withholds processor *Healthy* on config-type↔config-schema mismatch); complements the shipped WebAPI **Gate B** (`PayloadConfigSchemaValidator`); 10/10 CFG requirements, Phase-58 live close gate N=3 GREEN; see [milestones/v6.0.0-ROADMAP.md](milestones/v6.0.0-ROADMAP.md)
- 🚧 **v7.0.0 Per-Replica Processor Liveness & Self-Watchdog** — Phases 59-62 (in progress, started 2026-06-13) — per-instance L2 liveness keys `skp:proc:{processorId}:{instanceId}` + instance-index SET (replacing single `skp:{processorId}`), two-state `status`+per-schema `summary` written by both startup+heartbeat loops, in-memory L1 record, WebAPI ≥1-healthy orchestration-start gate, self-watchdog probe; 17 reqs (KEY/STATE/LOOP/L1/GATE/PROBE); builds on v6.0.0 Gate A

## 🚧 v7.0.0 Per-Replica Processor Liveness & Self-Watchdog (In Progress — started 2026-06-13)

**Milestone Goal:** Make processor liveness multi-replica-accurate and self-healing. Replace the single last-write-wins L2 liveness key `skp:{processorId}` with **per-instance keys** `skp:proc:{processorId}:{instanceId}` under a per-processor **instance-index SET** `skp:proc:{processorId}` (TTL = source of truth; index = discovery hint, lazily `SREM`'d when stale). The liveness value gains a **two-state `status`** (`healthy`/`unhealthy`) + a per-schema **`summary`** (`{inputSchema, outputSchema, configSchema ∈ SUCCESS|FAIL}`; any FAIL ⇒ unhealthy; `configSchema` reuses v6.0.0 Gate A's outcome) and is written by **BOTH** the startup loop (from its first iteration — so L2 reflects a *restarting* replica as `unhealthy`, never absent) and the heartbeat loop (timestamp refresh only; health frozen `healthy` once heartbeat starts, monotonic-within-process, reset-on-restart). An **in-memory L1 liveness record** is added (updated by both loops). The WebAPI orchestration-start gate becomes a **≥1-healthy-and-fresh** check across replicas (`SMEMBERS` the index → `GET` each per-instance key → pass iff ≥1 present + `status=healthy` + non-stale `timestamp + interval×2 > now`; present-but-unhealthy/stale fails *that* replica; lazy-`SREM` absent members). A **self-watchdog liveness probe** reads the L1 record's staleness (active-interval ×2 grace) so a silently-crashed loop ⇒ stale L1 ⇒ probe fails ⇒ (future) K8s pod restart. The `inputDefinition`/`outputDefinition` write-only dead weight is **dropped from L2** (the per-instance value is liveness-only). **Breaking change** to the processor liveness contract (keyspace + wire shape + WebAPI gate logic). Phases continue at **59**; builds directly on v6.0.0 Gate A.

**Source of truth:** This milestone's planning conversation (2026-06-13), design confirmed point-by-point — per-instance keys + index, definitions dropped, unhealthy-is-written, frozen-healthy, self-watchdog. Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (17 reqs across KEY/STATE/LOOP/L1/GATE/PROBE).

**Build order (locked, dependency-driven — the keyspace/value reshape is the prerequisite foundation):** 59 (L2 per-instance keyspace + two-state value reshape) → 60 (dual-loop writer + in-memory L1 + unhealthy-is-written) → 61 (WebAPI ≥1-healthy gate + self-watchdog probe) → 62 (live proof + close gate).

### Phases

- [ ] **Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value** — Reshape the L2 liveness contract to per-instance keys + an instance-index SET, with a two-state `status` + per-schema `summary` value (definitions dropped).
- [ ] **Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record** — Startup + heartbeat loops both write the (per-instance, TTL'd) entry to L2 + L1 each iteration; startup writes `unhealthy`; split startup/heartbeat intervals; frozen-healthy.
- [ ] **Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe** — WebAPI gate iterates the instance index and admits iff ≥1 replica is healthy-and-fresh (lazy-prune stale); the processor's liveness probe fails on stale L1.
- [ ] **Phase 62: Live Proof & Close Gate** — Real-stack E2E proof of the reshaped per-replica liveness + the triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` net-zero close gate (N=3 GREEN).

### Phase Details

#### Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value
**Goal**: The L2 processor-liveness contract is reshaped from the single last-write-wins key `skp:{processorId}` to **per-instance keys** `skp:proc:{processorId}:{instanceId}` discovered through a per-processor **instance-index Redis SET** `skp:proc:{processorId}`, and the per-instance value becomes a **liveness-only** record carrying a two-state `status` (`healthy`/`unhealthy`) + a per-schema `summary` (`{inputSchema, outputSchema, configSchema ∈ SUCCESS|FAIL}`; any FAIL ⇒ unhealthy; `configSchema` sourced from v6.0.0 Gate A, null-is-skip). `instanceId` is the existing pod identity (`POD_NAME → HOSTNAME → MachineName → GUID`). `inputDefinition`/`outputDefinition` are dropped from L2. This is the prerequisite foundation the dual-loop writer (Phase 60) and the WebAPI gate (Phase 61) both consume.
**Depends on**: — (first phase of v7.0.0; builds on the shipped v6.0.0 Gate A outcome + the v5.0.0 `L2ProjectionKeys`)
**Requirements**: KEY-01, KEY-02, KEY-03, KEY-04, STATE-01, STATE-02
**Success Criteria** (what must be TRUE):
  1. `L2ProjectionKeys` exposes a per-instance liveness key builder `skp:proc:{processorId}:{instanceId}` plus the per-processor instance-index SET key `skp:proc:{processorId}` (golden-test-pinned), replacing the single `skp:{processorId}` key.
  2. `instanceId` resolves from the existing `POD_NAME → HOSTNAME → MachineName → GUID` chain (reused, no new mechanism), and is the discriminator that makes two replicas of the same processor write to two distinct keys (no cross-replica overwrite).
  3. The per-instance liveness value is liveness-only — it carries `status ∈ {healthy, unhealthy}` and a `summary` `{inputSchema, outputSchema, configSchema}` each `SUCCESS|FAIL` (any FAIL ⇒ `status=unhealthy`; null schema id ⇒ not-failing); `inputDefinition`/`outputDefinition` no longer appear in the L2 value (a serialization/shape test confirms their absence).
  4. The `configSchema` summary field is derived from the v6.0.0 Gate A startup config-compat outcome (not recomputed), and a null `ConfigSchemaId` is treated as not-failing (null-is-skip), consistent with input/output schemas.
  5. Solution builds 0-warning (Release + Debug); the hermetic suite is green against the reshaped contract.
**Plans**: 2 plans (Wave 1, parallel - zero file overlap)
- [x] 59-01-PLAN.md - Value contract: LivenessStatus.Unhealthy + SchemaOutcome SoT + ProcessorLivenessEntry record + Create factory + shape/invariant tests (KEY-04, STATE-01, STATE-02)
- [x] 59-02-PLAN.md - Keyspace + identity: PerInstance/InstanceIndex key builders + shared InstanceId.Resolve() SoT + golden/resolver tests (KEY-01, KEY-02, KEY-03)

#### Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record
**Goal**: The processor's startup loop and heartbeat loop **both** write the per-instance liveness entry to L2 **and** update an in-memory **L1 liveness record** on every iteration, and the startup loop writes its entry as `unhealthy` from its first iteration (so a starting/failed replica is visible in L2 as `unhealthy`, never absent). Each replica `SADD`s its own `instanceId` to the instance-index SET on its first liveness write; each per-instance key carries a TTL (the source-of-truth liveness signal). Liveness intervals split into `startup_interval` (startup cadence) and `heartbeat_interval` (heartbeat cadence), each entry records its active interval, and health is **frozen `healthy`** once the heartbeat loop starts (timestamp-only refresh thereafter — monotonic within a process, reset on restart). Builds on the Phase 59 keyspace/value shape.
**Depends on**: Phase 59 (the per-instance key + two-state value shape must exist before the loops can write to it)
**Requirements**: STATE-03, LOOP-01, LOOP-02, LOOP-03, LOOP-04, L1-01
**Success Criteria** (what must be TRUE):
  1. The startup loop writes the replica's liveness entry to **both** L2 (the per-instance key) and the in-memory L1 record on every iteration, with `status=unhealthy` and a `summary` reflecting current schema-resolution progress (stays `unhealthy` until identity + all non-null schemas resolve) — a restarting replica is observable in L2 as `unhealthy`, never absent.
  2. On its first liveness write each replica `SADD`s its own `instanceId` to `skp:proc:{processorId}` (mirroring the Phase-22 workflow parent-index discipline), and each per-instance key is written with a TTL so a dead replica's key TTL-expires (the index SET is only a discovery hint).
  3. On startup success the heartbeat loop starts; each heartbeat iteration refreshes the entry's timestamp in **both** L2 and L1, with health **frozen `healthy`** (no mid-life re-validation — monotonic within a process, reset on restart).
  4. Liveness intervals are split into a `startup_interval` and a `heartbeat_interval` (the `Ttl` knob retained), and each liveness entry records its active interval so downstream staleness math can adapt to which loop wrote it.
  5. The in-memory L1 liveness record (`timestamp`, active `interval`, `status`, `summary`) is updated by BOTH loops on every iteration and is the single source the self-watchdog probe (Phase 61) reads.
**Plans**: 4 plans
- [ ] 60-01-PLAN.md - Config interval split (StartupInterval=30, Interval/Ttl retained) + lock-free L1 holder + binding/state tests (LOOP-03, L1-01)
- [x] 60-02-PLAN.md - Shared internal writer: L2 SET(perInstance, TTL=max(interval*2,Ttl)) + index SADD + L1 Update + log-and-continue; writer facts (LOOP-03, LOOP-04) ✓ ProcessorLivenessWriter (public sealed) + 5 ProcessorLivenessWriterFacts; Phase=60 12/12 GREEN, Release+Debug 0/0 warnaserror; LOOP-03/04; commits 70d0ff0, 9d8cdb8
- [x] 60-03-PLAN.md - Heartbeat swap to per-instance frozen-healthy entry via shared writer; remove old ProcessorProjection write; re-point heartbeat facts (LOOP-02, LOOP-04) ✓ frozen-healthy beat via ProcessorLivenessWriter, old flat-key dual-write removed (D-05); heartbeat facts re-pointed to PerInstance+entry+index+L1; Phase=60 15/15 GREEN, Release+Debug 0/0 warnaserror; LOOP-02/04; commits 348bf89, 421823f
- [ ] 60-04-PLAN.md - Orchestrator inline unhealthy write per iteration + DI registration + 3 ctor-site facts + AddBaseProcessor descriptors + StartupUnhealthyWriteFacts (STATE-03, LOOP-01)

#### Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe
**Goal**: Two reader-side consumers of the reshaped keyspace land. (a) The **WebAPI orchestration-start gate** discovers a processor's replicas via `SMEMBERS skp:proc:{processorId}`, `GET`s each per-instance key, and admits the processor iff **≥1** replica is **present AND `status=healthy` AND non-stale** (`timestamp + interval×2 > now`) — a present-but-unhealthy or stale replica fails *that* replica (presence no longer implies live); when none qualify, orchestration start is blocked **422 + RFC 7807**, and an absent/TTL-expired index member is skipped and lazily `SREM`'d (self-healing). (b) The **self-watchdog liveness probe** reads the in-memory L1 record and reports `unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace (detecting a silently-crashed startup/heartbeat loop while the host stays up), returning the per-schema `summary` in its body. K8s probe wiring is future; this delivers the probe semantics.
**Depends on**: Phase 60 (the gate reads the per-instance keys + index the loops populate; the probe reads the in-memory L1 record the loops maintain)
**Requirements**: GATE-01, GATE-02, GATE-03, PROBE-01, PROBE-02
**Success Criteria** (what must be TRUE):
  1. The orchestration-start validator discovers replicas by `SMEMBERS skp:proc:{processorId}` and reads each per-instance key with no prior knowledge of instanceIds (replacing the single-key `present ⟺ live` read).
  2. A processor passes the gate iff **≥1** replica is present AND `status=healthy` AND non-stale (`timestamp + interval×2 > now`); a present-but-`unhealthy` replica and a stale replica each fail *that* replica (a single healthy-and-fresh replica admits the workflow even when siblings are unhealthy/stale).
  3. When no replica satisfies the gate, orchestration start is blocked with **422 + RFC 7807** (genuine Redis faults still surface as 500, not 422); an absent/TTL-expired index member is skipped and lazily `SREM`'d from the index.
  4. The processor's liveness probe reads the in-memory L1 record and reports `unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace — a silently-crashed startup or heartbeat loop makes the probe fail while the host process stays up.
  5. The probe returns the per-schema `summary` in its response body (so the future K8s restart trigger has the diagnostic it needs).
**Plans**: TBD
**UI hint**: yes

#### Phase 62: Live Proof & Close Gate
**Goal**: A real-stack end-to-end proof that the reshaped per-replica liveness works live — two replicas of a processor each self-register a per-instance key under the instance index; a restarting replica is visible as `unhealthy`; orchestration start admits a workflow when ≥1 replica is healthy-and-fresh and blocks 422 when none are; the self-watchdog probe fails on a silently-stale L1 — sealed behind the milestone close gate (triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` BEFORE==AFTER net-zero, N=3 consecutive GREEN), mirroring shipped Phases 49/55/58.
**Depends on**: Phase 61 (the full per-replica liveness + gate + probe path must exist before it can be proven live and closed)
**Requirements**: TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):
  1. RealStack E2E: two replicas of one processor each write a distinct per-instance key `skp:proc:{processorId}:{instanceId}` and `SADD` themselves to the instance-index SET; a starting/failed replica is observable in L2 as `unhealthy` (never absent), and a dead replica's key TTL-expires + is lazily `SREM`'d. (TEST-01)
  2. RealStack E2E: orchestration start admits a workflow when **≥1** required-processor replica is healthy-and-fresh (even with an unhealthy/stale sibling) and is blocked **422 + RFC 7807** when no replica qualifies; the self-watchdog probe returns `unhealthy` + the per-schema `summary` when the in-memory L1 record is stale beyond the active-interval ×2 grace. (TEST-02)
  3. The milestone close gate holds — N=3 consecutive GREEN + triple-SHA (psql `\l` / redis-cli `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER net-zero, DLQ depth 0 — at Release + Debug 0-warning. (TEST-03)
**Plans**: TBD

## ✅ v6.0.0 Config & Payload Validation Hardening (SHIPPED 2026-06-13)

**Milestone Goal:** Guarantee that any processor which reaches *Healthy* can deserialize every orchestration-admitted payload — by giving authors a **typed base-config seam** on `BaseProcessor` (the framework deserializes the dispatch `payload` into the author's typed config, replacing the raw-string `ProcessAsync(string validatedData, string payload)` seam at `BaseProcessor.cs:29`) and gating processor health on a startup **config-schema↔config-type compatibility check (Gate A)**. On a Gate A incompatibility the processor withholds `MarkHealthy` (the latch is one-way — `ProcessorContext.cs:83-89`), so its liveness heartbeat no-ops (`ProcessorLivenessHeartbeat.cs:70`), no `skp:{id}` L2 key is written, and the existing orchestration-start `ProcessorLivenessValidator` blocks any workflow using that processor (422). Config incompatibility is **terminal** (permanent, not retry-transient like a missing definition); a null `ConfigSchemaId` skips Gate A (null-is-skip). Complements the already-shipped WebAPI **Gate B** (`PayloadConfigSchemaValidator`, `payload ⊨ ConfigSchemaId` at orchestration start, RETAINED UNCHANGED). Net effect: `payload ⊨ ConfigSchemaId` (Gate B) ∧ `ConfigSchemaId ⊨ configType` (Gate A) ⟹ the delivered payload always deserializes — runtime payload-deserialization exceptions become impossible except for in-transit payload mutation. **Breaking change** to the `BaseProcessor` author contract (like v4.0.0's Pre/In/Post seam break). Phases continue at **56**.

**Source of truth:** Locked Gate A / Gate B analysis (this milestone's planning conversation, 2026-06-12). Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (10 CFG reqs). Three open decisions are deliberately deferred to `/gsd-spec-phase`: (1) Gate A compatibility direction/fidelity; (2) terminal-unhealthy mechanics (the latch is binary — likely "withhold Healthy + a separate diagnostic"); (3) TOCTOU policy (CFG-10 — immutability vs re-validate-on-change).

**Build order (locked, dependency-driven — the config-seam is the prerequisite that makes Gate A possible):** 56 (typed base-config seam) → 57 (startup config-schema fetch + Gate A + TOCTOU policy) → 58 (orchestration-gate integration proof + live close).

#### Phase 56: Typed Base-Config Seam
**Goal**: Processor authors declare configuration as a typed class inheriting a framework-provided base config; the framework deserializes the dispatch `payload` into that typed config and supplies it to the author's transform — replacing the raw-string `payload` parameter. `Processor.Sample` is the migrated worked example. This is the prerequisite seam that makes a config-type↔config-schema compatibility check (Gate A, Phase 57) possible.
**Depends on**: — (first phase of v6.0.0; builds on the shipped v5.0.0 `BaseProcessor` pipeline)
**Requirements**: CFG-01, CFG-02
**Success Criteria** (what must be TRUE):
  1. A processor author can declare its configuration as a typed class inheriting a framework-provided base config, and override the transform to receive that typed config instance (no raw `payload` string in the author seam).
  2. The framework deserializes the dispatch `payload` into the author's typed config before invoking the transform; a payload that does not deserialize into the config type surfaces as a deterministic failure (not a silent corruption).
  3. `Processor.Sample` is migrated to the typed-config seam as the worked example — the old raw-string `ProcessAsync(string validatedData, string payload)` deserialize is removed (clean break), and `Processor.Sample` still completes a round-trip.
  4. Solution builds 0-warning (Release + Debug); the hermetic suite is green with the new seam.
**Plans**: 2 plans (2 waves)
- [x] 56-01-PLAN.md — Framework typed-config seam (empty marker + shared options + generic deserialize layer) + Processor.Sample clean-break migration (CFG-01, CFG-02)
- [x] 56-02-PLAN.md — Hermetic suite migration to the typed seam + deser-failure StepFailed fact + 0-warning dual-config build & full-suite gate (CFG-01, CFG-02)

#### Phase 57: Startup Config-Schema Fetch + Gate A
**Goal**: At startup the processor fetches the `ConfigSchemaId` definition (extending Loop B at `ProcessorStartupOrchestrator.cs:124`, lifting the D-05 "never read the config schema id" carve-out) and stores it on `ProcessorContext`; Gate A then validates that the concrete config type *covers* that definition. On incompatibility the processor never reaches Healthy (withholds `MarkHealthy`, terminal — not retried); a missing definition stays transient (retry, boot-before-register); a null `ConfigSchemaId` skips Gate A. The spec-locked TOCTOU policy (immutability or re-validate-on-change) closes the schema-mutation window between this startup check and a later orchestration-start Gate B check.
**Depends on**: Phase 56 (the typed config seam must exist before its type can be compared against the config-schema definition)
**Requirements**: CFG-03, CFG-04, CFG-05, CFG-06, CFG-07, CFG-10
**Success Criteria** (what must be TRUE):
  1. When `ConfigSchemaId` is non-null, startup fetches the config-schema definition over the bus and stores it on the processor context; a missing definition is transient (the startup loop retries on `SchemaDefinitionNotFound`/timeout exactly as for input/output definitions — boot-before-register tolerated).
  2. Gate A validates that the concrete config type covers the fetched config-schema definition (every payload valid under `ConfigSchemaId` deserializes into the config type — direction/fidelity locked during spec).
  3. On a Gate A incompatibility the processor never reaches Healthy — `MarkHealthy` is withheld, the heartbeat no-ops (`ProcessorLivenessHeartbeat.cs:70`), no `skp:{id}` L2 key is written, the reason is logged, and the incompatibility is terminal (not retried like a missing definition).
  4. A processor with a null `ConfigSchemaId` skips Gate A entirely and reaches Healthy on identity + input/output definitions alone (null-is-skip, matching `ProcessorStartupOrchestrator.cs:127-128` and `PayloadConfigSchemaValidator.cs:42`).
  5. The config-schema definition mutation window between the startup Gate A check and a later orchestration-start Gate B check is closed by the spec-locked TOCTOU policy (immutability or re-validate-on-change), with a test recording the chosen mechanism.
**Plans**: 4 plans
Plans:
- [x] 57-01-PLAN.md — Wave 0 (BLOCKING): STJ rule-table spike + RED test scaffolds (covers facts, freeze integration, inverted/extended harness facts)
- [x] 57-02-PLAN.md — Gate A covers-checker `ConfigSchemaCoverageCheck.Evaluate` (CFG-05/07)
- [x] 57-03-PLAN.md — Wire Gate A: Loop B config fetch + ConfigDefinition + decoupled MarkReady/MarkHealthy (CFG-03/04/06/07)
- [x] 57-04-PLAN.md — Frozen-once-referenced schema Definition + 409 handler (CFG-10)

#### Phase 58: Orchestration-Gate Integration Proof & Close
**Goal**: A real-stack end-to-end proof that Gate A composes with the existing orchestration-start liveness gate — a config-incompatible (never-Healthy) processor blocks orchestration start with 422 via `ProcessorLivenessValidator` ("absent"), while a config-compatible processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally (Gate A is not a false-positive blocker) — sealed behind the milestone close gate.
**Depends on**: Phase 57 (Gate A must withhold/grant Healthy before the orchestration-start gate integration can be proven end-to-end)
**Requirements**: CFG-08, CFG-09
**Success Criteria** (what must be TRUE):
  1. RealStack E2E: an orchestration whose graph includes a config-incompatible (never-Healthy) processor is blocked at orchestration start with 422 via the existing `ProcessorLivenessValidator` ("absent").
  2. RealStack E2E: a config-compatible processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally — proving Gate A is not a false-positive blocker (the negative-control).
  3. The milestone close gate holds — N-consecutive-GREEN + triple-SHA (psql/redis/rabbitmq) BEFORE==AFTER net-zero — at Release + Debug 0-warning.
**Plans**: 5 plans (3 waves)
- [x] 58-01-PLAN.md - Processor.BadConfig project (clashing TConfig, distinct SourceHash) + SK_P.sln (CFG-08)
- [x] 58-02-PLAN.md - Two-schema GET-or-create seed helpers + compatible Sample seed path + SC1/2/3 retag Phase 58 (CFG-09)
- [x] 58-03-PLAN.md - Profile-gated processor-badconfig compose service + Gate-A composition E2E (CFG-08 clash-log+absent+422 / CFG-09 204) (CFG-08, CFG-09)
- [x] 58-04-PLAN.md - phase-58-close.ps1 (verbatim phase-55 triple-SHA + two-schema/two-processor CREATE-IF-ABSENT seed) (CFG-08, CFG-09) (completed 2026-06-13 — dual embedded-hash read Sample+BadConfig, two-schema/two-processor CREATE-IF-ABSENT seed never PUT, no badconfig SHA exclusion/liveness pre-flight, triple-SHA block verbatim; AST PARSE OK; 5e283bb)
- [x] 58-05-PLAN.md - 58-HUMAN-UAT.md operator runbook + live N=3 GREEN close gate (ticks CFG-08, CFG-09) (CFG-08, CFG-09) (completed 2026-06-13 — live N=3 close gate exit 0: triple-SHA BEFORE==AFTER psql ed52e389/redis e3b0c442/rabbitmq 88000972, 568 facts x3, skp-dlq-1 depth 0, skp:msg:* 0; CFG-08 three-signal 422 + CFG-09 204 both GREEN; mid-run fix bfa5a65 corrected the Gate-A clash-log ES query; 6faa169, bfa5a65, 52f2f81)

## 🚧 v5.0.0 Recovery Re-architecture — messageId slot-array + 3-state keeper (In Progress — started 2026-06-11)

**Milestone Goal:** Replace v4.0.0's keeper-owned composite-backup recovery (Model B — `UPDATE`/`CLEANUP`, 5 states) with a **processor-owned `messageId` slot-array** recovery model + a **3-state keeper** (`REINJECT`/`INJECT`/`DELETE`). Per-message `L2[messageId][x]=entryId` allocation index (allocation-before-data), retired to `guid.empty` only **after** a confirmed orchestrator send; split infra taxonomy (`infra_messageId`→drop / `infra_entryId`→`INJECT`); a recovery branch (`if exist L2[messageId]`) that re-sends completed + `REINJECT`s-without-deleting-source on any unverifiable entry; configurable **DLQ1-vs-sustained-outage** keeper exhaustion; **gate-closed non-destructive consume**. Retains the v4.0.0 BIT gate + global pause/resume (A14), four typed `Step*` records (A15), at-least-once/no-dedup (A16), single `skp-dlq-1` (A4). Breaking — supersedes the v4.0.0 recovery core.

**Source of truth:** [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) → "Recovery Re-architecture (A18)" (LOCKED 2026-06-11) + "Active Index GC (A19)" (LOCKED 2026-06-12 — active terminal index delete + atomic both-key keeper GC). Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (24 reqs).

**Build order (locked, build-before-teardown):** 50 (contracts + slot-array key reshape) → 51 (processor forward + recovery pipeline) → 52 (3-state keeper) → 53 (Model-B teardown) → 54 (terminal index delete + atomic keeper GC) → 55 (live proof + close gate).

#### Phase 50: Contracts & Slot-Array L2 Key Reshape
**Goal**: The new recovery vocabulary exists — the `L2[messageId][x]=entryId` slot-array allocation-index key builder is defined, the three surviving Keeper-state contracts (`REINJECT`/`INJECT`/`DELETE`) carry their A18 id sets (`INJECT` carries `data`+`deleteEntryId`, `REINJECT` carries source `entryId`+`Payload`, `DELETE` carries `entryId`), and the Model-B contracts (`UPDATE`/`CLEANUP` + composite backup key + `BackupOptions`) are removed at the contract level — solution buildable.
**Depends on**: — (first phase of v5.0.0; builds on shipped v4.0.0 contracts)
**Requirements**: RETIRE-01, RETIRE-02 (contract-level)
**Success Criteria**:
  1. `L2ProjectionKeys` exposes a slot-array index builder `L2[messageId][slot]` (golden-test-pinned) alongside the retained no-TTL GUID data key.
  2. `UPDATE`/`CLEANUP` contracts + the composite backup key `corr:wf:proc:exec` + `BackupOptions` are deleted; a source/reflection scan finds no references.
  3. `INJECT`/`REINJECT`/`DELETE` carry their A18 id sets in `Messaging.Contracts`.
  4. Solution builds 0-warning (Release + Debug); hermetic suite green.
**Plans**: 2 plans (2 waves)
- [x] 50-01-PLAN.md — Additive contract surface: MessageIndex slot-array key builder + golden pin + KeeperInject A18 id-set (RETIRE-02; no deletions)
- [x] 50-02-PLAN.md — Atomic Model-B teardown: delete UPDATE/CLEANUP/BackupOptions/composite key, re-home single-owner endpoint, stub survivors, reconcile test surface + reflection guard (RETIRE-01, RETIRE-02)

#### Phase 51: Processor Forward + Recovery Pipeline
**Goal**: `ProcessorPipeline` runs the slot-array forward pass (allocation-before-data, split infra, per-item dispatch, source-delete tail) and the `if exist L2[messageId]` recovery pass (temp-list, send-before-retire, `REINJECT`-no-source-delete), replacing the Model-B Post-Process backup/cleanup mechanics.
**Depends on**: Phase 50
**Requirements**: SLOT-01, SLOT-02, SLOT-03, INFRA-01, INFRA-02, FWD-01, FWD-02, FWD-03, RECOV-01, RECOV-02, RECOV-03
**Success Criteria**:
  1. Forward Post writes the allocation index before the data key; allocation-write exhaustion → `infra_messageId`→drop, data-write exhaustion → `infra_entryId`→keeper `INJECT`.
  2. Forward dispatch routes per item (non-infra→orchestrator / `infra_entryId`→`INJECT` / `infra_messageId`→drop); happy-path tail deletes the source `entryId` (exhaustion→`DELETE`).
  3. `NOT exist L2[messageId]` runs the forward pass; existence-check/source-read exhaustion → `REINJECT` (input intact).
  4. `exist L2[messageId]` runs the recovery pass — temp-list per slot, `completed`→re-send+retire(`guid.empty`), not-exist→drop, `infra_entryId`→preserve slot; any `infra_entryId`→`REINJECT` without deleting source, else delete source.
  5. Hermetic facts prove forward + recovery flows; solution 0-warning.
**Plans**: 3 plans
- [x] 51-01-PLAN.md — SlotArrayOptions (TTL random) record + DI bind + bind facts (Wave 0) ✓ SlotArrayOptions sealed class (300/600, D-04/D-05) + Configure<SlotArrayOptions>(Processor section) + 2 bind facts; ProcessorOptionsBindingFacts 4/4 GREEN, SK_P.sln Release 0/0; SLOT-01; commits 6433c27, 368b5e3
- [x] 51-02-PLAN.md — Pipeline dispatcher + FORWARD pass, BuildInject fix, WR-01 finally removal, messageId seam, forward facts (Wave 1) ✓ A18 thin dispatcher (exist L2[messageId] branch) + RunForwardAsync (allocation-before-data, split infra infra_messageId/infra_entryId, inline source-delete tail; WR-01 finally RETIRED) + BuildInject id-set fix + consumer messageId null fail-fast; Pipeline facts 26/26 GREEN, SK_P.sln Release+Debug 0/0; UseMessageRetry=keep-latch (none deferred to Phase 53); SLOT-01/02, INFRA-01/02, FWD-01/02/03; commits 5412ca6, c4df040, ec8567e
- [x] 51-03-PLAN.md — RECOVERY pass (temp-list, send-before-retire, REINJECT-no-source-delete), recovery + consumer facts (Wave 2)

#### Phase 52: 3-State Keeper
**Goal**: The Keeper recovery consumer applies the three surviving states gate-open-only — `REINJECT` (read source / re-inject with payload), `INJECT` (forward-only write→send→delete), `DELETE` — with gate-closed non-destructive consume and a configurable DLQ1-vs-sustained-outage exhaustion policy.
**Depends on**: Phase 51
**Requirements**: KEEP-01, KEEP-02, KEEP-03, KEEP-04, KEEP-05
**Success Criteria**:
  1. `REINJECT` reads source `entryId` (drops if absent) and re-injects a reconstructed `EntryStepDispatch` carrying `Payload`; `INJECT` writes `L2[entryId]=data`→sends `StepCompleted`→deletes `deleteEntryId`; `DELETE` deletes the key (drops if absent).
  2. The keeper performs an L2 op only when the BIT gate is open; gate-closed does not dequeue-and-drop (consumption pauses / requeues without ack).
  3. The exhaustion policy is config-driven: DLQ1 mode dead-letters to `skp-dlq-1`; sustained-outage mode holds/requeues for L2 recovery.
  4. Hermetic facts prove each state + the gate-closed and exhaustion-policy behaviors; solution 0-warning.
**Plans**: 3 plans
- [x] 52-01-PLAN.md — Three recovery-state bodies (REINJECT drop-flip, INJECT forward-only, DELETE verify) + base gate-wait strip + KeeperMetrics drop counter (Wave 1) ✓ completed 2026-06-11; KEEP-01/02/03; 16/16 Keeper facts, 518/518 hermetic, Release 0/0
- [x] 52-02-PLAN.md — keeper-recovery static→ConnectReceiveEndpoint conversion + handle singleton + configurable exhaustion policy (Dlq1 vs SustainedOutage) + integration facts (Wave 2) ✓ completed 2026-06-11; KEEP-04/05; 18/18 Keeper facts, 520/520 hermetic, Release 0/0; commits fd72d9d, 266b80c, 5f24bb9
- [x] 52-03-PLAN.md — BitHealthLoop endpoint Stop/Start driver on BIT health edges + driver facts + full keeper-suite green gate (Wave 3) ✓ completed 2026-06-11; KEEP-04 (end-to-end); BitHealthLoop now Stops the recovery endpoint on the unhealthy edge / Starts it on the healthy edge (additive to gate.Open/Close + PauseAll/ResumeAll, under WR-01); BitHealthLoopTests 8/8, Keeper namespace 32/32, solution Debug 0/0; commits 63bff7a, fed352e

#### Phase 53: Model-B Teardown
**Goal**: The v4.0.0 Model-B recovery surface is fully removed — composite backup key, `UPDATE`/`CLEANUP` consumers, and the 5-state consumer collapsed to the 3 surviving states — leaving the system buildable on the slot-array path alone.
**Depends on**: Phase 52
**Requirements**: RETIRE-03 (RETIRE-01/02 contract removal lands in Phase 50; remnant-verified here)
**Success Criteria**:
  1. The composite backup key + its TTL are gone (remnant-verified); `UPDATE`/`CLEANUP` consumers + definitions deleted.
  2. The recovery consumer registers exactly 3 states (`REINJECT`/`INJECT`/`DELETE`); a reflection/source sweep finds no Model-B remnant.
  3. Full hermetic suite green; solution 0-warning Release + Debug.
**Plans**: 3 plans (Wave 0 guards, then 2 parallel removal plans)
- [x] 53-01-PLAN.md — Standing guards FIRST (5→3 reflection + D-01/D-03/D-07 source-scan facts; land RED) (Wave 0)
- [x] 53-02-PLAN.md — Orchestrator retry teardown (strip UseMessageRetry + dead IOptions/Ignore<> from all 5 owners) (Wave 1)
- [x] 53-03-PLAN.md — Processor keep-latch removal + comment reconcile + D-03 ConfigureError→keeper move + SC-3 gate (Wave 1)

#### Phase 54: Terminal Index Delete + Atomic Keeper GC
**Goal**: The processor actively reclaims the `L2[messageId]` allocation index at end-of-message — the happy-path tail (forward + recovery all-clear) deletes BOTH the source `L2[entryId]` and the `L2[messageId]` index in one atomic multi-key `DEL`, escalating a delete exhaustion to a keeper `DELETE` that now carries `{messageId, entryId}` and deletes both keys atomically (persisting the index's TTL on escalate). Restores the v4 CLEANUP-grade deterministic net-zero that A18's TTL-only index reclaim gave up.
**Depends on**: Phase 53
**Requirements**: GC-01, GC-02, GC-03
**Design**: [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) → "Active Index GC (A19)" (LOCKED 2026-06-12)
**Success Criteria**:
  1. The forward + recovery all-clear tails delete the source `entryId` AND the `messageId` index as a single atomic Redis multi-key `DEL`; the index is no longer left to its random TTL on a non-crash path.
  2. The terminal two-key delete is mutually exclusive with `REINJECT` — on any `infra_entryId`/`REINJECT` neither key is deleted (the index survives for the replay's recovery pass).
  3. A terminal-delete exhaustion `PERSIST`s the `L2[messageId]` index (cancels its TTL) and escalates to keeper `DELETE` carrying `{messageId, entryId}`; the `DELETE` consumer deletes both keys atomically (drop-on-absent).
  4. Hermetic facts prove the atomic two-key delete, the `REINJECT`-mutual-exclusion preservation, the persist-on-escalate, and the both-key keeper `DELETE`; solution 0-warning (Release + Debug).
**Plans**: 4 plans (3 waves)
- [x] 54-01-PLAN.md — Test-kit mock scaffolding: array `KeyDeleteAsync(RedisKey[])` overload + `KeyPersistAsync` stubs on every tail mux, array When/Do on fault muxes, new `ReadOkDeleteAndPersistFaultL2` persist-exhaust sibling (Wave 1) (GC-01/02/03 enabling)
- [x] 54-02-PLAN.md — Contract: `KeeperDelete.MessageId` init prop (D-05, additive) (Wave 1) (GC-03)
- [x] 54-03-PLAN.md — Production: unified `DeleteTerminalAsync` (atomic two-key DEL + best-effort persist-on-escalate + `KeeperDelete{messageId}` handoff, D-01/02/03/06) rewiring both tails + `BuildDelete(d, messageId)` + `DeleteConsumer` both-key DEL (Wave 2) (GC-01/02/03)
- [x] 54-04-PLAN.md — Facts: invert/harden `PipelineEndDeleteFacts` + `PipelineRecoveryFacts` + `DeleteConsumerFacts` to the atomic array shape (ONE array call + scalar `DidNotReceive`) + persist/MessageId asserts + new `EndDelete_PersistExhaust_StillSendsKeeper` + 0-warning dual-config gate (Wave 3) (GC-01/02/03, AC-1..10)

#### Phase 55: Live Proof & Close Gate
**Goal**: A real-stack E2E proves the slot-array forward + recovery passes, each keeper state, and the A19 active two-key index delete, sealed behind an N-consecutive-GREEN triple-SHA net-zero close gate.
**Depends on**: Phase 54
**Requirements**: TEST-01, TEST-02
**Success Criteria**:
  1. RealStack E2E proves the forward pass (dispatch→slot-array write→orchestrator advance) and the recovery pass.
  2. RealStack E2E proves each keeper state: `REINJECT` (source-present re-inject / source-absent drop), `INJECT`, `DELETE`.
  3. Close gate N×GREEN + triple-SHA (psql/redis/rabbitmq) BEFORE==AFTER net-zero — slot-array index + data keys leak-free, proven by the A19 active delete (not a TTL settle) — at Release+Debug 0-warning.
**Plans**: 4 plans
Plans:
- [x] 55-01-PLAN.md — clone phase-49 close gate → phase-55-close.ps1 (composite removed, skp:msg:* count==0 added) + operator runbook (TEST-02)
- [x] 55-02-PLAN.md — adapt SC1 (slot-array index + A19 net-zero), retag SC3, delete composite sweeps (TEST-01)
- [x] 55-03-PLAN.md — rewrite SC2 (3-state + both-key DELETE) + organic recovery test (TEST-01)
- [x] 55-04-PLAN.md — autonomous build gate (D-08) + operator-gated live N=3×GREEN close run (D-09)

## ✅ v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume (SHIPPED 2026-06-07)

Archived: [milestones/v3.7.0-ROADMAP.md](milestones/v3.7.0-ROADMAP.md) · [REQUIREMENTS](milestones/v3.7.0-REQUIREMENTS.md) · [AUDIT](milestones/v3.7.0-MILESTONE-AUDIT.md). 10 phases / 32 plans / 37 requirements satisfied + live-proven (Phase-39 close gate: 3×500 GREEN, triple-SHA net-zero; audit `tech_debt`, 0 functional blockers). Full milestone detail below (collapsed).

<details>
<summary>✅ v3.7.0 Keeper (Phases 33-42) — SHIPPED 2026-06-07</summary>

**Milestone Goal:** Make the autonomous execution loop (cron fire → dispatch → process → result → fan-out) **self-heal through transient L2 (Redis) outages without operator intervention.** A new multi-replica `Keeper` console reacts to the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` events that the execution-path consumers publish on retry-budget exhaustion, probes L2 health on a bounded loop, pauses the affected workflow's cron (via the single-replica orchestrator's in-memory L1) so the outage stops spreading, re-injects recovered work to its origin (riding the receiver's existing `flag[H]` idempotency), resumes when no recoveries remain pending, and parks the genuinely unrecoverable in `keeper-dlq` for operator triage. Keeper is the automated operator for v3.6.0's accepted "an infra-faulting workflow keeps dead-lettering until an operator intervenes" gap. Operator commands (Start/Stop) are out of scope — a failed Start/Stop is simply re-issued.

**Build order (locked):** 33 (spike — de-risk the `Fault<T>`→reinject→`flag[H]`-collapse round-trip) → 34 (Keeper console foundation) → 35 (fault intake + correlation) → 36 (L2 probe loop + two DLQs) → 37 (orchestrator pause/resume) → 38 (metrics + real-stack E2E + close gate).

- [x] **Phase 33: Fault-Recovery Spike (de-risk)** — Prove `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` consumption via pub/sub, inner-message + 6-id correlation extraction, re-inject to origin, and receiver `flag[H]` collapse — before building anything. (INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06) (completed 2026-06-05 — LIVE-PROVEN: spike GREEN + close gate GATE_EXIT=0, 453 facts ×3, triple-SHA net-zero held; 2 trip recipes corrected, c2d6ea6)
- [x] **Phase 34: Keeper Console Foundation** — Runnable multi-replica `Keeper` on `BaseConsole.Core`; builds, containerizes, joins compose healthy; competing-consumer load-balancing. (KEEP-01, KEEP-02, KEEP-03) (completed 2026-06-05 — hermetic 4/4: round-robin test consumed==1, ComposeYamlFacts shape guards, docker build green, 0-warning Release+Debug, 454-pass suite; live multi-replica + compose-health smokes operator-pending, 34-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 35: Fault Intake & Correlation** — Production intake of the two `Fault<T>` events; extract 6-id tuple + `H`; open execution log-scope; `_error` → TTL'd forensic DLQ-1 only. (INTAKE-03, KMET-04) (completed 2026-06-05 — hermetic: BuildState byte-identical refactor (6 scope-guard classes GREEN), two real `Fault<T>` consumers on `keeper-fault-recovery` with manual CorrelationId scope + KeeperFaultConsumerScopeTests 3/3 (SC2), 0-warning Release; INTAKE-03 separation slice + KMET-04 hermetic-proven; SC3 live ES-correlation operator-pending, 35-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 36: L2 Health-Probe Recovery Loop & DLQs** — Bounded crash-survivable L2 read+write probe loop; re-inject on success, give-up to `keeper-dlq` (DLQ-2); ack-after-loop; two DLQs split by exhaustion mechanism (Immediate(N) → DLQ-1, probe → DLQ-2); shared `Immediate(N)` from appsettings across all consumers. (PROBE-01..05, DLQ-01..04) (completed 2026-06-06 — hermetic 5/5 SC verified against source: `L2ProbeRecovery` bounded loop (RedisException-only, 5×12=60s<1800s), both consumers re-inject-by-type | park-original-to-`keeper-dlq`, ack-after-loop, consolidated `skp-dlq-1` (7d TTL) wired once in `BaseConsole.Core` keeping `GenerateFaultFilter`; 467-pass hermetic suite, 0-warning Release; code review 0 critical / 3 warning; live recover/give-up + kill-mid-loop operator-pending, 36-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 37: Orchestrator Pause/Resume Coordination** — New `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator consumers; per-workflow pending-recovery set keyed by `H` in single-replica L1; idempotent; stays paused on give-up. *(Only phase touching the Orchestrator project.)* (PAUSE-01..05) (completed 2026-06-06 — hermetic 5/5 SC verified against source: deterministic-TriggerKey three-state model (Quartz `PauseJob`/`GetTriggerState`, no L1 state field), `ConcurrentMessageLimit=1` consumers on dedicated fan-out endpoint, Keeper `Publish` Pause-at-intake + Resume-on-Recovered (GaveUp parks, no Resume); 477-pass hermetic suite (`--filter-not-trait Category=RealStack`), 0-warning Release; **caught + fixed a load-bearing 37-02 self-reschedule regression** (`RescheduleAsync` `ScheduleJob`→`RescheduleJob` replace, `571498f`) the 37-04 run had mis-filed as pre-existing; code review 0 critical / 2 warning; live pause↔resume bus round-trip operator-pending, 37-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 38: Uniform `service_name` + Instance Labels Across All Metrics** — `service_name={name}_{version}` + `service_instance_id` on every metric series (runtime/HTTP/business); processor name+version sourced from the DB (not appsettings); logs `service.name` unchanged; Prometheus query consumers updated. (MLBL-01..05) (completed 2026-06-06 — verified 5/5 must-haves: combined `service_name={name}_{version}` on the metrics resource for all 4 consoles + non-empty `service_instance_id` across runtime/HTTP/business; processor name/version DB-sourced via the `MeterProviderHolder` swap after identity Loop A; LOGS `service.name` stays bare (LogsResourceBareNameFacts); PromQL consumers reconciled (0 bare literals). Hermetic 479-pass / 0-warning Release; live RealStack `MetricsRoundTripE2ETests` 1/1 GREEN after container rebuild — :9090 scrape proved sk-api_3.2.0 / orchestrator_3.4.0 / DB-sourced sample-proc-…_1.0.0 (placeholder count=0); no live operator step. Code review 0 critical / 2 warning (MeterProviderHolder lifecycle, advisory))
- [x] **Phase 39: Keeper Observability + Real-Stack E2E + Close Gate** (2026-06-06) — `Keeper` meter + counters/histograms; E2E proving recover-both-paths + give-up; 3×GREEN triple-SHA net-zero close gate (both DLQs + scratch-key scan-clean). (KMET-01/02/03, TEST-01/02/03)

### Gap-Closure Phases (v3.7.0 audit — 2026-06-06)

- [x] **Phase 40: Keeper Recovery Hardening** (2026-06-06 — KHARD-01/02/03 verified 9/9; live 3×-GREEN close-gate Manual-Only, tracked in 40-HUMAN-UAT.md) — Bound the recover→reinject cycle with a config attempt cap (persistent fault parks instead of flooding the stack); make the keeper-dlq give-up-park drain deterministic (poll-until-stably-empty teardown → close-gate `keeper-dlq depth==0` holds); extract the shared fault-consumer recovery logic so the cap lands in one place. (KHARD-01, KHARD-02, KHARD-03)
- [x] **Phase 41: Orchestrator Pause/Resume Diagnostics** — Log on the `ResumeAsync` silent-ignore path (dropped Resume becomes diagnosable); harden `WorkflowScheduler.RescheduleAsync` fallback against a purged non-durable job. (closes 37-REVIEW WR-01, WR-02) (completed 2026-06-07 — 2/2 plans, verifier 2/2 SC, code review clean; LogInformation on ResumeAsync ignore branch (log-only, D-01/D-02), RescheduleAsync threaded workflowId + null-fallback re-creates job+trigger (D-04), RescheduleSchedulingTests asserts re-establishment (D-06); hermetic 505/0, Release 0-warning)
- [x] **Phase 42: v3.7.0 Docs & Traceability Reconciliation** — Flip stale REQUIREMENTS.md checkboxes `[ ]→[x]` for satisfied INTAKE/PROBE/DLQ/PAUSE/KMET-04 + fix their traceability rows; add missing MLBL-01..05 rows + correct the footer count; fix ROADMAP Phase-38 progress row; backfill `39-VERIFICATION.md`. (doc-only) (completed 2026-06-07 — 3/3 plans, verifier 4/4 SC: SC1 20 checkboxes + 16 traceability rows, SC2 MLBL-01..05 + 34/34 footer, SC3 Phase-38 row 4/4, SC4 39-VERIFICATION.md backfilled; encoding clean, close-gate NOT re-run)

</details>

## Phases (shipped milestones)

<details>
<summary>✅ v3.2.0 Steps API MVP (Phases 1-11) — SHIPPED 2026-05-28</summary>

11 phases / 41 plans / 142 integration facts GREEN × 3 consecutive runs. Full phase details, decisions, and execution narrative archived to [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md).

- [x] Phase 1: Repository Scaffold (3/3 plans) — 2026-05-26
- [x] Phase 2: Postgres + Docker Compose (2/2 plans) — 2026-05-26
- [x] Phase 3: EF Core Persistence Base (2/2 plans) — 2026-05-27
- [x] Phase 4: Cross-Cutting Middleware + Error Handling (2/2 plans) — 2026-05-27
- [x] Phase 5: Observability + Health Probes (2/2 plans) — 2026-05-27
- [x] Phase 6: Validation + Mapping Base (2/2 plans) — 2026-05-27
- [x] Phase 7: Generic HTTP Base + Composition Root (2/2 plans) — 2026-05-27
- [x] Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests (8/8 plans) — 2026-05-28
- [x] Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop (3/3 plans) — 2026-05-28
- [x] Phase 10: Remove SchemaId on AssignmentEntity, add ConfigSchemaId on ProcessorEntity (5/5 plans) — 2026-05-28
- [x] Phase 11: Migrate Prometheus + Elasticsearch from compose stack sk2_1 to sk_p (10/10 plans) — 2026-05-28

</details>

<details>
<summary>✅ v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline (Phases 12-16) — SHIPPED 2026-05-29</summary>

5 phases / 26 plans / 235 integration facts GREEN × 3 consecutive runs, dual-SHA (`psql \l` + `redis-cli --scan`) BEFORE=AFTER held. 64/64 requirements satisfied (audit PASSED). Full phase details, success criteria, and decisions archived to [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md).

- [x] Phase 12: Redis infra + composition + healthcheck + DI registration (8/8 plans) — 2026-05-29
- [x] Phase 13: OrchestrationService split + L3 fetch + L1 build (3/3 plans) — 2026-05-29
- [x] Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) (5/5 plans) — 2026-05-29
- [x] Phase 15: L2 Redis projection write + Stop existence check (5/5 plans) — 2026-05-29
- [x] Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout (5/5 plans) — 2026-05-29

</details>

<details>
<summary>✅ v3.4.0 BaseConsole + Orchestrator Messaging (Phases 17-24 + 24.1) — SHIPPED 2026-06-01</summary>

9 phases / 31 plans. A reusable `BaseConsole.Core` Generic-Host library + a runnable `Orchestrator` console connected to the WebApi over MassTransit/RabbitMQ, with body-carried CorrelationId proven end-to-end (HTTP → Redis L2 → fan-out → orchestrator log in Elasticsearch), the full orchestrator lifecycle (L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown), the processor→orchestrator result round-trip + L1-only step advancement, and a gating redesign (L2-existence dedup, boot-gate/plugin removal, atomic Stop). Final clean-build suite 335/335 GREEN (real-stack E2E live), Release 0 warnings. 70/70 requirements (ORCH-GATE-01 superseded by 24.1). Milestone audit PASSED. Full phase details archived to [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md).

- [x] Phase 17: Messaging.Contracts + Shared L2 Root Extract (2/2 plans) — 2026-05-30
- [x] Phase 18: BaseConsole.Core Library (4/4 plans) — 2026-05-30
- [x] Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier (4/4 plans) — 2026-05-30
- [x] Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout (4/4 plans) — 2026-05-31
- [x] Phase 21: v3.4.0 Closeout Hygiene — shared L2ProjectionKeys (1/1 plan) — 2026-05-31
- [x] Phase 22: L2 Root-Parent Restructure + Processor Self-Registration (5/5 plans) — 2026-05-31
- [x] Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown (5/5 plans) — 2026-05-31
- [x] Phase 24: Orchestrator Result-Consume & Step Advancement (5/5 plans) — 2026-06-01
- [x] Phase 24.1: Gating Redesign — L2-dedup + Gate Removal (gap-closure) (1/1 plan) — 2026-06-01

</details>

<details>
<summary>✅ v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip (Phases 31-32.1) — SHIPPED 2026-06-05</summary>

4 phases / 9 active plans. The orchestrator↔processor round-trip became exactly-once-effect: deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded) + effect-first `flag[H]` CAS dedup at both hops → each step's downstream effect happens exactly once, no lost branch, `ProcessAsync` needs no idempotency logic. Content-addressed two-level L2 (blobs + manifest), per-edge merge (content-collapse), N×M manifest fan-out, configurable retry budget. A late course-correction reverted the planned cancelled circuit-breaker (Phase 32 → 32.1) to plain dead-lettering on exhaustion, preserving the Phase-31 idempotency layer. 14/14 active requirements satisfied + live-verified (Phase-32's 8 retired); audit tech_debt with 0 functional blockers; 32.1 close gate `GATE_EXIT=0` (3×GREEN=452, triple-SHA BEFORE==AFTER held). Full phase details, decisions, and the breaker-revert rationale archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md).

- [x] Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) (6/6 plans) — 2026-06-04
- [x] Phase 31.1: Close-Gate Redis Net-Zero (gap closure) (1/1 plan) — 2026-06-04
- [x] Phase 32: Cancelled Circuit-Breaker (5/6 plans) — **superseded by 32.1** (breaker reverted)
- [x] Phase 32.1: Dead-Letter on Exhaustion (Breaker Reverted) (2/2 plans) — 2026-06-05

</details>

## 🚧 v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip (In Progress)

**Milestone Goal:** Stand up a reusable `BaseProcessor.Core` library + a first concrete `Processor.Sample` console (the processor-side mirror of `BaseConsole.Core`/`Orchestrator`) that self-identifies via an assembly-embedded SourceHash, self-registers its liveness into Redis L2 (only-when-Healthy, lock-free), and runs the full live orchestrator→processor→orchestrator execution round-trip — with the actual transform isolated to one minimal `abstract ProcessAsync` seam. The v3.4.0 `Orchestrator`, the `EntryStepDispatch`/`ExecutionResult` wire contracts, and `ProcessorLivenessValidator` are all reused **unchanged**.

**Build order (locked, mirrors the v3.4.0 leaf→base→wiring→concrete→proof cadence):**
25 (leaf shared contracts + WebApi responders) → 26 (`BaseProcessor.Core`: library + identity + two-loop startup + liveness worker) → 27 (execution round-trip) → 28 (SourceHash MSBuild identity + `Processor.Sample` + real-stack E2E closeout).

- [x] **Phase 25: Shared Contracts + WebApi Responders** — Leaf contract extracts (`ProcessorProjection` public, `ExecutionData` key, `"Healthy"` constant, 2 request/response pairs) + relax the WebApi publish-only firewall to host `GetProcessorBySourceHash` + `GetSchemaDefinition` responders. (completed 2026-06-01)
- [x] **Phase 26: BaseProcessor.Core — Library, Identity & Liveness** — Reusable Generic-Host scaffold on `BaseConsole.Core`; two-loop startup (identity-by-SourceHash + schema-definition resolution via `IRequestClient` with retry); only-when-Healthy liveness heartbeat worker into Redis L2. (completed 2026-06-01)
- [x] **Phase 27: Execution Round-Trip** — Durable `queue:{processorId:D}` consumer bound at Healthy; L2 input resolution + input validation; the `abstract ProcessAsync` seam; per-result output validation + L2 data write + result minting + one-by-one `ExecutionResult` sends; ack-after-send / business-ack / infra-throw; inherited correlation.
- [x] **Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout** — MSBuild SourceHash target (SHA-256, lowercase 64-hex, LF-normalized, folded over base+concrete `.cs`) + assembly-metadata embed; first concrete `Processor.Sample` (dummy `ProcessAsync` + multistage Dockerfile + compose tier); real-stack E2E round-trip proof + 3-GREEN/triple-SHA close gate.
- [x] **Phase 29: Structured Execution-Scope Logging** — Ambient structured-attribute logs (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId) via MEL log scopes serialized by OTel `IncludeScopes` to Elasticsearch: unchanged `InboundCorrelationConsumeFilter` + new bus-wide `InboundExecutionScopeConsumeFilter` (execution id-set for `IExecutionCorrelated`, both consoles), shared `ExecutionLogScope` keys, skip `Guid.Empty`, per-result inner scope for minted ExecutionId/output EntryId, process-wide ProcessorId enricher from `IProcessorContext`, explicit scope in the Quartz `WorkflowFireJob`. (completed 2026-06-02)
- [x] **Phase 30: Runtime & Business Metrics** — Code-defined runtime + business metrics carrying a per-replica `service_instance_id` label (the pod name in k8s), set as a resource attribute in the base libs; new orchestrator + processor send/consume counters (labelled by `ProcessorId`, processor adds `outcome`) registered via `AddMeter` — enabling PromQL rate/diff analysis of orchestrator→processor dispatch throughput and per-processor outcome bottlenecks across replicas, with no high-cardinality `workflowId` label and no collector-side metric config. (planned 2026-06-02) (completed 2026-06-02)

## Phase Details

<details>
<summary>✅ v3.5.0 Phase Details (Phases 25-30) — shipped 2026-06-02</summary>

### Phase 25: Shared Contracts + WebApi Responders
**Goal**: The leaf shared-contract vocabulary both sides depend on exists in `Messaging.Contracts`, and the WebApi can answer identity + schema-definition bus requests — so the processor (built later) has something to query.
**Depends on**: Phase 24.1 (v3.4.0 close — `Messaging.Contracts`, `L2ProjectionKeys`, `ProcessorService.GetBySourceHashAsync`, and the publish-only `AddBaseApiMessaging` firewall all in place)
**Requirements**: CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03
**Success Criteria** (what must be TRUE):
  1. `ProcessorProjection` is public in `Messaging.Contracts.Projections` and is the single shared type used by both WebApi and (later) the processor — no duplicate definition.
  2. `L2ProjectionKeys.ExecutionData(Guid entryId)` returns `skp:data:{entryId:D}`, distinct from the existing `root`/`step`/`processor` key builders, with a golden test pinning the exact string.
  3. The liveness `status` value `"Healthy"` is a shared constant in `Messaging.Contracts` (single source of truth — writer and reader cannot desync), and the two `GetProcessorBySourceHash` / `GetSchemaDefinition` request/response record pairs are defined there.
  4. The WebApi bus join, extended from publish-only, answers a `GetProcessorBySourceHash` request with `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (or a not-found response) backed by `ProcessorService.GetBySourceHashAsync`, and a `GetSchemaDefinition(schemaId)` request with `{ Definition }` (or not-found) backed by the existing schema read.
  5. The CRUD surface is unaffected — existing WebApi HTTP behavior and the v3.4.0 publish path are unchanged (no regression in the existing suite).
**Plans**: 2 plans
- [x] 25-01-PLAN.md — Shared contract extracts in Messaging.Contracts (ProcessorProjection move, ExecutionData key, Healthy const, request/response record pairs + queue constants)
- [x] 25-02-PLAN.md — WebApi responder host (two-hook bus join extension + GetProcessorBySourceHash / GetSchemaDefinition dual-response consumers, firewall + Degraded-cap preserved)

### Phase 26: BaseProcessor.Core — Library, Identity & Liveness
**Goal**: A reusable `BaseProcessor.Core` library exists on which a concrete processor self-identifies via its embedded SourceHash, resolves its identity + schema definitions over the bus (retrying through boot-before-register), and self-registers liveness into Redis L2 — only while Healthy, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.
**Depends on**: Phase 25 (the request/response contracts + WebApi responders must exist before the processor can resolve identity/definitions)
**Requirements**: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01
**Success Criteria** (what must be TRUE):
  1. `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inheriting soft-dep Redis, embedded health probes, metrics-only OTel, MassTransit/RabbitMQ, inbound/outbound correlation filters), and `AddBaseProcessor` wires the startup orchestration so a concrete `Program.cs` stays minimal.
  2. At runtime the processor reads its SourceHash from assembly metadata via reflection, then resolves its identity (`Id` + three nullable schema Ids) by issuing a `GetProcessorBySourceHash` `IRequestClient` query, retrying on timeout/not-found until it succeeds (booting before the DB row exists is tolerated).
  3. For each non-null (input, output) schema Id the processor resolves the definition via a `GetSchemaDefinition` `IRequestClient` query (retry until resolved); null/optional schema Ids are skipped by design and never cause failure (the config schema is not resolved).
  4. A background heartbeat worker writes/refreshes `skp:{processorId:D}` every `Interval` seconds with `{ inputDefinition, outputDefinition, liveness{ timestamp, interval, status: "Healthy" } }`, re-applying the configured `Ttl` expiry each beat (sliding) — written **only once Healthy** (identity + all required definitions resolved); a starting/restarting/unhealthy replica does not write (orchestrator sees `absent`), and the written `interval` equals the configured delay so `timestamp + interval×2` staleness holds.
  5. The written L2 value shape exactly matches what the v3.4.0 `ProcessorLivenessValidator` reads (reused unchanged — presence+freshness ⟺ "≥1 replica healthy"), and multi-replica writes are lock-free: the shared liveness key is a blind whole-value `SET` of equivalent only-when-Healthy content (last-write-wins, no synchronization).
**Plans**: 3 plans
  - [x] 26-01-PLAN.md — Project skeleton + identity/seam/options contracts + Wave 0 test scaffold + exchange: request-client confirmation (BPC-01, BPC-02, IDENT-03, CONFIG-01) — completed 2026-06-01
  - [x] 26-02-PLAN.md — AddBaseProcessor composition root + two-loop startup orchestrator (BPC-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02) — completed 2026-06-01
  - [x] 26-03-PLAN.md — Only-when-Healthy liveness heartbeat worker + closed reader round-trip (LIVE-01..06) — completed 2026-06-01

### Phase 27: Execution Round-Trip
**Goal**: A Healthy processor consumes a real `EntryStepDispatch`, resolves + validates its input from L2, runs the `abstract ProcessAsync` transform, validates + writes each output to L2, mints results, and sends `ExecutionResult`s back to the orchestrator one-by-one — with the framework owning all id-minting, validation, L2 I/O, and sending so a concrete overrides only `ProcessAsync`.
**Depends on**: Phase 26 (the processor must resolve identity + definitions and be Healthy before it can bind the dispatch queue and validate input/output)
**Requirements**: EXEC-01, EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02
**Success Criteria** (what must be TRUE):
  1. The processor consumes `EntryStepDispatch` on a **durable** `queue:{processorId:D}` competing-consumer endpoint that is bound only once Healthy (and `Healthy` is written to L2 only after the bind), so the orchestrator never sends to a non-existent queue; a restarting/unhealthy processor leaves dispatches queued (not lost, not processed) until it recovers.
  2. Input data is read only from `L2[data(entryId)]` (existence-checked; `Payload` is config, never input) and validated against `inputDefinition` when present — an empty definition skips validation; a non-empty definition with missing/empty `entryId` yields a `Failed` result with an error message.
  3. The sole transform seam is `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` (`config` = dispatch `Payload`); for each result the framework validates output vs `outputDefinition` (empty = valid), mints a new `entryId` + writes the output to `L2[data(newEntryId)]` with the configured execution-data TTL on success (nothing written on output-validation failure → `Failed`), and mints a per-result `executionId` + stamps the shared Ids.
  4. Results are sent to `queue:orchestrator-result` one-by-one (never a batched list); an empty result list with no exception/cancellation acks only (no message), while `Failed` (incl. caught exceptions, with an error message) and `Cancelled` are always sent.
  5. The dispatch is acked only after all sends complete; infra faults throw and retry (`Immediate(3)`) (business-ack / infra-throw, mirroring the orchestrator), and the body `CorrelationId` is inherited from `BaseConsole.Core` — flowing from the dispatch into the log scope and onto every published `ExecutionResult`.
**Plans**: 3 plans
- [x] 27-01-PLAN.md — Foundation: firm ProcessResult + BaseProcessor internal invoker, port SSRF-locked Json.Schema validator, add CONFIG-02 ExecutionDataTtlSeconds (EXEC-03/04/05, CONFIG-02) — completed 2026-06-01
- [x] 27-02-PLAN.md — EntryStepDispatchConsumer: L2 input read/validate, ProcessAsync invoke, per-result output-validate/mint/write, one-by-one ExecutionResult send, business-ack/infra-throw (EXEC-02/04/05/06/07/08/09/10) — completed 2026-06-01
- [x] 27-03-PLAN.md — Wiring: register consumer (ExcludeFromConfigureEndpoints) + runtime ConnectReceiveEndpoint bind-then-MarkHealthy (EXEC-01) — completed 2026-06-02

### Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout
**Goal**: The deterministic build-time SourceHash identity is embedded into the assembly, the first concrete `Processor.Sample` exists and joins the compose stack, and a real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the liveness-gated Start — all behind the 3-GREEN / triple-SHA close gate.
**Depends on**: Phase 27 (the full framework round-trip must exist before a concrete + E2E can exercise it end-to-end)
**Requirements**: IDENT-01, IDENT-02, SAMPLE-01, SAMPLE-02, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):
  1. An MSBuild target (`BeforeTargets=CoreCompile`) computes the SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded deterministically (ordinal path sort) over `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`) — emits it as `[assembly: AssemblyMetadata("SourceHash", …)]`, and re-runs on implementation source change (no stale hash on incremental builds).
  2. `Processor.Sample` is the first concrete console (family convention `Processor.<Purpose>`), implementing `ProcessAsync` with a minimal POC dummy result list and carrying no infrastructure/id/L2/bus code.
  3. `Processor.Sample` ships a multistage Dockerfile and joins the compose stack (mirroring the Orchestrator tier), and its built binary's embedded SourceHash (lowercase 64-hex, satisfying the DB `^[a-f0-9]{64}$` validator) is the value registered as the Processor DB row via CRUD.
  4. A real-stack E2E proves the live round-trip — a dispatch is consumed, output is written to L2, and the orchestrator advances on the returned `ExecutionResult` — and proves the liveness-gated Start path (a live `Processor.Sample` heartbeat lets orchestration Start pass).
  5. The phase-close gate holds: 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER, with scan-clean teardown covering the new processor-liveness and execution-data keys.
**Plans**: 4 plans
- [x] 28-01-PLAN.md — SourceHash.targets (inline RoslynCodeTaskFactory + two-target emit) + Processor.Sample project skeleton + hermetic reflection/unit facts (IDENT-01/02, SAMPLE-01) — complete 2026-06-02
- [x] 28-02-PLAN.md — Multistage Dockerfile + processor-sample compose tier + ComposeYamlFacts + cross-OS dual-build hash-reproducibility gate (SAMPLE-02; IDENT-02 reproducibility proven cross-OS) — complete 2026-06-02
- [x] 28-03-PLAN.md — Real-stack SampleRoundTripE2ETests (genuine embedded hash, no synthetic liveness seed, truthful liveness-gated Start) (TEST-01) — complete 2026-06-02
- [x] 28-04-PLAN.md — phase-28-close.ps1 (3-GREEN + triple-SHA, steady-state processor-id pre-flight seed) (TEST-02) — complete 2026-06-02 (gate exit 0: 395 facts GREEN x3 + triple-SHA BEFORE==AFTER held)

### Phase 29: Structured Execution-Scope Logging
**Goal**: Every project emits logs as structured attributes only (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId), carried ambiently via MEL log scopes and serialized by OTel `IncludeScopes` into Elasticsearch — so the full orchestrator→processor→orchestrator round-trip is queryable by any id without interpolating ids into message templates or threading them through method signatures.
**Depends on**: Phase 28 (the full round-trip + both consoles + the `IExecutionCorrelated` contracts must exist to scope)
**Requirements**: LOG-01, LOG-02, LOG-03, LOG-04, LOG-05, LOG-06 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):
  1. All ids appear as Elasticsearch attributes (`attributes.CorrelationId` / `WorkflowId` / `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`) sourced from log SCOPES (values under fixed keys, never interpolated into message text — T-18-04), via the existing `IncludeScopes` + `ParseStateValues` OTel bridge.
  2. `InboundCorrelationConsumeFilter` is unchanged (still scopes `CorrelationId` for all messages); a new bus-wide open-generic `InboundExecutionScopeConsumeFilter` scopes the execution id-set for `IExecutionCorrelated` messages and passes through all others, registered in `AddBaseConsoleMessaging` so BOTH the orchestrator (`ResultConsumer` ← `ExecutionResult`) and the processor (`EntryStepDispatchConsumer` ← `EntryStepDispatch`) are covered with no per-console wiring.
  3. A shared `ExecutionLogScope` keys class in `Messaging.Contracts` is the single source of truth, its key strings equal to the structured-param names (`{WorkflowId}` …) so scope-derived and param-derived attributes coincide on the same field; `Guid.Empty` values are skipped (no zero-guid noise attributes).
  4. The processor's per-result minted `ExecutionId` + output `EntryId` are captured via a nested `BeginScope` in `EntryStepDispatchConsumer` (overriding the inbound values for the write/send lines), and `ProcessorId` enriches ALL processor logs (startup, heartbeat, consume) via an OTel `LogRecord` enricher reading `IProcessorContext.Id` (null-safe before identity resolves).
  5. `WorkflowFireJob` (a Quartz job, outside the consume pipeline) opens an explicit `BeginScope(CorrelationId + WorkflowId)` in `Execute` so its fire logs correlate with the round-trip it triggers; the full hermetic + real-stack suite stays GREEN with no log-shape regression and the close-gate triple-SHA still holds.
**Plans**: 5 plans
- [x] 29-01-PLAN.md - ExecutionLogScope keys class in Messaging.Contracts + key-pin test (LOG-03) — 2026-06-02
- [x] 29-02-PLAN.md - InboundExecutionScopeConsumeFilter (5 ids, Guid.Empty-skip, non-IExecutionCorrelated no-op) + bus-wide registration + hermetic probe test (LOG-01/02/03) — 2026-06-02
- [x] 29-03-PLAN.md - ProcessorId LogRecord enricher (null-safe, processor-side only) + nested BeginScope for minted ExecutionId+EntryId in EntryStepDispatchConsumer + tests (LOG-01/04) — 2026-06-02
- [x] 29-04-PLAN.md - WorkflowFireJob explicit BeginScope(CorrelationId + WorkflowId) + hermetic test (LOG-01/05)
- [x] 29-05-PLAN.md - Real-stack E2E scope-sourced processor-side proof (L1 trap closed) + scripts/phase-29-close.ps1 close gate (LOG-01/06)

### Phase 30: Runtime & Business Metrics
**Goal**: Every service emits code-defined metrics carrying a per-replica `service_instance_id` label so that, across multiple orchestrator/processor replicas, PromQL can measure the rate of orchestrator→processor dispatch *sending* vs processor *consuming* (the per-processor bottleneck) and per-processor outcome rates — without high-cardinality workflow labels and without collector-side metric config.
**Depends on**: Phase 29 (the full round-trip + both consoles in place; the new counters instrument the same send/consume sites the logging phase scoped)
**Requirements**: METRIC-01, METRIC-02, METRIC-03, METRIC-04, METRIC-05, METRIC-06, METRIC-07 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):
  1. A `service.instance.id` resource attribute is set **in code** in BOTH base libs (`BaseApi.Core` `ObservabilityServiceCollectionExtensions` + `BaseConsole.Core` `BaseConsoleObservabilityExtensions`) from the pod identity (`POD_NAME`/`HOSTNAME` env, GUID fallback off-cluster); every emitted metric (runtime, HTTP, business) carries a uniform `service_instance_id` Prometheus label per replica — and the `otel-collector` metrics pipeline is NOT modified to add it (the existing generic `resource_to_telemetry_conversion` forwards it).
  2. All three process types (WebApi, Orchestrator, every `Processor.*`) emit .NET runtime metrics (existing `AddRuntimeInstrumentation`), and the WebApi emits ASP.NET Core HTTP server metrics (existing `AddAspNetCoreInstrumentation`) — all now carrying `service_instance_id`.
  3. The Orchestrator defines a code-owned `Meter` with two monotonic counters — `orchestrator_dispatch_sent_total` (at the `EntryStepDispatch` send) and `orchestrator_result_consumed_total` (in `ResultConsumer`) — each labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter`; **no** `workflowId` label.
  4. `BaseProcessor.Core` defines a code-owned `Meter` with `processor_dispatch_consumed_total` (on consuming `EntryStepDispatch`) and `processor_result_sent_total` (per `ExecutionResult` sent, labelled by `outcome` ∈ {completed, failed, cancelled}) — both labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter` so every `Processor.*` inherits them; **no** `workflowId` label; the in-flight "processing" outcome is deferred.
  5. The counters align by `ProcessorId` so PromQL `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[…])) − sum by (ProcessorId)(rate(processor_dispatch_consumed_total[…]))` quantifies per-processor dispatch backlog across replicas, and per-outcome rates are queryable — proven by a real-stack assertion that the new series appear in Prometheus with the expected `ProcessorId` / `outcome` / `service_instance_id` labels after a live round-trip.
**Plans**: 4 plans (2 waves)
- [x] 30-01-PLAN.md — service.instance.id resource attr in both base libs + PrometheusTestClient/ResolveInstanceId scaffolding (METRIC-01/02/03/07) — 2026-06-02
- [x] 30-02-PLAN.md — Orchestrator counters: dispatch_sent + result_consumed keyed by ProcessorId (METRIC-04) — 2026-06-02
- [x] 30-03-PLAN.md — BaseProcessor.Core counters: dispatch_consumed + result_sent{outcome} via firewall-correct meter seam (METRIC-05) — 2026-06-02
- [x] 30-04-PLAN.md — RealStack MetricsRoundTripE2ETests: Prometheus series + by-ProcessorId bottleneck PromQL (METRIC-06; live proof of 01..05; 07 gate) — 2026-06-02

</details>

### Phases 31-32.1 (v3.6.0 — SHIPPED 2026-06-05)

Full phase details (31, 31.1, 32→32.1), success criteria, plans, decisions, and the cancelled-circuit-breaker revert rationale are archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md). Requirements: [milestones/v3.6.0-REQUIREMENTS.md](milestones/v3.6.0-REQUIREMENTS.md). Audit: [milestones/v3.6.0-MILESTONE-AUDIT.md](milestones/v3.6.0-MILESTONE-AUDIT.md).

<details>
<summary>v3.7.0 Phase Details (Phases 33-42) — SHIPPED, archived to milestones/v3.7.0-ROADMAP.md</summary>

### Phase 33: Fault-Recovery Spike (De-Risk)
**Goal**: Prove the load-bearing assumption of the whole milestone — that a published `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` event can be consumed by an external subscriber, the original message + correlation extracted from `Fault<T>.Message`, re-injected to its origin endpoint by type, and silently collapsed by the receiver's surviving Phase-31 `flag[H]` dedup — before committing to the full Keeper build.
**Depends on**: — (first phase of the milestone)
**Requirements**: INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06
**Success Criteria** (what must be TRUE):
  1. A spike consumer binding `IConsumer<Fault<EntryStepDispatch>>` and `IConsumer<Fault<ExecutionResult>>` receives the fault events the processor/orchestrator publish on retry-budget exhaustion — via pub/sub, no per-`{procId}_error`-queue binding — while `Fault<StartOrchestration>`/`Fault<StopOrchestration>` are demonstrably NOT delivered.
  2. From `Fault<T>.Message` the spike extracts the original message + full 6-id `IExecutionCorrelated` tuple (correlationId, workflowId, stepId, processorId, entryId, executionId) + `H`, proving the inner-message shape is reachable even though `Fault<T>` is not itself `IExecutionCorrelated`.
  3. The extracted message is re-injected directly to its origin endpoint by type (`queue:{processorId:D}` for dispatch, `queue:orchestrator-result` for result) — no orchestrator round-trip — and the downstream effect happens exactly once.
  4. A deliberately duplicated re-inject collapses at the receiver via its existing `flag[H]` gate with no second downstream effect (Keeper needs no dedup of its own). The `_error`-retention decision (TTL'd-forensic vs suppress) is recorded.
**Plans**: 2 plans
- [x] 33-01-PLAN.md — Author FaultRecoverySpikeE2ETests (clone rig + 6 grafts: dual Fault<T> capture, double-.Message unwrap, WRONGTYPE dispatch+result trips, a-priori H, verbatim re-inject x2, negative command-fault proof); hermetic compile + zero-regression gate [autonomous] ✓ Release 0/0, hermetic 447/0 RealStack-excluded
- [~] 33-02-PLAN.md — phase-33-close.ps1 (clone phase-32.1-close) + record D-10 _error decision + operator runbook for the live trip/recover/re-inject/collapse + close gate [autonomous:false] — AUTHORED + committed (`26e174a`); LIVE close gate pending operator (GATE_EXIT=0)

### Phase 34: Keeper Console Foundation
**Goal**: Stand up a runnable, multi-replica `Keeper` console on `BaseConsole.Core` (mirroring `Orchestrator`) that builds, containerizes, and joins the compose stack as a healthy tier with work load-balanced across replicas.
**Depends on**: Phase 33
**Requirements**: KEEP-01, KEEP-02, KEEP-03
**Success Criteria** (what must be TRUE):
  1. A `Keeper` console exists on `BaseConsole.Core` (Generic-Host, metrics-only OTel, soft-dep Redis, embedded health probes, MassTransit/RabbitMQ, inherited correlation filters) with a minimal `Program.cs` mirroring `Orchestrator`.
  2. Keeper runs multi-replica with fault work bound to a shared competing-consumer endpoint (not instance-unique fan-out); RabbitMQ round-robins fault events across replicas.
  3. Keeper builds clean (Release+Debug, 0 warnings) and containerizes via a multi-stage Dockerfile.
  4. Keeper joins the compose stack as a new healthy tier alongside `orchestrator` / `processor-sample` (health probes report ready live).
**Plans**: 3 plans
  - [x] 34-01-PLAN.md — KeeperQueues const + Keeper.csproj + SK_P.sln registration (KEEP-01 foundation)
  - [x] 34-02-PLAN.md — Keeper console body: placeholder message/consumer/definition + Program.cs + appsettings + Dockerfile (KEEP-01/02)
  - [x] 34-03-PLAN.md — compose keeper tier + ComposeYamlFacts + 4 hermetic Keeper tests + live operator smoke (KEEP-01/02/03)

### Phase 35: Fault Intake & Correlation
**Goal**: Wire the production fault-intake path — consuming the two execution-path `Fault<T>` events, extracting the inner message + 6-id correlation + `H`, opening the propagated execution log-scope, and confirming the `_error` record consolidates into the TTL'd forensic DLQ-1 (never Keeper's worklist).
**Depends on**: Phase 34
**Requirements**: INTAKE-03, KMET-04
**Success Criteria** (what must be TRUE):
  1. The transport-exhaustion record consolidates into DLQ-1 (TTL'd forensic) — Keeper recovers off the `Fault<T>` pub/sub events, never reads the error/DLQ-1 queue, and recovered work is never double-processed from it.
  2. On every consumed fault, Keeper opens the execution log-scope from the extracted inner message so its OTel logs carry the propagated correlationId + execution-scope ids (consistent with the other consoles).
  3. A faulted message processed by Keeper produces an Elasticsearch log correlated to the original execution by correlationId + ids (observable end-to-end).
**Plans**: 3 plans
- [x] 35-01-PLAN.md — Shared ExecutionLogScope.BuildState refactor (D-07), byte-identical + regression-guarded
- [x] 35-02-PLAN.md — Two real Fault<T> consumers + definitions + Program.cs swap + placeholder deletion + hermetic scope proof (completed 2026-06-05 — KeeperFaultConsumerScopeTests 3/3 GREEN proving CorrelationId + 5 exec ids; both defs on keeper-fault-recovery, single retry owner; 3 placeholders deleted; SK_P.sln 0/0 Release; hermetic suite 457/0; b71233c, 418bc3f)
- [x] 35-03-PLAN.md — RealStack SC3: running-Keeper-container correlated-ES-log proof (authored — KeeperFaultIntakeE2ETests sibling clone of the Phase-33 spike: WRONGTYPE live-trip → running-Keeper-container correlated ES log on service.name=keeper + attributes.CorrelationId == tripped correlationId + attributes.StepId + body.text ~ "keeper fault intake"; net-zero teardown; no DLQ-1/TTL scope creep; SK_P.sln 0/0 Release; hermetic suite unchanged; 1b64143. **LIVE SC3 run OPERATOR-PENDING** — not observed this session; runbook in 35-03-SUMMARY; INTAKE-03/KMET-04 stay unticked until the operator's GREEN live run)

### Phase 36: L2 Health-Probe Recovery Loop & DLQs
**Goal**: Implement the core recovery engine — a bounded, crash-survivable L2 read+write probe loop that re-injects to origin on first success or parks the unrecoverable in `keeper-dlq` (DLQ-2) on give-up — plus the two-DLQ topology (Immediate(N) exhaustion → DLQ-1; probe exhaustion → DLQ-2) and the shared `Immediate(N)` policy across all consumers.
**Depends on**: Phase 35
**Requirements**: PROBE-01, PROBE-02, PROBE-03, PROBE-04, PROBE-05, DLQ-01, DLQ-02, DLQ-03, DLQ-04
**Success Criteria** (what must be TRUE):
  1. On intake Keeper runs a recovery loop with config-driven inter-attempt delay (s) and max-attempts, each iteration probing L2 by reading `skp:data:{entryId}` AND write-then-deleting a scratch key (read + write, not mere connectivity); `delay × attempts` is documented as bounded under RabbitMQ's delivery-ack timeout.
  2. On the first successful probe Keeper exits and triggers recovery (re-inject + resume); when max-attempts exhaust it parks the original message in `keeper-dlq` (DLQ-2) and exits.
  3. The fault message is acked only after the loop exits (success or give-up); killing Keeper mid-loop leaves it un-acked → redelivered → loop restarts, with no lost message (at-least-once recovery observable).
  4. Two DLQs exist split by mechanism: DLQ-1 (all `Immediate(N)` transport exhaustions, consolidated across processor/orchestrator/Keeper, TTL'd forensic) and DLQ-2 `keeper-dlq` (probe give-ups); `keeper-dlq` depth is the primary Prometheus alert; on give-up the workflow stays paused (no auto-resume).
  5. All consumers (processor dispatch, orchestrator result/start/stop, Keeper) use the same `Immediate(N)` bound from the shared `RetryOptions` appsettings, routed uniformly to DLQ-1 (same pattern across consoles).
**Plans**: 4 plans
- [x] 36-01-PLAN.md — Contracts (keeper-dlq const + KeeperProbe key) + ProbeOptions + Wave-0 FakeRedis/bound test (completed 2026-06-05 — KeeperQueues.DeadLetter + L2ProjectionKeys.KeeperProbe; Keeper-local ProbeOptions 5×12 bound+appsettings; FakeRedis down/half-open/up double + ProbeOptions_Bound test; SK_P.sln 0/0 Release; hermetic suite 458/0; 52d2b67, 85c526a, 8f386c6)
- [x] 36-02-PLAN.md — L2ProbeRecovery loop helper + consumer re-inject/park bodies (PROBE-01..05) (completed 2026-06-05 — bounded read+write-then-delete loop, catch RedisException only; both consumers re-inject verbatim inner to origin on Recovered / park original Fault<T> to keeper-dlq on GaveUp; 6 facts GREEN, SK_P.sln 0/0 Release, hermetic 464/0; 399570f, cf627b9)
- [x] 36-03-PLAN.md — Consolidated DLQ-1 error transport in BaseConsole.Core (DLQ-01/02/04, all 3 consoles) (completed 2026-06-06 — mechanism-a custom IFilter<ExceptionReceiveContext> confirmed against MT 8.5.5 assemblies + in-mem spike; ConsolidatedErrorTransportFilter moves Immediate(N) exhaustion to ONE skp-dlq-1 (x-message-ttl 7d) across processor/orchestrator/Keeper, GenerateFaultFilter retained; typed ConsolidatedFault forensic envelope; 3 hermetic facts GREEN, SK_P.sln 0/0 Release, Keeper ns 16/16, hermetic 465/467 (2 reds = documented cross-ns in-mem-MT flake, GREEN in isolation); live broker-arg/move/drain = Plan 04/Phase 39; edc4787, 28d528e)
- [x] 36-04-PLAN.md — RealStack recover-both-paths + give-up E2E (operator-gated live half) (authored 2026-06-06 — KeeperRecoveryE2ETests sibling of FaultRecoverySpikeE2ETests: KeeperRecovery_RecoversBothPaths dispatch+result re-inject exactly-once CountEsHitsAsync==1 / KeeperRecovery_GivesUp_ParksToDlq probe-data poison → keeper-dlq park caught+ack-drained; skp:keeper:probe:* net-zero scan; SK_P.sln 0/0 Release, hermetic 467/0 unchanged, RealStack adds 0 hermetic tests; 1b0d7d9; live GREEN operator-gated, PROBE-03/04/05 unticked, Phase-39 authoritative)

### Phase 37: Orchestrator Pause/Resume Coordination
**Goal**: Add the `PauseWorkflow`/`ResumeWorkflow` contracts and the orchestrator-side consumers that halt a workflow's cron via Quartz `PauseJob` (D-08) and reschedule it from L1 on any successful recovery (D-09), with **Quartz `GetTriggerState` as the single source of truth** for the Running/Paused/Stopped state (no L1 state field, no pending-recovery set); idempotent under duplicate/concurrent signals via `ConcurrentMessageLimit = 1` + idempotent transitions + redelivery (D-07).
**Depends on**: Phase 36 (re-inject lives in Keeper per INTAKE-04; this is the cron-scheduling half)
**Requirements**: PAUSE-01, PAUSE-02, PAUSE-03, PAUSE-04, PAUSE-05
**Success Criteria** (what must be TRUE):
  1. New `PauseWorkflow` and `ResumeWorkflow` contracts exist in `Messaging.Contracts`, fanned from Keeper to the orchestrator (cron scheduling only; re-injection stays in Keeper).
  2. (Revised by **D-08**) On Keeper's pause signal the orchestrator halts the workflow's future cron fires via Quartz **`PauseJob`** (L1 preserved); the Paused state is owned by Quartz and read via `GetTriggerState(TriggerKey(jobId))` — **no separate L1 state field and no in-memory pending-recovery set**. (The original criterion's `UnscheduleOnlyAsync` + pending-recovery-set wording is superseded.)
  3. When no recoveries remain pending for a workflow, the orchestrator reschedules it from L1 (`ScheduleAsync` with the L1 root's `jobId` + `cron`) and future cron fires resume.
  4. (Revised by **D-07**) Duplicate/concurrent pause/resume signals (Keeper crash/redelivery) are absorbed by `ConcurrentMessageLimit = 1` serial consume + idempotent Quartz transitions + redelivery-on-crash — **no dedicated lock, no per-`workflowId` semaphore, no reference-counting set**. `H` is correlation/observability only (D-02), so PAUSE-04's do-not-double-count holds trivially.
  5. (Revised by **D-09**) The orchestrator resumes a workflow on **any** successful recovery regardless of sibling outcomes; a given-up message is parked to `keeper-dlq` and publishes nothing (does NOT re-pin paused). A workflow stays paused only if **no** recovery ever succeeds. Resume acts only when `GetTriggerState == Paused` (None=operator-Stopped and Normal=already-Running are ignored).
**Plans**: 4 plans
  - [x] 37-01-PLAN.md — Wave 0: four RED test files (contracts, Keeper publish, scheduling Pause/Resume/ignore, consumer idempotency) (completed 2026-06-06 — deliberate RED; build fails only on the 6 missing production symbols plans 02/03 create)
  - [x] 37-02-PLAN.md — Contracts + load-bearing deterministic TriggerKey stamping + PauseAsync/GetTriggerStateAsync (completed 2026-06-06 — PauseResumeContractTests 4/4 + PauseResumeSchedulingTests 3/3 GREEN; SchedulingTests no regression; consumer/Keeper tests remain RED for plans 03/04)
  - [x] 37-03-PLAN.md — Orchestrator Pause/Resume consumers + lifecycle seams + ConcurrentMessageLimit=1 definitions + Program wiring (completed 2026-06-06 — PauseResumeConsumerTests 1/1 GREEN; scheduling/contract tests no regression; Keeper tests remain RED for plan 04)
  - [x] 37-04-PLAN.md — Keeper publish sites: PauseWorkflow at intake + ResumeWorkflow on Recovered (GaveUp unchanged) (completed 2026-06-06 — KeeperPausePublishTests 2/2 GREEN; full phase-37 assembly compiles end-to-end; Keeper Release 0/0; live round-trip operator-pending)

### Phase 38: Uniform `service_name` + Instance Labels Across All Metrics
**Goal**: Every Prometheus metric series (runtime, HTTP, and business instruments) for all four consoles carries a human-distinguishable `service_name = {name}_{version}` label plus a non-empty `service_instance_id` label — where the processor's `{name}_{version}` is sourced from the **database** (the single source of truth), not appsettings. No live operator verification required (hermetic + scrape-assertion provable).
**Depends on**: Phase 30 (metrics foundation) + Phase 26 (processor identity round-trip). Independent of Phases 36/37; sequenced BEFORE the Phase 39 close gate so the gate seals the final metric-label contract.
**Requirements**: MLBL-01, MLBL-02, MLBL-03, MLBL-04, MLBL-05 (locked in `38-SPEC.md`)
**Success Criteria** (what must be TRUE):
  1. Every metric series (runtime / HTTP / business) for each console carries `service_name = {name}_{version}` (e.g. `keeper_3.7.0`, `orchestrator_3.4.0`, `sk-api_3.2.0`, and the processor's DB `{Name}_{Version}`); no series carries a bare `service_name` lacking the version suffix.
  2. Every metric series carries a non-empty `service_instance_id`, verified present on all three instrument families — not only the Phase-30 business counters.
  3. The processor's steady-state name+version are sourced from the DB: `ProcessorIdentityFound` is extended with `Name`+`Version`, the responder + `IProcessorContext` carry them; the processor's appsettings `Service:Name`/`Service:Version` are **retained** as the boot-window placeholder (GA-3 amendment - supersedes the original "removed / `processor-pending`" framing), and metrics before identity-resolution carry the appsettings `{name}_{version}` (e.g. `processor-sample_3.5.0`), then the MeterProvider is swapped to the DB-sourced resource once identity resolves.
  4. The logs' `service.name` stays the bare identity (metrics-only version suffix) — the Phase-35 ES assertion `service.name="keeper"` still passes.
  5. All in-repo Prometheus query consumers (incl. the Phase-11 `service_name="sk-api"` round-trip assertion) are updated to the combined label and pass; no high-cardinality labels introduced.
**Plans**: 4 plans
  - [x] 38-01-PLAN.md - Processor identity round-trip: extend ProcessorIdentityFound + responder + IProcessorContext with Name/Version; update 3 IProcessorContext fakes (CS0535 firewall) (MLBL-03) (completed 2026-06-06 — ProcessorResponderTests 2/2 GREEN; SK_P.sln 0/0 Debug+Release; commits 1ccae71, 867edaf)
  - [x] 38-02-PLAN.md - Combine service_name={name}_{version} on the metrics resource (both base libs); keep logs bare + hermetic guard; reconcile PromQL literals to sk-api_3.2.0 (MLBL-01/04/05) (completed 2026-06-06 — 26/26 hermetic Observability GREEN; LogsResourceBareNameFacts 1/1; SK_P.sln 0/0 Release; 0 bare service_name literals; commits 013bc0a, 39792b3, 4d67977)
  - [x] 38-03-PLAN.md - MeterProviderHolder swap (Model A1): placeholder->DB service.name on identity-resolve in Loop A; hermetic MeterProviderHolderFacts (MLBL-03) (completed 2026-06-06 — MeterProviderHolderFacts 1/1 GREEN; orchestrator-drive tests 5/5 GREEN; 27/27 hermetic Observability GREEN; SK_P.sln 0/0 Debug+Release; commits 69bbd55, ef209d0, e41a475)
  - [x] 38-04-PLAN.md - RealStack scrape gate: combined service_name + non-empty service_instance_id across runtime/HTTP/business; DB-sourced processor series; appsettings-retained + MLBL-05 inventory (MLBL-01/02/03/05) (completed 2026-06-06 — RealStack MetricsRoundTripE2ETests 1/1 GREEN after container rebuild; scrape proof sk-api_3.2.0/orchestrator_3.4.0 + DB-sourced sample-proc-..._1.0.0; 479/479 hermetic GREEN; SK_P.sln 0/0 Release; 0 bare service_name literals; commit 30a23d7)

### Phase 39: Keeper Observability + Real-Stack E2E + Close Gate
**Goal**: Register the Keeper meter + throughput/saturation instruments, then prove the full recover-and-give-up behavior live against the real stack and lock a 3×GREEN triple-SHA net-zero close gate.
**Depends on**: Phase 38 (the final metric-label contract the gate seals) + Phase 37
**Requirements**: KMET-01, KMET-02, KMET-03, TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):
  1. A code-defined `Keeper` meter is registered per the house pattern (snake_case, no `_total` suffix, inherited `service_instance_id`).
  2. Throughput/outcome counters (`keeper_fault_consumed`, `keeper_recovered`, `keeper_dlq_pushed{reason}`, `keeper_workflow_paused`, `keeper_workflow_resumed`, `keeper_l2_probe_failed`) and saturation/latency signals (`keeper_in_flight` UpDownCounter, `keeper_recovery_duration` histogram) are emitted and Prometheus-scrapable, labeled by `processorId` where meaningful with no high-cardinality `workflowId`.
  3. A real-stack E2E induces an L2 outage that dead-letters both an `EntryStepDispatch` and an `ExecutionResult`, then proves Keeper pauses the workflow, recovers on L2 return, resumes, and re-injects each to origin with exactly-once downstream effect (no duplicate).
  4. A real-stack E2E proves the give-up path: L2 stays down past max-attempts → message in `keeper-dlq`, workflow stays paused, `keeper_dlq_pushed` increments.
  5. The close gate runs 3× consecutive GREEN with triple-SHA (psql `\l` / redis `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER — including both DLQs + probe scratch-key scan-clean (net-zero) — at Release+Debug 0-warning.
**Plans**: 4 plans (4 waves)
  - [x] 39-01-PLAN.md — KeeperMetrics meter (6 counters + UpDownCounter + Histogram) + Program.cs AddMeter symmetry + Wave-0 DiagnosticSource version gate (KMET-01/03)
  - [x] 39-02-PLAN.md — Instrument both fault consumers + L2ProbeRecovery + hermetic KeeperMetricsFacts (KMET-01/02/03) (completed 2026-06-06 — 6 increment sites/consumer + in_flight ++/finally-- + l2_probe_failed; RunAsync threads procId; 11/11 hermetic GREEN via MeterListener; SK_P 0/0 Release; 047531d, 7fe4db4, f347dc8)
  - [x] 39-03-PLAN.md — Extend the two KeeperRecovery RealStack facts with keeper_* Prometheus scrape assertions + Wave-0 histogram-suffix gate (TEST-01/02) (completed 2026-06-06 — recover fact: fault_consumed/recovered/workflow_paused/workflow_resumed/recovery_duration_seconds_count{recovered}; give-up fact: dlq_pushed{probe_exhausted,result}/recovery_duration_seconds_count{gave_up}/l2_probe_failed; all ProcessorId-filtered via PollPromForQuery + non-empty service_instance_id + no-workflowId ban; PromPollTimeoutMs=120_000; Wave-0 suffix written _seconds per Plan 01, live-confirm on 39-04 gate; test build 0/0 Release; 9e938eb)
  - [x] 39-04-PLAN.md — Clone phase-39-close.ps1 (triple-SHA + keeper rebuild + both-DLQ depth==0) + live 3xGREEN gate (TEST-03)

### Phase 40: Keeper Recovery Hardening
**Goal**: A persistent (non-transient) fault can no longer flood the stack, the give-up-park drain is deterministic so the close gate's `keeper-dlq depth==0` invariant holds every run, and the two Keeper fault consumers share one recovery body so future recovery changes land in a single place.
**Depends on**: Phase 39 (close gate + KeeperMetrics in place)
**Requirements**: KHARD-01, KHARD-02, KHARD-03
**Gap Closure**: Closes the two functional tech-debt items + IN-01 from `.planning/v3.7.0-MILESTONE-AUDIT.md`.
**Success Criteria** (what must be TRUE):
  1. The recover→reinject cycle is bounded by a config attempt cap; when the cap is reached for a given `H`, Keeper parks the original `Fault<T>` to `keeper-dlq` (give-up) instead of reinjecting again — a persistent fault converges to a single park, not an unbounded reinject loop. A hermetic test proves the cap is honored (no reinject after cap; exactly one park).
  2. The give-up RealStack E2E teardown drains `keeper-dlq` with a poll-until-stably-empty strategy (bounded), so `scripts/phase-39-close.ps1` (or the Phase-40 gate) yields `keeper-dlq depth==0` deterministically across the 3× cadence — no late give-up park races the AFTER snapshot.
  3. The recover/probe/re-inject/park/pause/resume logic shared by `FaultEntryStepDispatchConsumer` and `FaultExecutionResultConsumer` is extracted into one shared helper/base; both consumers delegate to it (no near-total duplication); KHARD-01's cap exists in exactly one place. Hermetic suite stays GREEN, Release 0-warning.

**Plans**: 3 plans (2 waves)
Plans:
- [x] 40-01-PLAN.md -- KHARD-03: extract one shared KeeperRecoveryHandler; both consumers delegate (keystone, Wave 1)
- [x] 40-02-PLAN.md -- KHARD-01: per-H recover-attempt cap + hermetic cap test (Wave 2)
- [x] 40-03-PLAN.md -- KHARD-02: poll-until-stably-empty keeper-dlq drain in the give-up E2E teardown (Wave 2)

### Phase 41: Orchestrator Pause/Resume Diagnostics
**Goal**: A Resume dropped during the narrow fire window is diagnosable, and the scheduler's reschedule fallback cannot throw on a purged non-durable job.
**Depends on**: Phase 37
**Requirements**: (closes 37-REVIEW WR-01, WR-02 — code-quality, no new REQ-IDs)
**Gap Closure**: Closes the Phase-37 code-quality warnings from the v3.7.0 audit.
**Success Criteria** (what must be TRUE):
  1. `WorkflowLifecycle.ResumeAsync` emits an informational log on the `state != TriggerState.Paused` ignore branch (WorkflowId + observed state), so a Resume that arrives mid-fire and is dropped is observable in logs.
  2. `WorkflowScheduler.RescheduleAsync` no longer assumes the non-durable job still exists on the `RescheduleJob`-returns-null fallback path — it either re-creates the job+trigger or fails loudly with a clear message rather than an opaque Quartz throw. Hermetic test covers the fallback path.
**Plans:** 2/2 plans complete
- [x] 41-01-PLAN.md — WR-01: informational log on the ResumeAsync non-Paused ignore branch (WorkflowId + observed TriggerState; log-only, no re-arm) (Wave 1) (completed 2026-06-07 — 9e14eeb; LogInformation on ignore branch, no behavioral re-arm per D-01/D-02; hermetic 504/0, Release 0-warning)
- [x] 41-02-PLAN.md — WR-02: thread workflowId into RescheduleAsync + re-create full job+trigger in the purged-job fallback so it cannot throw; hermetic fallback test (Wave 1) (completed 2026-06-07 — de0cec0/0edeb53; 4-arg RescheduleAsync re-creates job+trigger in null-fallback per D-04, RescheduleSchedulingTests asserts re-establishment per D-06; hermetic 505/0, Release 0-warning)

### Phase 42: v3.7.0 Docs & Traceability Reconciliation
**Goal**: REQUIREMENTS.md and ROADMAP.md tell the truth about v3.7.0 before archival — every satisfied requirement is checked, MLBL is in the traceability table, counts are correct, and the close-gate phase has a VERIFICATION.md.
**Depends on**: Phases 40, 41 (so the doc pass reflects final state)
**Requirements**: (doc-only — no new REQ-IDs)
**Gap Closure**: Closes the documentation-drift tech-debt items from the v3.7.0 audit.
**Success Criteria** (what must be TRUE):
  1. REQUIREMENTS.md checkboxes for all satisfied v3.7.0 reqs read `[x]` (INTAKE-01..04, PROBE-01..06, DLQ-01..04, PAUSE-01..05, KMET-01..04) and their traceability rows reflect "Complete (Phase-39 live gate)" rather than "Not started".
  2. MLBL-01..05 rows exist in the REQUIREMENTS.md traceability table mapped to Phase 38; the coverage footer reads the correct totals (34 requirements across phases 33-39, plus the gap-closure KHARD set) — no stale "29 / 6 phases / 33-38".
  3. The ROADMAP.md progress table Phase-38 row reads its true plan count + "Complete" (not "0/? Not started").
  4. `39-VERIFICATION.md` exists for the close-gate phase, recording the 3×500 GREEN triple-SHA result and the accepted keeper-dlq drain-timing follow-up.

**Plans:** 3/3 plans complete
- [x] 42-01-PLAN.md - REQUIREMENTS.md: flip satisfied checkboxes [x] + reconcile traceability rows + add MLBL-01..05 + correct coverage footer (SC1, SC2)
- [x] 42-02-PLAN.md - ROADMAP.md: fix Progress-table Phase-38 row to 4/4 Complete (SC3)
- [x] 42-03-PLAN.md - backfill 39-VERIFICATION.md from close-gate evidence (SC4)

</details>

## Progress

**Execution Order:**
Phases execute in numeric order: 25 → 26 → 27 → 28 → 29 → 30 → 31 → 31.1 → 32 → 32.1 → 33 → 34 → 35 → 36 → 37 → 38 → 39 → 40 → 41 → 42 → 43 → 44 → 45 → 46 → 47 → 48 → 49 → 50 → 51 → 52 → 53 → 54 → 55 → 56 → 57 → 58

| Phase | Milestone | Plans Complete | Status   | Completed  |
| ----- | --------- | -------------- | -------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete | 2026-05-28 |
| 12-16 | v3.3.0    | 26/26          | Complete | 2026-05-29 |
| 17    | v3.4.0    | 2/2            | Complete | 2026-05-30 |
| 18    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 19    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 20    | v3.4.0    | 4/4            | Complete | 2026-05-31 |
| 21    | v3.4.0    | 1/1            | Complete | 2026-05-31 |
| 22    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 23    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 24    | v3.4.0    | 5/5            | Complete | 2026-06-01 |
| 24.1  | v3.4.0    | 1/1            | Complete | 2026-06-01 |
| 25. Shared Contracts + WebApi Responders | v3.5.0 | 2/2 | Complete    | 2026-06-01 |
| 26. BaseProcessor.Core — Library, Identity & Liveness | v3.5.0 | 3/3 | Complete    | 2026-06-01 |
| 27. Execution Round-Trip | v3.5.0 | 3/3 | Complete | 2026-06-02 |
| 28. SourceHash Identity + Processor.Sample + E2E Closeout | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 29. Structured Execution-Scope Logging | v3.5.0 | 5/5 | Complete    | 2026-06-02 |
| 30. Runtime & Business Metrics | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 31. Idempotent Execution Round-Trip (Exactly-Once-Effect) | v3.6.0 | 6/6 | Complete    | 2026-06-04 |
| 31.1 Close-Gate Redis Net-Zero (gap closure) | v3.6.0 | 1/1 | Complete | 2026-06-04 |
| 32. Cancelled Circuit-Breaker | v3.6.0 | 5/6 | Superseded by 32.1 | — |
| 32.1 Dead-Letter on Exhaustion (Breaker Reverted) | v3.6.0 | 2/2 | Complete    | 2026-06-05 |
| 33. Fault-Recovery Spike (De-Risk) | v3.7.0 | 2/2 | Complete    | 2026-06-05 |
| 34. Keeper Console Foundation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 35. Fault Intake & Correlation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 36. L2 Health-Probe Recovery Loop & DLQs | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 37. Orchestrator Pause/Resume Coordination | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 38. Uniform `service_name` + Instance Labels Across All Metrics | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 39. Keeper Observability + Real-Stack E2E + Close Gate | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 40. Keeper Recovery Hardening (gap closure) | v3.7.0 | 3/3 | Complete (live gate Manual-Only) | 2026-06-06 |
| 41. Orchestrator Pause/Resume Diagnostics (gap closure) | v3.7.0 | 2/2 | Complete    | 2026-06-06 |
| 42. v3.7.0 Docs & Traceability Reconciliation (gap closure) | v3.7.0 | 3/3 | Complete    | 2026-06-07 |
| 43. Message Contracts & L2 Key Reshape | v4.0.0 | 5/5 | Complete | 2026-06-08 |
| 44. Pre/In/Post-Process Pipeline | v4.0.0 | 3/3 | Complete | 2026-06-08 |
| 45. Keeper BIT Health Gate + Global Pause/Resume | v4.0.0 | 3/3 | Complete | 2026-06-08 |
| 46. Keeper 5-State Recovery + Orchestrator Per-Item Consume | v4.0.0 | 4/4 | Complete | 2026-06-09 |
| 47. DLQ Consolidation + At-Least-Once Semantics | v4.0.0 | 3/3 | Complete | 2026-06-09 |
| 48. v3.x Teardown | v4.0.0 | 3/3 | Complete | 2026-06-09 |
| 49. Live-Proof Close Gate | v4.0.0 | 6/6 | In Progress (live gate operator-gated) | — |
| 50. Contracts & Slot-Array L2 Key Reshape | v5.0.0 | 2/2 | Complete    | 2026-06-11 |
| 51. Processor Forward + Recovery Pipeline | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 52. 3-State Keeper | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 53. Model-B Teardown | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 54. Live Proof & Close Gate | v5.0.0 | 4/4 | Complete    | 2026-06-11 |
| 55. Live Proof & Close Gate | v5.0.0 | 4/4 | Complete    | 2026-06-12 |
| 56. Typed Base-Config Seam | v6.0.0 | 2/2 | Complete    | 2026-06-12 |
| 57. Startup Config-Schema Fetch + Gate A | v6.0.0 | 4/4 | Complete    | 2026-06-12 |
| 58. Orchestration-Gate Integration Proof & Close | v6.0.0 | 5/5 | Complete    | 2026-06-12 |

---
*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 shipped 2026-06-01 (9 phases 17-24+24.1, BaseConsole + Orchestrator Messaging). v3.5.0 shipped 2026-06-02 (6 phases 25-30, Processor Console — `BaseProcessor.Core` + `Processor.Sample`, assembly-embedded SourceHash, WebApi bus responders, L2 liveness self-registration, live execution round-trip + runtime/business metrics) — note: formal archival (ROADMAP/MILESTONES/tag) deferred. v3.6.0 shipped 2026-06-05 (4 phases 31-32.1, Idempotent Execution — exactly-once-effect round-trip via deterministic `H` + effect-first `flag[H]` dedup at both hops; cancelled circuit-breaker built then reverted to plain dead-lettering). Next milestone planning begins with `/gsd-new-milestone`.*


## ✅ Milestone v4.0.0 — Processor Pre/In/Post-Process + Keeper Recovery Redesign (Phases 43-49) — SHIPPED 2026-06-11

**Status:** 🚧 Planning (started 2026-06-08). **Breaking** successor to the v3.x execution model. Phases continue at **43**.
**Source of truth:** `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED 2026-06-08).
**Posture:** at-least-once; **no dedup / idempotency key**; duplicate effects tolerated. Recovery assumes a **transient** L2 outage (the recovery backup lives in L2 itself).

**Milestone Goal:** Replace v3.x's effect-first exactly-once model with (a) an explicit three-stage **Pre / In / Post-Process** processor pipeline (author owns per-item outcomes + mints `executionId`), (b) a proactive Keeper **BIT health gate** driving **global pause-all / resume-all** plus a gate-open-only **5-state** recovery consumer (`UPDATE`/`REINJECT`/`INJECT`/`DELETE`/`CLEANUP`, **partitioned by exec** for per-key ordering), and (c) a single consolidated `_DLQ1` — while tearing down the `H`/`flag[H]` dedup + CAS, content-addressing + result manifest + N×M fan-out, and the reactive `Fault<T>` recovery path + `keeper-dlq`.

**Build order (locked, build-before-teardown so every intermediate stays buildable/testable):**
43 (message-contract + L2-key reshape) → 44 (processor Pre/In/Post pipeline + retry loops) → 45 (Keeper BIT gate + global pause-all/resume-all + orchestrator idempotent pause) → 46 (Keeper 5-state recovery consumer, per-key ordered + orchestrator per-item result consume) → 47 (`_DLQ1` consolidation + at-least-once semantics) → 48 (v3.x teardown) → 49 (live proof + close gate).

### Phase Table

| Phase | Name | Plans | Status | Completed |
| ----- | ---- | ----- | ------ | --------- |
| 43 | Message Contracts & L2 Key Reshape | 5/5 | Complete    | 2026-06-08 |
| 44 | Processor Pre/In/Post-Process Pipeline | 3/3 | Complete    | 2026-06-08 |
| 45 | Keeper BIT Health Gate + Global Pause/Resume | 3/3 | Complete    | 2026-06-08 |
| 46 | Keeper 5-State Recovery + Orchestrator Per-Item Consume | 4/4 | Complete    | 2026-06-08 |
| 47 | DLQ Consolidation + At-Least-Once Semantics | 3/3 | Complete    | 2026-06-09 |
| 48 | v3.x Teardown | 3/3 | Complete    | 2026-06-09 |
| 49 | Live Proof & Close Gate | 6/6 | In Progress (live gate operator-gated) | — |

### Phase Details

#### Phase 43: Message Contracts & L2 Key Reshape
**Goal**: The reshaped wire vocabulary exists — `EntryStepDispatch`/`ExecutionResult` carry the six ids and no longer carry `H`, `entryId` is a GUID with `Guid.Empty` as the explicit source-step sentinel, the five Keeper-state contracts exist, and the two L2 key schemes (GUID data key with no TTL + composite backup key `corr:wf:proc:exec` with a configurable 2-day TTL) are defined in one place. This phase is NOT "just vocabulary": removing `H` from the wire and retyping `entryId` to a GUID is compile-forced to drop the `H`/`flag[H]`/CAS dedup machinery (RETIRE-01) and the content-addressing + result manifest + N×M fan-out (RETIRE-02) — so RETIRE-01/02 are coupled INTO Phase 43 (D-01), landing alongside MSG-01/02/03 rather than in Phase 48.
**Depends on**: — (first phase of the milestone; builds on the existing `Messaging.Contracts` + `L2ProjectionKeys`)
**Requirements**: MSG-01, MSG-02, MSG-03, RETIRE-01, RETIRE-02 (the last two coupled per D-01/D-02)
**Success Criteria** (what must be TRUE):
  1. `EntryStepDispatch` and `ExecutionResult` carry exactly the six ids (`correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`) and no longer carry `H` — a golden/contract test pins the shape and asserts `H` is absent.
  2. `entryId` is a `Guid`; `Guid.Empty` is recognized as the source-step sentinel by a shared helper (so consumers can branch "skip read / skip end-delete" off one predicate, not an ad-hoc check).
  3. Five Keeper message contracts exist — `UPDATE` (carrying validated data), `REINJECT`, `INJECT`, `DELETE`, `CLEANUP` — each carrying its specified id set, defined in `Messaging.Contracts`.
  4. `L2ProjectionKeys` exposes both schemes as single-source-of-truth builders: the per-item GUID **data key** (no TTL) and the composite **backup key** `correlationId:workFlowId:ProcessorId:executionId` (TTL = 2 days, configurable in days); golden tests pin both key strings.
**Plans**: 5 plans (4 waves)
- [x] 43-01-PLAN.md — Wave 0: contract/golden/predicate/options test stubs (RED) + delete RETIRE-01/02 machinery tests (MSG-01/02/03)
- [x] 43-02-PLAN.md — Wave 1: Messaging.Contracts core reshape — drop H, Guid entryId, four Step* records, SourceStep, five Keeper contracts + IKeeperRecoverable, L2 key builders, BackupOptions; delete ExecutionResult + MessageIdentity (MSG-01/02/03)
- [x] 43-03-PLAN.md — Wave 2: straight-through Orchestrator + BaseProcessor consumer adaptation (remove flag[H]/CAS/manifest; Guid entryId) (MSG-01/02)
- [x] 43-04-PLAN.md — Wave 2: D-14 dark reactive-path retarget (KeeperRecoveryHandler off inner.H, L2ProbeRecovery to Guid, neutralize Fault<ExecutionResult>, retain Fault<EntryStepDispatch>) (MSG-01/02)
- [x] 43-05-PLAN.md — Wave 3: reshape surviving tests + FULL-SUITE-GREEN phase gate + ROADMAP/REQUIREMENTS RETIRE-01/02 reconciliation (MSG-01/02/03)

#### Phase 44: Processor Pre/In/Post-Process Pipeline
**Goal**: `BaseProcessor` consumes a dispatch and runs an explicit Pre → In → Post pipeline — Pre reads + validates input (skipping on `Guid.Empty`), In is the author-overridden per-item transform that may throw a status-carrying exception, Post validates/writes/routes each item, and a `finally` end-delete reclaims `L2[entryId]` on every read-succeeded path — with a bounded retry loop wrapping every L2 op and every send.
**Depends on**: Phase 43 (the reshaped contracts, the `Guid.Empty` sentinel, the Keeper-state contracts, and both L2 key schemes must exist first)
**Requirements**: PIPE-01, PIPE-02, PIPE-03, PIPE-04, PIPE-05, PIPE-06, PIPE-07, PIPE-08, RESIL-01
**Success Criteria** (what must be TRUE):
  1. Pre-Process reads `L2[entryId]` through a bounded retry loop where a Redis exception **or** an absent/empty key counts as failure → after exhaustion `infra(READ)` sends Keeper `REINJECT` and ends the round trip (input left intact); `entryId == Guid.Empty` skips the read with empty validated data; a read-data input-schema validation failure is a business `Failed` (orchestrator result then end-delete), not infra.
  2. In-Process is an author-overridden abstract method `(validatedData, payload) → List<Item>` where each `Item = { result: completed|failed, data, executionId }` with an author-minted `executionId`; it is wrapped in try/catch so any thrown status (`processing`/`failed`/`cancelled`, an unexpected exception ⇒ `failed`) sends exactly one orchestrator result and aborts the batch (no Post-Process), then runs end-delete.
  3. Post-Process per `completed` item validates output against the output schema, sends Keeper `UPDATE` (validated data → composite backup), generates a GUID `entryId`, and writes `L2[entryId]` (no TTL) through a bounded retry loop whose exhaustion downgrades the item to `failed (infra)`; on a successful write it sends Keeper `CLEANUP` to delete the now-redundant composite backup.
  4. Post-Process routes each item: not-infra (`completed` ∪ business-`failed`) → one orchestrator result (a `completed` result carries `entryId` + `executionId`); infra → Keeper `INJECT`; N completed items produce N separate per-item orchestrator results (no manifest).
  5. End-delete runs in a `finally` over every read-succeeded path (happy, pre-process business-fail, In-Process exception), is skipped only on `infra(READ)`/`REINJECT` and `Guid.Empty` source steps, deletes `L2[entryId]` through a bounded retry loop, and on exhaustion sends Keeper `DELETE` without altering any result already sent; every L2 op and every send uses the shared `Retry:Limit` immediate-attempt loop.
**Plans**: 3 plans (3 waves — interface-first; clean break in the final wave)
- [x] 44-01-PLAN.md — Wave 0 foundation: ProcessOutcome/ProcessItem/ProcessStatusException + RetryLoop/KeyAbsentException + RetryLoopFacts (RESIL-01, PIPE-04/05 type-level)
- [x] 44-02-PLAN.md — ProcessorPipeline (Pre→In→Post→end-delete) + thin consumer + Retry reconcile + four pipeline Wave-0 fact files (PIPE-01..08, RESIL-01)
- [x] 44-03-PLAN.md — Clean break: migrate Processor.Sample to the new seam + delete ProcessResult.cs + Retry appsettings + full-suite green gate (PIPE-04, RESIL-01) ✓ Release 0/0, hermetic 488/0 RealStack-excluded

#### Phase 45: Keeper BIT Health Gate + Global Pause/Resume
**Goal**: The Keeper runs a suppressed background BIT loop that probes L2 (read + write-then-delete) on a configurable delay and broadcasts a global pause-all (unhealthy) / resume-all (healthy) decision to all orchestrators, and the orchestrator's pause-all/resume-all is idempotent per job via Quartz `TriggerState`.
**Depends on**: Phase 44 (the processor now emits the five Keeper-state messages; the gate is the precondition for the Phase-46 recovery consumer, so the gate + pause/resume land first)
**Requirements**: KEEP-01, KEEP-02, KEEP-03, ORCH-02
**Success Criteria** (what must be TRUE):
  1. A background `while` loop runs a BIT against L2 (read + write-then-delete probe) on a configurable `Probe:DelaySeconds` delay, and BIT exceptions are suppressed so a probe failure never crashes the loop (it simply reports unhealthy).
  2. Each BIT result fans out a **global** broadcast to all orchestrators — unhealthy → pause all jobs, healthy → resume all jobs.
  3. The recovery consumer's gate is wired: an L2 op is permitted only while the BIT gate is open, and a gate-closed consumer waits for the gate bounded under the broker consumer timeout (the wait mechanism exists and is honored, exercised by the Phase-46 ops).
  4. Orchestrator pause-all/resume-all is idempotent per job via Quartz `TriggerState` — pause only if Running, resume only if Paused — so a repeated broadcast is a no-op and no job is double-paused or spuriously resumed.
**Plans**: 3 plans (2 waves — Wave 0 contracts+test scaffold; Wave 1 Keeper + Orchestrator in parallel)
- [x] 45-00-PLAN.md — Wave 0: PauseAll/ResumeAll no-H contracts + 5 failing test stubs (KEEP-01/02/03, ORCH-02 scaffold) (completed 2026-06-08 — 2 contracts + 18 RED stubs; SK_P.sln 0/0 Release)
- [x] 45-01-PLAN.md — Keeper: IL2HealthGate (Toub gate) + BitHealthLoop edge-trigger BackgroundService + ProbeOnceAsync extraction (KEEP-01/02/03) (completed 2026-06-08 — 12/12 Keeper Health GREEN; SK_P.sln 0/0 Release)
- [x] 45-02-PLAN.md — Orchestrator: PauseAllConsumer/ResumeAllConsumer + defs on orchestrator-global-pauseresume + PauseAllAsync seam (ORCH-02) (completed 2026-06-08 — 6/6 Orchestrator consumer tests GREEN incl. the no-burst negative; full hermetic suite 506/506; SK_P.sln 0/0 Release)

#### Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume
**Goal**: The Keeper recovery consumer — **partitioned by `corr:wf:ProcessorId:executionId`** so each exec's messages process in order — applies the five states gate-open-only: `UPDATE` writes the composite backup (TTL = crash-backstop), `REINJECT` re-injects a reconstructed dispatch (or terminates at `_DLQ1` if the data is gone), `INJECT` reconstructs a `Completed` `ExecutionResult` to the orchestrator **then deletes the composite copy**, `DELETE` reclaims the data key, and `CLEANUP` deletes the redundant composite copy on the happy path — and the orchestrator advances workflow steps off per-item `ExecutionResult` messages with no manifest fan-out, a Keeper-`INJECT`'d completion being indistinguishable from a direct one.
**Depends on**: Phase 45 (the BIT gate must exist before the gate-open-only consumer can apply any op)
**Requirements**: KEEP-04, KEEP-05, KEEP-06, KEEP-07, KEEP-08, KEEP-09, ORCH-01
**Success Criteria** (what must be TRUE):
  1. The recovery consumer is **partitioned by `corr:wf:ProcessorId:executionId`** (per-key ordering): same-exec messages process in arrival order so `UPDATE` always precedes that exec's `CLEANUP`/`INJECT`, while different execs run in parallel.
  2. `UPDATE` writes `validatedData` to `L2[corr:wf:ProcessorId:executionId]` with the configurable TTL (default 2 days, a crash-backstop only), only while the gate is open.
  3. `REINJECT` reads `L2[entryId]`: if present (transient outage, data survived) it re-injects a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`; if absent/empty (data truly gone) the read fails → retry loop → `_DLQ1`.
  4. `INJECT` reads the composite copy, generates a new `entryId`, writes `L2[entryId]` (no TTL), injects a reconstructed `ExecutionResult(Completed, carrying entryId + executionId)` to the orchestrator result queue, and **deletes the composite copy**; `DELETE` deletes `L2[entryId]` (GC only); `CLEANUP` deletes the redundant composite copy on the happy path.
  5. After a happy-path or recovery completion the composite copy is gone (deleted by `CLEANUP`/`INJECT`, not left to its 2-day TTL) — a multi-item run leaves no composite keys behind.
  6. The orchestrator consumes per-item `ExecutionResult` messages (no manifest fan-out) and advances workflow steps accordingly; a Keeper-`INJECT`'d `Completed` result is processed identically to a direct processor completion (carries the same `entryId` + `executionId`).
**Plans**: 4 plans (3 waves — Wave 0 foundation: RetryLoop relocation + D-01 Payload ripple + RED test stubs; Wave 1 Keeper bodies + Orchestrator typed consumers in parallel; Wave 2 Keeper endpoint partitioner + registration)
- [x] 46-01-PLAN.md — Wave 0: RetryLoop→BaseConsole.Core (D-05) + KeeperReinject.Payload ripple (D-01) + 8 RED Phase=46 test stubs (KEEP-04..09, ORCH-01 scaffold) — completed 2026-06-08
- [x] 46-02-PLAN.md — Wave 1: Keeper 5-state recovery base + UPDATE/REINJECT/INJECT/DELETE/CLEANUP bodies + gate-wait/RetryLoop + RecoveryOptions + data-gone marker (KEEP-04/05/06/07/08) — completed 2026-06-08
- [x] 46-04-PLAN.md — Wave 1: Orchestrator TypedResultConsumer<T> base + 4 typed subclasses + defs + Program swap (ORCH-01) — completed 2026-06-08 (abstract Outcome knob replaces hardcoded StepOutcome.Completed; no status if/switch; single-owner UseMessageRetry on shared orchestrator-result endpoint; ResultConsumer(Definition) deleted; TypedResultConsumerFacts 7/7 incl. ORCH-01 indistinguishability; SK_P.sln 0/0; a9d387a, c5b56cb, 4425ef8)
- [x] 46-03-PLAN.md — Wave 2: 5 recovery ConsumerDefinitions (single-owner UsePartitioner on 4-tuple + retry) + Program/appsettings registration + partition/dead-letter facts (KEEP-09) — completed 2026-06-09 (UpdateConsumerDefinition owns UseMessageRetry + five shared-Partitioner UsePartitioner; four siblings no-op; 8.5.5 types in MassTransit.Middleware, Guid-keyed endpoint overload → PartitionGuid=SHA256(4-tuple); RecoveryOptions bound; five consumers registered additively; Phase=46 18/18; SK_P.sln 0/0; c4e429c, df65065, 114bbd8)

#### Phase 47: DLQ Consolidation + At-Least-Once Semantics
**Goal**: Every terminal give-up across the processor and the Keeper (a send exception with its retry loop exhausted; a Keeper L2 op exhausted) routes to a single consolidated `_DLQ1`, and the whole execution path is at-least-once with no dedup/idempotency key — duplicate effects are tolerated downstream by construction.
**Depends on**: Phase 46 (both the processor give-up paths and all five Keeper recovery ops must exist before their give-ups can be consolidated into one queue)
**Requirements**: RESIL-02, RESIL-03
**Success Criteria** (what must be TRUE):
  1. A single consolidated `_DLQ1` receives every terminal send/L2 give-up from the processor and the Keeper — there is no separate `keeper-dlq`, and the routing is wired once across the consoles (same pattern everywhere).
  2. The execution path carries no dedup/idempotency key (no `H`, no `flag[H]` gate); a redelivered or re-injected message reproduces its effect rather than being collapsed, and the system tolerates the duplicate (no lost branch, no crash).
  3. The `REINJECT`-data-gone case (redelivery after end-delete, or genuinely missing input) terminates deterministically at `_DLQ1` for operator triage rather than looping.
**Plans**: 3 plans (2 waves — Wave 1: two independent test plans in parallel [DLQ-consolidation structural guards; at-least-once duplicate-delivery + R2 re-tag]; Wave 2: audit doc + design-doc amendment, gated on Wave-1 facts being green)
- [x] 47-01-PLAN.md — Wave 1: processor send-exhaustion -> skp-dlq-1 fact + AtLeastOnceStructuralFacts (reflection no-dedup R4 + source-scan no-keeper-dlq R1) [RESIL-02, RESIL-03] ✓ 3/3 Phase=47 facts GREEN, 528/528 hermetic, SK_P.sln 0/0 Release; commits 43e4855, 4c771f3
- [x] 47-02-PLAN.md — Wave 1: duplicate-delivery no-collapse facts (StepCompleted + KeeperReinject, R3) + Phase-47 re-tag of the data-gone fact (R2 cited) [RESIL-02, RESIL-03] ✓ 6/6 Phase=47 facts GREEN, 530/530 hermetic, SK_P.sln 0/0; commits 29002c9, f6139d7
- [x] 47-03-PLAN.md — Wave 2: 47-DLQ-AUDIT.md traceability ledger (R5) + design-doc at-least-once amendment bundling the Phase-46 KeeperReinject.Payload note (D-02) [RESIL-02, RESIL-03] ✓ 8-row ledger (all rows green); design-doc A16 additive (1 insertion); Phase=47 6/6 GREEN, SK_P.sln 0/0; doc-only (git diff --stat = 2 doc files); commits 651d644, 61e9f3e

#### Phase 48: v3.x Teardown
**Goal**: The reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path + the `keeper-dlq` queue are removed (RETIRE-03), plus a final remnant sweep, leaving the system buildable and running on the v4 path alone. NOTE (D-02): the `H`/`flag[H]`/CAS dedup machinery (RETIRE-01) and the content-addressing + result manifest + N×M fan-out (RETIRE-02) were already removed in Phase 43 — coupled to the field/type reshape (compile-forced per D-01) — so this phase shrinks to RETIRE-03 + the remnant sweep.
**Depends on**: Phase 47 (the full v4 path — pipeline, BIT gate, 5-state recovery, consolidated `_DLQ1` — must be in place and the new give-up routing live before the old reactive recovery path is deleted, so no intermediate is non-buildable)
**Requirements**: RETIRE-03 (RETIRE-01/02 coupled-forward to Phase 43 per D-01/D-02)
**Success Criteria** (what must be TRUE):
  1. (RETIRE-01 — landed in Phase 43, remnant-sweep verify) The `H` identity, the `flag[H]` dedup gate, and the CAS `Pending→Ack` flips are removed from the processor and the orchestrator — no `MessageIdentity`/`flag[H]` references remain on the execution path, and the solution builds 0-warning.
  2. (RETIRE-02 — landed in Phase 43, remnant-sweep verify) Content-addressed L2 data, the result manifest, and the N×M manifest fan-out are removed — the orchestrator no longer fans a manifest, and L2 data is the GUID `entryId` scheme only.
  3. The reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path and the `keeper-dlq` queue are removed — Keeper recovers solely via the BIT gate + the four state messages, and there is no `Fault<T>` consumer and no `keeper-dlq` topology left.
  4. The full hermetic suite is GREEN and the solution builds clean (Release + Debug, 0 warnings) on the v4 path with all retired machinery gone (no dead `Ignore<>`/binding/key remnants).
**Plans**: 3 plans (3 waves — Wave 1: atomic teardown [reactive source+metrics delete, L2ProbeRecovery partial-delete, Program.cs unwire, const/config/key sweep, all coupled test deletes/edits — every intermediate builds per research §7]; Wave 2: Phase-48 negative-guard facts + widen the Phase-47 keeper-dlq scan, gated on the deletes landing; Wave 3: D-04 reconciliation [48-TEARDOWN-AUDIT.md + REQUIREMENTS flips + design-doc A17] + the SC-4 hermetic ×3-green + 0-warning close gate)
- [x] 48-01-PLAN.md — Wave 1: atomic teardown of the reactive Fault<T> path + KeeperMetrics + const/config/key sweep + coupled test deletes/edits (RETIRE-03; SC-3 + SC-4 build half) — complete 2026-06-09; Debug build 0-warning, hermetic suite 503/503 (RealStack-excluded). Commits 5f0e210, 384bde5.
- [x] 48-02-PLAN.md — Wave 2: ReactivePathRetiredFacts negative-guard (no Fault<T> consumer / no keeper-dlq literal / const absence / SC-2 ExecutionData-Guid-only) + widen the Phase-47 keeper-dlq scan (RETIRE-03, RETIRE-01/02 remnant-verify; SC-1/SC-2/SC-3) — complete 2026-06-09; Phase=48 trait facts 4/4 GREEN, widened Phase-47 scan GREEN, Debug build 0-warning. Commits 7bf0f18, 3ef4248.
- [x] 48-03-PLAN.md — Wave 3: 48-TEARDOWN-AUDIT.md ledger + REQUIREMENTS RETIRE-01/02/03 satisfied + design-doc A17 amendment + SC-4 close gate (hermetic ×3 GREEN + Release/Debug 0-warning) (RETIRE-01/02/03; SC-4) — complete 2026-06-09; SC-4 met (507/507 ×3, Release+Debug 0-warning). Commits c0df0bc, 8033773.

#### Phase 49: Live Proof & Close Gate
**Goal**: A real-stack E2E proves the full Pre/In/Post round trip plus each recovery path and the BIT-gate global pause-all/resume-all across a transient L2 outage, all sealed behind an N-consecutive-GREEN triple-SHA (psql / redis / rabbitmq) net-zero close gate matching prior-milestone discipline.
**Depends on**: Phase 48 (the gate seals the final v4-only contract, after teardown)
**Requirements**: TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):
  1. A real-stack E2E proves the full Pre → In → Post round trip end to end (dispatch consumed → output written to L2 → orchestrator advances on the per-item `ExecutionResult`).
  2. A real-stack E2E proves each recovery path: `REINJECT` data-present (re-injected to `queue:{ProcessorId}`), `REINJECT` data-gone → `_DLQ1`, `INJECT` (reconstructed `Completed` → orchestrator), and `DELETE`.
  3. A real-stack E2E proves the BIT-gate global pause-all/resume-all across a transient L2 outage (outage → pause all → L2 recovers → resume all), with pause/resume idempotent per job.
  4. The close gate runs N consecutive GREEN with triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE==AFTER net-zero — including the composite backup key (proven cleaned by `CLEANUP`/`INJECT`, not lingering on its 2-day TTL), the GUID data keys, and `_DLQ1` — at Release + Debug 0-warning.
**Plans**: 6 plans (4 + 2 gap-closure: 49-05 GAP-49-2, 49-06 GAP-49-3/4/5 — code closed; live N×GREEN close gate operator-gated)
  - [x] 49-01-PLAN.md — SC1 RealStack round-trip E2E (Pre->In->Post; output-to-L2; orchestrator-advance)
  - [x] 49-02-PLAN.md — SC2 RealStack recovery-paths E2E (REINJECT present/gone, INJECT, DELETE via keeper-recovery direct-publish)
  - [x] 49-03-PLAN.md — SC3 RealStack pause-resume-outage E2E (docker stop/start sk-redis; non-parallel collection)
  - [x] 49-04-PLAN.md — phase-49-close.ps1 triple-SHA close gate + 49-HUMAN-UAT.md operator runbook
  - [ ] 49-05-PLAN.md ΓÇö GAP-49-2 fix: ResumeAll clears pausedTriggerGroups after per-job loop (group-resume wrapper + no-burst/ordering regression)

### Progress (v4.0.0)

| Phase | Plans Complete | Status | Completed |
| ----- | -------------- | ------ | --------- |
| 43. Message Contracts & L2 Key Reshape | 5/5 | Complete | 2026-06-08 |
| 44. Processor Pre/In/Post-Process Pipeline | 3/3 | Complete | 2026-06-08 |
| 45. Keeper BIT Health Gate + Global Pause/Resume | 3/3 | Complete | 2026-06-08 |
| 46. Keeper 5-State Recovery + Orchestrator Per-Item Consume | 4/4 | Complete | 2026-06-09 |
| 47. DLQ Consolidation + At-Least-Once Semantics | 3/3 | Complete | 47-01 ✓ (RESIL-02, RESIL-03 structural guards); 47-02 ✓ (R3 no-collapse facts + R2 Phase-47 re-tag); 47-03 ✓ (47-DLQ-AUDIT.md ledger + design-doc A16 amendment) |
| 48. v3.x Teardown | 3/3 | Complete | 2026-06-09 |
| 49. Live Proof & Close Gate | 6/6 | In Progress (live gate operator-gated) | — |
