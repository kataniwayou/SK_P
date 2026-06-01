---
phase: 26-baseprocessor-core-library-identity-liveness
verified: 2026-06-01T20:30:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
---

# Phase 26: BaseProcessor.Core Library — Identity + Liveness Verification Report

**Phase Goal:** A reusable `BaseProcessor.Core` library exists on which a concrete processor self-identifies via its embedded SourceHash, resolves its identity + schema definitions over the bus (retrying through boot-before-register), and self-registers liveness into Redis L2 — only while Healthy, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.

**Verified:** 2026-06-01T20:30:00Z
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core`, and `AddBaseProcessor` wires startup so `Program.cs` stays minimal | VERIFIED | `BaseProcessor.Core.csproj` references only `BaseConsole.Core` + `Messaging.Contracts` (firewall confirmed; no `BaseApi.Service` ref, no `Version=` CPM violation); `AddBaseProcessor` in `BaseProcessorServiceCollectionExtensions.cs` composes the full stack in one call (L47-95) |
| 2 | At runtime the processor reads its SourceHash via reflection and resolves its identity by `GetProcessorBySourceHash` IRequestClient, retrying on timeout/not-found until it succeeds | VERIFIED | `AssemblyMetadataSourceHashProvider.Get()` reads `AssemblyMetadataAttribute` and throws `InvalidOperationException` on absent hash; `ProcessorStartupOrchestrator` Loop A retries on both `RequestTimeoutException` and `ProcessorIdentityNotFound` with `Math.Min(delay*2, cap)` bounded backoff; proven by `IdentityResolutionFacts` (NotFound->NotFound->Found, call count >= 3) |
| 3 | For each non-null input/output schema Id the processor resolves the definition via `GetSchemaDefinition`; null schema Ids are skipped; ConfigSchemaId is never queried | VERIFIED | Loop B iterates `{ context.InputSchemaId, context.OutputSchemaId }` only, skipping nulls via `if (schemaId is not { } id) continue;`; `ConfigSchemaId` appears 0 times in `ProcessorStartupOrchestrator.cs`; proven by `SchemaResolutionFacts` — input+output resolve, ConfigId never queried, null-input still reaches Healthy |
| 4 | A background heartbeat writes/refreshes `skp:{processorId:D}` every `Interval` seconds with sliding `Ttl` expiry, only once Healthy; a not-yet-Healthy replica writes nothing | VERIFIED | `ProcessorLivenessHeartbeat.ExecuteAsync` gates on `context.IsHealthy && context.Id is { } id` before each write; uses `StringSetAsync(L2ProjectionKeys.Processor(id), json, expiry: TimeSpan.FromSeconds(opts.TtlSeconds))`; proven by `LivenessHeartbeatFacts` (Case A: not-Healthy -> `KeyExists` false; Case B: Healthy -> key exists with TTL in range) |
| 5 | The written L2 value exactly matches what `ProcessorLivenessValidator` reads; multi-replica writes are lock-free (blind SET, last-write-wins) | VERIFIED | Writer reuses frozen `ProcessorProjection`/`LivenessProjection` records + `L2ProjectionKeys.Processor` builder + `LivenessStatus.Healthy` const (D-09, no literal `"skp:"` or `"Healthy"`); interval written in seconds; proven by `LivenessReaderRoundTripFacts` — real unchanged validator reads as live, then stale past `interval*2`; `Assert.Equal(interval, projection.Liveness.Interval)` (LIVE-03); blind whole-value `StringSetAsync` (LIVE-06) |

**Score:** 5/5 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/BaseProcessor.Core.csproj` | New library, CPM no-Version refs | VERIFIED | References `BaseConsole.Core` + `Messaging.Contracts`; no `Version=`; no `BaseApi.Service` |
| `src/BaseProcessor.Core/Identity/ISourceHashProvider.cs` | Stubbable seam interface | VERIFIED | `public interface ISourceHashProvider { string Get(); }` |
| `src/BaseProcessor.Core/Identity/AssemblyMetadataSourceHashProvider.cs` | Reflection read, fail-fast throw-when-absent (IDENT-03) | VERIFIED | Reads `AssemblyMetadataAttribute`; throws `InvalidOperationException` naming key only |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | Id + 3 schema Ids + defs + IsHealthy + WhenHealthy (D-06) | VERIFIED | All members present including `Task WhenHealthy` and `void MarkHealthy()` |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs` | Thread-safe `public sealed` impl | VERIFIED | `public sealed class ProcessorContext`; Volatile.Read/Interlocked.Exchange int-latch; TCS for WhenHealthy; MarkHealthy is idempotent via CAS |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | Abstract `ProcessAsync` seam (BPC-02) | VERIFIED | `protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` — locked signature per PROJECT.md:32 |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` | Minimal positional record (fields firmed Phase 27) | VERIFIED | `public sealed record ProcessResult()` — intentionally minimal, per-plan boundary |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | 4 independent seconds knobs via `[ConfigurationKeyName]` (CONFIG-01) | VERIFIED | `IntervalSeconds`/`TtlSeconds`/`RequestTimeoutSeconds`/`BackoffCapSeconds` each with `[ConfigurationKeyName]` mapping to bare key |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | `AddBaseProcessor` composition root (BPC-03) | VERIFIED | 95 lines; composes AddBaseConsole + AddBaseConsoleMessaging with both exchange:-scheme request clients; registers TimeProvider/ISourceHashProvider/IProcessorContext/orchestrator/heartbeat; removes StartupCompletionService |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | Two-loop startup orchestrator (IDENT-04/SCHEMA-01/02/RPC-04) | VERIFIED | 162 lines; Loop A identity + Loop B definitions with BackoffAsync returning next delay (TimeSpan?); MarkHealthy + MarkReady on completion; ConfigSchemaId never read |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` | Only-when-Healthy sliding-SET liveness writer (LIVE-01..06) | VERIFIED | 117 lines; Healthy gate; frozen records; L2ProjectionKeys.Processor builder; LivenessStatus.Healthy const; sliding SET..EX; D-11 log-and-continue catch |
| `tests/BaseApi.Tests/Processor/ProcessorTestHarness.cs` | Sequenceable NotFound->Found responder fixture | VERIFIED | `ResponderSequence` with Interlocked counters; both responders on named endpoints; exchange: request clients |
| `tests/BaseApi.Tests/Processor/RequestClientSchemeFacts.cs` | Wave 0 exchange: scheme + dual-response overload confirmation | VERIFIED | Uses `Response<T1,T2>` (corrected from RESEARCH); `response.Is(out Response<T>?)` confirmed |
| `tests/BaseApi.Tests/Processor/SourceHashProviderFacts.cs` | Throws-when-absent + message-names-KEY | VERIFIED | Substantive fact; asserts `InvalidOperationException` containing "SourceHash" |
| `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs` | Independent binding + defaults | VERIFIED | 2 facts; Interval/Ttl bind independently; empty config yields defaults |
| `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` | Test double overrides seam + DI-resolves | VERIFIED | Test double subclasses `BaseProcessor`; DI-resolves as abstract base |
| `tests/BaseApi.Tests/Processor/AddBaseProcessorFacts.cs` | Descriptor-inspection (graph + request clients + gate removal) | VERIFIED | 3 facts; asserts IProcessorContext, ISourceHashProvider, TimeProvider, orchestrator registered; both IRequestClients resolv from scope; StartupCompletionService removed |
| `tests/BaseApi.Tests/Processor/IdentityResolutionFacts.cs` | Loop A retry-then-resolve (IDENT-04/RPC-04) | VERIFIED | FakeTimeProvider-driven; asserts call count >= 3; Id populated; gate flipped |
| `tests/BaseApi.Tests/Processor/SchemaResolutionFacts.cs` | Loop B never-config + null-input-still-Healthy (SCHEMA-01/02) | VERIFIED | CapturingSchemaResponder asserts ConfigId never queried; null-input still reaches Healthy |
| `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` | Not-Healthy no-write + Healthy sliding TTL (LIVE-04/01/02/06) | VERIFIED | Case A: KeyExists false; Case B: key exists with TTL in range; valid JSON |
| `tests/BaseApi.Tests/Processor/LivenessReaderRoundTripFacts.cs` | Closed writer-reader loop: live then stale (LIVE-05/03) | VERIFIED | Real ProcessorLivenessValidator (InternalsVisibleTo); live case: no throw; stale case: OrchestrationValidationException("stale"); `Liveness.Interval == IntervalSeconds` (LIVE-03) |
| `tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs` | Dead-Redis log-and-continue, worker non-fault (D-11) | VERIFIED | Dead endpoint; ExecuteTask not Faulted; CapturingLogger asserts warning naming processorId |
| `tests/BaseApi.Tests/Processor/FakeProcessorContext.cs` | Test double IProcessorContext for heartbeat facts | VERIFIED | Settable IsHealthy/Id/definitions; correctly in tests/ only (not production) |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | `src/BaseProcessor.Core/BaseProcessor.Core.csproj` | ProjectReference | WIRED | `<ProjectReference Include="..\..\src\BaseProcessor.Core\BaseProcessor.Core.csproj" />` confirmed at line 132 |
| `BaseProcessorServiceCollectionExtensions.cs` | `ProcessorQueues.IdentityQuery / SchemaQuery` | `exchange:` scheme request clients | WIRED | `"exchange:" + ProcessorQueues.IdentityQuery` and `"exchange:" + ProcessorQueues.SchemaQuery` at lines 56-57 |
| `BaseProcessorServiceCollectionExtensions.cs` | `StartupCompletionService` removal | `services.Where(d => d.ImplementationType == ...).Remove` | WIRED | Verbatim removal loop at lines 86-91 |
| `ProcessorStartupOrchestrator.cs` | `IProcessorContext` | `SetIdentity / SetDefinition / MarkHealthy` | WIRED | All three called in ExecuteAsync; `MarkReady` also called at line 138 |
| `ProcessorLivenessHeartbeat.cs` | `Redis skp:{id:D}` | `L2ProjectionKeys.Processor(id) + StringSetAsync` | WIRED | `StringSetAsync(L2ProjectionKeys.Processor(id), json, expiry: ...)` at line 90-93 |
| `ProcessorLivenessHeartbeat.cs` | `IProcessorContext.IsHealthy` | per-beat gate | WIRED | `if (_context.IsHealthy && _context.Id is { } id)` at line 70 |
| `LivenessReaderRoundTripFacts.cs` | `ProcessorLivenessValidator` (unchanged reader) | real validator construction | WIRED | `new ProcessorLivenessValidator(_redis.Multiplexer, liveClock)` at lines 109, 115 |
| `SK_P.sln` | `src/BaseProcessor.Core/BaseProcessor.Core.csproj` | solution project entry | WIRED | `Project("...") = "BaseProcessor.Core", "src\BaseProcessor.Core\BaseProcessor.Core.csproj"` confirmed |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `ProcessorLivenessHeartbeat.cs` | `projection` (written to Redis) | `IProcessorContext.InputDefinition/OutputDefinition` (populated by orchestrator Loop B) + `clock.GetUtcNow().UtcDateTime` | Yes — real definitions from bus + real clock timestamp | FLOWING |
| `ProcessorStartupOrchestrator.cs` | `context.Id` / `context.InputDefinition` / `context.OutputDefinition` | `identityClient.GetResponse<...>` / `schemaClient.GetResponse<...>` (dual-response bus round-trip) | Yes — bus responses from named ReceiveEndpoints | FLOWING |
| `LivenessReaderRoundTripFacts.cs` | `projection` (read from real Redis) | One heartbeat write driven by the test at a fixed `FakeTimeProvider` instant | Yes — real Redis write/read, not hardcoded | FLOWING |

---

### Behavioral Spot-Checks

The Processor test slice is confirmed 32/32 GREEN per the Plan 03 SUMMARY verification evidence. No runnable server entry points exist in this phase (library + test only), so spot-checks via `curl` / `node` are not applicable.

| Behavior | Method | Result | Status |
|----------|--------|--------|--------|
| exchange: scheme routes to named endpoint | `RequestClientSchemeFacts` in-memory harness round-trip | Both identity + schema respond Found | PASS (confirmed by test) |
| Loop A retries NotFound before resolving | `IdentityResolutionFacts` with NotFoundCount=2 | call count >= 3, Id populated | PASS (confirmed by test) |
| Only-when-Healthy write gate | `LivenessHeartbeatFacts` Case A (IsHealthy=false) | KeyExists = false | PASS (confirmed by test) |
| Closed writer-reader round-trip | `LivenessReaderRoundTripFacts` real Redis + real validator | Live then stale past interval*2 | PASS (confirmed by test) |
| Dead-Redis resilience | `LivenessResilienceFacts` dead endpoint | ExecuteTask not Faulted; warning logged | PASS (confirmed by test) |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| BPC-01 | Plan 01 | `BaseProcessor.Core` reusable Generic-Host library built on `BaseConsole.Core` | SATISFIED | `BaseProcessor.Core.csproj` references BaseConsole.Core; CPM; bus firewall |
| BPC-02 | Plan 01 | Concrete processor subclasses one `abstract` method only | SATISFIED | `BaseProcessor` declares only `protected abstract ProcessAsync`; `BaseProcessorSeamFacts` proves DI-resolution |
| BPC-03 | Plan 02 | `AddBaseProcessor` composition root, minimal concrete `Program.cs` | SATISFIED | `BaseProcessorServiceCollectionExtensions.AddBaseProcessor` folds full stack; `AddBaseProcessorFacts` proves registration graph |
| IDENT-03 | Plan 01 | Runtime reads SourceHash from assembly metadata via reflection | SATISFIED | `AssemblyMetadataSourceHashProvider`; `SourceHashProviderFacts` proves throw-when-absent |
| IDENT-04 | Plan 02 | Processor resolves identity over bus, retrying on failure, boot-before-register tolerated | SATISFIED | `ProcessorStartupOrchestrator` Loop A; `IdentityResolutionFacts` proves retry-then-resolve |
| RPC-04 | Plan 02 | Both queries via MassTransit `IRequestClient` (first console-side RPC usage) | SATISFIED | `AddBaseProcessor` registers both clients on `exchange:` scheme; `RequestClientSchemeFacts` confirms scheme + dual-response API |
| SCHEMA-01 | Plan 02 | Non-null schema Ids resolved over bus, retrying on failure | SATISFIED | `ProcessorStartupOrchestrator` Loop B; `SchemaResolutionFacts` proves both input+output resolve |
| SCHEMA-02 | Plan 02 | Null schema Ids skipped, never a failure | SATISFIED | `if (schemaId is not { } id) continue;`; `SchemaResolutionFacts.LoopB_Null_Input_Skips_Request_And_Still_Reaches_Healthy` proves null-input still Healthy |
| LIVE-01 | Plan 03 | Heartbeat writes `skp:{processorId:D}` every Interval seconds, only while Healthy | SATISFIED | `ProcessorLivenessHeartbeat`; `LivenessHeartbeatFacts` |
| LIVE-02 | Plan 03 | Each beat refreshes timestamp + re-applies Ttl expiry (sliding) | SATISFIED | `StringSetAsync(..., expiry: TimeSpan.FromSeconds(opts.TtlSeconds))`; TTL asserted in `LivenessHeartbeatFacts` Case B |
| LIVE-03 | Plan 03 | Written `interval` equals configured heartbeat delay in seconds | SATISFIED | `new LivenessProjection(now, opts.IntervalSeconds, ...)` (seconds, not ms); `Assert.Equal(interval, projection.Liveness.Interval)` in `LivenessReaderRoundTripFacts` |
| LIVE-04 | Plan 03 | Writes only once Healthy; not-yet-Healthy replica does not write (absent to reader) | SATISFIED | `if (_context.IsHealthy && _context.Id is { } id)` gate; `LivenessHeartbeatFacts` Case A proves no key written when unhealthy |
| LIVE-05 | Plan 03 | Written L2 shape matches `ProcessorLivenessValidator` (v3.4.0 unchanged) | SATISFIED | Frozen records reused; `LivenessReaderRoundTripFacts` constructs real `ProcessorLivenessValidator`; live then stale proven |
| LIVE-06 | Plan 03 | Multi-replica writes lock-free (blind whole-value SET, last-write-wins) | SATISFIED | Blind `StringSetAsync` (no read-modify-write, no distributed lock); documented in code comments (T-26-11 accept) |
| CONFIG-01 | Plan 01 | `Interval` and `Ttl` are two independent appsettings values | SATISFIED | Two separate properties (`IntervalSeconds` / `TtlSeconds`) with independent defaults; `ProcessorOptionsBindingFacts` asserts Interval=10, Ttl=45 in the same test (independence proven) |

All 15 required IDs: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01 — SATISFIED.

No orphaned requirements: REQUIREMENTS.md maps exactly these 15 IDs to Phase 26, all checked [x].

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `BaseProcessorServiceCollectionExtensions.cs` (XML doc comment) | 32-33 | Stale comment says heartbeat "is NOT registered here" but code at line 79 registers it | Info | Documentation-only inconsistency; code is correct; behavior is as intended |
| `ProcessorStartupOrchestrator.cs` | 156 | `return null` in `BackoffAsync` | Info | NOT a stub — this is the documented shutdown path; `null` signals caller to return on cancellation |

No blockers. No stub indicators in production code paths. The `ProcessResult` minimal record is intentionally deferred to Phase 27 per explicit plan boundary (not a stub — it is the declared-but-not-invoked seam artifact).

---

### Human Verification Required

None. All success criteria are verifiable programmatically against the codebase. The key behavioral proof (writer/reader round-trip with the real `ProcessorLivenessValidator`) is covered by `LivenessReaderRoundTripFacts` running against real Redis with a `FakeTimeProvider` — no visual or real-time behavior requires human judgment.

---

### Gaps Summary

No gaps. All 5 observable truths verified, all 23 artifacts exist and are substantive and wired, all 8 key links confirmed, all 15 requirement IDs satisfied, data flows are real (not hardcoded), all 7 task commits exist in git.

The one documentation inconsistency (stale XML doc comment in `BaseProcessorServiceCollectionExtensions.cs` line 32-33) is an informational artifact — the code is correct, only the comment was not updated when Plan 03 added the heartbeat registration line.

---

### Commit Verification

All 7 task commits confirmed in git:

| Commit | Task |
|--------|------|
| `502d0a0` | feat(26-01): create BaseProcessor.Core project + wire into solution and tests |
| `d55f41d` | feat(26-01): identity contracts + liveness options + abstract seam |
| `459d869` | test(26-01): Wave 0 harness + confirm exchange: scheme and dual-response overload |
| `5eba602` | feat(26-02): AddBaseProcessor composition root + StartupCompletionService removal |
| `7c18ea1` | feat(26-02): ProcessorStartupOrchestrator two-loop identity+definition resolution |
| `c3e55c7` | feat(26-03): only-when-Healthy liveness heartbeat + register in AddBaseProcessor |
| `4ced004` | test(26-03): closed writer-reader round-trip + only-when-Healthy + Redis-fault resilience |

---

_Verified: 2026-06-01T20:30:00Z_
_Verifier: Claude (gsd-verifier)_
