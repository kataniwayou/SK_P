---
phase: quick-260615-dbf
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs
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
autonomous: true
requirements: [DBF-UNIFY-TTL]

must_haves:
  truths:
    - "The L2[messageId] slot-array index EXPIRE and the L2[entryId] data StringSet both apply the SAME TimeSpan derived from Processor:ExecutionDataTtl"
    - "No RNG (Random.Shared) remains anywhere in the slot/index write path"
    - "Index TTL >= data TTL still holds (now exactly equal — effect-once ordering preserved)"
    - "SlotArrayOptions type and its DI bind/validate no longer exist"
    - "dotnet build SK_P.sln -c Release is 0-warning (TreatWarningsAsErrors)"
    - "All hermetic Pipeline*Facts compile against the new 9-arg ctor and pass"
  artifacts:
    - path: "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs"
      provides: "Unified-TTL pipeline: both KeyExpireAsync calls use executionDataTtl; no SlotTtl() helper; no IOptions<SlotArrayOptions> ctor param"
      contains: "executionDataTtl"
    - path: "tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs"
      provides: "Hermetic fact asserting index EXPIRE TTL == data StringSet TTL == configured ExecutionDataTtl, deterministic single value"
      contains: "KeyExpireAsync"
  key_links:
    - from: "ProcessorPipeline.RunAsync"
      to: "ProcessorPipeline.RunRecoveryAsync"
      via: "executionDataTtl threaded as a new parameter"
      pattern: "RunRecoveryAsync\\(.*executionDataTtl"
    - from: "ProcessorPipeline forward-Post KeyExpireAsync"
      to: "executionDataTtl const"
      via: "the same const the data StringSetAsync uses"
      pattern: "KeyExpireAsync\\(L2ProjectionKeys\\.MessageIndex\\(messageId\\), executionDataTtl"
---

<objective>
Unify the L2[messageId] slot-array index TTL with the L2[entryId] execution-data TTL so the two
can never desync. The index HASH stores entryIds that point at the data keys, so their lifetimes are
semantically coupled; an independent random [300,600]s knob let a compose ExecutionDataTtl override
desync them (Phase 68 TEST-06: data expired at 5s while the index lived 300-600s). Collapsing both to
the single `executionDataTtl` const that RunAsync already computes makes "expire together" exact.

Purpose: Remove the desync foot-gun; "expire together" becomes a structural guarantee, not a config invariant.
Output: Pipeline edits, deletion of SlotArrayOptions + its DI wiring, and updated/added hermetic facts.

This is a purely hermetic unit/config refactor — NO docker, NO live stack. The live 7-scenario re-proof
happens separately AFTER this task.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md

<interfaces>
<!-- Current ProcessorPipeline primary ctor (src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:62-71).
     The slotOptions param (line 69) is being REMOVED. New ctor is 9 args. -->
```csharp
public sealed class ProcessorPipeline(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<ProcessorLivenessOptions> livenessOptions,
    IOptions<SlotArrayOptions> slotOptions,   // <-- DELETE THIS PARAM
    ProcessorMetrics metrics,
    ILogger<ProcessorPipeline> logger)
```

RunAsync already computes (line 91):
```csharp
var executionDataTtl = TimeSpan.FromSeconds(livenessOptions.Value.ExecutionDataTtlSeconds);
```
It threads executionDataTtl into RunForwardAsync (line 105) but NOT into RunRecoveryAsync (line 103).

The two index-TTL call sites that currently use SlotTtl():
- Recovery retire refresh — ProcessorPipeline.cs:164-165 (inside RunRecoveryAsync, retire-succeeded branch)
- Forward-Post allocation refresh — ProcessorPipeline.cs:265-266 (inside RunForwardAsync)

The data-key write (already correct, LEAVE IT) — ProcessorPipeline.cs:275:
```csharp
() => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl)
```

The KeyExpireAsync overload the production code + all DispatchTestKit stubs bind to:
`db.KeyExpireAsync(RedisKey, TimeSpan?, CommandFlags)` — the 2-arg call site supplies the TimeSpan and
defaults CommandFlags. This binding is UNCHANGED by the edit (TimeSpan value source changes only).

