---
phase: quick-260615-kgz
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
  - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Processor.Sample/SampleProcessor.cs
  - src/Processor.BadConfig/BadConfigProcessor.cs
  - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
  - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
  - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
  - tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
autonomous: true
requirements: [ORCH-EXEC-PROPAGATE, PROC-PER-EXEC-LOG, OBS-PER-EXEC-KEY]

must_haves:
  truths:
    - "A continuation dispatch carries the inbound result's ExecutionId UNCHANGED (no regeneration)."
    - "SampleProcessor at ENTRY (executionId == Guid.Empty) spawns 2 ProcessItems, each with its OWN newly-minted ExecutionId."
    - "SampleProcessor DOWNSTREAM (executionId != Guid.Empty) returns 1 ProcessItem REUSING the inbound ExecutionId, value = inbound.number + config.Number with NO random."
    - "Every Step_* completed log carries BOTH the StepLabel structured param AND that execution's ExecutionId (via a nested BeginScope)."
    - "The analyzer groups Step_* ES hits by (correlationId, executionId) — each pair is one RunTrace; completeness/duplicate/started are per-instance."
    - "OBS-03 reconciliation is spawn-aware: ResultConsumed reconciles against DispatchSent + spawnExtra, where spawnExtra is derived from data (distinct-correlationId count), not hard-coded."
  artifacts:
    - path: "src/Orchestrator/Consumers/TypedResultConsumer.cs"
      provides: "ExecutionId propagation in continuation dispatch (m.ExecutionId, not NewId.NextGuid())"
      contains: "m.ExecutionId"
    - path: "src/BaseProcessor.Core/Processing/BaseProcessor.cs"
      provides: "internal ExecuteAsync seam extended with Guid executionId"
      contains: "Guid executionId"
    - path: "src/Processor.Sample/SampleProcessor.cs"
      provides: "entry/downstream branch keyed on executionId + per-execution log carrying StepLabel and ExecutionId"
      contains: "executionId == Guid.Empty"
    - path: "tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs"
      provides: "ExecutionIdFieldPath const mirroring StepLabelFieldPath"
      contains: "ExecutionIdFieldPath"
    - path: "tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs"
      provides: "(correlationId, executionId) keyed RunTrace"
      contains: "ExecutionId"
  key_links:
    - from: "ProcessorPipeline.RunForwardAsync"
      to: "processor.ExecuteAsync"
      via: "pass d.ExecutionId"
      pattern: "ExecuteAsync\\(validatedData, d\\.Payload, d\\.ExecutionId, ct\\)"
    - from: "TypedResultConsumer.Consume"
      to: "dispatcher.DispatchAsync"
      via: "executionId arg = m.ExecutionId"
      pattern: "m\\.CorrelationId, m\\.ExecutionId, m\\.EntryId"
    - from: "AnalyzerE2ETests.BuildRunTraces"
      to: "RunTrace.FromLabels"
      via: "group by (correlationId, executionId)"
      pattern: "ExecutionId"
---

<objective>
Make the v8 fan-out proof score a clean per-instance PASS by closing the gap where the 2 spawned
execution instances per cron fire could not each be tracked to a full 7/7. THREE coordinated changes:

1. Orchestrator propagates `ExecutionId` UNCHANGED through continuation dispatch (it was regenerated —
   a bug that erased instance lineage across steps).
2. Processor threads `executionId` through the seam, branches on entry-vs-downstream, and emits a
   per-execution log carrying BOTH `StepLabel` (which step) AND `ExecutionId` (which instance).
3. Analyzer keys per-(correlationId, executionId) instead of correlationId-only, and makes the OBS-03
   reconciliation spawn-aware (entry spawns 2 results from 1 dispatch).

Purpose: each spawned instance becomes an independently traceable run with a stable ExecutionId from
ENTRY through both fan-out sinks, so the analyzer can verify all 9 labels per instance.

Output: edited orchestrator consumer, processor seam + transform, analyzer model/engine/fixture, and
all hermetic facts updated. HERMETIC ONLY — the live 7-scenario sweep is run separately afterward.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md

