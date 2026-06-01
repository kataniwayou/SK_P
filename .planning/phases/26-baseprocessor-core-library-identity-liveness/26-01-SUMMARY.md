---
phase: 26-baseprocessor-core-library-identity-liveness
plan: 01
subsystem: infra
tags: [masstransit, request-client, dotnet, redis, processor, identity, liveness, di]

# Dependency graph
requires:
  - phase: 25-shared-contracts-webapi-responders
    provides: GetProcessorBySourceHash/GetSchemaDefinition dual-response contracts + ProcessorQueues endpoint-name constants + WebApi responders on named ReceiveEndpoints
  - phase: 18-baseconsole-core-library
    provides: StartupGate int-latch idiom mirrored by ProcessorContext; BaseConsole.Core host base referenced by the new library
provides:
  - "src/BaseProcessor.Core library wired into SK_P.sln + referenced by tests/BaseApi.Tests; references BaseConsole.Core + Messaging.Contracts only (firewalled from BaseApi.Service)"
  - "ISourceHashProvider seam + AssemblyMetadataSourceHashProvider (IDENT-03, fail-fast throw-when-absent)"
  - "IProcessorContext + ProcessorContext (Id + 3 schema Ids + input/output defs + volatile IsHealthy + Task WhenHealthy latch; thread-safe SetIdentity/SetDefinition/MarkHealthy)"
  - "abstract BaseProcessor.ProcessAsync seam (D-12/BPC-02) + ProcessResult record (declared, not invoked)"
  - "ProcessorLivenessOptions (4 independent seconds knobs: Interval/Ttl/RequestTimeout/BackoffCap, CONFIG-01) bound via [ConfigurationKeyName]"
  - "Wave 0 ProcessorTestHarness (sequenceable NotFound->Found responders on named endpoints + exchange: request clients) + CONFIRMED exchange:{name} scheme + GetResponse<TFound,TNotFound> overload + response.Is API for 8.5.5"
affects: [phase-27-execution-round-trip, baseprocessor-startup-orchestrator, baseprocessor-liveness-heartbeat]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Request client targets exchange:{ProcessorQueues.name} to route to the WebApi's named ReceiveEndpoint (RPC-04)"
    - "Dual-response GetResponse<TFound,TNotFound> returns Response<T1,T2> (not base Response); .Is(out Response<T>) is on the dual type"
    - "[ConfigurationKeyName] maps Seconds-suffixed property names to bare config keys (Interval/Ttl/RequestTimeout/BackoffCap)"
    - "public sealed holder types so AddSingleton<IFoo,Foo>() resolves cross-assembly without InternalsVisibleTo (StartupGate idiom)"

key-files:
  created:
    - src/BaseProcessor.Core/BaseProcessor.Core.csproj
    - src/BaseProcessor.Core/Identity/ISourceHashProvider.cs
    - src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs
    - src/BaseProcessor.Core/Identity/IProcessorContext.cs
    - src/BaseProcessor.Core/Identity/ProcessorContext.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/ProcessResult.cs
    - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
    - tests/BaseApi.Tests/Processor/SourceHashProviderFacts.cs
    - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs
    - tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs
  modified:
    - SK_P.sln
    - tests/BaseApi.Tests/BaseApi.Tests.csproj

key-decisions:
  - "ProcessorLivenessOptions uses [ConfigurationKeyName] to bind bare config keys (Interval/Ttl/RequestTimeout/BackoffCap) onto Seconds-suffixed properties — binds cleanly with descriptive property names"
  - "AssemblyMetadataSourceHashProvider throws InvalidOperationException naming only the KEY when SourceHash absent (fail-fast, T-26-02 DoS + V7 info-disclosure mitigation)"
  - "Wave 0 A5 correction: 8.5.5 dual GetResponse<T1,T2> returns Response<T1,T2>, not base Response; .Is(out Response<T>) lives on the dual type"
  - "Wave 0 correction: IRequestClient<T> is a SCOPED service — resolve from a DI scope, never the root provider"

patterns-established:
  - "Pattern: BaseProcessor.Core mirrors BaseConsole.Core csproj conventions (CPM no-Version, inherited Directory.Build.props props, firewall to bus contracts only)"
  - "Pattern: sequenceable test responder (shared ResponderSequence with Interlocked counters + NotFound thresholds) for retry-then-resolve fixtures"

requirements-completed: [BPC-01, BPC-02, IDENT-03, CONFIG-01]

# Metrics
duration: 7min
completed: 2026-06-01
---

# Phase 26 Plan 01: BaseProcessor.Core Skeleton + Identity Contracts + Wave 0 Harness Summary

