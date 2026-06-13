---
phase: 62-live-proof-close-gate
plan: 02
subsystem: testing
tags: [liveness, gate, realstack, fabricated-keys, net-zero, no-info-leak]

# Dependency graph
requires:
  - phase: 59-61
    provides: per-instance L2 keyspace (skp:proc:{procId:D}:{instanceId} + index SET) + the >=1-healthy orchestration-start gate (ProcessorLivenessValidator) the fabricated keys round-trip through
  - phase: 62-01
    provides: the RealStack regression suite carried at [Trait("Phase","62")] + the RealStackWebAppFactory harness reused wholesale
provides:
  - "SeedFabricatedLivenessAsync helper (writes a per-instance liveness key + SADDs the index member + registers both for net-zero teardown) co-located in SampleRoundTripE2ETests"
  - "InstanceIndexMembersToSrem teardown list on RealStackWebAppFactory + a DisposeAsync SREM loop draining it on the existing teardown connection"
  - "GateKeyspaceE2ETests — deterministic fabricated-key gate-verdict proof (>=1-healthy admit / none -> 422 / malformed -> 422) with a counts-only no-info-leak guard"
affects: [62-03, phase-62-close-script, 62-HUMAN-UAT]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fabricated-key craft-redis-state: build a ProcessorLivenessEntry via Create(...) + JsonSerializer.Serialize, write it directly to host Redis to drive the in-process gate verdict deterministically (no container, no timing race) — Phase-61 WR-01/WR-02 style"
    - "Per-skp:proc index net-zero: a distinct InstanceIndexMembersToSrem list (NOT the bare skp: ParentIndexMembersToSrem) SREM'd in DisposeAsync, because RealStackNetZeroSweepFixture does not sweep skp:proc:*"
    - "Distinct throwaway procId per pure-fabrication verdict test so the fabricated instance index never collides with a live replica's index"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/GateKeyspaceE2ETests.cs
  modified:
    - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs

key-decisions:
  - "Helper reused the class-level HostRedis const (SampleRoundTripE2ETests.cs:407) co-located with PollForHealthyLivenessAsync (the plan's :447 pointer is HostRedisFull inside the factory; the co-located static uses the outer const) — same endpoint, no new connection string"
  - "Test C writes the malformed value inline (the Task-1 helper only accepts a valid ProcessorLivenessEntry by design) and registers the key + index member explicitly, keeping the helper's valid-entry invariant intact"
  - "Entries built ONLY via ProcessorLivenessEntry.Create + consts (SchemaOutcome.Fail, LivenessStatus.*); no hand-authored skp:proc JSON except the deliberate malformed value in Test C"

patterns-established:
  - "Pattern: deterministic gate-verdict proof via fabricated per-instance liveness keys round-tripping through the in-process ProcessorLivenessValidator"
  - "Pattern: counts-only no-info-leak assertion (Assert.DoesNotContain on the fabricated-id prefix against the 422 problem body)"

requirements-completed: [TEST-01, TEST-02]

# Metrics
duration: 14min
completed: 2026-06-13
---

# Phase 62 Plan 02: Fabricated-Key Gate-Verdict Tests Summary

Deterministic D-04 fabricated-key gate-verdict proof: `GateKeyspaceE2ETests` drives the already-shipped
`ProcessorLivenessValidator` through the in-process WebAPI by crafting per-instance liveness keys directly
in host Redis — proving, with zero container lifecycle and zero timing race, that the gate ADMITS (204)
when >=1 fabricated healthy replica exists alongside unhealthy/stale siblings, BLOCKS (422 + RFC 7807) when
none qualify (carrying counts-only, no instanceIds), and treats a malformed value as 422 (never 500).
Backed by a co-located `SeedFabricatedLivenessAsync` helper + a new `skp:proc`-index SREM teardown list so
no fabricated key or member survives a test.

## What was built

### Task 1 — `SeedFabricatedLivenessAsync` helper + `InstanceIndexMembersToSrem` teardown (commit `27fb119`)
- Added `internal static SeedFabricatedLivenessAsync(factory, procId, instanceId, entry, ct)` next to
  `PollForHealthyLivenessAsync` in `SampleRoundTripE2ETests.cs`. It opens the host-Redis multiplexer,
  `StringSetAsync`s the per-instance key (`L2ProjectionKeys.PerInstance`, 60s TTL) with
  `JsonSerializer.Serialize(entry)`, `SetAddAsync`s the instanceId into the index
  (`L2ProjectionKeys.InstanceIndex`), and registers the key in `L2KeysToCleanup` + the member in the new
  `InstanceIndexMembersToSrem` list. No string literals — both key builders are used.
- Added `public List<(Guid ProcId, RedisValue Member)> InstanceIndexMembersToSrem` to
  `RealStackWebAppFactory` (distinct from `ParentIndexMembersToSrem`, which SREMs the bare `skp:` parent
  index — NOT `skp:proc:{procId}`).
