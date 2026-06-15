---
phase: quick-260615-dbf
plan: 01
subsystem: processor-pipeline
tags: [redis, ttl, slot-array, l2-projection, refactor, hermetic]
requires:
  - ProcessorLivenessOptions.ExecutionDataTtlSeconds (the single TTL source of truth)
provides:
  - "Structurally-coupled L2[messageId] index TTL == L2[entryId] data TTL (no desync foot-gun)"
affects:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
tech-stack:
  removed:
    - SlotArrayOptions (type + DI bind/validate + test helper + binding facts)
    - "Random.Shared RNG in the slot/index write path"
  patterns:
    - "Unify two coupled TTLs onto one const so 'expire together' is a structural guarantee, not a config invariant"
key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs
    - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
  deleted:
    - src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs
decisions:
  - "Commit Task 2's SlotArrayOptions.cs deletion alongside Task 1's pipeline commit (git rm was staged with the pipeline edit); the DI-block removal stayed its own commit — net effect and history are coherent."
  - "Kept every `using BaseProcessor.Core.Configuration;` import — ProcessorLivenessOptions/RetryOptions still resolve through it in all four touched files; removing it would break the 0-warning build."
metrics:
  duration: ~25m
  completed: 2026-06-15
  tasks: 3
  files: 11 modified, 1 deleted
---

# Phase quick-260615-dbf Plan 01: Unify L2 Slot-Array Index TTL to ExecutionDataTtl Summary

Collapsed the independent random `[300,600]s` slot-array index TTL onto the single bounded
`executionDataTtl` const the data write already uses, so the `L2[messageId]` allocation index and the
`L2[entryId]` data it points at can no longer desync (Phase-68 TEST-06: data expired at 5s while the
index lived 300-600s). "Expire together" is now a structural guarantee, not a config invariant.

## What Changed

- **ProcessorPipeline.cs** — Deleted the `SlotTtl()` RNG helper and the `IOptions<SlotArrayOptions>`
  ctor param (ctor is now 9 args). Both `KeyExpireAsync(L2[messageId], …)` sites (forward-Post
  allocation refresh + recovery retire refresh) now apply `executionDataTtl`, the same const the
  sibling `StringSetAsync(L2[entryId], …)` data write uses. Threaded `executionDataTtl` into
  `RunRecoveryAsync`. Rewrote the stale "random whole-HASH TTL / jitter / D-06" doc-comments to state
  the index TTL is the same bounded `executionDataTtl` as the data key (no RNG, expire-together).
  Control flow, effect-once / allocation-before-data / IN-01 retire-branch semantics unchanged.
- **SlotArrayOptions.cs** — Deleted (`git rm`); the type had no remaining consumer.
- **BaseProcessorServiceCollectionExtensions.cs** — Removed the
  `AddOptions<SlotArrayOptions>().Bind().Validate().ValidateOnStart()` block and its WR-01 comment.
- **Tests** — Dropped `DispatchTestKit.SlotOptions()` and the `SlotOptions()` arg at all 8 pipeline
  ctor call sites; removed the two `SlotArrayOptions` binding facts; added
  `PipelineForwardFacts.IndexTtl_EqualsDataTtl_EqualsConfiguredExecutionDataTtl_NoRng` proving the
  index EXPIRE TTL == the data SET TTL == the configured 300s (a single deterministic value, no range)
  — the Phase-68 TEST-06 desync regression guard.

## Invariant Preserved

Index TTL `>=` data TTL is satisfied by exact equality (both = `executionDataTtl`); the index is never
shorter than the data, so the effect-once ordering is intact.

## Verification

- `dotnet build SK_P.sln -c Release` → **0 warnings, 0 errors** (TreatWarningsAsErrors gate).
- No `SlotTtl` / `SlotArrayOptions` / `slotOptions` / `Random.Shared` / `SlotArrayTtl` token remains
  in `src/BaseProcessor.Core`; `SlotArrayOptions.cs` deleted; no `SlotArray*` keys in any
  `src/**/appsettings.json`.
- All in-scope hermetic facts pass: `Pipeline*Facts` (8/8 in PipelineForwardFacts incl. the new fact),
  `EntryStepDispatchConsumerFacts`, `ProcessorOptionsBindingFacts`.

## Deviations from Plan

None — the plan executed as written. Implementation note (not a deviation): the `git rm` of
`SlotArrayOptions.cs` (logically Task 2) was staged with and committed in Task 1's pipeline commit;
the DI-block removal remained its own Task 2 commit. The net change set and git history are coherent.

## Deferred Issues (out of scope — pre-existing)

The full `dotnet test tests/BaseApi.Tests` run was **289 failed / 481 passed / 770 total**. Every
failure is pre-existing and unrelated to this hermetic refactor (logged in `deferred-items.md`):

- **~286** live-stack failures (Integration/Postgres/RabbitMQ tests + `Failed to stop bus … (Not
  Started)`) — the docker stack was intentionally not started; this task is explicitly hermetic.
- **`ComposeYamlFacts.ComposeYaml_ProcessorSample_Sets_Short_ExecutionDataTtl`** expects
  `Processor__ExecutionDataTtl: "5"` but the parent commit `da91d32` set `compose.yaml` to `"300"`.
  A Phase-68 compose-restore vs. compose-config-test conflict; this task does not touch `compose.yaml`.

The live 7-scenario re-proof (with the docker stack up) is deferred to a separate task, per the plan.

## Self-Check: PASSED

- FOUND: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
- FOUND: src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
- FOUND: tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs (new fact present)
- DELETED (as intended): src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs
- Commits: 479f53d, fbadcd7, 981ddd6 (all present in `git log`)