<interfaces>
<!-- Verified from the codebase. Use these directly — no exploration needed. -->

EntryStepDispatch (src/Messaging.Contracts/EntryStepDispatch.cs):
  public Guid ExecutionId { get; init; } = Guid.Empty;   // already exists; carries the inbound exec

ProcessItem (src/BaseProcessor.Core/Processing/ProcessItem.cs):
  public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);

ExecutionLogScope (src/Messaging.Contracts/ExecutionLogScope.cs):
  public const string ExecutionId = "ExecutionId";  // surfaces at ES attributes.ExecutionId
  // NOTE: BuildState (the bus-wide consume filter) SKIPS ExecutionId when Guid.Empty (line 31).
  // That is WHY entry-spawn logs must carry the MINTED exec via a nested BeginScope here.

SampleConfig (src/Processor.Sample/SampleConfig.cs):
  public sealed record SampleConfig(int Number, string? Label) : ProcessorConfig;

Current seam chain:
  BaseProcessor (abstract):           internal abstract Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, CancellationToken ct);
  BaseProcessor<TConfig> (override):  internal sealed override ... ExecuteAsync(string validatedData, string payload, CancellationToken ct) { ...; return ProcessAsync(validatedData, config, ct); }
                                      protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, TConfig? config, CancellationToken ct);
  ProcessorPipeline.cs:235 call site: items = await processor.ExecuteAsync(validatedData, d.Payload, ct);

ALL overriders of the generic ProcessAsync (signature change ripples to each):
  - src/Processor.Sample/SampleProcessor.cs (Component 2 main rewrite)
  - src/Processor.BadConfig/BadConfigProcessor.cs (trivial — add Guid executionId param, ignore it)
  - tests: BaseProcessorSeamFacts.TestProcessor (+ its public InvokeAsync forwarder)
  - tests: PipelineInFacts.RealDeserProcessor
  - tests: DispatchTestKit.FakeProcessor (its _impl Func + ProcessAsync override)

