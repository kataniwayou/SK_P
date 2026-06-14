# Phase 64 — Deferred / Out-of-Scope Items

## RealStack E2E failures (out of scope for plan 64-01)

**Discovered during:** plan 64-01 phase-gate test run.

**Context:** Plan 64-01's verification is the hermetic `SampleProcessorFacts` suite (3 facts) plus
0-warning Debug/Release builds — all GREEN. A separate *unfiltered* full-suite run (which the
`--filter "FullyQualifiedName~SampleProcessorFacts"` arg silently fails to scope, because the test
project uses Microsoft.Testing.Platform — `VSTestTestCaseFilter` emits warning **MTP0001** and is
ignored) reported **7 failed / 619 passed / 626 total**. The correctly-scoped run
(`dotnet test ... -- --filter-method "*SampleProcessorFacts*"`) returns **3/3 GREEN**.

**Failure set:** the 7 failures are confined to the `[Trait("Category","RealStack")]` live-stack
E2E tests (9 files under `tests/BaseApi.Tests/Orchestrator/` + `…/Observability/`). These tests:
- are explicitly excluded from the hermetic suite (`Category!=RealStack`), and
- drive the **deployed** processor containers (image ~15 h old at execution time = the *old*
  `SampleConfig(string? Value)` echo behavior), which this plan changed in source but did NOT
  redeploy. Source change vs. deployed image mismatch ⇒ any E2E asserting the new (or old) shape
  diverges from the running stack.

**Why out of scope:** Plan 64-01 changes only `SampleConfig.cs`, `SampleProcessor.cs`, and
`SampleProcessorFacts.cs` (source + hermetic facts). Updating/redeploying the live E2E path
(`SampleRoundTripE2ETests` payload `{"value":...}` → `{"number":N,"label":"Step_*"}`, container
rebuild) belongs to the later phase-64 plans and the milestone's fan-out-workflow E2E + clean-state
harness (Phase 65+). These failures are pre-existing relative to this plan's scope boundary —
they were not introduced by, and cannot be resolved within, plan 64-01's three-file change.

**Action required (later phase):** rebuild the `processor-sample` image from the new source,
update the RealStack E2E payload/schema + assertions to the `{number,label}` shape, then re-run the
RealStack suite against the redeployed stack to obtain exact pass/fail per test.