Test ctor call shape (all 7 Pipeline*Facts + EntryStepDispatchConsumerFacts) currently:
```csharp
new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
    DispatchTestKit.SlotOptions(), DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
```
The `DispatchTestKit.SlotOptions()` arg must be dropped from EVERY call site (8 files).

DispatchTestKit.Options(300) -> IOptions<ProcessorLivenessOptions> with ExecutionDataTtlSeconds=300,
so the unified TTL the pipeline applies is exactly TimeSpan.FromSeconds(300) under the hermetic facts.
</interfaces>

<!-- Confirmed during planning: NO SlotArrayTtlMin/Max keys exist in any src/**/appsettings.json
     (grep "SlotArrayTtl" over src/**/*.json returned zero matches). So step 3's appsettings cleanup
     is a no-op verification only — do NOT invent keys to remove. -->
</context>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: Unify both index TTLs to executionDataTtl; delete SlotTtl + SlotArrayOptions param</name>
  <files>src/BaseProcessor.Core/Processing/ProcessorPipeline.cs</files>
  <behavior>
    After this edit (verified by the facts in Task 3):
    - Forward-Post: the KeyExpireAsync on L2[messageId] applies executionDataTtl (the same const the
      sibling StringSetAsync data write uses).
    - Recovery retire: the KeyExpireAsync on L2[messageId] applies executionDataTtl.
    - No Random.Shared / RNG anywhere in the write path.
    - Index TTL == data TTL (>= invariant satisfied by equality; never shorter).
  </behavior>
  <action>
    In src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:

    1. DELETE the `SlotTtl()` helper (lines ~79-83, including its doc-comment) — the
       `Random.Shared.Next(...)` draw. Also delete the unused `using BaseProcessor.Core.Configuration;`
       import IF (and only if) nothing else in the file references that namespace after the param is
       removed (SlotArrayOptions was the only consumer — verify by checking remaining type references;
       removing an unused using is required for 0-warning under TreatWarningsAsErrors, but removing a
       still-needed one breaks the build, so confirm first).

    2. REMOVE the `IOptions<SlotArrayOptions> slotOptions,` parameter from the primary ctor (line 69).
       The ctor becomes 9 args.

    3. Thread `executionDataTtl` into RunRecoveryAsync:
       - Change its signature (line ~121-122) from
         `RunRecoveryAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)`
         to add `TimeSpan executionDataTtl` (mirror the position used by RunForwardAsync, i.e. before `ct`):
         `RunRecoveryAsync(EntryStepDispatch d, Guid messageId, IDatabase db, int limit, TimeSpan executionDataTtl, CancellationToken ct)`.
       - Update the RunAsync call site (line 103) to pass `executionDataTtl`:
         `await RunRecoveryAsync(d, messageId, db, limit, executionDataTtl, ct);`

    4. Repoint BOTH index KeyExpireAsync calls from `SlotTtl()` to `executionDataTtl`:
       - Recovery retire refresh (line ~164-165):
         `() => db.KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), executionDataTtl), limit, ct`
       - Forward-Post allocation refresh (line ~265-266):
         `() => db.KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), executionDataTtl), limit, ct`

    5. LEAVE the data StringSetAsync (line 275) exactly as-is — it already uses executionDataTtl.

    6. Update the now-stale doc-comments that describe a "random whole-HASH TTL" / "D-06 random TTL" /
       "[min,max]" / "expiry jitter" / "synchronized expiry herd" (the class-level <summary> Forward-Post
       bullet ~line 42, the inline comments at the two EXPIRE sites ~lines 168-170 and 265, and the
       DeleteTerminalAsync persist comment ~line 318 that says "cancel the random TTL"). Rewrite them to
       state the index TTL is the SAME bounded executionDataTtl as the data key (no RNG, expire-together).
       Keep the effect-once / allocation-before-data / IN-01 retire-branch semantics intact — only the
       TTL-source wording changes. Do NOT alter control flow.
  </action>
  <verify>
    <automated>dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release</automated>
  </verify>
  <done>
    BaseProcessor.Core compiles 0-warning in Release; no `Random` / `SlotTtl` / `SlotArrayOptions` /
    `slotOptions` token remains in ProcessorPipeline.cs; both index KeyExpireAsync sites read
    `executionDataTtl`; RunRecoveryAsync takes and uses the threaded executionDataTtl.
  </done>
</task>