**Stood up the compiling `BaseProcessor.Core` library (bus-firewalled) with the SourceHash seam, the shared `IProcessorContext` Healthy holder, the abstract `ProcessAsync` seam, and the liveness options POCO — and CONFIRMED the MEDIUM-confidence MassTransit 8.5.5 `exchange:` request-client scheme + dual-response `GetResponse<TFound,TNotFound>` overload against a real in-memory responder before Plan 02 relies on them.**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-06-01T19:09:21Z
- **Completed:** 2026-06-01T19:16:10Z
- **Tasks:** 3
- **Files modified:** 15 (13 created + 2 modified)

## Accomplishments
- New `src/BaseProcessor.Core` .NET 8 library: references BaseConsole.Core + Messaging.Contracts only (NO BaseApi.Service — bus firewall), CPM no-Version refs, builds clean on MassTransit 8.5.5; in the solution and referenced by tests.
- All dependency-free contracts: `ISourceHashProvider` + reflection impl (throws-when-absent), `IProcessorContext`/`ProcessorContext` (Id + 3 schema Ids + input/output defs + volatile `IsHealthy` + `Task WhenHealthy` latch, thread-safe mutators), abstract `BaseProcessor.ProcessAsync` seam + `ProcessResult`, `ProcessorLivenessOptions` (4 independent seconds knobs).
- Wave 0 harness fixture (sequenceable NotFound->Found responders on the named `processor-identity-query` / `schema-definition-query` endpoints + `exchange:` request clients) and a passing confirmation fact proving the `exchange:{name}` scheme routes + the dual-response overload + `response.Is` resolve for both identity and schema queries.
- Full solution builds 0 warnings / 0 errors; `*Processor*` slice 22/22 GREEN (5 new fact methods + Phase 25/8 Processor tests, no regression).

## Task Commits

1. **Task 1: Create BaseProcessor.Core project + wire into solution and tests** - `502d0a0` (feat)
2. **Task 2: Identity contracts + liveness options + abstract seam** - `d55f41d` (feat, TDD task)
3. **Task 3: Wave 0 harness + confirm exchange: scheme and dual-response overload** - `459d869` (test)

_Plan metadata commit follows this SUMMARY._

## Files Created/Modified
- `src/BaseProcessor.Core/BaseProcessor.Core.csproj` - New library, CPM no-Version refs, BaseConsole.Core + Messaging.Contracts, firewalled from BaseApi.Service
- `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs` - Stubbable SourceHash seam (IDENT-03)
- `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs` - Reflection read of `[assembly: AssemblyMetadata("SourceHash",...)]`, fail-fast throw when absent
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` - Shared identity/Healthy holder contract (D-06), exposes IsHealthy + WhenHealthy
- `src/BaseProcessor.Core/Identity/ProcessorContext.cs` - public sealed thread-safe impl (Interlocked latch + TCS WhenHealthy)
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` - abstract ProcessAsync seam (D-12/BPC-02), declared not invoked
- `src/BaseProcessor.Core/Processing/ProcessResult.cs` - minimal positional record (fields firmed in Phase 27)
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` - 4 independent seconds knobs via [ConfigurationKeyName] (CONFIG-01)
- `tests/BaseApi.Tests/Processor/SourceHashProviderFacts.cs` - throws-when-absent + message-names-KEY
- `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` - independent binding + defaults
- `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` - test double overrides seam + DI-resolves
- `tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs` - sequenceable responders + exchange: request clients fixture
- `tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs` - Wave 0 confirmation (identity + schema)
- `SK_P.sln` - added BaseProcessor.Core
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` - added BaseProcessor.Core ProjectReference

## Decisions Made
- **ProcessorLivenessOptions binding via `[ConfigurationKeyName]`**: the config keys are bare (`Interval`/`Ttl`/`RequestTimeout`/`BackoffCap`) but the properties carry the `Seconds` suffix for clarity; the attribute bridges them so the section binds cleanly. The two-property independence of Interval/Ttl is asserted by ProcessorOptionsBindingFacts (CONFIG-01).
- **SourceHash fail-fast**: `Get()` throws naming only the KEY (never a value) when the attribute is absent — fail-fast (T-26-02 DoS) + info-disclosure (V7) mitigation.
- **ProcessorContext mirrors StartupGate**: `public sealed` + `Volatile.Read`/`Interlocked.Exchange` int-latch + a `RunContinuationsAsynchronously` TCS for `WhenHealthy`; `MarkHealthy` is idempotent (CAS-guarded TCS completion).

## CONFIRMED Request-Client API (Wave 0 — for Plan 02 to use verbatim)

The MEDIUM-confidence RESEARCH §3 assumption was mostly correct but the response TYPE differs. The CONFIRMED 8.5.5 shape, proven by `RequestClientSchemeFacts`:

