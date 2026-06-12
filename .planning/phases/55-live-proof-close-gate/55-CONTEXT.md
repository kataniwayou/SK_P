# Phase 55: Live Proof & Close Gate - Context

**Gathered:** 2026-06-12
**Status:** Ready for planning

<domain>
## Phase Boundary

A real-stack E2E proves the v5.0.0 slot-array **forward** pass (dispatch → slot-array allocation-index write → orchestrator advance), the organic **recovery** pass (`if exist L2[messageId]` → HGETALL slot array → re-send completed + retire), each **3-state keeper** transition (`REINJECT` present/absent, `INJECT`, `DELETE` both-key), and the **A19 active two-key index delete** — sealed behind an N-consecutive-GREEN triple-SHA net-zero close gate at Release+Debug 0-warning.

Requirements **TEST-01, TEST-02** (locked in REQUIREMENTS.md). This is the v5.0.0 milestone closeout — the live counterpart to the hermetic A18/A19 build shipped in Phases 50–54. It is an **adaptation** of the proven v4.0.0 Phase-49 close gate + SC E2E suite to the v5 model, NOT new infrastructure.

**Build-before-proof split (Phase 54 carry):** the hermetic build gate (0-warning Release+Debug + E2E compile) is the phase's autonomously-verifiable deliverable; the live N×GREEN run is operator-gated.

</domain>

<decisions>
## Implementation Decisions

### E2E test reshaping (Hybrid)
- **D-01:** Hybrid reshape of the three v4 SC E2E tests (`tests/BaseApi.Tests/Orchestrator/SC{1,2,3}*E2ETests.cs`):
  - **SC3** (`SC3PauseResumeOutageE2ETests` — BIT-gate global pause/resume across a `docker stop/start sk-redis` outage): retag essentially **as-is**. The BIT health gate + global pause-all/resume-all (A14) is **retained unchanged** in v5 (Phase 52 kept `BitHealthLoop`). Verify the v5 keeper still `bus.Publish`es `PauseAll`/`ResumeAll` on each health edge the same way; otherwise no behavior change.
  - **SC1** (`SC1RoundTripE2ETests` — forward round-trip): **adapt** to additionally assert the v5 slot-array allocation-index write `skp:msg:{messageId}` (written **before** the data key, allocation-before-data) and the A19 two-key net-zero at end-of-message.
  - **SC2** (`SC2RecoveryPathsE2ETests` — keeper states by direct-publish): **rewrite/extend** for the v5 3-state keeper (DELETE becomes the A19 **both-key** delete) AND add the organic recovery-pass proof (see D-03).
- **D-02:** Re-tag all SC tests `[Trait("Phase","55")]` (from `"49"`) so the phase-55 close gate's live run includes them while the hermetic suite (`Category!=RealStack`) still excludes them.

