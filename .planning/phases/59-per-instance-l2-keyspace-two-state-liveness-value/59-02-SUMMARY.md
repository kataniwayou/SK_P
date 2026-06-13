---
phase: 59-per-instance-l2-keyspace-two-state-liveness-value
plan: 02
subsystem: messaging-contracts
tags: [l2-keyspace, instance-identity, liveness, golden-tests, key-01, key-02, key-03]
requires:
  - "Messaging.Contracts leaf (existing L2ProjectionKeys SoT)"
provides:
  - "L2ProjectionKeys.PerInstance(Guid, string) — skp:proc:{processorId:D}:{instanceId} (KEY-01)"
  - "L2ProjectionKeys.InstanceIndex(Guid) — skp:proc:{processorId:D} (KEY-02), prefix of PerInstance"
  - "Messaging.Contracts.Identity.InstanceId.Resolve() — shared per-replica instance-id SoT (KEY-03 / D-04)"
affects:
  - "Phase-60 writer (PerInstance write + InstanceIndex SADD + InstanceId.Resolve injection)"
  - "Phase-61 gate (InstanceIndex SMEMBERS -> PerInstance GET each)"
tech-stack:
  added: []
  patterns:
    - "proc: discriminator on the L2 key (follows existing data:/msg: precedent)"
    - "explicit :D format specifier on processorId (self-documenting hyphenated form)"
    - "BCL-only resolver in the leaf (zero new dependency, zero cycle risk)"
    - "hermetic env-mutation facts with [Collection(\"Observability\")] + try/finally restore"
key-files:
  created:
    - src/Messaging.Contracts/Identity/InstanceId.cs
    - tests/BaseApi.Tests/Identity/InstanceIdResolverFacts.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
decisions:
  - "D-04 resolver home: Messaging.Contracts.Identity (consciously overrides the stale Phase-30 'wrong home' comment, which predates the resolver becoming a cross-cutting liveness SoT)"
  - "Deferred: repointing the two observability copies + ResolveInstanceIdFacts mirror to the new SoT is a separate dedupe sweep (NOT this phase); SoT and copies temporarily coexist"
  - "Used MTP-native --filter-class instead of the plan's stale VSTest --filter (which MTP silently ignores -> would run the full suite)"
metrics:
  duration: ~9m
  completed: 2026-06-13
---

# Phase 59 Plan 02: Per-Instance L2 Keyspace + Shared Instance-Identity Resolver Summary

Added the per-instance L2 key builders (`PerInstance` + `InstanceIndex`) to the existing `L2ProjectionKeys` SoT and hoisted the duplicated `instanceId` resolution chain into a single shared `Messaging.Contracts.Identity.InstanceId.Resolve()`, both pinned with hermetic golden + env-precedence facts. Purely additive — no consumer wired (Phase-60 writer + Phase-61 gate consume these next).

## What Was Built

- **KEY-01 — `L2ProjectionKeys.PerInstance(Guid processorId, string instanceId)`** → `skp:proc:{processorId:D}:{instanceId}`. The `proc:` discriminator follows the existing `data:`/`msg:` precedent; explicit `:D` on the processorId (no `"N"` in the key segment, Pitfall 2 avoided). `instanceId` is a plain already-resolved string, not a Guid.
- **KEY-02 — `L2ProjectionKeys.InstanceIndex(Guid processorId)`** → `skp:proc:{processorId:D}`, the per-processor instance-index SET key the Phase-60 writer SADDs into and the Phase-61 gate SMEMBERS. It is the exact prefix (before the trailing `:{instanceId}`) of `PerInstance` — locked by a `StartsWith` forward-fit fact.
- **KEY-03 / D-04 — `Messaging.Contracts.Identity.InstanceId.Resolve()`** → byte-identical `POD_NAME ?? HOSTNAME ?? MachineName ?? Guid.NewGuid().ToString("N")` chain, hoisted into the leaf (BCL-only body, the only assembly all three callers reference without a cycle).
- **Tests** — 3 new golden facts (KEY-01 literal pin, KEY-02 literal pin, prefix relationship) added to `L2ProjectionKeysTests` (now 12 facts); new `InstanceIdResolverFacts` (3 hermetic env-precedence facts against the real `Resolve()`).

The legacy flat `Processor(Guid)` builder was left in place per D-03 (retired in 60/61); the two observability copies and `ResolveInstanceIdFacts.cs` were left untouched (deferred dedupe sweep).

## Verification Results

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release -warnaserror` → **0 Warning / 0 Error** (after both Task 1 and Task 2).
- `L2ProjectionKeysTests` (MTP `--filter-class "*L2ProjectionKeysTests"`) → **12 passed / 0 failed**.
- `InstanceIdResolverFacts` (MTP `--filter-class "*InstanceIdResolverFacts"`) → **3 passed / 0 failed**.
- Scope guard: `git diff HEAD~3 HEAD` shows only the 4 in-scope files changed, **zero deletions**, `Processor(Guid)` still present, observability files + `ResolveInstanceIdFacts.cs` unchanged.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking tooling] Used MTP-native `--filter-class` instead of the plan's `--filter`**
- **Found during:** Task 3 verification.
- **Issue:** The plan's `dotnet test ... --filter "FullyQualifiedName~..."` uses VSTest syntax, which this project's Microsoft.Testing.Platform (MTP) runner silently ignores (emits MTP0001 and runs the FULL suite) — confirmed by the prior-wave (59-01) tooling note.
- **Fix:** Ran the two targeted classes with MTP-native `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -- --filter-class "*ClassName"`, serialized to avoid the TestResults log-lock collision.
- **Files modified:** None (verification-command-only deviation).
- **Commit:** N/A.

## Threat Surface

No new external input boundary introduced (per the plan's threat register T-59-04/05/06, all `accept`). `instanceId` originates from process-controlled env/BCL within the orchestration trust domain; the GUID fallback is a discriminator, not a security token. No threat flags raised.

## Commits

- `047faf7` — feat(59-02): add PerInstance + InstanceIndex L2 key builders
- `4f8b60b` — feat(59-02): hoist shared InstanceId.Resolve resolver into the leaf
- `d986023` — test(59-02): golden pins for PerInstance/InstanceIndex + resolver facts

## Self-Check: PASSED

All 5 declared files exist; all 3 commit hashes present in git history.
