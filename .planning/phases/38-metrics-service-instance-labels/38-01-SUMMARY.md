---
phase: 38-metrics-service-instance-labels
plan: 01
subsystem: processor-identity-roundtrip
tags: [contract, responder, processor-context, MLBL-03]
requires:
  - "ProcessorReadDto.Name / .Version (already exposed)"
  - "GetProcessorBySourceHashConsumer (RPC-01 responder)"
provides:
  - "ProcessorIdentityFound carries Name+Version (DB single-source-of-truth)"
  - "IProcessorContext / ProcessorContext expose resolved Name+Version"
affects:
  - "Plan 38-03 MeterProvider swap (reads found.Message.Name/.Version at the swap call-site)"
tech-stack:
  added: []
  patterns:
    - "positional record extension (append params, source-compatible only at known call-sites)"
    - "plain auto-prop identity members under the WR-03 read-after-IsHealthy invariant"
key-files:
  created: []
  modified:
    - src/Messaging.Contracts/ProcessorQueries.cs
    - src/BaseApi.Service/Features/Processor/Responders/GetProcessorBySourceHashConsumer.cs
    - src/BaseProcessor.Core/Identity/IProcessorContext.cs
    - src/BaseProcessor.Core/Identity/ProcessorContext.cs
    - tests/BaseApi.Tests/Messaging/ProcessorResponderTests.cs
    - tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs
    - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs
    - tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs
decisions:
  - "Task 1 (tdd) committed as a single feat: the positional-record extension + its assertion are atomically coupled (a record param cannot compile separately from its construction sites), so RED (compile-fail) and GREEN landed together rather than as two commits."
metrics:
  duration: "~25 min"
  completed: "2026-06-06"
  tasks: 2
  commits: 2
  files-modified: 10
requirements: [MLBL-03]
---

# Phase 38 Plan 01: Processor Identity Round-Trip (Name/Version Plumbing) Summary

Threaded the DB `Name`/`Version` through the processor identity round-trip (contract -> responder -> context) so the DB is the single source of truth for the processor's `{Name}_{Version}` metric identity (MLBL-03 (i)) — the contract/context half that gates the Plan 03 MeterProvider swap.

## What Was Built

**Task 1 — contract + responder (TDD):**
- `ProcessorIdentityFound` positional record extended with `string Name, string Version` (appended after the existing 4 params; leading comment updated to note the DB single-source-of-truth).
- `GetProcessorBySourceHashConsumer` now responds `new ProcessorIdentityFound(p.Id, ..., p.Name, p.Version)` (`p` is a `ProcessorReadDto` that already exposes both).
- `ProcessorResponderTests.SeededHash_Responds_..._With_Seeded_Fields` extended to assert `found.Message.Name == "seed"` and `found.Message.Version == "1.0.0"` (the seeded row values).

**Task 2 — context plumbing + CS0535 firewall:**
- `IProcessorContext` gained `string? Name { get; }` + `string? Version { get; }` (mirroring the schema-Id XML-doc style); the WR-03 memory-visibility invariant list now includes `Name`/`Version`.
- `ProcessorContext` added two plain private-set auto-props (no volatile, per WR-03) and stores `Name = identity.Name; Version = identity.Version;` inside `SetIdentity`.
- All three `IProcessorContext` test fakes updated: `StubContext` + `FakeProcessorContext` (settable props), `RecordingContext` (delegate-to-`_inner`).

## Verification Evidence

- `dotnet build SK_P.sln -c Debug` -> **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `dotnet build SK_P.sln -c Release` -> **Build succeeded, 0 Warning(s), 0 Error(s)** (CS0535 firewall holds — no missing interface members from the new `Name`/`Version`).
- `BaseApi.Tests.exe --filter-class "*ProcessorResponderTests*"` -> **2/2 passed** (both the seeded-Name/Version Found assertion and the unchanged NotFound regression).
- `BaseApi.Tests.exe --filter-class "*SchemaResolutionFacts*" "*DispatchBindSequenceFacts*" "*ProcessorIdEnricherTests*"` -> **6/6 passed** (no regression from the interface extension / fake updates).
- Note: `dotnet test ... --filter-not-trait` is not usable here — this is a Microsoft.Testing.Platform (MTP) test project; tests were run via the built `BaseApi.Tests.exe` with `--filter-class` (per the repo's established MTP discipline).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed 3 additional `ProcessorIdentityFound` construction sites broken by the positional extension**
- **Found during:** Task 1 (full test-project build after the record change).
- **Issue:** The plan's `<interfaces>` note claimed the positional extension was "source-compatible only at the two call-sites" (responder + `SetIdentity` reads). In fact three more *test* construction sites build the record positionally and broke with CS7036 (missing `Name`/`Version`): `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs:124` and `:160`, and `tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs:62`.
- **Fix:** Supplied placeholder `Name: "proc", Version: "1.0.0"` at each site (these fixtures do not assert on Name/Version; they only need to compile and drive the round-trip).
- **Files modified:** `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs`, `tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs`.
- **Commit:** `1ccae71` (folded into the Task 1 commit, since the build cannot pass without them).

## Authentication Gates

None.

## Known Stubs

None. The placeholder `Name`/`Version` values in the test fixtures (`"proc"`/`"1.0.0"`) are test-double inputs, not production stubs — the production path (`GetProcessorBySourceHashConsumer`) sources `Name`/`Version` from the real `ProcessorReadDto`.

## TDD Gate Compliance

Task 1 was `tdd="true"`. RED was proven first: the added `found.Message.Name`/`.Version` assertions caused a CS1061 compile failure (the record had no such members) — captured before the implementation. GREEN followed by extending the record + responder; the 2 facts then passed. Because a C# positional-record parameter cannot compile independently of the sites that construct it, the RED assertion and the GREEN implementation are an atomic unit and landed in one `feat(38-01)` commit (`1ccae71`) rather than separate `test`/`feat` commits — the RED->GREEN transition is documented above and reproducible from the commit's diff.

## Commits

- `1ccae71` feat(38-01): carry processor Name+Version through the identity contract+responder
- `867edaf` feat(38-01): expose processor Name+Version on IProcessorContext/ProcessorContext

## Self-Check: PASSED

All 4 modified production files + the SUMMARY exist on disk; both task commits (`1ccae71`, `867edaf`) exist in git history.
