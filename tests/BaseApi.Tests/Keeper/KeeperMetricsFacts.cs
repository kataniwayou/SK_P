using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Keeper;
using Keeper.Consumers;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KMET-01 + KMET-03 (Phase 39 Plan 01). Hermetic guard for the code-defined <c>Keeper</c> meter
/// (<see cref="KeeperMetrics"/>) — the house IMeterFactory pattern (no <c>static Meter</c> field, so
/// no cross-test static leak), asserting:
/// <list type="bullet">
///   <item><description>the meter-name const is exactly <c>"Keeper"</c>;</description></item>
///   <item><description>all eight instruments construct non-null (six <see cref="Counter{T}"/>, one
///   <see cref="UpDownCounter{T}"/>, one <see cref="Histogram{T}"/>);</description></item>
///   <item><description>each instrument's emitted name is the EXACT snake_case literal with NO
///   <c>_total</c>/<c>_seconds</c>/<c>_count</c> embedded suffix (the collector appends those);</description></item>
///   <item><description>the histogram carries the second-scale custom bucket boundaries
///   <c>{1,5,10,30,60,120}</c> via <c>InstrumentAdvice&lt;double&gt;</c> (Route A — DiagnosticSource 10.0.0);</description></item>
///   <item><description>a recorded measurement carries the bounded <c>ProcessorId</c> tag but NO
///   <c>workflowId</c>/<c>WorkflowId</c> tag key (KMET-02 cardinality guard, T-39-01).</description></item>
/// </list>
/// Hermetic (default Category) — no real stack.
/// </summary>
public sealed class KeeperMetricsFacts
{
    private static IMeterFactory NewMeterFactory(out ServiceProvider provider)
    {
        provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        return provider.GetRequiredService<IMeterFactory>();
    }

    [Fact]
    public void Meter_Name_Const_Is_Keeper()
    {
        Assert.Equal("Keeper", KeeperMetrics.MeterName);
    }