Analyzer pipeline (all under tests/BaseApi.Tests/Observability):
  RunTrace.FromLabels(string correlationId, IReadOnlyList<string> labels)  — currently correlationId-only
  PassFailEngine.Analyze(runs, prom, triggerCount, scenarioId)             — startedRuns = runs.Count
  AnalyzerE2ETests.BuildRunTraces(hits)                                    — groups by attributes.CorrelationId
  EsIndexNames.StepLabelFieldPath = "attributes.StepLabel"                 — mirror for ExecutionId
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Orchestrator — propagate ExecutionId unchanged through continuation dispatch</name>
  <files>
    src/Orchestrator/Consumers/TypedResultConsumer.cs
    tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
  </files>
  <action>
    COMPONENT 1 (bug fix — propagate executionId, like the adjacent CorrelationId).

    TypedResultConsumer.cs:
    - At the dispatch call (currently line 82-85), change the executionId arg from `NewId.NextGuid()`
      to `m.ExecutionId`. Only stepId + step.ProcessorId change per successor; WorkflowId, CorrelationId,
      ExecutionId, EntryId all flow through UNCHANGED. Resulting call:
        await dispatcher.DispatchAsync(
            m.WorkflowId, stepId, step.ProcessorId, step.Payload,
            m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
    - Fix the comment at line ~80 that says "a freshly regenerated executionId (lineage)" — it now
      propagates the inbound result's ExecutionId UNCHANGED (preserving the per-instance lineage). Keep
      the comment accurate and concise; do not introduce v1/future language.
    - If `using MassTransit;` (line 1) becomes unused after removing NewId.NextGuid(), remove it to stay
      0-warning under TreatWarningsAsErrors. VERIFY whether MassTransit is referenced elsewhere in the
      file first — if any other symbol uses it, keep the using.

    ResultConsumeTests.cs:
    - Line 140: `Assert.NotEqual(Guid.Empty, msg.ExecutionId);  // regenerated lineage` → assert the
      dispatched ExecutionId EQUALS the inbound result's executionId:
        Assert.Equal(executionId, msg.ExecutionId);   // propagated UNCHANGED (not regenerated)
      (`executionId` is the local already declared at line 118 and set on the StepCompleted at line 126.)
    - Update the doc comment at line 25 ("executionId regenerated") → "executionId propagated unchanged".

    ResultAckTests.cs:
    - Line 169: `Assert.NotEqual(Guid.Empty, dispatched.ExecutionId); // regenerated lineage` → assert
      equality to the inbound result's executionId. The result at line 154-158 sets CorrelationId +
      EntryId but NOT ExecutionId, so add an explicit `ExecutionId = <local>` to the StepCompleted
      initializer and assert the dispatched call's ExecutionId equals that local (the RecordingDispatcher
      captures ExecutionId at line 216/223).
    - Update the doc comment at line 19 ("a regenerated executionId") → "the inbound executionId
      (propagated unchanged)".
  </action>
  <verify>
    <automated>dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-class "*ResultConsumeTests" --filter-class "*ResultAckTests"</automated>
  </verify>
  <done>
    Continuation dispatch passes m.ExecutionId (not NewId.NextGuid()); both flipped tests assert
    dispatched ExecutionId EQUALS the inbound result's ExecutionId; build 0-warning.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: Processor — seam extension + entry/downstream branch + per-execution log (StepLabel + ExecutionId)</name>
  <files>
    src/BaseProcessor.Core/Processing/BaseProcessor.cs
    src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
    src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    src/Processor.Sample/SampleProcessor.cs
    src/Processor.BadConfig/BadConfigProcessor.cs
    tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  </files>
  <behavior>
    SampleProcessor.ProcessAsync (the rewritten facts):
    - ENTRY (executionId == Guid.Empty): returns EXACTLY 2 ProcessItems; their ExecutionIds are distinct,
      non-empty, and NOT Guid.Empty (newly minted); each Data is {number = config.Number + Random.Next(0,100),
      label = config.Label}; emits 2 logs, log[i] carries StepLabel == config.Label AND ExecutionId ==
      result[i].ExecutionId.
    - DOWNSTREAM (executionId != Guid.Empty): given validatedData = {"number":N,"label":"Step_X"}, returns
      EXACTLY 1 ProcessItem REUSING the inbound executionId; Data.number == N + config.Number (NO random,
      deterministic); emits 1 log carrying StepLabel == config.Label AND ExecutionId == the inbound executionId.
    - Null config at entry still spawns 2 items (baseNumber 0) + 2 logs.
  </behavior>
  <action>
    COMPONENT 2. Do the SEAM signature change first (interface-first), then the SampleProcessor transform,
    then every other overrider, then the facts.

    SEAM (thread Guid executionId end to end):
    - BaseProcessor.cs: internal abstract ExecuteAsync → add `Guid executionId` param:
        internal abstract Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, Guid executionId, CancellationToken ct);
      Update the XML doc to mention the per-execution id seam (keep concise; no scope-reduction language).
    - BaseProcessor`1.cs: the internal sealed override gains `Guid executionId` and threads it to
      ProcessAsync; the protected abstract ProcessAsync gains `Guid executionId`:
        internal sealed override Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, Guid executionId, CancellationToken ct)
        { TConfig? config = ...; return ProcessAsync(validatedData, config, executionId, ct); }
        protected abstract Task<List<ProcessItem>> ProcessAsync(string validatedData, TConfig? config, Guid executionId, CancellationToken ct);
      Update the ProcessAsync XML doc to describe executionId (Guid.Empty == entry/seed; non-empty ==
      downstream, reuse it).
    - ProcessorPipeline.cs call site (line ~235): `processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct)`.
      Pass d.ExecutionId UNCHANGED. (No other pipeline change — Post still scopes item.ExecutionId at
      lines 293-299 for the framework RESULT log; that is separate from the author's Step_* log.)

    SampleProcessor.cs ProcessAsync — implement the entry/downstream branch (per <behavior>):
    - Signature gains `Guid executionId`.
    - `var baseNumber = config?.Number ?? 0; var label = config?.Label;`
    - if (executionId == Guid.Empty)  // ENTRY/seed
        for i in 0..1:
          var sum = baseNumber + Random.Shared.Next(0, 100);   // independent random per execution
          var thisExec = Guid.NewGuid();                       // mint a NEW exec per spawned execution
          var data = JsonSerializer.Serialize(new { number = sum, label }, ProcessorConfig.SerializerOptions);
          using (logger.BeginScope(new Dictionary<string, object> { [ExecutionLogScope.ExecutionId] = thisExec.ToString() }))
              logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);   // KEEP {StepLabel}; nested scope adds ExecutionId
          items.Add(new(ProcessOutcome.Completed, data, thisExec));
      else  // DOWNSTREAM
        parse validatedData {number,label} via JsonDocument/JsonSerializer (ProcessorConfig.SerializerOptions);
        var incomingNumber = parsed.number;
        var sum = incomingNumber + baseNumber;   // NO random — deterministic accumulate
        var data = JsonSerializer.Serialize(new { number = sum, label }, ProcessorConfig.SerializerOptions);
        using (logger.BeginScope(new Dictionary<string, object> { [ExecutionLogScope.ExecutionId] = executionId.ToString() }))
            logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);   // reuse inbound exec
        items.Add(new(ProcessOutcome.Completed, data, executionId));   // REUSE the inbound exec
      Add `using Messaging.Contracts;` for ExecutionLogScope. The structured {StepLabel} param MUST stay
      (the analyzer needs both StepLabel AND ExecutionId on every hit). The 6 correlation ids remain
      ambient from the consume filter; ExecutionId is the only id added here.
      Update the class/method XML doc to describe entry-vs-downstream + the dual StepLabel+ExecutionId log.

    OTHER OVERRIDERS (add `Guid executionId`, ignore it — must not break single-execution authors):
    - BadConfigProcessor.cs: ProcessAsync gains `Guid executionId` (unused; the body is the dead path).
    - BaseProcessorSeamFacts.cs: TestProcessor.ProcessAsync gains `Guid executionId`; its public
      InvokeAsync forwarder gains `Guid executionId` and threads it; the [Fact] call passes Guid.NewGuid()
      (or Guid.Empty — pick one; the double ignores it).
    - PipelineInFacts.cs: RealDeserProcessor.ProcessAsync gains `Guid executionId` (unused).
    - DispatchTestKit.cs: FakeProcessor._impl Func type and the ProcessAsync override gain `Guid executionId`
      (thread it into _impl or ignore — the existing _impl lambdas take (validatedData, config, _)).

    SampleProcessorFacts.cs (rewrite the two transform facts per <behavior>):
    - InvokeProcessAsync: update the reflection invocation arg array to the new 4-arg signature
      { validatedData, config, executionId, CancellationToken.None } and add a `Guid executionId` param to
      the helper.
    - Rewrite ProcessAsync_Spawns_Two... as the ENTRY fact: call with executionId = Guid.Empty; assert 2
      items, distinct non-empty ExecutionIds, 2 logs, each log carrying StepLabel == "Step_A1" AND an
      ExecutionId KVP == result[i].ExecutionId.ToString() (the log state now contains the BeginScope dict —
      BUT note CapturingLogger.BeginScope returns NullScope and does NOT merge scope state into Log state).
      DECISION (Claude's discretion): the existing CapturingLogger swallows scope via NullScope, so it
      cannot observe the BeginScope ExecutionId. To assert the ExecutionId-on-log behavior hermetically,
      make CapturingLogger CAPTURE the scope state: have BeginScope record the pushed dictionary onto a
      stack and expose, per captured Entry, the active scope KVPs (merge or store the scope dict alongside
      the message state). Assert the ExecutionId KVP from the captured scope for log[i] equals
      result[i].ExecutionId. Keep StepLabel/Sum assertions against the message state KVPs as before.
    - Add a DOWNSTREAM fact: call with a non-empty executionId and validatedData
      {"number":7,"label":"Step_B"} + config (3, "Step_B"); assert 1 item, ExecutionId == the inbound
      executionId, Data.number == 10 (7 + 3, no random), 1 log carrying StepLabel "Step_B" AND the inbound
      ExecutionId.
    - Update the null-config fact to pass executionId = Guid.Empty (still 2 items + 2 logs, baseNumber 0).
  </action>
  <verify>
    <automated>dotnet build SK_P.sln -c Release; dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-class "*SampleProcessorFacts" --filter-class "*PipelineInFacts" --filter-class "*BaseProcessorSeamFacts"</automated>
  </verify>
  <done>
    Seam threads Guid executionId; SampleProcessor branches entry (2 minted execs) vs downstream (reuse
    inbound, no random); each Step_* log carries StepLabel AND ExecutionId; all overriders compile;
    SampleProcessorFacts entry+downstream+null facts green; build 0-warning.
  </done>
</task>

<task type="auto">
  <name>Task 3: Analyzer — per-(correlationId, executionId) keying + spawn-aware OBS-03 reconciliation</name>
  <files>
    tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
    tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs
    tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
    tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
    tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
    tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  </files>
  <action>
    COMPONENT 3. Each (correlationId, executionId) pair is ONE RunTrace instance.

    EsIndexNames.cs:
    - Add `public const string ExecutionIdFieldPath = "attributes.ExecutionId";` mirroring
      StepLabelFieldPath (same DIRECT-path, no .keyword rationale — copy the xml-doc warning). The
      ExecutionId surfaces at attributes.ExecutionId via the BeginScope key from ExecutionLogScope
      (verified: same MEL→OTLP bridge as StepLabel/CorrelationId). Keep the comment noting this is the
      expected field; if a future Wave-0 probe shows otherwise, it is a single-const edit.

    RunTrace.cs:
    - Add `public required string ExecutionId { get; init; }` alongside CorrelationId (each trace = one
      (correlationId, executionId) instance).
    - FromLabels signature → `FromLabels(string correlationId, string executionId, IReadOnlyList<string> labels)`;
      set ExecutionId on the returned record. Duplicate/distinct arithmetic is unchanged (now WITHIN an
      instance — a label appearing >1× for the same (correlationId,executionId)).
    - Update the class XML doc: "one triggered run (one correlationId)" → "one execution instance (one
      (correlationId, executionId) pair)".

    AnalyzerE2ETests.cs (the RealStack fixture):
    - BuildStepSearchBody: add an `{ "exists": { "field": "<ExecutionIdFieldPath>" } }` filter clause so
      only hits carrying ExecutionId are grouped (entry-spawn + downstream logs all carry it now).
    - BuildRunTraces: change the grouping key from correlationId to the (correlationId, executionId)
      composite. Read attributes.ExecutionId per hit (skip defensively if missing, like CorrelationId/
      StepLabel). Use a Dictionary keyed by a value-tuple or composite string "corr|exec". Emit one
      RunTrace per pair via RunTrace.FromLabels(correlationId, executionId, labels).
    - SPAWN-AWARE OBS-03 (the reconciliation/precondition consequence): the entry step now emits 2 results
      from 1 dispatch, so ResultConsumed = DispatchSent + spawnExtra. Derive spawnExtra from data — it
      equals the number of entry dispatches = cron fires = distinct correlationIds (NOT hard-coded 2). The
      fixture passes the data the engine needs; compute distinctCorrelationIds from `traces`
      (traces.Select(t => t.CorrelationId).Distinct().Count()) and supply it to the engine. Do NOT change
      the ES-binding completeness verdict path — completeness is now per-instance (each pair must have all
      9 labels), which the per-instance RunTrace grouping already delivers.

    PassFailEngine.cs:
    - startedRuns = runs.Count still holds (now = distinct (correlationId, executionId) instances). Update
      the "STARTED" doc/comments to say "distinct (correlationId, executionId)" instead of
      "distinct correlationIds".
    - OBS-03 corroboration: make the reconciliation spawn-aware. Add a parameter for the spawn extra (e.g.
      `int distinctCorrelations` or a precomputed `spawnExtra`) and incorporate it where ResultConsumed /
      DispatchSent are reconciled. The existing impliedRuns = round(DispatchSentDelta / 9) corroboration
      assumed 1 dispatch per step per run; document that entry fan-out spawns an extra result per entry
      dispatch and reconcile ResultConsumedDelta against DispatchSentDelta + spawnExtra within the existing
      ±1-run tolerance. Keep it NON-BINDING (a warning, never flips a green ES verdict). Derive spawnExtra
      from data passed in — do NOT hard-code 2.
    - AnalyzerReport.cs: if the engine surfaces a new spawn-aware field (e.g. SpawnExtra or
      reconciled-ResultConsumed expectation), add the required init property + xml doc so the report stays
      serializable. Keep report-shape changes minimal and documented.

    PassFailEngineFacts.cs (update the ~7 per-branch facts for the new grouping + spawn-aware recon):
    - Every RunTrace.FromLabels call gains an executionId arg. Use distinct execIds where the fact needs
      distinct instances (e.g. Complete_AllStartedRuns: 3 instances → give corr/exec pairs); a duplicate
      WITHIN one (corr,exec) still drives Duplicate_TwoStepC fail-closed.
    - Update CorroboratingSnapshot / the corroboration facts to reflect spawn-aware reconciliation: if the
      engine now expects ResultConsumed = DispatchSent + spawnExtra, set the synthetic snapshot accordingly
      (e.g. spawnExtra = distinct-correlation count) so the clean facts stay Reconciled and the dead-run
      warning fact still fires.
    - Add/adjust a fact proving spawn-aware reconciliation: a cohort where ResultConsumedDelta exceeds
      DispatchSentDelta by exactly spawnExtra (the entry fan-out) reconciles CLEAN (no warning), and one
      where the excess is wrong raises the non-fatal warning. Keep the ES-binding verdict facts
      (complete/incomplete/duplicate) green/red as before, now per-instance.
  </action>
  <verify>
    <automated>dotnet build SK_P.sln -c Release; dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-class "*PassFailEngineFacts" --filter-class "*RunTrace*" --filter-class "*EsIndexNames*"</automated>
  </verify>
  <done>
    EsIndexNames has ExecutionIdFieldPath; RunTrace is (correlationId, executionId)-keyed; the fixture
    groups Step_* hits per instance and reads attributes.ExecutionId; OBS-03 reconciliation is spawn-aware
    (spawnExtra derived from distinct-correlationId count, never hard-coded); all PassFailEngineFacts green;
    build 0-warning. AnalyzerE2ETests (RealStack) compiles but is NOT run hermetically (Category=RealStack).
  </done>
</task>

</tasks>

<verification>
- `dotnet build SK_P.sln -c Release` → 0 warnings (TreatWarningsAsErrors).
- Full hermetic suite green (RealStack excluded):
  `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-not-trait "Category=RealStack"`
  (if that MTP flag form is unavailable, run the targeted per-class filters from each task — they cover
  every edited fact class.)
- No `--filter` (silently ignored in this MTP suite); use `-- --filter-class`/`--filter-method` with a
  single leading/trailing wildcard only (no middle wildcard).
- AnalyzerE2ETests is Category=RealStack — it must COMPILE but is intentionally NOT executed here
  (HERMETIC ONLY; the live 7-scenario sweep runs separately afterward).
</verification>

<success_criteria>
- COMPONENT 1: continuation dispatch carries m.ExecutionId unchanged; ResultConsumeTests +
  ResultAckTests assert equality to the inbound exec (regeneration tests flipped).
- COMPONENT 2: seam threads Guid executionId; SampleProcessor spawns 2 minted-exec items at entry and
  reuses the inbound exec downstream (no random downstream); every Step_* log carries BOTH StepLabel and
  ExecutionId; all 5 ProcessAsync overriders updated; SampleProcessorFacts entry/downstream/null facts green.
- COMPONENT 3: analyzer keys per (correlationId, executionId); ExecutionIdFieldPath added; OBS-03
  reconciliation spawn-aware (spawnExtra derived, not hard-coded); PassFailEngineFacts green.
- Build 0-warning; full hermetic suite green; no production single-execution processor broken.
</success_criteria>

<output>
After completion, create `.planning/quick/260615-kgz-per-correlationid-executionid-multi-exec/260615-kgz-SUMMARY.md`
</output>