<task type="auto">
  <name>Task 2: Delete SlotArrayOptions type and its DI bind/validate</name>
  <files>src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs, src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs</files>
  <action>
    1. DELETE the file src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs entirely
       (use: Remove-Item / git rm — the type is no longer referenced after Task 1).

    2. In src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs remove
       the SlotArrayOptions registration block (lines ~104-115): the
       `services.AddOptions<SlotArrayOptions>().Bind(...).Validate(...).ValidateOnStart();` call AND its
       leading comment block (the "Phase 51 (D-04): the slot-array random-TTL knobs..." / "WR-01: validate
       the [min,max] range ONCE..." comment, ~lines 104-109). Leave the surrounding step 3
       (ProcessorLivenessOptions Configure, line 102) and step 3b (RetryOptions, line 121) intact.

    3. Remove the now-unused `using BaseProcessor.Core.Configuration;` import (line 4) ONLY if no other
       symbol in this file resolves through it after the block is removed (verify — ProcessorLivenessOptions
       lives in that same namespace and IS still used at line 102, so this using is almost certainly STILL
       NEEDED; do not remove it unless a build proves otherwise).

    4. Appsettings: confirmed during planning there are NO SlotArrayTtlMin/SlotArrayTtlMax keys in any
       src/**/appsettings.json (Processor.Sample, Processor.BadConfig, etc.). Verify once with a grep; if
       truly absent, this is a no-op. Do NOT add or invent keys.
  </action>
  <verify>
    <automated>dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Release</automated>
  </verify>
  <done>
    SlotArrayOptions.cs is gone; the AddOptions&lt;SlotArrayOptions&gt; bind/validate block is gone;
    BaseProcessor.Core compiles 0-warning; no appsettings carries SlotArray* keys.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 3: Fix test ctor call sites; drop SlotOptions helper; assert unified deterministic TTL</name>
  <files>tests/BaseApi.Tests/Processor/DispatchTestKit.cs, tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs, tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs, tests/BaseApi.Tests/Processor/PipelinePostFacts.cs, tests/BaseApi.Tests/Processor/PipelinePreFacts.cs, tests/BaseApi.Tests/Processor/PipelineInFacts.cs, tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs, tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs, tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs</files>
  <behavior>
    - Every Pipeline*Facts + EntryStepDispatchConsumerFacts constructs the 9-arg ProcessorPipeline
      (no SlotOptions arg) and compiles.
    - A NEW hermetic fact in PipelineForwardFacts proves: on a completed forward item, the index
      KeyExpireAsync(L2[messageId]) TTL == the data StringSetAsync(L2[entryId]) TTL == the configured
      ExecutionDataTtl (300s under DispatchTestKit.Options(300)) — a single deterministic value, NO range,
      NO RNG. (Index TTL >= data TTL holds by equality.)
    - The existing Completed_AllocationBeforeData "D-06 random TTL" wording is updated to "unified TTL"
      (the ordering assertion stays valid).
    - SlotArrayOptions binding facts removed (the type no longer exists).
  </behavior>
  <action>
    1. In DispatchTestKit.cs DELETE the `SlotOptions(int min = 300, int max = 600)` helper (lines ~529-537,
       including its doc-comment). Remove any now-unused `using BaseProcessor.Core.Configuration;` ONLY if
       no other symbol needs it (verify — DispatchTestKit references ProcessorLivenessOptions/RetryOptions
       which live in that namespace, so the using is likely STILL needed; build to confirm).

    2. In ALL 8 pipeline-ctor call sites, drop the `DispatchTestKit.SlotOptions(),` argument so each
       constructs the 9-arg ctor. Files + lines:
         - PipelineForwardFacts.cs:35-36
         - PipelineRecoveryFacts.cs:33-34 AND 39-40 (two Build overloads)
         - PipelinePostFacts.cs:32-33
         - PipelinePreFacts.cs:25-26
         - PipelineInFacts.cs:28-29
         - PipelineEndDeleteFacts.cs:27-28
         - EntryStepDispatchConsumerFacts.cs:31-32
       After removal each call reads:
         `new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),`
         `    DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);`

    3. In ProcessorOptionsBindingFacts.cs DELETE the two SlotArrayOptions facts
       (`SlotArray_Binds_Min_Max_From_Processor_Section` lines ~63-79 and
       `SlotArray_Empty_Config_Yields_Baked_Defaults` lines ~81-92) and the
       `using BaseProcessor.Core.Configuration;` import IF it becomes unused (the remaining
       ProcessorLivenessOptions facts use that namespace too — likely keep it; build to confirm). The
       ProcessorLivenessOptions facts in this file are UNCHANGED.

    4. In PipelineForwardFacts.cs update Completed_AllocationBeforeData: change the "D-06 ... whole-HASH
       random TTL" comment wording (lines ~93-98) to describe the UNIFIED executionDataTtl (the
       expireIdx > hashIdx ordering assertion is still correct and stays).

    5. ADD a new hermetic fact to PipelineForwardFacts.cs — e.g.
       `IndexTtl_EqualsDataTtl_EqualsConfiguredExecutionDataTtl_NoRng` — using the existing
       DispatchTestKit.ForwardOkL2 fixture + Build(...) (Options(300)). After RunAsync on a single
       completed item:
         - Collect db.ReceivedCalls(). Find the KeyExpireAsync call whose first arg key ==
           L2ProjectionKeys.MessageIndex(messageId); read its TimeSpan?-typed argument (overload-agnostic:
           `Array.FindIndex(parameters, p => p.ParameterType == typeof(TimeSpan?))`, mirroring
           PipelinePostFacts.PostCompleted_WritesWithTtl). Assert it equals TimeSpan.FromSeconds(300).
         - Find the StringSetAsync call whose key starts with `{L2ProjectionKeys.Prefix}data:`; read its
           TimeSpan? expiry arg the same overload-agnostic way. Assert it equals TimeSpan.FromSeconds(300).
         - Assert the two TTLs are EQUAL (index == data) and BOTH == the configured 300s — a single
           deterministic value (no [300,600] range). This is the regression guard for the Phase-68 TEST-06
           desync. (No RNG is provable structurally: SlotTtl() is gone — the assertion that the value is an
           exact configured constant, not a range, is the hermetic proxy for "no RNG in the write path".)
       Keep the fact self-contained; reuse Ctx(), Build(), DispatchTestKit.ForwardOkL2,
       DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out")), DispatchTestKit.Dispatch.
  </action>
  <verify>
    <automated>dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-class "*Pipeline*Facts*"</automated>
  </verify>
  <done>
    All Pipeline*Facts + EntryStepDispatchConsumerFacts compile against the 9-arg ctor and pass; the new
    unified-TTL fact passes (index TTL == data TTL == 300s, deterministic); ProcessorOptionsBindingFacts no
    longer references SlotArrayOptions; full BaseApi.Tests suite builds 0-warning in Release.
  </done>