### Forward + recovery + keeper-state coverage
- **D-03:** Prove the recovery pass **organically** — leave a populated slot-array index (`skp:msg` HASH carrying entryId slots) in L2 and drive a recovery (re-fire), asserting the recovery branch end-to-end: `HGETALL` the slot array → re-send each completed step with a fresh `NewId.NextGuid()` exec (SLOT-03 **send-before-retire**) → retire the slot to `Guid.Empty` → A19 two-key delete net-zero at end. This is the `if exist L2[messageId]` branch the roadmap SC-1 requires.
- **D-04:** ALSO keep **per-state direct-publish** proofs (as v4 SC2) for deterministic per-state assertions, published straight at `queue:keeper-recovery`:
  - **REINJECT data-present** → re-inject a reconstructed `EntryStepDispatch` carrying `KeeperReinject.Payload` to `queue:{ProcessorId:D}` (assert origin-queue depth).
  - **REINJECT data-gone** → by-design silent drop (no throw/send/dead-letter; `keeper_reinject_dropped` counter); assert origin queue stays empty and DLQ does NOT increment.
  - **INJECT** → write `L2[m.EntryId]=m.Data` → send reconstructed `StepCompleted` → delete `m.DeleteEntryId` (assert data key written + source deleted).
  - **DELETE** = the **A19 both-key** delete → assert BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` are gone (single `DEL`), drop-on-absent. (This is the v5 delta from the v4 source-only DELETE.)

### Net-zero close gate (redis SHA + explicit index check)
- **D-05:** Clone `scripts/phase-49-close.ps1` → `scripts/phase-55-close.ps1`, keeping the proven triple-SHA protocol **verbatim**: idempotent Processor-row seed, compose-health pre-flight, BOTH-config 0-warning build gate, **N=3** consecutive-GREEN identical-fact-count cadence, `psql \l` SHA, **unfiltered** `redis-cli --scan` SHA, `rabbitmqctl -q list_queues name` SHA (NOT `name messages`), separate `skp-dlq-1` depth==0 additive check, steady-state exclusions (`skp:{procId:D}` liveness key + `_bus_` transient auto-delete queues).
- **D-06:** v5 net-zero deltas vs the v4 script:
  - **(a)** DROP the retired composite-backup namespace `skp:{corr}:{wf}:{proc}:{exec}` (Model-B retired in Phase 50) and its no-settle-wait note.
  - **(b)** The slot-array index `skp:msg:*` + data keys `skp:data:*` are the net-zero targets, captured automatically by the unfiltered `--scan` SHA (BEFORE==AFTER).
  - **(c)** ADD an explicit additive **`skp:msg:*` count==0** assertion (parallel to the `skp-dlq-1` depth==0 check) proving the A19 **active** reclaim. Net-zero is proven by the active two-key DELETE, **NOT** a TTL settle (the `skp:msg` random TTL is 300/600s and cannot be waited out). A lingering `skp:msg` surfaces as BOTH a redis SHA mismatch AND count>0 — never a silent TTL pass.
- **D-07:** Net-zero discipline stays in the E2E **teardown**: every minted `skp:data:*` / `skp:msg:*` is registered into `factory.L2KeysToCleanup` so a leak surfaces at the gate as a snapshot-and-compare mismatch — NO gate-side destructive flush, NO prefix filter narrowing the scan.

### Build-before-proof split + operator gate
- **D-08:** **Build gate runs FIRST** (the phase's autonomously-verifiable deliverable): `dotnet build SK_P.sln -c Release` AND `-c Debug` both 0-warning, AND the new/adapted RealStack E2E tests **COMPILE** (they're excluded from the hermetic run by `Category=RealStack` but must build). The phase-55-close.ps1 script exists and is syntactically valid.
- **D-09:** The live **N=3×GREEN** triple-SHA close run is **operator-gated** via a HUMAN-UAT runbook (e.g. `55-HUMAN-UAT.md`), mirroring Phase 49/39/33. It requires the rebuilt v5 docker stack (`docker compose up -d --build baseapi-service orchestrator processor-sample keeper`). **TEST-01/02 stay unticked** until the operator's GREEN run. The v5 wire contract is a BREAKING change (slot-array + 3-state + A19) — a mixed-version deployment mis-deserializes, and the embedded SourceHash must match the host build or the liveness gate false-passes/times out.

### Carried-forward locks (precedent / convention — captured, not re-decided)
- **D-10:** N=3 consecutive GREEN with an identical-fact-count Smell-A guard (phase-39/49 precedent).
- **D-11:** Stable Processor row seeded idempotently (genuine embedded SourceHash via GET-or-create on `uq_processor_source_hash`) so the procId — hence its `skp:{procId:D}` liveness key + `{procId:D}` dispatch queue — is stable across the whole 3-run gate (steady-state in both BEFORE and AFTER snapshots). **Verify the v5 Processor.Sample version** for the seed string (phase-49 used `"3.5.0"` from `src/Processor.Sample/appsettings.json`).

### Claude's Discretion
- xUnit collection/parallelization shaping for the new organic recovery-pass test (SC3 already isolates the redis-outage in `RedisOutageSerialCollection`; the organic recovery test can share the shared collection if it does NOT stop redis).
- Host-Redis polling / ES seam-log assertion mechanics — reuse SC1's `RealStackWebAppFactory` + `PollForHealthyLivenessAsync` + `ElasticsearchTestClient.PollEsForLog` precedent.
- The exact seed-string version value (verify against `src/Processor.Sample/appsettings.json`).

### Folded Todos
None — no pending todos matched this phase.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase requirements (locked)
- `.planning/REQUIREMENTS.md` — TEST-01 (RealStack E2E: forward + recovery + each keeper state), TEST-02 (close gate N×GREEN + triple-SHA BEFORE==AFTER net-zero incl. slot-array index + data keys, Release+Debug 0-warning). **MUST read.**

### Design source of truth
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → "Recovery Re-architecture (A18)" (slot-array forward/recovery + 3-state keeper) + "Active Index GC (A19)" (active two-key index delete; deterministic net-zero replacing A18's TTL-only reclaim) — LOCKED.

### Proven close-gate template (clone)
- `scripts/phase-49-close.ps1` — the v4.0.0 triple-SHA close gate to clone. Its header comments document every steady-state exclusion + net-zero discipline (unfiltered scan, composite no-settle, procId/`_bus_` exclusions, separate DLQ depth check, 3-GREEN cadence).
- `scripts/phase-39-close.ps1` — the original proven triple-SHA protocol (phase-49 is itself a clone of this).

### E2E tests to adapt (v4 → v5)
- `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` — forward round-trip (adapt: + slot-array index `skp:msg` write + A19 net-zero).
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — keeper states by direct-publish (rewrite: 3-state, both-key DELETE) + add organic recovery pass.
- `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` — BIT-gate pause/resume outage (retag ~as-is; A14 retained).
- `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — the `RealStackWebAppFactory` host overrides + net-zero teardown + `PollForHealthyLivenessAsync` + ES seam-log poll precedent that the SC tests reuse.