```csharp
// IRequestClient<T> is SCOPED — resolve from a DI scope, NOT the root provider.
var client = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();

// The dual GetResponse<TFound,TNotFound> overload returns Response<TFound,TNotFound>
// (a tuple-like dual type), NOT the base `Response`. `.Is(out Response<T>)` is declared on
// the dual type — declaring the variable as base `Response` does NOT compile.
Response<ProcessorIdentityFound, ProcessorIdentityNotFound> response =
    await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
        new GetProcessorBySourceHash(hash), ct, RequestTimeout.After(s: 5));

if (response.Is(out Response<ProcessorIdentityFound>? found))
    context.SetIdentity(found!.Message);
```

- `exchange:{name}` URI scheme + named `ReceiveEndpoint` routing: CONFIRMED (endpoints `Configured`/`Ready` in the harness log, responders replied).
- `RequestTimeout.After(s: 5)`: CONFIRMED.
- **Plan 02 must declare the dual response as `Response<T1,T2>` and resolve `IRequestClient<T>` from a scope.** The RESEARCH §3 snippet typed it as bare `Response` and resolved from the root — both will fail.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Type-name collision: `BaseProcessor` namespace vs type in BaseProcessorSeamFacts**
- **Found during:** Task 2 (seam fact)
- **Issue:** `using BaseProcessor.Core.Processing;` made the bare type name `BaseProcessor` resolve to the namespace root (CS0118: 'BaseProcessor' is a namespace but is used like a type).
- **Fix:** Added `using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;` (+ a ProcessResult alias) and referenced the alias.
- **Files modified:** tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
- **Verification:** Slice compiles + 4 passed.
- **Committed in:** d55f41d (Task 2 commit)

**2. [Rule 1 - Bug] RESEARCH §3 typed the dual response as base `Response`**
- **Found during:** Task 3 (Wave 0 confirmation — the explicit purpose of the gate)
- **Issue:** Declaring the dual `GetResponse<T1,T2>` result as base `Response` does not compile — `.Is(out Response<T>)` is declared on `Response<T1,T2>` (CS1061).
- **Fix:** Typed the variable as `Response<TFound, TNotFound>`. Recorded the corrected API above for Plan 02.
- **Files modified:** tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs
- **Verification:** Compiles, then fact passes.
- **Committed in:** 459d869 (Task 3 commit)

**3. [Rule 1 - Bug] `IRequestClient<T>` resolved from root provider (scoped service)**
- **Found during:** Task 3 (Wave 0 confirmation runtime)
- **Issue:** Resolving `IRequestClient<T>` from the root provider throws `Cannot resolve scoped service ... from root provider` at runtime.
- **Fix:** Created a DI scope (`provider.CreateScope()`) and resolved the request clients from `scope.ServiceProvider`. Recorded for Plan 02.
- **Files modified:** tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs
- **Verification:** Fact passes (1/1), `*Processor*` slice 22/22.
- **Committed in:** 459d869 (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (1 blocking compile collision, 2 bug corrections to the MEDIUM-confidence research API — surfaced exactly as the Wave 0 gate intended).
**Impact on plan:** All within the plan's explicit Wave 0 mandate ("if the build reveals the overload arg-order or `.Is` API differs from RESEARCH §3, FIX the harness/test to the actual 8.5.5 API and record the correction"). No scope creep.

## TDD Gate Compliance

Task 2 was marked `tdd="true"`. The task's deliverables are contract DECLARATIONS (interface, abstract seam, POCOs) where a separate RED commit on a not-yet-existing type would not compile in isolation; source + the three fact slices were authored together and the slices pass GREEN (4/4). No `test(...)`-before-`feat(...)` two-commit split was produced for Task 2 — recorded here for transparency. Task 3 is correctly a `test(...)` commit (`459d869`).

## Issues Encountered
- None beyond the three auto-fixed deviations above (all anticipated by the Wave 0 confirmation mandate).

## User Setup Required
None - no external service configuration required. The Wave 0 confirmation runs against an in-memory MassTransit harness (no real broker).

## Next Phase Readiness
- Plan 02 (composition root + startup orchestrator) can consume the frozen contracts directly: `IProcessorContext`, `ISourceHashProvider`, `ProcessorLivenessOptions`, `BaseProcessor`.
- **Plan 02 MUST use the CONFIRMED request-client shape above**: dual response typed as `Response<T1,T2>`, `IRequestClient<T>` resolved from a scope. The RESEARCH §3 snippet is otherwise correct (exchange: scheme, RequestTimeout.After).
- No blockers.

## Self-Check: PASSED

All 13 created files verified present on disk; all 3 task commits (502d0a0, d55f41d, 459d869) verified in git log.

---
*Phase: 26-baseprocessor-core-library-identity-liveness*
*Completed: 2026-06-01*