    [Fact]
    public void All_Eight_Instruments_Construct_NonNull()
    {
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new KeeperMetrics(meterFactory);

            Assert.NotNull(metrics.FaultConsumed);
            Assert.NotNull(metrics.Recovered);
            Assert.NotNull(metrics.DlqPushed);
            Assert.NotNull(metrics.WorkflowPaused);
            Assert.NotNull(metrics.WorkflowResumed);
            Assert.NotNull(metrics.L2ProbeFailed);
            Assert.NotNull(metrics.InFlight);
            Assert.NotNull(metrics.RecoveryDuration);
        }
    }

    [Fact]
    public void Instrument_Names_Are_Exact_SnakeCase_NoSuffix()
    {
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new KeeperMetrics(meterFactory);

            Assert.Equal("keeper_fault_consumed",   metrics.FaultConsumed.Name);
            Assert.Equal("keeper_recovered",        metrics.Recovered.Name);
            Assert.Equal("keeper_dlq_pushed",       metrics.DlqPushed.Name);
            Assert.Equal("keeper_workflow_paused",  metrics.WorkflowPaused.Name);
            Assert.Equal("keeper_workflow_resumed", metrics.WorkflowResumed.Name);
            Assert.Equal("keeper_l2_probe_failed",  metrics.L2ProbeFailed.Name);
            Assert.Equal("keeper_in_flight",        metrics.InFlight.Name);
            Assert.Equal("keeper_recovery_duration", metrics.RecoveryDuration.Name);

            // No instrument embeds the Prometheus suffix the collector appends.
            foreach (var name in new[]
                     {
                         metrics.FaultConsumed.Name, metrics.Recovered.Name, metrics.DlqPushed.Name,
                         metrics.WorkflowPaused.Name, metrics.WorkflowResumed.Name, metrics.L2ProbeFailed.Name,
                         metrics.InFlight.Name, metrics.RecoveryDuration.Name,
                     })
            {
                Assert.False(name.EndsWith("_total"),   $"{name} embeds _total");
                Assert.False(name.EndsWith("_seconds"), $"{name} embeds _seconds");
                Assert.False(name.EndsWith("_count"),   $"{name} embeds _count");
            }
        }
    }

    [Fact]
    public void RecoveryDuration_Has_SecondScale_Custom_Buckets()
    {
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new KeeperMetrics(meterFactory);

            // Route A (DiagnosticSource 10.0.0): the bucket boundaries are carried on the instrument's
            // Advice (InstrumentAdvice<double>.HistogramBucketBoundaries) — readable off the live instrument.
            var boundaries = metrics.RecoveryDuration.Advice?.HistogramBucketBoundaries;

            Assert.NotNull(boundaries);
            Assert.Equal(new double[] { 1, 5, 10, 30, 60, 120 }, boundaries!.ToArray());
            Assert.Equal("s", metrics.RecoveryDuration.Unit);
        }
    }

    [Fact]
    public void Recorded_Measurement_Carries_ProcessorId_But_No_WorkflowId_Label()
    {
        var processorId = new System.Guid("99999999-9999-9999-9999-999999999999").ToString("D");
        var capturedTagKeySets = new List<string[]>();

        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new KeeperMetrics(meterFactory);

            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == KeeperMetrics.MeterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                capturedTagKeySets.Add(tags.ToArray().Select(t => t.Key).ToArray());
            });
            listener.Start();

            var tag = new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, processorId);
            metrics.FaultConsumed.Add(1, tag);
            metrics.InFlight.Add(1, tag);
        }

        Assert.Equal(2, capturedTagKeySets.Count);
        foreach (var keys in capturedTagKeySets)
        {
            Assert.Contains("ProcessorId", keys);
            Assert.DoesNotContain("workflowId", keys);
            Assert.DoesNotContain("WorkflowId", keys);
        }
    }

    [Fact]
    public void Interned_Tag_Labels_Are_The_Locked_Literals()
    {
        // The closed-enum labels Plan 02's two consumers emit (KMET-02 cardinality contract).
        Assert.Equal("fault_type",      KeeperMetricTags.FaultType);
        Assert.Equal("outcome",         KeeperMetricTags.Outcome);
        Assert.Equal("reason",          KeeperMetricTags.Reason);
        Assert.Equal("ProcessorId",     KeeperMetricTags.ProcessorId);

        Assert.Equal("dispatch",        KeeperMetricTags.FaultTypeDispatch);
        Assert.Equal("result",          KeeperMetricTags.FaultTypeResult);
        Assert.Equal("recovered",       KeeperMetricTags.OutcomeRecovered);
        Assert.Equal("gave_up",         KeeperMetricTags.OutcomeGaveUp);
        Assert.Equal("probe_exhausted", KeeperMetricTags.ReasonProbeExhausted);
    }

    // ---- Plan 02 (KMET-02/03): the instrumentation-site wiring contract ----

    [Fact]
    public void Both_Fault_Consumers_Delegate_To_KeeperRecoveryHandler_Which_Owns_KeeperMetrics()
    {
        // KHARD-03 (Phase-40): the increment sites moved out of the two consumers into the single shared
        // KeeperRecoveryHandler. The consumers now ctor-inject the handler; the handler ctor-injects
        // KeeperMetrics. Assert the relocated-by-one-hop wiring point exists structurally.
        Assert.True(CtorHasParam(typeof(FaultEntryStepDispatchConsumer), typeof(KeeperRecoveryHandler)),
            "FaultEntryStepDispatchConsumer must take KeeperRecoveryHandler in its ctor");
        Assert.True(CtorHasParam(typeof(FaultExecutionResultConsumer), typeof(KeeperRecoveryHandler)),
            "FaultExecutionResultConsumer must take KeeperRecoveryHandler in its ctor");
        Assert.True(CtorHasParam(typeof(KeeperRecoveryHandler), typeof(KeeperMetrics)),
            "KeeperRecoveryHandler must take KeeperMetrics in its ctor (the single increment-site owner)");
    }

    [Fact]
    public void L2ProbeRecovery_Takes_KeeperMetrics_And_ProcId_Threaded_RunAsync()
    {
        Assert.True(CtorHasParam(typeof(L2ProbeRecovery), typeof(KeeperMetrics)),
            "L2ProbeRecovery must take KeeperMetrics in its ctor");

        // OPEN QUESTION resolved: RunAsync threads a `procId` string param so l2_probe_failed/in_flight
        // carry {ProcessorId}. Signature: RunAsync(string entryId, string h, string procId, CancellationToken).
        var run = typeof(L2ProbeRecovery).GetMethod(nameof(L2ProbeRecovery.RunAsync));
        Assert.NotNull(run);
        var paramNames = run!.GetParameters().Select(p => p.Name).ToArray();
        Assert.Contains("procId", paramNames);
    }

    private static bool CtorHasParam(System.Type type, System.Type paramType) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == paramType));

    // ── Hermetic faked-flow capture (KMET-01/02/03 falsifiable signal) ─────────────────────────────────
    //
    // Drives the REAL increment code (the two consumers + the probe loop) over a MeterListener that captures
    // every (name, value, tags) measurement from the "Keeper" meter, then asserts each instrument fired with
    // the EXACT tag set, in_flight measured +1 then -1, and NO measurement carries a workflowId tag.

    private sealed record Captured(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>A MeterListener that records every long+double measurement from the "Keeper" meter.</summary>
    private sealed class KeeperCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Captured> Measurements { get; } = new();

        public KeeperCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == KeeperMetrics.MeterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((inst, m, tags, _) => Record(inst.Name, m, tags));
            _listener.SetMeasurementEventCallback<double>((inst, m, tags, _) => Record(inst.Name, m, tags));
            _listener.Start();
        }

        private void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags) dict[t.Key] = t.Value;
            lock (Measurements) Measurements.Add(new Captured(name, value, dict));
        }

        public void Dispose() => _listener.Dispose();
    }

    private static KeeperMetrics MetricsFor(IMeterFactory f) => new(f);

    private static IOptions<ProbeOptions> Opts(int maxAttempts = 1, int delaySeconds = 0) =>
        Options.Create(new ProbeOptions { DelaySeconds = delaySeconds, MaxAttempts = maxAttempts });

    private static EntryStepDispatch DispatchInner(Guid processorId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), processorId, "payload")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("D"),
            H = "hermetic-h",
        };

    /// <summary>A ConsumeContext substitute carrying the Fault&lt;EntryStepDispatch&gt; envelope.</summary>
    private static ConsumeContext<Fault<EntryStepDispatch>> FaultContext(EntryStepDispatch inner)
    {
        var fault = Substitute.For<Fault<EntryStepDispatch>>();
        fault.Message.Returns(inner);
        fault.Exceptions.Returns(Array.Empty<ExceptionInfo>());

        var context = Substitute.For<ConsumeContext<Fault<EntryStepDispatch>>>();
        context.Message.Returns(fault);
        context.CancellationToken.Returns(CancellationToken.None);
        context.GetSendEndpoint(Arg.Any<Uri>()).Returns(Task.FromResult(Substitute.For<ISendEndpoint>()));
        return context;
    }

    private static bool HasTag(Captured c, string key, string value) =>
        c.Tags.TryGetValue(key, out var v) && (v as string) == value;

    [Fact]
    public async Task RecoveredFlow_Fires_All_Recovered_Instruments_With_Exact_Tags()
    {
        var processorId = Guid.NewGuid();
        var procId = processorId.ToString("D");
        var inner = DispatchInner(processorId);

        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using (provider)
        using (var cap = new KeeperCapture())
        {
            var metrics = MetricsFor(provider.GetRequiredService<IMeterFactory>());
            // Up Redis → the probe recovers on the first iteration → the recovered branch fires.
            var capRedis = new FakeRedis(FakeRedis.RedisHealth.Up);   // KHARD-01: cap counter reachable (n=1 <= cap → reinject, recovered metrics)
            var recovery = new L2ProbeRecovery(capRedis.Multiplexer, Opts(), metrics);
            // KHARD-03 (Phase-40): the consumer now delegates to the shared KeeperRecoveryHandler.
            var handler = new KeeperRecoveryHandler(NullLogger<KeeperRecoveryHandler>.Instance, recovery, metrics, capRedis.Multiplexer, Opts());
            var consumer = new FaultEntryStepDispatchConsumer(handler);

            await consumer.Consume(FaultContext(inner));

            var ms = cap.Measurements;

            Assert.Contains(ms, c => c.Name == "keeper_fault_consumed"
                && HasTag(c, KeeperMetricTags.FaultType, KeeperMetricTags.FaultTypeDispatch)
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));
            Assert.Contains(ms, c => c.Name == "keeper_workflow_paused"
                && HasTag(c, KeeperMetricTags.ProcessorId, procId)
                && !c.Tags.ContainsKey(KeeperMetricTags.FaultType));   // workflow-scoped — no fault_type
            Assert.Contains(ms, c => c.Name == "keeper_workflow_resumed"
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));
            Assert.Contains(ms, c => c.Name == "keeper_recovered"
                && HasTag(c, KeeperMetricTags.FaultType, KeeperMetricTags.FaultTypeDispatch)
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));
            Assert.Contains(ms, c => c.Name == "keeper_recovery_duration"
                && HasTag(c, KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeRecovered)
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));

            // No dlq on the recovered path.
            Assert.DoesNotContain(ms, c => c.Name == "keeper_dlq_pushed");
            AssertNoWorkflowIdAnywhere(ms);
        }
    }

    [Fact]
    public async Task GiveUpFlow_Fires_Dlq_GaveUp_And_ProbeFailed_With_Exact_Tags()
    {
        var processorId = Guid.NewGuid();
        var procId = processorId.ToString("D");
        var inner = DispatchInner(processorId);

        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using (provider)
        using (var cap = new KeeperCapture())
        {
            var metrics = MetricsFor(provider.GetRequiredService<IMeterFactory>());
            // Down Redis for all attempts → give-up branch fires + one l2_probe_failed per attempt.
            var capRedis = new FakeRedis(FakeRedis.RedisHealth.Down);   // GaveUp → the cap check is never reached
            var recovery = new L2ProbeRecovery(capRedis.Multiplexer, Opts(maxAttempts: 2), metrics);
            // KHARD-03 (Phase-40): the consumer now delegates to the shared KeeperRecoveryHandler.
            var handler = new KeeperRecoveryHandler(NullLogger<KeeperRecoveryHandler>.Instance, recovery, metrics, capRedis.Multiplexer, Opts());
            var consumer = new FaultEntryStepDispatchConsumer(handler);

            await consumer.Consume(FaultContext(inner));

            var ms = cap.Measurements;

            Assert.Contains(ms, c => c.Name == "keeper_dlq_pushed"
                && HasTag(c, KeeperMetricTags.Reason, KeeperMetricTags.ReasonProbeExhausted)
                && HasTag(c, KeeperMetricTags.FaultType, KeeperMetricTags.FaultTypeDispatch)
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));
            Assert.Contains(ms, c => c.Name == "keeper_recovery_duration"
                && HasTag(c, KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeGaveUp)
                && HasTag(c, KeeperMetricTags.ProcessorId, procId));
            // One probe-failed per Down attempt (maxAttempts = 2).
            Assert.Equal(2, ms.Count(c => c.Name == "keeper_l2_probe_failed"
                && HasTag(c, KeeperMetricTags.ProcessorId, procId)));

            // No recovered-path instruments on give-up.
            Assert.DoesNotContain(ms, c => c.Name == "keeper_recovered");
            Assert.DoesNotContain(ms, c => c.Name == "keeper_workflow_resumed");
            AssertNoWorkflowIdAnywhere(ms);
        }
    }

    [Fact]
    public async Task InFlight_Measures_Plus1_Then_Minus1_Across_RunAsync()
    {
        var procId = Guid.NewGuid().ToString("D");

        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        using (provider)
        using (var cap = new KeeperCapture())
        {
            var metrics = MetricsFor(provider.GetRequiredService<IMeterFactory>());
            var recovery = new L2ProbeRecovery(new FakeRedis(FakeRedis.RedisHealth.Up).Multiplexer, Opts(), metrics);

            await recovery.RunAsync("entry-id", "h", procId, CancellationToken.None);

            // Filter by THIS test's unique procId: KeeperMetricsFacts has no [Collection], so it runs in
            // parallel with other "Keeper"-meter tests; the process-wide MeterListener would otherwise also
            // capture a concurrent test's in_flight +1/-1 (observed as Expected 2 / Actual 4). The other
            // assertions in this file already ProcessorId-filter; this one was missing it.
            var inFlight = cap.Measurements.Where(c => c.Name == "keeper_in_flight"
                && HasTag(c, KeeperMetricTags.ProcessorId, procId)).ToArray();
            Assert.Equal(2, inFlight.Length);
            Assert.Equal(1d, inFlight[0].Value);    // +1 on entry
            Assert.Equal(-1d, inFlight[1].Value);   // -1 in finally
            Assert.All(inFlight, c => Assert.True(HasTag(c, KeeperMetricTags.ProcessorId, procId)));

            AssertNoWorkflowIdAnywhere(cap.Measurements);
        }
    }

    private static void AssertNoWorkflowIdAnywhere(IEnumerable<Captured> measurements)
    {
        foreach (var c in measurements)
        {
            Assert.DoesNotContain("workflowId", c.Tags.Keys);
            Assert.DoesNotContain("WorkflowId", c.Tags.Keys);
            Assert.DoesNotContain("correlationId", c.Tags.Keys);
            Assert.DoesNotContain("CorrelationId", c.Tags.Keys);
        }
    }
}