### A19 behavior being proven (Phase 54)
- `.planning/phases/54-terminal-index-delete/54-SPEC.md` + `54-CONTEXT.md` — the active two-key index delete (GC-01/02/03) this phase proves live.

### Key shapes
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData` = `skp:data:{entryId}`, `MessageIndex` = `skp:msg:{messageId}` (the slot-array allocation index, the A19 net-zero target).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- SC1's `RealStackWebAppFactory` host overrides + net-zero teardown (`L2KeysToCleanup`) + `PollForHealthyLivenessAsync` blocking-liveness + `ElasticsearchTestClient.PollEsForLog` seam-log poll — reused by all SC tests; the new organic recovery test reuses the same harness.
- `scripts/phase-49-close.ps1` — verbatim triple-SHA protocol; only v5 namespace/service/DLQ/index-check deltas change (D-05/D-06).
- `SC3PauseResumeOutageE2ETests.RedisOutageSerialCollection` — isolated non-parallel xUnit collection for any redis-stopping test.

### Established Patterns
- Triple-SHA BEFORE==AFTER (`psql \l` / `redis --scan` / `rabbitmq list_queues name`); separate additive depth/count==0 checks (`skp-dlq-1`; now + `skp:msg:*` count==0); steady-state exclusions; N=3 identical-fact-count cadence.
- `[Trait("Category","RealStack")]` excludes E2E from the hermetic suite; `[Trait("Phase","55")]` includes them in the close-gate live run.
- Net-zero via **active cleanup, not TTL settle** (v4 composite discipline → v5 `skp:msg` index).
- xUnit v3 on Microsoft.Testing.Platform: `dotnet test --filter` is ignored (MTP0001); filtered runs use the compiled `BaseApi.Tests.exe` with `--filter-not-trait Category=RealStack` (hermetic) / native MTP flags. (Carry into the close script + runbook.)

### Integration Points
- The live stack must run **rebuilt v5 images** (`baseapi-service orchestrator processor-sample keeper`); the embedded SourceHash must match the host build or the liveness gate false-passes.
- `skp-dlq-1` is now **keeper-local** (Phase 53) — the single surviving DLQ; the gate's DLQ loop stays the single-element `@('skp-dlq-1')`.

</code_context>

<specifics>
## Specific Ideas

- The A19 net-zero is the **headline distinction** from v4: `skp:msg:*` is now ACTIVELY deleted at end-of-message (two-key `DEL`), so close-gate net-zero is a deterministic production property, NOT a TTL race. The explicit `skp:msg:*` count==0 assertion (D-06c) makes this provable rather than silently folded into the unfiltered SHA.
- The **DELETE** keeper state is now the A19 **both-key** delete — the SC2 DELETE proof must assert BOTH `skp:data:{entryId}` AND `skp:msg:{messageId}` are gone in one `DEL`, not just the data key (the v4 SC2 asserted source-only).
- SC3 is the lowest-risk adaptation (A14 retained verbatim); SC2 + the new organic recovery test are the highest-effort (the slot-array recovery branch + 3-state + both-key DELETE are all v5-new).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within the TEST-01/02 scope. (This phase IS the milestone close gate; no further deferral.)

</deferred>

---

*Phase: 55-live-proof-close-gate*
*Context gathered: 2026-06-12*
