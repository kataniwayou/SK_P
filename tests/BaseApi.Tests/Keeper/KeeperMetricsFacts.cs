using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Keeper.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
}