</task>

</tasks>

<verification>
1. `dotnet build SK_P.sln -c Release` → 0 warnings, 0 errors (TreatWarningsAsErrors gate).
2. No `SlotTtl`, `SlotArrayOptions`, `slotOptions`, `Random.Shared`, `SlotArrayTtl` token remains in
   src/BaseProcessor.Core (grep). SlotArrayOptions.cs deleted.
3. `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → all tests pass (MTP: omit
   --filter or use the `-- --filter-class`/`-- --filter-method` MTP-native form; plain `--filter` is
   silently ignored and runs all 638 — which is acceptable for the final full run).
4. The new unified-TTL fact proves index EXPIRE TTL == data SET TTL == configured ExecutionDataTtl (300s),
   a single deterministic value — the Phase-68 TEST-06 desync regression guard.
</verification>

<success_criteria>
- Both L2[messageId] index KeyExpireAsync calls and the L2[entryId] data StringSetAsync apply the SAME
  executionDataTtl const (no independent knob, no RNG).
- Index TTL >= data TTL preserved (now exactly equal — effect-once ordering intact).
- SlotArrayOptions type + DI bind/validate + test helper + binding facts removed.
- `dotnet build SK_P.sln -c Release` is 0-warning; full BaseApi.Tests suite passes.
- Purely hermetic — no docker invoked. Live 7-scenario re-proof deferred to a separate task.
</success_criteria>

<output>
After completion, create `.planning/quick/260615-dbf-unify-l2-slot-array-index-ttl-to-executi/260615-dbf-SUMMARY.md`
</output>