- Extended `DisposeAsync` to drain the new list with `SetRemoveAsync(L2ProjectionKeys.InstanceIndex(procId), member)`
  on the SAME already-open teardown connection (no second multiplexer).

### Task 2 — `GateKeyspaceE2ETests.cs` (commit `53ee963`)
- New RealStack test class with `[Trait("Category","E2E")]`, `[Trait("Category","RealStack")]`,
  `[Trait("Phase","62")]`, `[Collection("Observability")]`.
- Each test seeds a DISTINCT throwaway Processor row (genuine embedded Sample `SourceHash` via
  `AssemblyMetadataAttribute`, compatible config schema) + step + workflow via the promoted-internal
  helpers, then fabricates keys on that procId via the Task-1 helper + `ProcessorLivenessEntry.Create`.
- **Test A** (`FabricatedKeys_OneHealthyAmongUnhealthyAndStale_Admits204`): healthy + unhealthy
  (`SchemaOutcome.Fail`) + stale (`now-25`, interval 10) siblings → POST start → `204 NoContent`.
- **Test B** (`FabricatedKeys_NoHealthyReplica_Blocks422_NoInfoLeak`): only unhealthy + stale → `422
  UnprocessableEntity`; reads the problem body and `Assert.DoesNotContain("fab-", body)` (counts-only,
  V7 no-info-leak).
- **Test C** (`FabricatedKeys_MalformedPerInstanceValue_Blocks422_Not500`): inline-written malformed value
  (`"{not-an-entry"`) + SADD, both registered for teardown → `422`, never `500`.

## Verification results

- `dotnet build SK_P.sln -c Debug` — **Build succeeded, 0 warnings.**
- `dotnet build SK_P.sln -c Release` — **Build succeeded, 0 warnings.**
- Task 1 structural grep (`SeedFabricatedLivenessAsync` + `InstanceIndexMembersToSrem` +
  `L2ProjectionKeys.PerInstance` + `SetRemoveAsync`) — **PASS.**
- Task 2 structural grep (RealStack + Phase 62 traits + `ProcessorLivenessEntry.Create` +
  `L2ProjectionKeys.PerInstance` + `HttpStatusCode.NoContent` + `HttpStatusCode.UnprocessableEntity`) —
  **PASS.**
- Hermetic suite `BaseApi.Tests.exe --filter-not-trait Category=RealStack` — **590/591 pass.** The new
  GateKeyspace tests are excluded by `Category=RealStack` (live-stack proof is Plan 03). The single
  failure is unrelated to this plan — see Deferred Issues.

## Deviations from Plan

None to the plan's own scope. Plan executed exactly as written (the helper uses the co-located
class-level `HostRedis` const at `:407`; the plan's `:447` pointer named `HostRedisFull` inside the
factory — same `localhost:6380` endpoint, so behavior is identical; noted as a key decision, not a
deviation).

## Deferred Issues

**DI-62-A — stale compose guard fails after the Plan 62-01 reshape (OUT OF SCOPE).** The single hermetic
failure is `ComposeYamlFacts.ComposeYaml_Has_ProcessorSample_Service_Block`
(`tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs:133-139`): it asserts
`Assert.Contains("container_name: sk-processor-sample", content)`, but Plan **62-01** intentionally
deleted that line when reshaping `processor-sample` to `deploy.replicas: 2` (commit `de40b89`). The guard
was not updated alongside the compose change. This is a Plan-62-01 test-update gap — **not** introduced or
touched by Plan 62-02 (whose `files_modified` is restricted to the two Orchestrator E2E test files).
Logged to `.planning/phases/62-live-proof-close-gate/deferred-items.md`; suggested fix is to swap the
`container_name` assertion for a `processor-sample` `deploy.replicas: 2` regex mirroring the existing
`ComposeYaml_Keeper_Declares_Two_Replicas` guard. Addressable via a 62-01 follow-up or `/gsd-code-review-fix 62`.

## Threat mitigations proven

- **T-62-04 (Tampering — fabricated index leaks into a later gate read):** every fabricated per-instance
  key registered in `L2KeysToCleanup`; every index member in `InstanceIndexMembersToSrem` SREM'd in
  `DisposeAsync`; distinct throwaway procId per test.
- **T-62-05 (DoS/robustness — malformed value crashes the gate):** Test C locks malformed → 422, not 500.
- **T-62-06 (Info disclosure — 422 leaks instanceIds):** Test B asserts the 422 body carries no `fab-`
  fabricated id.

## Known Stubs

None. No placeholder data, hardcoded empties, or unwired components introduced.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Orchestrator/GateKeyspaceE2ETests.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
- FOUND: .planning/phases/62-live-proof-close-gate/62-02-SUMMARY.md
- FOUND: commit 27fb119 (Task 1)
- FOUND: commit 53ee963 (Task 2)
