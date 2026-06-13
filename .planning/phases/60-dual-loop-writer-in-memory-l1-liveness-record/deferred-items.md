# Phase 60 — Deferred Items (out of scope for this phase)

## E2E live-proof failures — the accepted D-06 stale window (Phase 61 reader + Phase 62 live proof)

7 `*E2ETests` in the full suite fail because they poll the **OLD flat liveness key**
`skp:{processorId}` (`L2ProjectionKeys.Processor(id)`) which Phase 60 **deliberately stopped
writing** (D-05 hard-swap; the heartbeat + startup now write only the per-instance key
`skp:proc:{id}:{instanceId}` + the index SET). The old flat-key WRITE was removed in **Plan 60-03**
(commit `348bf89`); these RealStack tests began failing then, not from Plan 60-04.

This is the LOCKED, accepted **D-06** decision (60-CONTEXT.md): "Between Phase 60 and Phase 61 the
orchestration-start liveness reader reads a key nobody writes → sees `absent`. This is accepted:
nothing live depends on orchestration-start liveness mid-milestone (no RealStack proof until Phase 62),
the reader swaps in Phase 61, and the hermetic suite stays green." RESEARCH §"Validation" + ROADMAP
defer the RealStack/triple-SHA close gate to **Phase 62** (TEST-01/02/03) and the `SMEMBERS`→`GET`-each
≥1-healthy reader swap to **Phase 61** (GATE-01/02/03).

Several of these also require the full docker compose stack on the `rabbitmq` compose hostname (the
`Failed to stop bus rabbitmq://rabbitmq/...` "Not Started" log noise) — an environment dependency, not
an assertion of in-scope behavior.

Failing tests (all poll the retired flat key / need the full live stack):
- `Orchestrator.SC3PauseResumeOutageE2ETests.LiveBitGate_PauseAllThenResumeAll_AcrossTrueTransientRedisOutage_DockerStopStart`
- `Orchestrator.SC1RoundTripE2ETests.LiveSampleProcessor_ForwardRoundTrip_OutputAndOrchestratorAdvance_Phase55`
- `Orchestrator.SC2RecoveryPathsE2ETests.LiveOrganicRecovery_PreSeededSlotArray_ReSendsCompletedThenRetiresThenTwoKeyDelete`
- `Orchestrator.SampleRoundTripE2ETests.LiveSampleProcessor_RoundTrip_AdvancesOrchestrator_OnTruthfulLivenessGate`
- `Orchestrator.GateACompositionE2ETests.SampleCompatible_GateAPasses_Healthy_Start204`
- `Orchestrator.GateACompositionE2ETests.BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422`
- `Orchestrator.MetricsRoundTripE2ETests.LiveRoundTrip_ProvesBusinessSeries_BottleneckPromQL_AndInstanceLabel`

**Action:** none in Phase 60. Re-point the live pollers to the per-instance key + index gate when the
reader swaps in **Phase 61**; prove green end-to-end in the **Phase 62** close gate.

**Hermetic suite:** GREEN — 582/582 non-E2E facts pass; full Phase=60 trait suite 17/17 green;
0-warning Release + Debug (`-warnaserror`).
